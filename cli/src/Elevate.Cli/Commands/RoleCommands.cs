using System.CommandLine;
using Elevate.Cli.Infrastructure;
using Elevate.Cli.Rendering;
using Elevate.Cli.Selection;
using Elevate.Core.Models;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace Elevate.Cli.Commands;

/// <summary><c>roles</c>, <c>status</c> and <c>watch</c>: the read-only views.</summary>
public static class RoleCommands
{
    public static Command Roles()
    {
        var account = CommonOptions.Account();
        var tenant = CommonOptions.Tenant();
        var kind = CommonOptions.Kind();
        var scope = CommonOptions.Scope();
        var active = new Option<bool>("--active") { Description = "Only roles that are active, pending or scheduled." };
        var filter = new Argument<string?>("filter") { Description = "Only roles whose name contains this text.", Arity = ArgumentArity.ZeroOrOne };
        var command = new Command("roles", "List the roles and groups you are eligible for, with their status.")
        {
            filter, account, tenant, kind, scope, active,
        };
        command.SetAction(async (parse, ct) =>
        {
            var context = CommandContext.From(parse);
            context.RequireSignedIn();
            var session = await context.SessionAsync(ct).ConfigureAwait(false);
            var roleFilter = CommonOptions.Filter(parse, account, tenant, kind, scope);
            await RefreshAsync(context, roleFilter, ct).ConfigureAwait(false);
            var now = DateTimeOffset.UtcNow;
            var text = parse.GetValue(filter);
            var roles = RoleSelector.Filtered(session, roleFilter)
                .Where(r => string.IsNullOrWhiteSpace(text) || r.DisplayName.Contains(text, StringComparison.OrdinalIgnoreCase))
                .Where(r => !parse.GetValue(active) || session.Active.ContainsKey(r.Key))
                .OrderBy(r => session.AccountName(r.Key.IdentityId), StringComparer.Ordinal)
                .ThenBy(r => session.TenantName(r.Key.TenantKey), StringComparer.Ordinal)
                .ThenBy(r => r.Key.Scope.Kind)
                .ThenBy(r => r.DisplayName, StringComparer.Ordinal)
                .ToList();
            if (context.Output.Json)
            {
                context.Output.WriteJson(roles.Select(r => Views.Role(session, r, now)).ToList());
                return ExitCodes.Ok;
            }

            if (roles.Count == 0)
            {
                context.Output.Plain("No eligible roles match.");
            }
            else
            {
                context.Output.Write(Views.RolesTable(session, roles, now, session.Identities.Count > 1));
            }

            ReportTenantErrors(context, roleFilter);
            return ExitCodes.Ok;
        });
        return command;
    }

    public static Command Status()
    {
        var command = new Command("status", "What is active, awaiting approval or scheduled, with time left. The default command.");
        command.SetAction((parse, ct) => RunStatusAsync(CommandContext.From(parse), ct));
        return command;
    }

    public static async Task<int> RunStatusAsync(CommandContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        var session = await context.SessionAsync(ct).ConfigureAwait(false);
        if (session.Identities.Count == 0)
        {
            if (context.Output.Json)
            {
                context.Output.WriteJson(Array.Empty<Dto.Role>());
                return ExitCodes.Ok;
            }

            context.Output.Plain("No accounts. Run 'elevate login' to add one, then 'elevate roles' to see what you can activate.");
            return ExitCodes.Ok;
        }

        await RefreshAsync(context, new RoleFilter(), ct).ConfigureAwait(false);
        var now = DateTimeOffset.UtcNow;
        if (context.Output.Json)
        {
            var rows = session.Active.Values
                .Select(a => session.Role(a.RoleKey) is { } role
                    ? Views.Role(session, role, now)
                    : new Dto.Role(ShortId.For(a.RoleKey), session.RoleName(a.RoleKey), Views.KindName(a.RoleKey.Scope.Kind), null, null, "discovered",
                        a.RoleKey.TenantId, session.TenantName(a.RoleKey.TenantKey), a.RoleKey.IdentityId, session.AccountName(a.RoleKey.IdentityId),
                        Views.Policy(RolePolicy.ManualDefault), false, null, Views.Assignment(a, now), a.RoleKey))
                .ToList();
            context.Output.WriteJson(rows);
            return ExitCodes.Ok;
        }

        if (session.Active.Count == 0)
        {
            context.Output.Plain("Nothing is active. 'elevate roles' lists what you can activate.");
        }
        else
        {
            context.Output.Write(Views.ActiveTable(session, session.Active.Values, now));
        }

        var approvals = session.AllApprovals.Count();
        if (approvals > 0)
        {
            context.Output.WriteLine($"[yellow]{approvals} request{(approvals == 1 ? "" : "s")} awaiting your approval[/]: 'elevate approvals'.");
        }

        ReportTenantErrors(context, new RoleFilter());
        await MiscCommands.DailyUpdateHintAsync(context, ct).ConfigureAwait(false);
        return ExitCodes.Ok;
    }

    public static Command Watch()
    {
        var interval = new Option<int>("--interval", "-i") { Description = "Seconds between reads from the service.", DefaultValueFactory = _ => 60 };
        var command = new Command("watch", "Keep a live table of active roles with countdowns on screen until Ctrl+C.") { interval };
        command.SetAction(async (parse, ct) =>
        {
            var context = CommandContext.From(parse);
            context.RequireSignedIn();
            if (context.Output.Json)
            {
                throw new CliException("watch has no JSON form; use 'elevate status --json' in a loop instead.", ExitCodes.Usage);
            }

            var session = await context.SessionAsync(ct).ConfigureAwait(false);
            var every = TimeSpan.FromSeconds(Math.Max(10, parse.GetValue(interval)));
            await RefreshAsync(context, new RoleFilter(), ct).ConfigureAwait(false);
            var lastRead = DateTimeOffset.UtcNow;
            var console = context.Output.Stderr;
            await console.Live(Render(session, lastRead)).AutoClear(false).StartAsync(async live =>
            {
                try
                {
                    while (!ct.IsCancellationRequested)
                    {
                        await Task.Delay(TimeSpan.FromSeconds(1), ct).ConfigureAwait(false);
                        if (DateTimeOffset.UtcNow - lastRead >= every)
                        {
                            await session.RefreshAllAsync(ct).ConfigureAwait(false);
                            lastRead = DateTimeOffset.UtcNow;
                        }

                        live.UpdateTarget(Render(session, lastRead));
                    }
                }
                catch (OperationCanceledException)
                {
                    // Ctrl+C: leave the last table on screen.
                }
            }).ConfigureAwait(false);
            return ExitCodes.Ok;
        });
        return command;
    }

    private static IRenderable Render(Session.ElevateSession session, DateTimeOffset lastRead)
    {
        var now = DateTimeOffset.UtcNow;
        var rows = new Rows(
            session.Active.Count == 0
                ? new Markup("[grey]Nothing is active.[/]")
                : Views.ActiveTable(session, session.Active.Values, now),
            new Markup($"[grey]Read {Markup.Escape(lastRead.ToLocalTime().ToString("HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture))} · Ctrl+C to stop[/]"));
        return rows;
    }

    /// <summary>Reads the tenants the filter touches, under a spinner.</summary>
    internal static async Task RefreshAsync(CommandContext context, RoleFilter filter, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        var session = await context.SessionAsync(ct).ConfigureAwait(false);
        var keys = RoleSelector.TenantsFor(session, filter);
        if (keys.Count == 0)
        {
            if (filter.Account is not null || filter.Tenant is not null)
            {
                throw new CliException("No tracked tenant matches those filters. Run 'elevate tenants' to list them.", ExitCodes.NotFound);
            }

            return;
        }

        var title = keys.Count == 1 ? $"Reading {session.TenantName(keys[0])}…" : $"Reading {keys.Count} tenants…";
        await context.Output.StatusAsync(title, () => session.RefreshAsync(keys, ct)).ConfigureAwait(false);
    }

    internal static void ReportTenantErrors(CommandContext context, RoleFilter filter)
    {
        var session = context.Session;
        foreach (var key in RoleSelector.TenantsFor(session, filter))
        {
            if (session.TenantErrors.TryGetValue(key, out var error))
            {
                context.Output.Warn($"{Markup.Escape(session.TenantName(key))}: {Markup.Escape(error)}");
            }
        }
    }
}
