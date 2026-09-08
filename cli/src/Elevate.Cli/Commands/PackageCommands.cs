using System.CommandLine;
using Elevate.Cli.Infrastructure;
using Elevate.Cli.Rendering;
using Elevate.Cli.Selection;
using Elevate.Cli.Session;
using Elevate.Core.Models;
using Elevate.Core.Providers;
using Spectre.Console;

namespace Elevate.Cli.Commands;

/// <summary><c>packages</c>: entitlement management access packages, the terminal counterpart of the apps' access packages window.</summary>
public static class PackageCommands
{
    public static Command Packages()
    {
        var account = CommonOptions.Account();
        var tenant = CommonOptions.Tenant();
        var command = new Command("packages", "Access packages you can request, have requested or hold. Bare 'packages' lists the requestable ones.") { account, tenant };
        command.SetAction((parse, ct) => RunListAsync(CommandContext.From(parse), parse.GetValue(account), parse.GetValue(tenant), ct));
        command.Subcommands.Add(List());
        command.Subcommands.Add(Requests());
        command.Subcommands.Add(Assigned());
        command.Subcommands.Add(Request());
        command.Subcommands.Add(Cancel());
        return command;
    }

    private static Command List()
    {
        var account = CommonOptions.Account();
        var tenant = CommonOptions.Tenant();
        var command = new Command("list", "Packages you may request, with the state of any pending request or delivered assignment.") { account, tenant };
        command.SetAction((parse, ct) => RunListAsync(CommandContext.From(parse), parse.GetValue(account), parse.GetValue(tenant), ct));
        return command;
    }

    internal static async Task<int> RunListAsync(CommandContext context, string? account, string? tenant, CancellationToken ct)
    {
        context.RequireSignedIn();
        var session = context.Session;
        var (reads, failures) = await ReadAsync(context, account, tenant, includePackages: true, ct).ConfigureAwait(false);
        if (context.Output.Json)
        {
            context.Output.WriteJson(reads.SelectMany(r => r.Packages.Select(p => Views.Package(session, r, p))).ToList());
        }
        else if (reads.All(r => r.Packages.Count == 0))
        {
            if (reads.Count > 0)
            {
                context.Output.Plain("No access packages are available to request.");
            }
        }
        else
        {
            context.Output.Write(Views.PackagesTable(session, reads, session.Identities.Count > 1));
            context.Output.Note("'elevate packages request <package> --justification …' to request one.");
        }

        return Exit(reads.Count, failures);
    }

    private static Command Requests()
    {
        var account = CommonOptions.Account();
        var tenant = CommonOptions.Tenant();
        var all = new Option<bool>("--all") { Description = "Include denied, failed and cancelled requests, with their completion date and the service's status text." };
        var command = new Command("requests", "Your own package requests; open ones by default.") { account, tenant, all };
        command.SetAction(async (parse, ct) =>
        {
            var context = CommandContext.From(parse);
            context.RequireSignedIn();
            var session = context.Session;
            var showAll = parse.GetValue(all);
            var (reads, failures) = await ReadAsync(context, parse.GetValue(account), parse.GetValue(tenant), includePackages: false, ct).ConfigureAwait(false);
            var now = DateTimeOffset.UtcNow;
            var rows = reads
                .SelectMany(r => r.Requests.Where(q => showAll || q.State.IsOpen()).Select(q => (r.Key, Request: q)))
                .OrderByDescending(x => x.Request.CreatedAt ?? DateTimeOffset.MinValue)
                .ThenBy(x => x.Request.PackageName, StringComparer.Ordinal)
                .ToList();
            if (context.Output.Json)
            {
                context.Output.WriteJson(rows.Select(x => Views.PackageRequest(session, x.Key, x.Request)).ToList());
            }
            else if (rows.Count == 0)
            {
                if (reads.Count > 0)
                {
                    context.Output.Plain(showAll ? "No package requests." : "No open package requests. '--all' includes finished ones.");
                }
            }
            else
            {
                context.Output.Write(Views.PackageRequestsTable(session, rows, now, showAll, session.Identities.Count > 1));
                if (rows.Any(x => x.Request.State.IsCancellable()))
                {
                    context.Output.Note("'elevate packages cancel <id>' withdraws a request that is still awaiting approval.");
                }
            }

            return Exit(reads.Count, failures);
        });
        return command;
    }

    private static Command Assigned()
    {
        var account = CommonOptions.Account();
        var tenant = CommonOptions.Tenant();
        var command = new Command("assigned", "Packages delivered to you, with expiry and policy.") { account, tenant };
        command.SetAction(async (parse, ct) =>
        {
            var context = CommandContext.From(parse);
            context.RequireSignedIn();
            var session = context.Session;
            var (reads, failures) = await ReadAsync(context, parse.GetValue(account), parse.GetValue(tenant), includePackages: false, ct).ConfigureAwait(false);
            var now = DateTimeOffset.UtcNow;
            var rows = reads
                .SelectMany(r => r.Assignments.Where(a => a.State == AccessPackageAssignmentState.Delivered).Select(a => (r.Key, Assignment: a)))
                .OrderBy(x => x.Assignment.ExpiresAt ?? DateTimeOffset.MaxValue)
                .ThenBy(x => x.Assignment.PackageName, StringComparer.Ordinal)
                .ToList();
            if (context.Output.Json)
            {
                context.Output.WriteJson(rows.Select(x => Views.PackageAssignment(session, x.Key, x.Assignment, now)).ToList());
            }
            else if (rows.Count == 0)
            {
                if (reads.Count > 0)
                {
                    context.Output.Plain("No access packages are assigned to you.");
                }
            }
            else
            {
                context.Output.Write(Views.PackageAssignmentsTable(session, rows, now, session.Identities.Count > 1));
            }

            return Exit(reads.Count, failures);
        });
        return command;
    }

    private static Command Request()
    {
        var package = new Argument<string>("package") { Description = "A package name or id from 'elevate packages list'." };
        var account = CommonOptions.Account();
        var tenant = CommonOptions.Tenant();
        var justification = new Option<string?>("--justification", "-j") { Description = "Why you need it; prompted in a terminal when omitted." };
        var policy = new Option<string?>("--policy", "-p") { Description = "The policy to request under (id or name); needed only when several apply." };
        var command = new Command("request", "Request an access package. Packages whose policy asks questions must be requested in My Access.") { package, account, tenant, justification, policy };
        command.SetAction(async (parse, ct) =>
        {
            var context = CommandContext.From(parse);
            context.RequireSignedIn();
            var session = context.Session;
            var (reads, _) = await ReadAsync(context, parse.GetValue(account), parse.GetValue(tenant), includePackages: true, ct).ConfigureAwait(false);
            var (key, chosen) = ResolvePackage(session, reads, parse.GetValue(package)!);

            var requirements = await context.Output.StatusAsync($"Checking the policies for {chosen.DisplayName}…",
                () => session.PackageRequirementsAsync(key, chosen.Id, ct)).ConfigureAwait(false);
            var selected = ChoosePolicy(requirements, parse.GetValue(policy), chosen.DisplayName);
            if (selected.RequiresAnswers)
            {
                throw new CliException(
                    $"{chosen.DisplayName} asks questions that Elevate does not collect. Request it in My Access: {AccessPackageProvider.MyAccessUrl(key.TenantId, chosen.Id)}",
                    ExitCodes.Failure);
            }

            var reason = (parse.GetValue(justification) ?? string.Empty).Trim();
            if (reason.Length == 0)
            {
                if (!context.Output.CanPrompt)
                {
                    throw new CliException($"{chosen.DisplayName} needs a justification: pass --justification.", ExitCodes.Usage);
                }

                reason = context.Output.Stderr.Prompt(new TextPrompt<string>($"Justification for [bold]{Markup.Escape(chosen.DisplayName)}[/]:")).Trim();
            }

            var created = await context.Output.StatusAsync($"Requesting {chosen.DisplayName}…",
                () => session.RequestPackageAsync(key, chosen.Id, selected.Id, reason, ct)).ConfigureAwait(false);
            if (context.Output.Json)
            {
                context.Output.WriteJson(Views.PackageRequest(session, key, created));
                return ExitCodes.Ok;
            }

            context.Output.WriteLine($"[green]Requested[/] {Markup.Escape(chosen.DisplayName)} in {Markup.Escape(session.TenantName(key))}: {Views.RequestStateMarkup(created.State)}.");
            context.Output.Note("'elevate packages requests' follows it.");
            return ExitCodes.Ok;
        });
        return command;
    }

    private static Command Cancel()
    {
        var requests = new Argument<string[]>("request") { Description = "Ids from 'elevate packages requests'.", Arity = ArgumentArity.OneOrMore };
        var account = CommonOptions.Account();
        var tenant = CommonOptions.Tenant();
        var command = new Command("cancel", "Withdraw package requests that are still awaiting approval.") { requests, account, tenant };
        command.SetAction(async (parse, ct) =>
        {
            var context = CommandContext.From(parse);
            context.RequireSignedIn();
            var session = context.Session;
            var (reads, _) = await ReadAsync(context, parse.GetValue(account), parse.GetValue(tenant), includePackages: false, ct).ConfigureAwait(false);
            var failures = 0;
            var attempted = 0;
            foreach (var term in parse.GetValue(requests) ?? [])
            {
                var (key, request) = ResolveRequest(session, reads, term);
                if (!request.State.IsCancellable())
                {
                    context.Output.Note($"{Markup.Escape(request.PackageName)}: cannot be cancelled once delivery started.");
                    continue;
                }

                attempted += 1;
                try
                {
                    await context.Output.StatusAsync($"Cancelling {request.PackageName}…", () => session.CancelPackageRequestAsync(key, request.Id, ct)).ConfigureAwait(false);
                    context.Output.WriteLine($"[green]Cancelled[/] {Markup.Escape(request.PackageName)}.");
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    failures += 1;
                    context.Output.Error($"{request.PackageName}: {ElevateSession.Describe(e)}");
                }
            }

            return failures == 0 ? ExitCodes.Ok : failures == attempted ? ExitCodes.Failure : ExitCodes.Partial;
        });
        return command;
    }

    /// <summary>Reads every candidate tenant; a tenant that refuses is warned about and left out.</summary>
    private static async Task<(IReadOnlyList<TenantPackages> Reads, int Failures)> ReadAsync(CommandContext context, string? account, string? tenant, bool includePackages, CancellationToken ct)
    {
        var session = context.Session;
        var keys = session.AccessPackageTenants(account, tenant);
        if (keys.Count == 0)
        {
            var excluded = session.Tenants.Select(t => session.AccessPackagesUnavailableReason(t.Key)).FirstOrDefault(r => r is not null);
            throw new CliException(excluded ?? "No tracked tenant matches. Run 'elevate tenants' to list them.", ExitCodes.NotFound);
        }

        var reads = new List<TenantPackages>();
        var failures = 0;
        var title = keys.Count == 1 ? $"Reading access packages in {session.TenantName(keys[0])}…" : $"Reading access packages in {keys.Count} tenants…";
        await context.Output.StatusAsync(title, async () =>
        {
            foreach (var key in keys)
            {
                try
                {
                    reads.Add(await session.ReadPackagesAsync(key, includePackages, ct).ConfigureAwait(false));
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception e)
                {
                    failures += 1;
                    context.Output.Warn($"{Markup.Escape(session.TenantName(key))}: {Markup.Escape(ElevateSession.Describe(e))}");
                }
            }
        }).ConfigureAwait(false);
        return (reads, failures);
    }

    private static int Exit(int reads, int failures) => failures == 0 ? ExitCodes.Ok : reads == 0 ? ExitCodes.Failure : ExitCodes.Partial;

    /// <summary>A short id, a Graph id, an exact name, then a substring, across the tenants read.</summary>
    internal static (TenantKey Key, AccessPackage Package) ResolvePackage(ElevateSession session, IReadOnlyList<TenantPackages> reads, string term)
    {
        var all = reads.SelectMany(r => r.Packages.Select(p => (r.Key, Package: p))).ToList();
        var byId = all.Where(x => ShortId.For(x.Key, x.Package.Id) == term || x.Package.Id.Equals(term, StringComparison.OrdinalIgnoreCase)).ToList();
        var matches = byId.Count > 0 ? byId : all.Where(x => x.Package.DisplayName.Equals(term, StringComparison.OrdinalIgnoreCase)).ToList();
        if (matches.Count == 0)
        {
            matches = all.Where(x => x.Package.DisplayName.Contains(term, StringComparison.OrdinalIgnoreCase)).ToList();
        }

        return matches.Count switch
        {
            1 => matches[0],
            0 => throw new CliException($"No requestable access package matches '{term}'. 'elevate packages list' shows them.", ExitCodes.NotFound),
            _ => throw new CliException($"'{term}' matches several packages; use the id: " + string.Join(", ",
                matches.Select(x => $"{x.Package.DisplayName} ({ShortId.For(x.Key, x.Package.Id)}, {session.TenantName(x.Key)})")), ExitCodes.NotFound),
        };
    }

    internal static (TenantKey Key, AccessPackageRequest Request) ResolveRequest(ElevateSession session, IReadOnlyList<TenantPackages> reads, string term)
    {
        var all = reads.SelectMany(r => r.Requests.Select(q => (r.Key, Request: q))).ToList();
        var matches = all.Where(x => ShortId.For(x.Key, x.Request.Id) == term || x.Request.Id.Equals(term, StringComparison.OrdinalIgnoreCase)).ToList();
        return matches.Count switch
        {
            1 => matches[0],
            0 => throw new CliException($"No package request matches '{term}'. 'elevate packages requests' shows the open ones.", ExitCodes.NotFound),
            _ => throw new CliException($"'{term}' matches several requests: " + string.Join(", ",
                matches.Select(x => $"{x.Request.PackageName} ({ShortId.For(x.Key, x.Request.Id)}, {session.TenantName(x.Key)})")), ExitCodes.NotFound),
        };
    }

    /// <summary>The single applicable policy, or the one named by <paramref name="term"/> when several apply.</summary>
    internal static PolicyRequirement ChoosePolicy(IReadOnlyList<PolicyRequirement> requirements, string? term, string packageName)
    {
        if (requirements.Count == 0)
        {
            throw new CliException($"No policy lets you request {packageName}.", ExitCodes.Failure);
        }

        if (string.IsNullOrWhiteSpace(term))
        {
            if (requirements.Count == 1)
            {
                return requirements[0];
            }

            var lines = requirements.Select(p => $"  {p.Id} — {p.DisplayName} ({(p.IsApprovalRequired ? "approval required" : "no approval")}{(p.RequiresAnswers ? ", asks questions" : string.Empty)})");
            throw new CliException($"{packageName} can be requested under several policies; pick one with --policy:\n" + string.Join("\n", lines), ExitCodes.Usage);
        }

        var matches = requirements.Where(p => p.Id.Equals(term, StringComparison.OrdinalIgnoreCase) || p.DisplayName.Equals(term, StringComparison.OrdinalIgnoreCase)).ToList();
        if (matches.Count == 0)
        {
            matches = requirements.Where(p => p.DisplayName.Contains(term, StringComparison.OrdinalIgnoreCase)).ToList();
        }

        return matches.Count switch
        {
            1 => matches[0],
            0 => throw new CliException($"No policy for {packageName} matches '{term}'.", ExitCodes.NotFound),
            _ => throw new CliException($"'{term}' matches several policies: " + string.Join(", ", matches.Select(p => $"{p.DisplayName} ({p.Id})")), ExitCodes.NotFound),
        };
    }
}
