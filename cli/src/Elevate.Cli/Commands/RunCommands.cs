using System.CommandLine;
using Elevate.Cli.Infrastructure;
using Elevate.Cli.Rendering;
using Elevate.Cli.Selection;
using Elevate.Cli.Session;
using Elevate.Core.Coordination;
using Elevate.Core.Models;
using Spectre.Console;

namespace Elevate.Cli.Commands;

/// <summary>
/// <c>run</c>: activate what is named, wait until all of it is active, run a command with the
/// terminal's own stdin and stdout, and exit with the command's code. The activate-then-retry loop
/// people otherwise do by hand after an <c>AuthorizationFailed</c>.
/// </summary>
public static class RunCommands
{
    /// <summary>Group claims reach a token a little after the membership is active; the default pause before the command.</summary>
    public static readonly TimeSpan GroupSettle = TimeSpan.FromSeconds(30);

    /// <summary>
    /// How long a role activated for one command stays active unless <c>--duration</c> says
    /// otherwise: just in time means minutes, not the hours a desk session takes. Capped by each
    /// role's policy maximum, and never remembered as the role's or the profile's duration.
    /// </summary>
    public static readonly TimeSpan RunDuration = TimeSpan.FromMinutes(10);

    public static Command Run()
    {
        var profile = new Option<string?>("--profile", "-p") { Description = "A profile to activate first (name or unique prefix)." };
        var roles = new Option<string[]>("--role") { Description = "A role to activate first; repeat for several. Names or ids from 'elevate roles'." };
        var command = new Argument<string[]>("command") { Description = "The command and its arguments. Put -- in front so its options are not read as elevate's.", Arity = ArgumentArity.ZeroOrMore };
        var account = CommonOptions.Account();
        var tenant = CommonOptions.Tenant();
        var kind = CommonOptions.Kind();
        var scope = CommonOptions.Scope();
        var duration = new Option<string?>("--duration", "-d") { Description = "How long the roles stay active, e.g. 30m. Default: 10m (or the policy maximum when lower), just enough for one command; the durations remembered for 'activate' and the profile are left alone." };
        var reason = new Option<string?>("--reason", "-r") { Description = "Justification. Default: the remembered reason; prompted when required and missing." };
        var ticket = new Option<string?>("--ticket") { Description = "Ticket number, when a policy asks for one." };
        var ticketSystem = new Option<string?>("--ticket-system") { Description = "Ticket system name to go with --ticket." };
        var deactivateAfter = new Option<bool>("--deactivate-after") { Description = "Deactivate the roles this call activated once the command exits." };
        var settle = new Option<string?>("--settle") { Description = "Extra pause once everything is active, for group claims to propagate, e.g. 2m or 0. Default: 30s when a group membership was activated, else none." };
        var timeout = new Option<string?>("--timeout") { Description = "How long to wait for the activations, approvals included, e.g. 1h. Default: 15m." };
        var run = new Command("run", "Activate roles or a profile, wait until they are active, then run a command and exit with its code.")
        {
            profile, roles, command, account, tenant, kind, scope, duration, reason, ticket, ticketSystem, deactivateAfter, settle, timeout,
        };
        run.SetAction(async (parse, ct) =>
        {
            var context = CommandContext.From(parse);
            if (context.Output.Json)
            {
                throw new CliException("run passes the command's own output through; --json has no meaning here.", ExitCodes.Usage);
            }

            var words = parse.GetValue(command) ?? [];
            if (words.Length == 0)
            {
                throw new CliException("Say what to run after --, e.g. elevate run --role Reader -- az group list.", ExitCodes.Usage);
            }

            var profileName = parse.GetValue(profile);
            var roleTerms = parse.GetValue(roles) ?? [];
            if (profileName is null && roleTerms.Length == 0)
            {
                throw new CliException("Name what to activate first with --profile or --role.", ExitCodes.Usage);
            }

            var wait = parse.GetValue(timeout) is { } t ? ShortDurationParser.Require(t, "timeout", allowZero: false) : ActivationWaiter.RunTimeout;
            var pause = parse.GetValue(settle) is { } s ? ShortDurationParser.Require(s, "settle time", allowZero: true) : (TimeSpan?)null;

            // The executable is looked up before anything is activated: a typo should not cost an activation.
            var executable = CommandLauncher.Resolve(words[0])
                ?? throw new CliException($"Command not found: {words[0]}", ExitCodes.CommandNotFound);

            context.RequireSignedIn();
            var session = await context.SessionAsync(ct).ConfigureAwait(false);
            var filter = CommonOptions.Filter(parse, account, tenant, kind, scope);
            var chosenProfile = profileName is null
                ? null
                : session.FindProfile(profileName) ?? throw new CliException($"No profile matches '{profileName}'. 'elevate profiles' lists them.", ExitCodes.NotFound);

            await ReadTenantsAsync(context, chosenProfile, roleTerms.Length > 0 ? filter : null, ct).ConfigureAwait(false);

            var ticketInfo = ActivationCommands.TicketFrom(parse.GetValue(ticket), parse.GetValue(ticketSystem));
            var wanted = parse.GetValue(duration) is { } d ? DurationParser.Require(d) : RunDuration;
            var waitFor = new List<RoleKey>();
            var requested = new List<RoleKey>();
            var outcomes = new List<ActivationOutcome>();

            if (chosenProfile is not null)
            {
                var plan = session.Plan(chosenProfile);
                foreach (var item in plan)
                {
                    var name = session.RoleName(item.RoleKey);
                    switch (item.Disposition)
                    {
                        case ProfilePlanDisposition.Activate when !session.CanActivate(item.RoleKey):
                            throw new CliException($"{name}: {session.EntraViewOnlyReason(item.RoleKey.TenantKey)}", ExitCodes.Failure);
                        case ProfilePlanDisposition.Pending:
                            context.Output.Note($"{Markup.Escape(name)}: already requested; waiting for it.");
                            waitFor.Add(item.RoleKey);
                            break;
                        case ProfilePlanDisposition.NotEligible:
                            throw new CliException($"{name} in {chosenProfile.Name}: not eligible, so the command could not count on it.", ExitCodes.Failure);
                        case ProfilePlanDisposition.NotLoaded:
                            context.Output.Warn($"{Markup.Escape(name)}: its account or tenant is not available here; skipped.");
                            break;
                        default:
                            break;
                    }
                }

                var toRun = plan.Where(i => i.Disposition == ProfilePlanDisposition.Activate).ToList();
                if (toRun.Count > 0)
                {
                    var justification = ProfileCommands.RequireJustification(context, chosenProfile, toRun, parse.GetValue(reason));
                    ticketInfo = ProfileCommands.RequireTicket(context, toRun, ticketInfo);
                    requested.AddRange(toRun.Select(i => i.RoleKey));
                    outcomes.AddRange(await context.Output.StatusAsync($"Running {chosenProfile.Name}…",
                        () => session.RunProfileAsync(chosenProfile, plan, justification, ticketInfo, null, null, ct, durationOverride: wanted)).ConfigureAwait(false));
                }
            }

            var requests = new List<ActivationRequest>();
            if (roleTerms.Length > 0)
            {
                foreach (var role in RoleSelector.Resolve(session, roleTerms, filter))
                {
                    if (requested.Contains(role.Key))
                    {
                        continue;
                    }

                    if (!session.CanActivate(role.Key))
                    {
                        throw new CliException($"{role.DisplayName}: {session.EntraViewOnlyReason(role.Key.TenantKey)}", ExitCodes.Failure);
                    }

                    if (session.Active.GetValueOrDefault(role.Key) is { } existing && existing.Status.Kind != AssignmentStatusKind.Failed)
                    {
                        if (existing.Status.Kind == AssignmentStatusKind.Active)
                        {
                            context.Output.Note($"{Markup.Escape(role.DisplayName)}: already active.");
                        }
                        else
                        {
                            context.Output.Note($"{Markup.Escape(role.DisplayName)}: already {Markup.Escape(Views.StatusName(existing.Status.Kind))}; waiting for it.");
                            waitFor.Add(role.Key);
                        }

                        continue;
                    }

                    requests.Add(ActivationCommands.BuildRequest(context, role, wanted, parse.GetValue(reason), ticketInfo, null));
                }
            }

            if (requests.Count > 0)
            {
                requested.AddRange(requests.Select(r => r.RoleKey));
                var title = requests.Count == 1 ? $"Activating {session.RoleName(requests[0].RoleKey)}…" : $"Activating {requests.Count} roles…";
                outcomes.AddRange(await context.Output.StatusAsync(title, () => session.ActivateAsync(requests, false, null, ct, rememberDuration: false)).ConfigureAwait(false));
            }

            if (outcomes.Count > 0 && !context.Output.Quiet)
            {
                context.Output.Stderr.Write(Views.OutcomesTable(session, outcomes, DateTimeOffset.UtcNow));
            }

            var failed = outcomes.Count(o => o.Result is ActivationResult.Failed);
            if (failed > 0)
            {
                throw new CliException($"{failed} activation{(failed == 1 ? "" : "s")} failed, so the command was not run.", ExitCodes.Failure);
            }

            waitFor.AddRange(outcomes.Where(o => ElevateSession.AssignmentOf(o.Result) is not null).Select(o => o.RoleKey));
            var waiting = waitFor.Distinct().ToList();
            var waiter = new ActivationWaiter(session);
            await context.Output.StatusAsync("Waiting for the roles to become active…",
                report => waiter.WaitAsync(waiting, wait, report, ct)).ConfigureAwait(false);

            var settleFor = pause ?? (waiting.Any(k => k.Scope.Kind == RoleScopeKind.Group) ? GroupSettle : TimeSpan.Zero);
            if (settleFor > TimeSpan.Zero)
            {
                await context.Output.StatusAsync($"Waiting {ShortDurationParser.Label(settleFor)} for the membership to reach new tokens…",
                    () => Task.Delay(settleFor, ct)).ConfigureAwait(false);
            }

            TokenHints.Report(context, outcomes);
            context.Output.Note($"Running {Markup.Escape(string.Join(' ', words))}");
            var exit = await CommandLauncher.RunAsync(executable, words[1..]).ConfigureAwait(false);

            if (parse.GetValue(deactivateAfter))
            {
                // Ctrl+C meant for the command may have cancelled the token; the clean-up was asked for regardless.
                await DeactivateAsync(context, requested).ConfigureAwait(false);
            }

            return exit;
        });
        return run;
    }

    /// <summary>Reads the tenants a profile and the role filters touch, once, under one spinner.</summary>
    private static Task ReadTenantsAsync(CommandContext context, ActivationProfile? profile, RoleFilter? filter, CancellationToken ct)
    {
        var session = context.Session;
        var keys = new HashSet<TenantKey>();
        if (profile is not null)
        {
            keys.UnionWith(profile.Entries.Select(e => e.RoleKey.TenantKey).Where(k => session.Tenant(k) is not null));
        }

        if (filter is not null)
        {
            var matched = RoleSelector.TenantsFor(session, filter);
            if (matched.Count == 0)
            {
                throw new CliException("No tracked tenant matches those filters. Run 'elevate tenants' to list them.", ExitCodes.NotFound);
            }

            keys.UnionWith(matched);
        }

        if (keys.Count == 0)
        {
            return Task.CompletedTask;
        }

        var title = keys.Count == 1 ? $"Reading {session.TenantName(keys.First())}…" : $"Reading {keys.Count} tenants…";
        return context.Output.StatusAsync(title, () => session.RefreshAsync(keys, ct));
    }

    /// <summary>Deactivates what this call activated and is active now; a refusal (minimum active time, say) is a warning, not a failure.</summary>
    private static async Task DeactivateAsync(CommandContext context, IReadOnlyList<RoleKey> requested)
    {
        var session = context.Session;
        foreach (var key in requested.Distinct())
        {
            if (session.Active.GetValueOrDefault(key)?.Status.Kind != AssignmentStatusKind.Active)
            {
                continue;
            }

            var name = session.RoleName(key);
            try
            {
                await context.Output.StatusAsync($"Deactivating {name}…", () => session.DeactivateAsync(key, CancellationToken.None)).ConfigureAwait(false);
                context.Output.Note($"Deactivated {Markup.Escape(name)}.");
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                context.Output.Warn($"{Markup.Escape(name)}: could not deactivate: {Markup.Escape(ElevateSession.Describe(e))}");
            }
        }
    }
}
