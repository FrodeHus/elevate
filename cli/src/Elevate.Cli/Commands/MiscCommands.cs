using System.CommandLine;
using System.Reflection;
using System.Runtime.InteropServices;
using Elevate.Cli.Completion;
using Elevate.Cli.Infrastructure;
using Elevate.Cli.Rendering;
using Elevate.Cli.Update;
using Elevate.Core.Catalogue;
using Elevate.Core.Managed;
using Elevate.Core.Models;
using Elevate.Core.Networking;
using Elevate.Core.Support;
using Spectre.Console;

namespace Elevate.Cli.Commands;

/// <summary><c>catalogue</c>, <c>diagnostics</c>, <c>update</c> and <c>completion</c>.</summary>
public static class MiscCommands
{
    public static string Version =>
        Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
        ?? Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0";

    public static Command Catalogue()
    {
        var query = new Argument<string?>("query") { Description = "Only roles whose name or description contains this text.", Arity = ArgumentArity.ZeroOrOne };
        var command = new Command("catalogue", "The built-in Entra directory roles, for 'tenants manual add --entra'.") { query };
        command.SetAction((parse, _) =>
        {
            var context = CommandContext.From(parse);
            var q = parse.GetValue(query);
            var roles = RoleCatalogue.EntraBuiltInRoles()
                .Where(r => string.IsNullOrWhiteSpace(q)
                    || r.DisplayName.Contains(q, StringComparison.OrdinalIgnoreCase)
                    || r.Description.Contains(q, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (context.Output.Json)
            {
                context.Output.WriteJson(roles.Select(r => new Dto.CatalogueEntry(r.TemplateId, r.DisplayName, r.Description, r.IsPrivileged)).ToList());
                return Task.FromResult(ExitCodes.Ok);
            }

            var table = new Table().Border(TableBorder.Rounded).Expand();
            table.AddColumn("Role");
            table.AddColumn("Template id");
            table.AddColumn("Description");
            foreach (var r in roles)
            {
                table.AddRow(Markup.Escape(r.DisplayName) + (r.IsPrivileged ? " [yellow]privileged[/]" : string.Empty), Markup.Escape(r.TemplateId), Markup.Escape(r.Description));
            }

            context.Output.Write(table);
            return Task.FromResult(ExitCodes.Ok);
        });
        return command;
    }

    public static Command Diagnostics()
    {
        var command = new Command("diagnostics", "A plain-text report to paste into a bug report. Never contains a token or client id.");
        command.SetAction(async (parse, ct) =>
        {
            var context = CommandContext.From(parse);
            var session = await context.SessionAsync(ct).ConfigureAwait(false);
            if (session.Identities.Count > 0)
            {
                try
                {
                    await context.Output.StatusAsync("Reading tenants…", () => session.RefreshAllAsync(ct)).ConfigureAwait(false);
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    session.LogError(e.Message);
                }
            }

            var input = new DiagnosticsInput(
                Version, RuntimeInformation.RuntimeIdentifier, "unsigned CLI", RuntimeInformation.OSDescription,
                [.. session.Identities.Select(i => new DiagnosticsAccount(i.Upn, i.SignInMethod.DisplayName, session.Tenants.Count(t => t.IdentityId == i.Id)))],
                [.. session.Tenants.Select(t => new DiagnosticsTenant(t.DisplayName, t.TenantId,
                    t.DiscoveryMode == DiscoveryMode.Automatic ? "automatic" : "manual roles", Views.TenantFlags(session, t)))],
                [.. session.Profiles.Select(p => p.Source == ProfileSource.Managed ? $"{p.Name} (managed)" : p.Name)],
                null,
                session.ErrorLog.Entries,
                // Key names only: a managed value (the client id) never belongs in a bug report.
                session.Settings.Managed is { IsEmpty: false } managed
                    ? new DiagnosticsManaged(managed.Origin ?? "policy", [.. managed.KeysInEffect.Select(k => k.Name())], managed.Warnings)
                    : null);
            // The shared renderer labels the OS line for the Windows app; this report is not Windows-only.
            var text = DiagnosticsReport.Render(input).Replace("\nWindows: ", "\nOS: ", StringComparison.Ordinal);
            context.Output.Plain(text);
            return ExitCodes.Ok;
        });
        return command;
    }

    public static Command Update()
    {
        var command = new Command("update", "Check GitHub for a newer release of the CLI.");
        command.SetAction(async (parse, ct) =>
        {
            var context = CommandContext.From(parse);
            // The organization turned the check off, so nothing reaches github.com — not even
            // from an explicit command. Both apps refuse a forced check for the same reason.
            if (context.Session.Settings.UpdateCheckDisabled)
            {
                if (context.Output.Json)
                {
                    context.Output.WriteJson(new { current = Version, managed = true });
                }
                else
                {
                    context.Output.Plain("Update checks are managed by your organization.");
                }

                return ExitCodes.Ok;
            }

            var checker = new UpdateChecker(new HttpClientAdapter(new HttpClient { Timeout = TimeSpan.FromSeconds(15) }));
            var latest = await context.Output.StatusAsync("Checking for updates…", () => checker.LatestAsync(ct)).ConfigureAwait(false);
            var current = Version;
            var newer = latest is not null && AppVersion.IsNewer(latest.Tag, current);
            context.Session.Settings.LastUpdateCheck = DateTimeOffset.UtcNow;
            context.Session.Settings.LatestKnownVersion = latest?.Version;
            if (context.Output.Json)
            {
                context.Output.WriteJson(new { current, latest = latest?.Version, url = latest?.Url, updateAvailable = newer });
                return ExitCodes.Ok;
            }

            if (latest is null)
            {
                context.Output.Plain($"elevate {current}; no release with a CLI build was found.");
            }
            else if (newer)
            {
                context.Output.WriteLine($"elevate {Markup.Escape(current)}; [green]{Markup.Escape(latest.Version)} is available[/]: {Markup.Escape(latest.Url.ToString())}");
                context.Output.Note("Homebrew: brew upgrade frodehus/elevate/elevate-cli · winget: winget upgrade Reothor.Elevate.CLI");
            }
            else
            {
                context.Output.Plain($"elevate {current} is up to date.");
            }

            return ExitCodes.Ok;
        });
        return command;
    }

    /// <summary>
    /// Whether the update hint after <c>status</c> is worth printing at all: not with machine
    /// output, not under --quiet, and never when the organization has turned the check off.
    /// </summary>
    internal static bool ShouldMentionUpdate(Output output, CliSettings settings)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(settings);
        return !output.Json && !output.Quiet && !settings.UpdateCheckDisabled;
    }

    /// <summary>At most once a day, and only when it costs nothing: a one-line hint after <c>status</c>.</summary>
    internal static async Task DailyUpdateHintAsync(CommandContext context, CancellationToken ct)
    {
        var settings = context.Session.Settings;
        if (!ShouldMentionUpdate(context.Output, settings))
        {
            return;
        }

        if (settings.LastUpdateCheck is { } last && DateTimeOffset.UtcNow - last < TimeSpan.FromHours(24))
        {
            if (settings.LatestKnownVersion is { } known && AppVersion.IsNewer(known, Version))
            {
                context.Output.Note($"elevate {Markup.Escape(known)} is available ('elevate update').");
            }

            return;
        }

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(3));
            var latest = await new UpdateChecker(new HttpClientAdapter(new HttpClient { Timeout = TimeSpan.FromSeconds(3) })).LatestAsync(cts.Token).ConfigureAwait(false);
            settings.LastUpdateCheck = DateTimeOffset.UtcNow;
            settings.LatestKnownVersion = latest?.Version;
            if (latest is not null && AppVersion.IsNewer(latest.Tag, Version))
            {
                context.Output.Note($"elevate {Markup.Escape(latest.Version)} is available ('elevate update').");
            }
        }
        catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            // Best effort only.
        }
    }

    public static Command Init()
    {
        var shell = new Argument<string>("shell") { Description = "bash, zsh, fish or pwsh." };
        var command = new Command("init", "Print a shell hook that suggests 'elevate run' when az, kubectl, terraform or helm fail with an authorization error. E.g. `eval \"$(elevate init zsh)\"`.") { shell };
        command.SetAction((parse, _) =>
        {
            Console.Out.Write(ShellHooks.Generate(parse.GetValue(shell)!));
            return Task.FromResult(ExitCodes.Ok);
        });
        return command;
    }

    public static Command Completion()
    {
        var shell = new Argument<string>("shell") { Description = "bash, zsh, fish or powershell." };
        var command = new Command("completion", "Print a shell completion script. E.g. `source <(elevate completion bash)`.") { shell };
        command.SetAction((parse, _) =>
        {
            var root = Program.BuildRootCommand();
            var script = CompletionScripts.Generate(root, parse.GetValue(shell)!);
            Console.Out.Write(script);
            return Task.FromResult(ExitCodes.Ok);
        });
        return command;
    }
}
