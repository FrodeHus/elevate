using System.CommandLine;
using Elevate.Cli.Infrastructure;
using Elevate.Cli.Rendering;
using Elevate.Cli.Selection;
using Elevate.Cli.Session;
using Elevate.Core.Models;
using Spectre.Console;

namespace Elevate.Cli.Commands;

/// <summary><c>approvals</c>: requests waiting for your decision as an approver.</summary>
public static class ApprovalCommands
{
    public static Command Approvals()
    {
        var command = new Command("approvals", "Requests awaiting your approval. Bare 'approvals' lists them.");
        command.SetAction(async (parse, ct) =>
        {
            var context = CommandContext.From(parse);
            context.RequireSignedIn();
            var session = context.Session;
            await RoleCommands.RefreshAsync(context, new RoleFilter(), ct).ConfigureAwait(false);
            var now = DateTimeOffset.UtcNow;
            var requests = session.ApprovalsOrdered;
            if (context.Output.Json)
            {
                context.Output.WriteJson(requests.Select(r => Views.Approval(session, r)).ToList());
                return ExitCodes.Ok;
            }

            if (requests.Count == 0)
            {
                context.Output.Plain("Nothing is waiting for your approval.");
            }
            else
            {
                context.Output.Write(Views.ApprovalsTable(session, requests, now));
                context.Output.Note("'elevate approvals approve <id>' or 'elevate approvals deny <id> --reason …'.");
            }

            return ExitCodes.Ok;
        });
        command.Subcommands.Add(Decide("approve", true));
        command.Subcommands.Add(Decide("deny", false));
        return command;
    }

    private static Command Decide(string verb, bool approve)
    {
        var ids = new Argument<string[]>("request") { Description = "Ids from 'elevate approvals', or part of the requester's name.", Arity = ArgumentArity.OneOrMore };
        var reason = new Option<string?>("--reason", "-r") { Description = approve ? "Justification. Default: the one used last; may be empty." : "Justification; required for a denial." };
        var command = new Command(verb, approve ? "Approve requests." : "Deny requests.") { ids, reason };
        command.SetAction(async (parse, ct) =>
        {
            var context = CommandContext.From(parse);
            context.RequireSignedIn();
            var session = context.Session;
            await RoleCommands.RefreshAsync(context, new RoleFilter(), ct).ConfigureAwait(false);
            var chosen = Resolve(session, parse.GetValue(ids) ?? []);
            var justification = (parse.GetValue(reason) ?? session.Settings.LastApprovalJustification).Trim();
            if (!approve && justification.Length == 0)
            {
                if (!context.Output.CanPrompt)
                {
                    throw new CliException("A denial needs --reason.", ExitCodes.Usage);
                }

                justification = context.Output.Stderr.Prompt(new TextPrompt<string>("Reason for denying:"));
            }

            var failures = 0;
            var results = new List<object>();
            foreach (var request in chosen)
            {
                if (request.Action != ApprovalAction.Activate)
                {
                    failures += 1;
                    context.Output.Error($"{request.TargetName} for {request.RequesterName}: {request.Action.ToString().ToLowerInvariant()} requests can only be decided in the portal.");
                    continue;
                }

                try
                {
                    await context.Output.StatusAsync($"{(approve ? "Approving" : "Denying")} {request.TargetName} for {request.RequesterName}…",
                        () => session.DecideAsync(request, approve, justification, ct)).ConfigureAwait(false);
                    results.Add(new { id = ShortId.For(request), requestId = request.Id, result = approve ? "approved" : "denied" });
                    if (!context.Output.Json)
                    {
                        context.Output.WriteLine($"[green]{(approve ? "Approved" : "Denied")}[/] {Markup.Escape(request.TargetName)} for {Markup.Escape(request.RequesterName)}.");
                    }
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    failures += 1;
                    results.Add(new { id = ShortId.For(request), requestId = request.Id, result = "failed", message = ElevateSession.Describe(e) });
                    context.Output.Error($"{request.TargetName} for {request.RequesterName}: {ElevateSession.Describe(e)}");
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

    private static IReadOnlyList<ApprovalRequest> Resolve(ElevateSession session, string[] terms)
    {
        var all = session.ApprovalsOrdered;
        var chosen = new List<ApprovalRequest>();
        foreach (var term in terms.Select(t => t.Trim()).Where(t => t.Length > 0))
        {
            var matches = all.Where(r => ShortId.For(r) == term || r.Id.Equals(term, StringComparison.OrdinalIgnoreCase)).ToList();
            if (matches.Count == 0)
            {
                matches = all.Where(r => r.RequesterName.Contains(term, StringComparison.OrdinalIgnoreCase)).ToList();
            }

            if (matches.Count == 0)
            {
                throw new CliException($"No pending request matches '{term}'.", ExitCodes.NotFound);
            }

            if (matches.Count > 1)
            {
                throw new CliException($"'{term}' matches {matches.Count} requests; use an id from 'elevate approvals'.", ExitCodes.NotFound);
            }

            if (!chosen.Contains(matches[0]))
            {
                chosen.Add(matches[0]);
            }
        }

        return chosen;
    }
}
