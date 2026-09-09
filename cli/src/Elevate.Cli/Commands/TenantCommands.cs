using System.CommandLine;
using Elevate.Cli.Infrastructure;
using Elevate.Cli.Rendering;
using Elevate.Core.Catalogue;
using Elevate.Core.Models;
using Spectre.Console;

namespace Elevate.Cli.Commands;

/// <summary><c>tenants</c> and its subcommands: list, discover, add, remove, retry, manual roles.</summary>
public static class TenantCommands
{
    public static Command Tenants()
    {
        var account = CommonOptions.Account();
        var command = new Command("tenants", "List the tenants each account is tracked in, with their flags.") { account };
        command.SetAction(async (parse, ct) =>
        {
            var context = CommandContext.From(parse);
            var session = await context.SessionAsync(ct).ConfigureAwait(false);
            var tenants = string.IsNullOrWhiteSpace(parse.GetValue(account))
                ? session.Tenants
                : [.. session.Tenants.Where(t => t.IdentityId == context.RequireAccount(parse.GetValue(account)).Id)];
            if (context.Output.Json)
            {
                context.Output.WriteJson(tenants.Select(t => Views.Tenant(session, t)).ToList());
                return ExitCodes.Ok;
            }

            if (tenants.Count == 0)
            {
                context.Output.Plain("No tenants. Run 'elevate login' first.");
                return ExitCodes.Ok;
            }

            var table = new Table().Border(TableBorder.Rounded);
            table.AddColumn("Tenant");
            table.AddColumn("Tenant id");
            table.AddColumn("Account");
            table.AddColumn("Source");
            table.AddColumn("Flags");
            foreach (var t in tenants.OrderBy(t => session.AccountName(t.IdentityId), StringComparer.Ordinal).ThenBy(t => t.DisplayName, StringComparer.Ordinal))
            {
                var flags = Views.TenantFlags(session, t);
                var flagText = flags.Count == 0 ? "[grey]—[/]" : $"[yellow]{Markup.Escape(string.Join(", ", flags))}[/]";
                if (t.LastDiscoveryError is { } error)
                {
                    flagText += $"\n[grey]{Markup.Escape(error)}[/]";
                }

                table.AddRow(Markup.Escape(t.DisplayName), Markup.Escape(t.TenantId), Markup.Escape(session.AccountName(t.IdentityId)),
                    t.Source.ToString().ToLowerInvariant(), flagText);
            }

            context.Output.Write(table);
            return ExitCodes.Ok;
        });

        command.Subcommands.Add(Discover());
        command.Subcommands.Add(Add());
        command.Subcommands.Add(Remove());
        command.Subcommands.Add(Retry());
        command.Subcommands.Add(Manual());
        return command;
    }

    private static Command Discover()
    {
        var account = CommonOptions.Account();
        var add = new Option<bool>("--add") { Description = "Track every tenant found, not just list them." };
        var command = new Command("discover", "Find the tenants an account can reach through Azure Resource Manager.") { account, add };
        command.SetAction(async (parse, ct) =>
        {
            var context = CommandContext.From(parse);
            var session = await context.SessionAsync(ct).ConfigureAwait(false);
            var identity = context.RequireAccount(parse.GetValue(account));
            var found = await context.Output.StatusAsync($"Discovering tenants for {identity.Upn}…", () => session.DiscoverTenantsAsync(identity, ct)).ConfigureAwait(false);
            var added = parse.GetValue(add) ? session.TrackTenants(identity, found) : [];
            if (context.Output.Json)
            {
                context.Output.WriteJson(found.Select(t => new
                {
                    tenantId = t.TenantId, displayName = t.DisplayName, defaultDomain = t.DefaultDomain,
                    tracked = session.Tenant(new TenantKey(identity.Id, t.TenantId)) is not null,
                    added = added.Any(a => a.TenantId == t.TenantId),
                    notPermitted = !session.IsTenantAllowed(t.TenantId),
                }).ToList());
                return ExitCodes.Ok;
            }

            var table = new Table().Border(TableBorder.Rounded);
            table.AddColumn("Tenant");
            table.AddColumn("Tenant id");
            table.AddColumn("Domain");
            table.AddColumn("Tracked");
            table.AddColumn("Flags");
            foreach (var t in found.OrderBy(t => t.DisplayName, StringComparer.Ordinal))
            {
                var tracked = session.Tenant(new TenantKey(identity.Id, t.TenantId)) is not null;
                table.AddRow(Markup.Escape(t.DisplayName), Markup.Escape(t.TenantId), Markup.Escape(t.DefaultDomain ?? "—"),
                    added.Any(a => a.TenantId == t.TenantId) ? "[green]added[/]" : tracked ? "yes" : "[grey]no[/]",
                    session.IsTenantAllowed(t.TenantId) ? "[grey]—[/]" : "[yellow]not permitted[/]");
            }

            context.Output.Write(table);
            if (!parse.GetValue(add) && found.Any(t => session.Tenant(new TenantKey(identity.Id, t.TenantId)) is null))
            {
                context.Output.Note("Add them all with --add, or one with 'elevate tenants add <domain or id>'.");
            }

            return ExitCodes.Ok;
        });
        return command;
    }

    private static Command Add()
    {
        var tenant = new Argument<string>("tenant") { Description = "A verified domain (contoso.com) or the tenant id." };
        var account = CommonOptions.Account();
        var command = new Command("add", "Track a tenant by domain or id, for tenants discovery does not list.") { tenant, account };
        command.SetAction(async (parse, ct) =>
        {
            var context = CommandContext.From(parse);
            var session = await context.SessionAsync(ct).ConfigureAwait(false);
            var identity = context.RequireAccount(parse.GetValue(account));
            var added = await context.Output.StatusAsync("Resolving the tenant…", () => session.AddTenantAsync(identity, parse.GetValue(tenant)!, ct)).ConfigureAwait(false);
            await context.Output.StatusAsync($"Reading roles in {added.DisplayName}…", () => session.RefreshTenantAsync(added.Key, ct)).ConfigureAwait(false);
            if (context.Output.Json)
            {
                context.Output.WriteJson(Views.Tenant(session, added));
                return ExitCodes.Ok;
            }

            var count = session.Roles.GetValueOrDefault(added.Key)?.Count ?? 0;
            context.Output.WriteLine($"Tracking [bold]{Markup.Escape(added.DisplayName)}[/] ({Markup.Escape(added.TenantId)}) for {Markup.Escape(identity.Upn)}: {count} eligible role{(count == 1 ? "" : "s")}.");
            if (session.TenantErrors.TryGetValue(added.Key, out var error))
            {
                context.Output.Warn(Markup.Escape(error));
            }

            return ExitCodes.Ok;
        });
        return command;
    }

    private static Command Remove()
    {
        var tenant = new Argument<string>("tenant") { Description = "Part of the tenant name, or the id." };
        var account = CommonOptions.Account();
        var command = new Command("remove", "Stop tracking a tenant. Its manual roles, remembered reasons and profile entries go with it.") { tenant, account };
        command.SetAction(async (parse, ct) =>
        {
            var context = CommandContext.From(parse);
            var session = await context.SessionAsync(ct).ConfigureAwait(false);
            var t = context.RequireTenant(parse.GetValue(tenant)!, parse.GetValue(account));
            session.RemoveTenant(t.Key);
            context.Output.Note($"Removed {Markup.Escape(t.DisplayName)}. Active assignments in Entra were not changed.");
            return ExitCodes.Ok;
        });
        return command;
    }

    private static Command Retry()
    {
        var tenant = new Argument<string>("tenant") { Description = "Part of the tenant name, or the id." };
        var account = CommonOptions.Account();
        var command = new Command("retry", "Clear a tenant's 'manual roles', 'azure off' and 'groups off' flags and read it again.") { tenant, account };
        command.SetAction(async (parse, ct) =>
        {
            var context = CommandContext.From(parse);
            var session = await context.SessionAsync(ct).ConfigureAwait(false);
            var t = context.RequireTenant(parse.GetValue(tenant)!, parse.GetValue(account));
            session.ResetTenant(t.Key);
            await context.Output.StatusAsync($"Reading {t.DisplayName}…", () => session.RefreshTenantAsync(t.Key, ct)).ConfigureAwait(false);
            var after = session.Tenant(t.Key)!;
            var flags = Views.TenantFlags(session, after);
            context.Output.WriteLine($"{Markup.Escape(after.DisplayName)}: {(session.Roles.GetValueOrDefault(t.Key)?.Count ?? 0)} eligible roles"
                + (flags.Count == 0 ? "." : $"; flags: {Markup.Escape(string.Join(", ", flags))}."));
            if (session.TenantErrors.TryGetValue(t.Key, out var error))
            {
                context.Output.Warn(Markup.Escape(error));
            }

            return ExitCodes.Ok;
        });
        return command;
    }

    private static Command Manual()
    {
        var command = new Command("manual", "Roles you know you hold in a tenant that refuses discovery.");
        var tenantArg = new Argument<string>("tenant") { Description = "Part of the tenant name, or the id." };
        var account = CommonOptions.Account();

        var list = new Command("list", "Show the manual roles of a tenant.") { tenantArg, account };
        list.SetAction(async (parse, ct) =>
        {
            var context = CommandContext.From(parse);
            var session = await context.SessionAsync(ct).ConfigureAwait(false);
            var t = context.RequireTenant(parse.GetValue(tenantArg)!, parse.GetValue(account));
            var roles = session.State.ManualRoles.Where(r => r.TenantKey == t.Key).ToList();
            if (context.Output.Json)
            {
                context.Output.WriteJson(roles.Select(r => new { name = r.DisplayName, kind = Views.KindName(r.Scope.Kind), scope = r.Scope }).ToList());
            }
            else if (roles.Count == 0)
            {
                context.Output.Plain("No manual roles.");
            }
            else
            {
                foreach (var r in roles)
                {
                    context.Output.Plain($"{r.DisplayName}  [{Views.KindName(r.Scope.Kind)}]  {Describe(r.Scope)}");
                }
            }

            return ExitCodes.Ok;
        });

        var entra = new Option<string[]>("--entra") { Description = "An Entra directory role by catalogue name, e.g. \"Global Reader\". Repeatable." };
        var azure = new Option<string[]>("--azure") { Description = "An Azure role as <scope>=<role name>, e.g. /subscriptions/…=Reader. Repeatable." };
        var group = new Option<string[]>("--group") { Description = "A PIM for Groups membership as <group id>[=owner]. Repeatable." };
        var replace = new Option<bool>("--replace") { Description = "Replace the tenant's manual roles instead of adding to them." };
        var add = new Command("add", "Add manual roles to a tenant.") { tenantArg, account, entra, azure, group, replace };
        add.SetAction(async (parse, ct) =>
        {
            var context = CommandContext.From(parse);
            var session = await context.SessionAsync(ct).ConfigureAwait(false);
            var t = context.RequireTenant(parse.GetValue(tenantArg)!, parse.GetValue(account));
            var roles = parse.GetValue(replace) ? [] : session.State.ManualRoles.Where(r => r.TenantKey == t.Key).ToList();
            foreach (var name in parse.GetValue(entra) ?? [])
            {
                var known = RoleCatalogue.EntraBuiltInRoles().FirstOrDefault(r => string.Equals(r.DisplayName, name.Trim(), StringComparison.OrdinalIgnoreCase))
                    ?? throw new CliException($"'{name}' is not a built-in Entra role; see 'elevate catalogue'.", ExitCodes.NotFound);
                roles.Add(new ManualRole(t.Key, new EntraDirectoryScope(known.TemplateId, "/"), known.DisplayName));
            }

            foreach (var spec in parse.GetValue(azure) ?? [])
            {
                var parts = spec.Split('=', 2);
                if (parts.Length != 2 || parts[0].Trim().Length == 0 || parts[1].Trim().Length == 0)
                {
                    throw new CliException($"--azure takes <scope>=<role name>, got '{spec}'.", ExitCodes.Usage);
                }

                roles.Add(new ManualRole(t.Key, new AzureResourceScope(parts[0].Trim(), parts[1].Trim()), parts[1].Trim()));
            }

            foreach (var spec in parse.GetValue(group) ?? [])
            {
                var parts = spec.Split('=', 2);
                var access = parts.Length == 2 && parts[1].Trim().Equals("owner", StringComparison.OrdinalIgnoreCase) ? GroupAccess.Owner : GroupAccess.Member;
                roles.Add(new ManualRole(t.Key, new GroupScope(parts[0].Trim(), access), parts[0].Trim()));
            }

            if (roles.Count == 0)
            {
                throw new CliException("Nothing to add: give --entra, --azure or --group.", ExitCodes.Usage);
            }

            session.SetManualRoles(t.Key, roles.DistinctBy(r => r.Scope));
            context.Output.Note($"{Markup.Escape(t.DisplayName)} now has {roles.Count} manual role{(roles.Count == 1 ? "" : "s")}.");
            return ExitCodes.Ok;
        });

        var clear = new Command("clear", "Remove every manual role of a tenant.") { tenantArg, account };
        clear.SetAction(async (parse, ct) =>
        {
            var context = CommandContext.From(parse);
            var session = await context.SessionAsync(ct).ConfigureAwait(false);
            var t = context.RequireTenant(parse.GetValue(tenantArg)!, parse.GetValue(account));
            session.SetManualRoles(t.Key, []);
            context.Output.Note($"Cleared the manual roles of {Markup.Escape(t.DisplayName)}.");
            return ExitCodes.Ok;
        });

        command.Subcommands.Add(list);
        command.Subcommands.Add(add);
        command.Subcommands.Add(clear);
        return command;
    }

    private static string Describe(RoleScope scope) => scope switch
    {
        AzureResourceScope a => a.Scope,
        GroupScope g => g.GroupId + (g.AccessId == GroupAccess.Owner ? " (owner)" : " (member)"),
        EntraDirectoryScope e => e.RoleDefinitionId,
        _ => string.Empty,
    };
}
