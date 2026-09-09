using System.CommandLine;
using Spectre.Console;
using Elevate.Cli.Auth;
using Elevate.Cli.Infrastructure;
using Elevate.Cli.Session;
using Elevate.Core.Managed;
using Elevate.Core.Models;
using Elevate.Core.Networking;
using Elevate.Core.Storage;

namespace Elevate.Cli.Commands;

/// <summary>The global options every command sees, and the session built from them on first use.</summary>
public sealed class CommandContext
{
    public static readonly Option<bool> JsonOption = new("--json") { Description = "Print machine-readable JSON on stdout instead of tables.", Recursive = true };
    public static readonly Option<bool> QuietOption = new("--quiet", "-q") { Description = "No progress notes on stderr.", Recursive = true };
    public static readonly Option<bool> NoColorOption = new("--no-color") { Description = "Plain output without colours (NO_COLOR is honoured too).", Recursive = true };
    public static readonly Option<bool> DeviceCodeOption = new("--device-code") { Description = "Sign in with a device code instead of the browser; for SSH sessions and containers.", Recursive = true };
    public static readonly Option<string?> DataDirOption = new("--data-dir") { Description = "Where state, settings and the token cache live (also ELEVATE_CLI_HOME).", Recursive = true };

    /// <summary>
    /// Test seam: the managed configuration commands see, instead of the platform source. Async-local
    /// so tests that run side by side do not see each other's policy.
    /// </summary>
    private static readonly AsyncLocal<ManagedConfiguration?> Override = new();

    internal static ManagedConfiguration? ManagedOverride
    {
        get => Override.Value;
        set => Override.Value = value;
    }

    private ElevateSession? _session;
    private bool _managedResolved;

    private CommandContext(Output output, string dataDirectory, InteractiveFlow flow)
    {
        Output = output;
        DataDirectory = dataDirectory;
        Flow = flow;
    }

    public Output Output { get; }

    public string DataDirectory { get; }

    public InteractiveFlow Flow { get; }

    public static CommandContext From(ParseResult parse)
    {
        ArgumentNullException.ThrowIfNull(parse);
        var output = new Output(parse.GetValue(JsonOption), parse.GetValue(QuietOption), parse.GetValue(NoColorOption));
        var flow = parse.GetValue(DeviceCodeOption) || Headless() ? InteractiveFlow.DeviceCode : InteractiveFlow.Browser;
        return new CommandContext(output, Infrastructure.DataDirectory.Resolve(parse.GetValue(DataDirOption)), flow);
    }

    /// <summary>The session, loaded on first use. Every command that touches state goes through here.</summary>
    public ElevateSession Session
    {
        get
        {
            if (_session is not null)
            {
                return _session;
            }

            var store = new AppStateStore(DataDirectory);
            var settings = new CliSettings(DataDirectory, ManagedOverride);
            var cache = new TokenCacheStore(DataDirectory, settings.UnprotectedCache);
            var tokens = new CliTokenProvider(cache, () => settings.ClientId, Flow, message => Output.Stderr.MarkupLine($"[blue]{Markup.Escape(message)}[/]"));
            var http = new HttpClientAdapter(new HttpClient { Timeout = TimeSpan.FromSeconds(60) });
            _session = new ElevateSession(store, settings, tokens, http);
            _session.Load();
            if (_session.LoadNotice is { } notice)
            {
                Output.Warn(Markup.Escape(notice));
            }

            foreach (var identity in _session.Identities)
            {
                tokens.Touch(identity.SignInMethod);
            }

            return _session;
        }
    }

    /// <summary>
    /// The session with the organization's managed tenants resolved: the allowed and pinned lists
    /// turned into tenant ids, tenants the organization no longer permits dropped, and the pinned
    /// ones tracked. Every command that lists or changes tenants or accounts goes through here, so
    /// the policy is in place before anything is shown or written. Resolves once per invocation,
    /// and does nothing at all when no tenants are managed.
    /// </summary>
    public async Task<ElevateSession> SessionAsync(CancellationToken ct = default)
    {
        var session = Session;
        if (_managedResolved)
        {
            return session;
        }

        _managedResolved = true;
        await session.ResolveManagedTenantsAsync(ct).ConfigureAwait(false);
        foreach (var warning in session.ManagedTenantWarnings)
        {
            Output.Warn(Markup.Escape(warning));
        }

        return session;
    }

    /// <summary>The account for <c>--account</c>, or the only one, or an error naming the choices.</summary>
    public Identity RequireAccount(string? account)
    {
        var identities = Session.Identities;
        if (identities.Count == 0)
        {
            throw new CliException("No account is signed in. Run 'elevate login' first.", ExitCodes.SignInRequired);
        }

        if (string.IsNullOrWhiteSpace(account))
        {
            if (identities.Count == 1)
            {
                return identities[0];
            }

            throw new CliException("Several accounts are signed in; say which with --account: " + string.Join(", ", identities.Select(i => i.Upn)), ExitCodes.Usage);
        }

        var matches = identities.Where(i =>
            i.Upn.Contains(account, StringComparison.OrdinalIgnoreCase)
            || i.DisplayName.Contains(account, StringComparison.OrdinalIgnoreCase)
            || i.Id.Equals(account, StringComparison.OrdinalIgnoreCase)).ToList();
        return matches.Count switch
        {
            1 => matches[0],
            0 => throw new CliException($"No signed-in account matches '{account}'.", ExitCodes.NotFound),
            _ => throw new CliException($"'{account}' matches several accounts: " + string.Join(", ", matches.Select(i => i.Upn)), ExitCodes.NotFound),
        };
    }

    /// <summary>The tenant for a name, id or domain fragment, optionally within one account.</summary>
    public TenantContext RequireTenant(string tenant, string? account)
    {
        var scope = string.IsNullOrWhiteSpace(account) ? Session.Tenants : [.. Session.Tenants.Where(t => t.IdentityId == RequireAccount(account).Id)];
        var matches = scope.Where(t =>
            t.DisplayName.Contains(tenant, StringComparison.OrdinalIgnoreCase)
            || t.TenantId.Equals(tenant, StringComparison.OrdinalIgnoreCase)).ToList();
        return matches.Count switch
        {
            1 => matches[0],
            0 => throw new CliException($"No tracked tenant matches '{tenant}'. Run 'elevate tenants' to list them.", ExitCodes.NotFound),
            _ => throw new CliException($"'{tenant}' matches several tenants; add --account or use the tenant id: "
                + string.Join(", ", matches.Select(t => $"{t.DisplayName} ({t.TenantId}, {Session.AccountName(t.IdentityId)})")), ExitCodes.NotFound),
        };
    }

    public void RequireSignedIn()
    {
        if (Session.Identities.Count == 0)
        {
            throw new CliException("No account is signed in. Run 'elevate login' first.", ExitCodes.SignInRequired);
        }
    }

    private static bool Headless()
    {
        if (OperatingSystem.IsWindows() || OperatingSystem.IsMacOS())
        {
            return false;
        }

        var display = Environment.GetEnvironmentVariable("DISPLAY");
        var wayland = Environment.GetEnvironmentVariable("WAYLAND_DISPLAY");
        return string.IsNullOrEmpty(display) && string.IsNullOrEmpty(wayland);
    }
}
