using System.CommandLine;
using Elevate.Cli.Infrastructure;
using Elevate.Cli.Rendering;
using Elevate.Cli.Selection;
using Elevate.Cli.Session;
using Elevate.Core.Coordination;
using Elevate.Core.Models;
using Elevate.Core.Support;
using Spectre.Console;

namespace Elevate.Cli.Commands;

/// <summary><c>activate</c>, <c>extend</c>, <c>deactivate</c> and <c>cancel</c>.</summary>
public static class ActivationCommands
{
    public static Command Activate()
    {
        var roles = new Argument<string[]>("role") { Description = "Role names or ids from 'elevate roles'. Prompts for a choice when omitted in a terminal.", Arity = ArgumentArity.ZeroOrMore };
        var account = CommonOptions.Account();
        var tenant = CommonOptions.Tenant();
        var kind = CommonOptions.Kind();
        var scope = CommonOptions.Scope();
        var duration = new Option<string?>("--duration", "-d") { Description = "How long, e.g. 2h, 30m, 1h30m. Default: the last duration used, else the policy default." };
        var reason = new Option<string?>("--reason", "-r") { Description = "Justification. Default: the reason remembered for the role; prompted when required and missing." };
        var ticket = new Option<string?>("--ticket") { Description = "Ticket number, when the policy asks for one." };
        var ticketSystem = new Option<string?>("--ticket-system") { Description = "Ticket system name to go with --ticket." };
        var at = new Option<string?>("--at") { Description = "Start later: +2h, 14:30 or 2026-09-08T09:00." };
        var wait = new Option<bool>("--wait") { Description = "Wait until the role is actually active (provisioning can take a moment); fails after 5 minutes." };
        var command = new Command("activate", "Activate one or more roles. Roles that are already active are left alone; use 'extend' for those.")
        {
            roles, account, tenant, kind, scope, duration, reason, ticket, ticketSystem, at, wait,
        };
        command.SetAction(async (parse, ct) =>
        {
            var context = CommandContext.From(parse);
            context.RequireSignedIn();
            var session = context.Session;
            var filter = CommonOptions.Filter(parse, account, tenant, kind, scope);
            await RoleCommands.RefreshAsync(context, filter, ct).ConfigureAwait(false);
            var chosen = await ChooseAsync(context, parse.GetValue(roles) ?? [], filter, "activate").ConfigureAwait(false);
            var wanted = parse.GetValue(duration) is { } d ? DurationParser.Require(d) : (TimeSpan?)null;
            var start = parse.GetValue(at) is { } a ? StartTimeParser.Require(a) : (DateTimeOffset?)null;
            var ticketInfo = TicketFrom(parse.GetValue(ticket), parse.GetValue(ticketSystem));

            var requests = new List<ActivationRequest>();
            var skipped = new List<EligibleRole>();
            foreach (var role in chosen)
            {
                if (!session.CanActivate(role.Key))
                {
                    throw new CliException($"{role.DisplayName}: {session.EntraViewOnlyReason(role.Key.TenantKey)}", ExitCodes.Failure);
                }

                if (session.Active.GetValueOrDefault(role.Key) is { } existing && existing.Status.Kind != AssignmentStatusKind.Failed)
                {
                    skipped.Add(role);
                    continue;
                }

                requests.Add(BuildRequest(context, role, wanted, parse.GetValue(reason), ticketInfo, start));
            }

            foreach (var role in skipped)
            {
                var assignment = session.Active[role.Key];
                var left = assignment.Status.Kind == AssignmentStatusKind.Active && assignment.EndDateTime is { } end && Countdown.Remaining(end) is { } remaining
                    ? $"active, {Countdown.Label(remaining)} left; 'elevate extend' re-activates it"
                    : Views.StatusName(assignment.Status.Kind);
                context.Output.Note($"{Markup.Escape(role.DisplayName)}: already {Markup.Escape(left)}.");
            }

            if (requests.Count == 0)
            {
                if (context.Output.Json)
                {
                    context.Output.WriteJson(Array.Empty<Dto.Outcome>());
                }

                return ExitCodes.Ok;
            }

            return await RunAsync(context, requests, deactivateFirst: false, parse.GetValue(wait), ct).ConfigureAwait(false);
        });
        return command;
    }

    public static Command Extend()
    {
        var roles = new Argument<string[]>("role") { Description = "Active roles to re-activate for a fresh duration.", Arity = ArgumentArity.OneOrMore };
        var account = CommonOptions.Account();
        var tenant = CommonOptions.Tenant();
        var kind = CommonOptions.Kind();
        var scope = CommonOptions.Scope();
        var duration = new Option<string?>("--duration", "-d") { Description = "How long from now, e.g. 2h. Default: the last duration used, else the policy default." };
        var reason = new Option<string?>("--reason", "-r") { Description = "Justification. Default: the remembered reason." };
        var wait = new Option<bool>("--wait") { Description = "Wait until the role is active again." };
        var command = new Command("extend", "Deactivate and re-activate an active role, so the clock starts over.")
        {
            roles, account, tenant, kind, scope, duration, reason, wait,
        };
        command.SetAction(async (parse, ct) =>
        {
            var context = CommandContext.From(parse);
            context.RequireSignedIn();
            var session = context.Session;
            var filter = CommonOptions.Filter(parse, account, tenant, kind, scope);
            await RoleCommands.RefreshAsync(context, filter, ct).ConfigureAwait(false);
            var chosen = RoleSelector.Resolve(session, parse.GetValue(roles) ?? [], filter);
            var wanted = parse.GetValue(duration) is { } d ? DurationParser.Require(d) : (TimeSpan?)null;
            var requests = new List<ActivationRequest>();
            foreach (var role in chosen)
            {
                if (session.Active.GetValueOrDefault(role.Key) is not { Status.Kind: AssignmentStatusKind.Active })
                {
                    throw new CliException($"{role.DisplayName} is not active; use 'elevate activate'.", ExitCodes.Usage);
                }

                if (role.Policy.RequiresApproval)
                {
                    throw new CliException($"{role.DisplayName} needs approval, so extending would leave you without the role while the request waits. Request it again with 'elevate activate' once it has expired.", ExitCodes.Usage);
                }

                requests.Add(BuildRequest(context, role, wanted, parse.GetValue(reason), null, null));
            }

            return await RunAsync(context, requests, deactivateFirst: true, parse.GetValue(wait), ct).ConfigureAwait(false);
        });
        return command;
    }

    public static Command Deactivate()
    {
        var roles = new Argument<string[]>("role") { Description = "Active roles to deactivate.", Arity = ArgumentArity.OneOrMore };
        var account = CommonOptions.Account();
        var tenant = CommonOptions.Tenant();
        var kind = CommonOptions.Kind();
        var scope = CommonOptions.Scope();
        var command = new Command("deactivate", "Deactivate active roles.") { roles, account, tenant, kind, scope };
        command.SetAction(async (parse, ct) =>
        {
            var context = CommandContext.From(parse);
            context.RequireSignedIn();
            var session = context.Session;
            var filter = CommonOptions.Filter(parse, account, tenant, kind, scope);
            await RoleCommands.RefreshAsync(context, filter, ct).ConfigureAwait(false);
            var chosen = RoleSelector.Resolve(session, parse.GetValue(roles) ?? [], filter);
            var failures = 0;
            var results = new List<object>();
            foreach (var role in chosen)
            {
                if (session.Active.GetValueOrDefault(role.Key) is not { Status.Kind: AssignmentStatusKind.Active })
                {
                    context.Output.Note($"{Markup.Escape(role.DisplayName)}: not active.");
                    continue;
                }

                try
                {
                    await context.Output.StatusAsync($"Deactivating {role.DisplayName}…", () => session.DeactivateAsync(role.Key, ct)).ConfigureAwait(false);
                    results.Add(new { id = ShortId.For(role.Key), name = role.DisplayName, result = "deactivated" });
                    if (!context.Output.Json)
                    {
                        context.Output.WriteLine($"[green]Deactivated[/] {Markup.Escape(role.DisplayName)} in {Markup.Escape(session.TenantName(role.Key.TenantKey))}.");
                    }
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    failures += 1;
                    results.Add(new { id = ShortId.For(role.Key), name = role.DisplayName, result = "failed", message = ElevateSession.Describe(e) });
                    context.Output.Error($"{role.DisplayName}: {ElevateSession.Describe(e)}");
                }
            }

            if (context.Output.Json)
            {
                context.Output.WriteJson(results);
            }

            return failures == 0 ? ExitCodes.Ok : failures == chosen.Count ? ExitCodes.Failure : ExitCodes.Partial;
        });
        return command;
    }

    public static Command Cancel()
    {
        var roles = new Argument<string[]>("role") { Description = "Roles with a request awaiting approval or a scheduled start.", Arity = ArgumentArity.OneOrMore };
        var account = CommonOptions.Account();
        var tenant = CommonOptions.Tenant();
        var kind = CommonOptions.Kind();
        var scope = CommonOptions.Scope();
        var command = new Command("cancel", "Withdraw a request that is awaiting approval or scheduled for later.") { roles, account, tenant, kind, scope };
        command.SetAction(async (parse, ct) =>
        {
            var context = CommandContext.From(parse);
            context.RequireSignedIn();
            var session = context.Session;
            var filter = CommonOptions.Filter(parse, account, tenant, kind, scope);
            await RoleCommands.RefreshAsync(context, filter, ct).ConfigureAwait(false);
            var chosen = RoleSelector.Resolve(session, parse.GetValue(roles) ?? [], filter);
            var failures = 0;
            foreach (var role in chosen)
            {
                if (session.Active.GetValueOrDefault(role.Key) is not { } a || a.Status.Kind is not (AssignmentStatusKind.PendingApproval or AssignmentStatusKind.Scheduled))
                {
                    context.Output.Note($"{Markup.Escape(role.DisplayName)}: nothing pending.");
                    continue;
                }

                try
                {
                    await context.Output.StatusAsync($"Cancelling {role.DisplayName}…", () => session.CancelPendingAsync(role.Key, ct)).ConfigureAwait(false);
                    context.Output.WriteLine($"[green]Cancelled[/] {Markup.Escape(role.DisplayName)}.");
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    failures += 1;
                    context.Output.Error($"{role.DisplayName}: {ElevateSession.Describe(e)}");
                }
            }

            return failures == 0 ? ExitCodes.Ok : ExitCodes.Partial;
        });
        return command;
    }

    /// <summary>Resolves the typed roles, or offers a checklist when none were typed and a person is there to pick.</summary>
    private static Task<IReadOnlyList<EligibleRole>> ChooseAsync(CommandContext context, string[] terms, RoleFilter filter, string verb)
    {
        var session = context.Session;
        if (terms.Length > 0)
        {
            return Task.FromResult(RoleSelector.Resolve(session, terms, filter));
        }

        if (!context.Output.CanPrompt)
        {
            throw new CliException($"Say which role to {verb}: a name or id from 'elevate roles'.", ExitCodes.Usage);
        }

        var candidates = RoleSelector.Filtered(session, filter)
            .Where(r => session.CanActivate(r.Key) && !session.Active.ContainsKey(r.Key))
            .OrderBy(r => session.TenantName(r.Key.TenantKey), StringComparer.Ordinal)
            .ThenBy(r => r.DisplayName, StringComparer.Ordinal)
            .ToList();
        if (candidates.Count == 0)
        {
            throw new CliException("Nothing to activate: every eligible role is already active.", ExitCodes.NotFound);
        }

        var prompt = new MultiSelectionPrompt<EligibleRole>()
            .Title($"Which roles do you want to {verb}?")
            .PageSize(15)
            .MoreChoicesText("[grey](move up and down to see more)[/]")
            .InstructionsText("[grey](space to toggle, enter to confirm)[/]")
            .UseConverter(r => $"{Markup.Escape(r.DisplayName)} [grey]{Markup.Escape(RoleSelector.Describe(session, r))}[/]")
            .AddChoices(candidates);
        IReadOnlyList<EligibleRole> picked = context.Output.Stderr.Prompt(prompt);
        return Task.FromResult(picked);
    }

    /// <summary>Duration and reason from the options, the remembered values, the policy or a prompt, in that order.</summary>
    private static ActivationRequest BuildRequest(CommandContext context, EligibleRole role, TimeSpan? wanted, string? reason, TicketInfo? ticket, DateTimeOffset? start)
    {
        var session = context.Session;
        var memory = session.Remembered(role.Key);
        var policy = role.Policy;
        var duration = wanted ?? memory?.LastDuration ?? policy.DefaultDuration;
        if (duration > policy.MaximumDuration)
        {
            context.Output.Note($"{Markup.Escape(role.DisplayName)}: the policy allows at most {Markup.Escape(Countdown.Label(policy.MaximumDuration))}; using that.");
            duration = policy.MaximumDuration;
        }

        var justification = (reason ?? memory?.Justification ?? string.Empty).Trim();
        if (justification.Length == 0 && policy.RequiresJustification)
        {
            if (!context.Output.CanPrompt)
            {
                throw new CliException($"{role.DisplayName} needs a reason: pass --reason.", ExitCodes.Usage);
            }

            justification = context.Output.Stderr.Prompt(new TextPrompt<string>($"Reason for [bold]{Markup.Escape(role.DisplayName)}[/]:"));
        }

        if (policy.RequiresTicket && ticket is null)
        {
            if (!context.Output.CanPrompt)
            {
                throw new CliException($"{role.DisplayName} needs a ticket: pass --ticket and --ticket-system.", ExitCodes.Usage);
            }

            var number = context.Output.Stderr.Prompt(new TextPrompt<string>($"Ticket number for [bold]{Markup.Escape(role.DisplayName)}[/]:"));
            var system = context.Output.Stderr.Prompt(new TextPrompt<string>("Ticket system:").AllowEmpty());
            ticket = new TicketInfo(number.Trim(), system.Trim());
        }

        return new ActivationRequest(role.Key, duration, justification, ticket, policy.AuthenticationContext, start);
    }

    private static TicketInfo? TicketFrom(string? number, string? system) =>
        string.IsNullOrWhiteSpace(number) ? null : new TicketInfo(number.Trim(), (system ?? string.Empty).Trim());

    /// <summary>Sends the requests, prints the outcomes, optionally waits for provisioning, and maps them to an exit code.</summary>
    internal static async Task<int> RunAsync(CommandContext context, IReadOnlyList<ActivationRequest> requests, bool deactivateFirst, bool wait, CancellationToken ct)
    {
        var session = context.Session;
        var title = requests.Count == 1
            ? $"{(deactivateFirst ? "Extending" : "Activating")} {session.RoleName(requests[0].RoleKey)}…"
            : $"{(deactivateFirst ? "Extending" : "Activating")} {requests.Count} roles…";
        var outcomes = await context.Output.StatusAsync(title, () => session.ActivateAsync(requests, deactivateFirst, null, ct)).ConfigureAwait(false);
        var ordered = requests.Select(r => outcomes.FirstOrDefault(o => o.RoleKey == r.RoleKey)
                ?? outcomes.FirstOrDefault(o => o.RoleKey.TenantKey == r.RoleKey.TenantKey && !requests.Any(q => q.RoleKey == o.RoleKey)))
            .OfType<ActivationOutcome>().Distinct().ToList();
        if (ordered.Count < outcomes.Count)
        {
            ordered = [.. outcomes];
        }

        if (wait)
        {
            await WaitForActiveAsync(context, ordered, ct).ConfigureAwait(false);
        }

        var now = DateTimeOffset.UtcNow;
        if (context.Output.Json)
        {
            context.Output.WriteJson(ordered.Select(o => Views.Outcome(session, o, now)).ToList());
        }
        else
        {
            context.Output.Write(Views.OutcomesTable(session, ordered, now));
        }

        var failed = ordered.Count(o => o.Result is ActivationResult.Failed);
        return failed == 0 ? ExitCodes.Ok : failed == ordered.Count ? ExitCodes.Failure : ExitCodes.Partial;
    }

    /// <summary>Polls the tenants until every activated role reports Active, up to five minutes.</summary>
    private static async Task WaitForActiveAsync(CommandContext context, IReadOnlyList<ActivationOutcome> outcomes, CancellationToken ct)
    {
        var session = context.Session;
        var waiting = outcomes.Where(o => o.Result is ActivationResult.Activated).Select(o => o.RoleKey).ToList();
        if (waiting.Count == 0)
        {
            return;
        }

        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromMinutes(5);
        await context.Output.StatusAsync("Waiting for the roles to become active…", async () =>
        {
            while (DateTimeOffset.UtcNow < deadline)
            {
                await session.RefreshAsync(waiting.Select(k => k.TenantKey), ct).ConfigureAwait(false);
                if (waiting.All(k => session.Active.GetValueOrDefault(k)?.Status.Kind == AssignmentStatusKind.Active))
                {
                    return;
                }

                await Task.Delay(TimeSpan.FromSeconds(10), ct).ConfigureAwait(false);
            }

            throw new CliException("The activation was accepted but the roles were not active after five minutes.", ExitCodes.Failure);
        }).ConfigureAwait(false);
    }
}
