using System.CommandLine;
using Elevate.Audit.Auth;
using Elevate.Audit.Collectors;
using Elevate.Audit.Infrastructure;
using Elevate.Audit.Model;
using Elevate.Audit.Networking;
using Elevate.Audit.Rendering;
using Elevate.Audit.Rules;
using Elevate.Core.Models;
using Elevate.Core.Networking;
using Elevate.Core.Providers;
using Spectre.Console;

namespace Elevate.Audit.Commands;

/// <summary>The default command: sign in (or load a snapshot), scan, run the rules, render, set the exit code.</summary>
public static class ScanCommand
{
    public static readonly Option<string> TenantOption = new("--tenant") { Description = "Tenant id or domain to scan. Default: the account's home tenant.", DefaultValueFactory = _ => "organizations" };
    public static readonly Option<string?> ClientIdOption = new("--client-id") { Description = $"Public client to use for Microsoft Graph instead of {ClientIds.GraphDefaultDisplayName}; it must be consented to the same read scopes." };
    public static readonly Option<bool> DeviceCodeOption = new("--device-code") { Description = "Sign in with a device code instead of the browser; for SSH sessions and containers." };
    public static readonly Option<bool> SkipAzureOption = new("--skip-azure") { Description = "Do not sign in to Azure Resource Manager or scan Azure RBAC." };
    public static readonly Option<bool> AllRolesOption = new("--all-roles") { Description = "Report every role, not only the privileged ones." };
    public static readonly Option<string> MinSeverityOption = new("--min-severity") { Description = "Lowest severity to show: high, medium, low or info.", DefaultValueFactory = _ => "info" };
    public static readonly Option<string[]> IgnoreOption = new("--ignore") { Description = "Rule code to skip (repeatable), e.g. --ignore GA-COUNT.", AllowMultipleArgumentsPerToken = true };
    public static readonly Option<bool> NoFailOption = new("--no-fail") { Description = "Exit 0 even when there are high findings." };
    public static readonly Option<string?> JsonOption = new("--json") { Description = "Write the JSON report to this file, or - for stdout (then nothing else goes to stdout)." };
    public static readonly Option<string?> HtmlOption = new("--html") { Description = "Write a self-contained HTML report to this file." };
    public static readonly Option<string?> SaveSnapshotOption = new("--save-snapshot") { Description = "Also write everything that was read to this file, for offline re-rendering or a bug report." };
    public static readonly Option<string?> FromSnapshotOption = new("--from-snapshot") { Description = "Skip sign-in and reading; run the rules on a saved snapshot." };
    public static readonly Option<bool> QuietOption = new("--quiet", "-q") { Description = "Only the summary line on stdout and no progress on stderr." };
    public static readonly Option<bool> NoColorOption = new("--no-color") { Description = "Plain output (NO_COLOR is honoured too)." };
    public static readonly Option<bool> VerboseOption = new("--verbose") { Description = "Log every request on stderr." };

    public static void AddTo(RootCommand root)
    {
        ArgumentNullException.ThrowIfNull(root);
        foreach (var option in new Option[] { TenantOption, ClientIdOption, DeviceCodeOption, SkipAzureOption, AllRolesOption, MinSeverityOption, IgnoreOption, NoFailOption, JsonOption, HtmlOption, SaveSnapshotOption, FromSnapshotOption, QuietOption, NoColorOption, VerboseOption })
        {
            root.Options.Add(option);
        }

        root.SetAction(RunAsync);
    }

    public static async Task<int> RunAsync(ParseResult parse, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(parse);
        var terminal = new Terminal(parse.GetValue(QuietOption), parse.GetValue(NoColorOption));
        if (!Severities.TryParse(parse.GetValue(MinSeverityOption), out var minSeverity))
        {
            throw new AuditException($"Unknown severity '{parse.GetValue(MinSeverityOption)}'. Use high, medium, low or info.");
        }

        var options = new AuditOptions(
            AllRoles: parse.GetValue(AllRolesOption),
            Ignored: (parse.GetValue(IgnoreOption) ?? []).Select(i => i.ToUpperInvariant()).ToList(),
            MinSeverity: minSeverity,
            SkipAzure: parse.GetValue(SkipAzureOption));
        var jsonTarget = parse.GetValue(JsonOption);
        var jsonToStdout = jsonTarget == "-";

        Snapshot snapshot;
        IReadOnlyList<string> scopesRequested;
        if (parse.GetValue(FromSnapshotOption) is { } snapshotPath)
        {
            snapshot = SnapshotFile.Load(snapshotPath);
            scopesRequested = [];
            terminal.Note($"Loaded snapshot {snapshotPath} (scanned {snapshot.ScannedAt:u}).");
        }
        else
        {
            var clientId = parse.GetValue(ClientIdOption);
            scopesRequested = clientId is null ? ClientIds.GraphReadScopeNames : [".default"];
            snapshot = await ScanLiveAsync(parse.GetValue(TenantOption)!, clientId, parse.GetValue(DeviceCodeOption), parse.GetValue(VerboseOption), options, terminal, ct).ConfigureAwait(false);
        }

        if (parse.GetValue(SaveSnapshotOption) is { } savePath)
        {
            SnapshotFile.Save(snapshot, savePath);
            terminal.Note($"Snapshot written to {savePath}.");
        }

        var findings = RuleRunner.Run(snapshot, options);
        var report = AuditReport.From(snapshot, RuleRunner.Visible(findings, options), options, AppInfo.Version, scopesRequested);

        if (jsonTarget is not null)
        {
            var json = JsonRenderer.Render(report);
            if (jsonToStdout)
            {
                Console.Out.Write(json);
            }
            else
            {
                File.WriteAllText(jsonTarget, json);
                terminal.Note($"JSON report written to {jsonTarget}.");
            }
        }

        if (parse.GetValue(HtmlOption) is { } htmlPath)
        {
            File.WriteAllText(htmlPath, HtmlRenderer.Render(report));
            terminal.Note($"HTML report written to {htmlPath}.");
        }

        if (!jsonToStdout)
        {
            TerminalRenderer.Render(report, terminal.Stdout, summaryOnly: terminal.Quiet);
        }

        return RuleRunner.HasHigh(findings) && !parse.GetValue(NoFailOption) ? ExitCodes.HighFindings : ExitCodes.Ok;
    }

    private static async Task<Snapshot> ScanLiveAsync(string tenant, string? clientId, bool deviceCode, bool verbose, AuditOptions options, Terminal terminal, CancellationToken ct)
    {
        var provider = new AuditTokenProvider(clientId, tenant, deviceCode, terminal.Say);
        Identity identity;
        try
        {
            identity = await provider.SignInAsync(ct).ConfigureAwait(false);
        }
        catch (PimException e)
        {
            throw new AuditException(MsalErrors.Explain(e, provider.GraphClientId));
        }

        IHttpClient http = new RetryingHttpClient(new HttpClientAdapter(new HttpClient { Timeout = TimeSpan.FromSeconds(100) }));
        if (verbose)
        {
            http = new LoggingHttpClient(http, line => terminal.Stderr.MarkupLine($"[grey]{Spectre.Console.Markup.Escape(line)}[/]"));
        }

        var graph = new GraphTransport(http, provider);
        var arm = options.SkipAzure ? null : new GraphTransport(http, provider, GraphTransport.MapArmError);
        var tenantId = Guid.TryParse(tenant, out _) ? tenant : identity.HomeTenantId;
        terminal.Note($"Signed in as {identity.Upn}.");
        try
        {
            return await new Scanner(graph, arm, identity, tenantId, options, AppInfo.Version, terminal.Note).ScanAsync(ct).ConfigureAwait(false);
        }
        catch (PimException e) when (e.Kind is PimErrorKind.Forbidden or PimErrorKind.ConsentRequired)
        {
            throw new AuditException(MsalErrors.Explain(e, provider.GraphClientId));
        }
    }
}
