using System.CommandLine;
using System.Text.Json;
using Elevate.Cli.Infrastructure;
using Elevate.Cli.Rendering;
using Elevate.Cli.Selection;
using Elevate.Core;
using Elevate.Core.Coordination;
using Elevate.Core.Models;
using Elevate.Core.Storage;
using Elevate.Core.Support;
using Spectre.Console;

namespace Elevate.Cli.Commands;

/// <summary><c>profiles</c>: named sets of roles activated together.</summary>
public static class ProfileCommands
{
    public static Command Profiles()
    {
        var command = new Command("profiles", "Named sets of roles activated together. Bare 'profiles' lists them.");
        command.SetAction(async (parse, ct) =>
        {
            var context = CommandContext.From(parse);
            var session = await context.SessionAsync(ct).ConfigureAwait(false);
            if (context.Output.Json)
            {
                context.Output.WriteJson(session.Profiles.Select(p => Views.Profile(session, p)).ToList());
                return ExitCodes.Ok;
            }

            if (session.Profiles.Count == 0)
            {
                context.Output.Plain("No profiles. Save one with 'elevate profiles save <name> <role…>' or copy the desktop app's with 'elevate profiles import'.");
                return ExitCodes.Ok;
            }

            var table = new Table().Border(TableBorder.Rounded);
            table.AddColumn("Profile");
            table.AddColumn("Contents");
            table.AddColumn("Last reason");
            foreach (var p in session.Profiles)
            {
                table.AddRow(Markup.Escape(p.Name), Markup.Escape(ProfileSummary.Caption(p.Entries)), Markup.Escape(p.LastJustification ?? "—"));
            }

            context.Output.Write(table);
            return ExitCodes.Ok;
        });

        command.Subcommands.Add(Show());
        command.Subcommands.Add(Save());
        command.Subcommands.Add(Run());
        command.Subcommands.Add(Rename());
        command.Subcommands.Add(Delete());
        command.Subcommands.Add(Import());
        return command;
    }

    private static Argument<string> NameArgument() => new("profile") { Description = "The profile's name (or a unique prefix of it)." };

    private static ActivationProfile Require(CommandContext context, string name) =>
        context.Session.FindProfile(name) ?? throw new CliException($"No profile matches '{name}'. 'elevate profiles' lists them.", ExitCodes.NotFound);

    private static Command Show()
    {
        var name = NameArgument();
        var command = new Command("show", "The roles in a profile.") { name };
        command.SetAction(async (parse, ct) =>
        {
            var context = CommandContext.From(parse);
            var session = await context.SessionAsync(ct).ConfigureAwait(false);
            var profile = Require(context, parse.GetValue(name)!);
            if (context.Output.Json)
            {
                context.Output.WriteJson(Views.Profile(session, profile));
                return ExitCodes.Ok;
            }

            var table = new Table().Border(TableBorder.Rounded).Title(Markup.Escape(profile.Name));
            table.AddColumn("[grey]ID[/]");
            table.AddColumn("Role");
            table.AddColumn("Kind");
            table.AddColumn("Tenant");
            table.AddColumn("Account");
            table.AddColumn("Last duration");
            foreach (var e in profile.Entries)
            {
                table.AddRow(
                    $"[grey]{ShortId.For(e.RoleKey)}[/]", Markup.Escape(session.RoleName(e.RoleKey)), RoleSelector.KindLabel(e.RoleKey.Scope.Kind),
                    Markup.Escape(session.TenantName(e.RoleKey.TenantKey)), Markup.Escape(session.AccountName(e.RoleKey.IdentityId)),
                    e.LastDuration is { } d ? Markup.Escape(Countdown.Label(d)) : "[grey]policy default[/]");
            }

            context.Output.Write(table);
            return ExitCodes.Ok;
        });
        return command;
    }

    private static Command Save()
    {
        var name = NameArgument();
        var roles = new Argument<string[]>("role") { Description = "Role names or ids from 'elevate roles'.", Arity = ArgumentArity.ZeroOrMore };
        var account = CommonOptions.Account();
        var tenant = CommonOptions.Tenant();
        var kind = CommonOptions.Kind();
        var scope = CommonOptions.Scope();
        var fromActive = new Option<bool>("--from-active") { Description = "Use everything that is active right now instead of naming roles." };
        var update = new Option<bool>("--update") { Description = "Replace the roles of an existing profile with this name." };
        var command = new Command("save", "Save a set of roles as a profile.") { name, roles, account, tenant, kind, scope, fromActive, update };
        command.SetAction(async (parse, ct) =>
        {
            var context = CommandContext.From(parse);
            context.RequireSignedIn();
            var session = await context.SessionAsync(ct).ConfigureAwait(false);
            var filter = CommonOptions.Filter(parse, account, tenant, kind, scope);
            await RoleCommands.RefreshAsync(context, filter, ct).ConfigureAwait(false);
            var terms = parse.GetValue(roles) ?? [];
            IReadOnlyList<RoleKey> keys;
            if (parse.GetValue(fromActive))
            {
                keys = [.. RoleSelector.Filtered(session, filter).Where(r => session.Active.GetValueOrDefault(r.Key)?.Status.Kind == AssignmentStatusKind.Active).Select(r => r.Key)];
            }
            else if (terms.Length > 0)
            {
                keys = [.. RoleSelector.Resolve(session, terms, filter).Select(r => r.Key)];
            }
            else
            {
                throw new CliException("Name the roles to save, or pass --from-active.", ExitCodes.Usage);
            }

            if (keys.Count == 0)
            {
                throw new CliException("Nothing to save: no roles matched.", ExitCodes.NotFound);
            }

            var wanted = parse.GetValue(name)!.Trim();
            var existing = session.Profiles.FirstOrDefault(p => string.Equals(p.Name, wanted, StringComparison.OrdinalIgnoreCase));
            ActivationProfile profile;
            if (existing is not null)
            {
                if (!parse.GetValue(update))
                {
                    throw new CliException($"A profile named '{existing.Name}' exists; pass --update to replace its roles.", ExitCodes.Usage);
                }

                session.UpdateProfile(existing, keys);
                profile = existing;
            }
            else
            {
                profile = session.SaveProfile(wanted, keys);
            }

            if (context.Output.Json)
            {
                context.Output.WriteJson(Views.Profile(session, profile));
            }
            else
            {
                context.Output.WriteLine($"Saved [bold]{Markup.Escape(profile.Name)}[/]: {Markup.Escape(ProfileSummary.Caption(profile.Entries))}.");
            }

            return ExitCodes.Ok;
        });
        return command;
    }

    private static Command Run()
    {
        var name = NameArgument();
        var reason = new Option<string?>("--reason", "-r") { Description = "Justification for every role. Default: the reason from the last run; prompted when missing." };
        var ticket = new Option<string?>("--ticket") { Description = "Ticket number, when a policy asks for one." };
        var ticketSystem = new Option<string?>("--ticket-system") { Description = "Ticket system name to go with --ticket." };
        var at = new Option<string?>("--at") { Description = "Start later: +2h, 14:30 or 2026-09-08T09:00." };
        var wait = new Option<bool>("--wait") { Description = "Wait until every activated role is actually active." };
        var dryRun = new Option<bool>("--dry-run") { Description = "Show the plan and stop." };
        var command = new Command("run", "Activate a profile. Roles already active or pending are skipped.") { name, reason, ticket, ticketSystem, at, wait, dryRun };
        command.SetAction(async (parse, ct) =>
        {
            var context = CommandContext.From(parse);
            context.RequireSignedIn();
            var session = await context.SessionAsync(ct).ConfigureAwait(false);
            var profile = Require(context, parse.GetValue(name)!);
            var tenants = profile.Entries.Select(e => e.RoleKey.TenantKey).Distinct().Where(k => session.Tenant(k) is not null).ToList();
            if (tenants.Count > 0)
            {
                await context.Output.StatusAsync($"Reading {tenants.Count} tenant{(tenants.Count == 1 ? "" : "s")}…", () => session.RefreshAsync(tenants, ct)).ConfigureAwait(false);
            }

            var plan = session.Plan(profile);
            var toRun = plan.Where(i => i.Disposition == ProfilePlanDisposition.Activate).ToList();
            if (!context.Output.Json)
            {
                context.Output.Write(PlanTable(context, plan));
            }

            if (parse.GetValue(dryRun) || toRun.Count == 0)
            {
                if (context.Output.Json)
                {
                    context.Output.WriteJson(plan.Select(i => PlanDto(context, i)).ToList());
                }
                else if (toRun.Count == 0)
                {
                    context.Output.Plain("Nothing to activate.");
                }

                return ExitCodes.Ok;
            }

            foreach (var item in toRun)
            {
                if (!session.CanActivate(item.RoleKey))
                {
                    throw new CliException($"{session.RoleName(item.RoleKey)}: {session.EntraViewOnlyReason(item.RoleKey.TenantKey)}", ExitCodes.Failure);
                }
            }

            var justification = RequireJustification(context, profile, toRun, parse.GetValue(reason));
            var ticketInfo = RequireTicket(context, toRun, ActivationCommands.TicketFrom(parse.GetValue(ticket), parse.GetValue(ticketSystem)));

            var start = parse.GetValue(at) is { } a ? StartTimeParser.Require(a) : (DateTimeOffset?)null;
            var outcomes = await context.Output.StatusAsync($"Running {profile.Name}…",
                () => session.RunProfileAsync(profile, plan, justification, ticketInfo, start, null, ct)).ConfigureAwait(false);
            var now = DateTimeOffset.UtcNow;
            if (parse.GetValue(wait))
            {
                await ActivationCommands.WaitForActiveAsync(context, outcomes, ct).ConfigureAwait(false);
            }

            if (context.Output.Json)
            {
                context.Output.WriteJson(new { plan = plan.Select(i => PlanDto(context, i)).ToList(), outcomes = outcomes.Select(o => Views.Outcome(session, o, now)).ToList() });
            }
            else
            {
                context.Output.Write(Views.OutcomesTable(session, outcomes, now));
            }

            TokenHints.Report(context, outcomes);
            var failed = outcomes.Count(o => o.Result is ActivationResult.Failed);
            return failed == 0 ? ExitCodes.Ok : failed == outcomes.Count ? ExitCodes.Failure : ExitCodes.Partial;
        });
        return command;
    }

    /// <summary>The reason for a profile run: the option, then the profile's last one, then a prompt when a policy needs one.</summary>
    internal static string RequireJustification(CommandContext context, ActivationProfile profile, IReadOnlyList<ProfilePlanItem> toRun, string? reason)
    {
        var justification = (reason ?? profile.LastJustification ?? string.Empty).Trim();
        if (justification.Length == 0 && toRun.Any(i => i.Role?.Policy.RequiresJustification ?? true))
        {
            if (!context.Output.CanPrompt)
            {
                throw new CliException("The profile needs a reason: pass --reason.", ExitCodes.Usage);
            }

            justification = context.Output.Stderr.Prompt(new TextPrompt<string>($"Reason for [bold]{Markup.Escape(profile.Name)}[/]:"));
        }

        return justification;
    }

    /// <summary>The ticket for a profile run: the one given, else a prompt when a policy needs one.</summary>
    internal static TicketInfo? RequireTicket(CommandContext context, IReadOnlyList<ProfilePlanItem> toRun, TicketInfo? ticket)
    {
        if (ticket is not null || !toRun.Any(i => i.Role?.Policy.RequiresTicket == true))
        {
            return ticket;
        }

        if (!context.Output.CanPrompt)
        {
            throw new CliException("A role in the profile needs a ticket: pass --ticket and --ticket-system.", ExitCodes.Usage);
        }

        var number = context.Output.Stderr.Prompt(new TextPrompt<string>("Ticket number:"));
        var system = context.Output.Stderr.Prompt(new TextPrompt<string>("Ticket system:").AllowEmpty());
        return new TicketInfo(number.Trim(), system.Trim());
    }

    private static Dto.PlanItem PlanDto(CommandContext context, ProfilePlanItem item)
    {
        var session = context.Session;
        return new Dto.PlanItem(ShortId.For(item.RoleKey), session.RoleName(item.RoleKey), session.TenantName(item.RoleKey.TenantKey),
            session.AccountName(item.RoleKey.IdentityId), Disposition(item.Disposition), Views.Iso(item.Duration));
    }

    private static string Disposition(ProfilePlanDisposition d) => d switch
    {
        ProfilePlanDisposition.Activate => "activate",
        ProfilePlanDisposition.AlreadyActive => "alreadyActive",
        ProfilePlanDisposition.Pending => "pending",
        ProfilePlanDisposition.NotEligible => "notEligible",
        _ => "notLoaded",
    };

    private static Table PlanTable(CommandContext context, IReadOnlyList<ProfilePlanItem> plan)
    {
        var session = context.Session;
        var table = new Table().Border(TableBorder.Rounded).Title("Plan");
        table.AddColumn("Role");
        table.AddColumn("Tenant");
        table.AddColumn("Duration");
        table.AddColumn("What happens");
        foreach (var item in plan)
        {
            var what = item.Disposition switch
            {
                ProfilePlanDisposition.Activate => item.Role?.Policy.RequiresApproval == true ? "[yellow]request approval[/]" : "[green]activate[/]",
                ProfilePlanDisposition.AlreadyActive => "[grey]already active[/]",
                ProfilePlanDisposition.Pending => "[grey]pending[/]",
                ProfilePlanDisposition.NotEligible => "[red]not eligible[/]",
                _ => "[red]tenant not loaded[/]",
            };
            table.AddRow(Markup.Escape(session.RoleName(item.RoleKey)), Markup.Escape(session.TenantName(item.RoleKey.TenantKey)),
                Markup.Escape(Countdown.Label(item.Duration)), what);
        }

        return table;
    }

    private static Command Rename()
    {
        var name = NameArgument();
        var newName = new Argument<string>("new-name") { Description = "The new name." };
        var command = new Command("rename", "Rename a profile.") { name, newName };
        command.SetAction((parse, _) =>
        {
            var context = CommandContext.From(parse);
            var profile = Require(context, parse.GetValue(name)!);
            var old = profile.Name;
            context.Session.RenameProfile(profile, parse.GetValue(newName)!);
            context.Output.Note($"Renamed {Markup.Escape(old)} to {Markup.Escape(profile.Name)}.");
            return Task.FromResult(ExitCodes.Ok);
        });
        return command;
    }

    private static Command Delete()
    {
        var name = NameArgument();
        var yes = CommonOptions.Yes();
        var command = new Command("delete", "Delete a profile. Active roles are not touched.") { name, yes };
        command.SetAction((parse, _) =>
        {
            var context = CommandContext.From(parse);
            var profile = Require(context, parse.GetValue(name)!);
            if (!parse.GetValue(yes) && context.Output.CanPrompt
                && !context.Output.Stderr.Confirm($"Delete the profile [bold]{Markup.Escape(profile.Name)}[/]?", defaultValue: false))
            {
                return Task.FromResult(ExitCodes.Ok);
            }

            context.Session.DeleteProfile(profile.Id);
            context.Output.Note($"Deleted {Markup.Escape(profile.Name)}.");
            return Task.FromResult(ExitCodes.Ok);
        });
        return command;
    }

    private static Command Import()
    {
        var from = new Option<string?>("--from") { Description = "A state.json to read profiles from. Default: the desktop app's on this machine." };
        var command = new Command("import", "Copy the desktop app's profiles into the CLI. Entries of accounts not signed in here plan as 'not loaded' until you sign them in.") { from };
        command.SetAction((parse, _) =>
        {
            var context = CommandContext.From(parse);
            var path = parse.GetValue(from) ?? DataDirectory.DesktopAppStateFile()
                ?? throw new CliException("There is no desktop app on this platform; pass --from <state.json>.", ExitCodes.Usage);
            if (!File.Exists(path))
            {
                throw new CliException($"No state file at {path}.", ExitCodes.NotFound);
            }

            AppState other;
            try
            {
                other = JsonSerializer.Deserialize<AppState>(File.ReadAllBytes(path), Json.Options) ?? new AppState();
            }
            catch (JsonException e)
            {
                throw new CliException($"Could not read {path}: {e.Message}");
            }

            var imported = context.Session.ImportProfiles(other);
            if (context.Output.Json)
            {
                context.Output.WriteJson(imported.Select(p => Views.Profile(context.Session, p)).ToList());
            }
            else
            {
                context.Output.Plain(imported.Count == 0
                    ? "No profiles to import (a profile whose name already exists here is skipped)."
                    : $"Imported {imported.Count} profile{(imported.Count == 1 ? "" : "s")}: {string.Join(", ", imported.Select(p => p.Name))}.");
            }

            return Task.FromResult(ExitCodes.Ok);
        });
        return command;
    }
}
