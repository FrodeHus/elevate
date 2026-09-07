using System.CommandLine;
using Elevate.Cli.Selection;

namespace Elevate.Cli.Commands;

/// <summary>Options that several commands share, created once so help and completion agree.</summary>
public static class CommonOptions
{
    public static Option<string?> Account() => new("--account", "-a") { Description = "Only this account (part of the address or name)." };

    public static Option<string?> Tenant() => new("--tenant", "-t") { Description = "Only this tenant (part of the name, or the id)." };

    public static Option<string?> Kind() => new("--kind", "-k") { Description = "Only this kind: entra, azure or groups." };

    public static Option<string?> Scope() => new("--scope", "-s") { Description = "Only Azure roles whose scope contains this text (subscription name, resource group…)." };

    public static Option<bool> Yes() => new("--yes", "-y") { Description = "Do not ask for confirmation." };

    public static RoleFilter Filter(ParseResult parse, Option<string?> account, Option<string?> tenant, Option<string?>? kind = null, Option<string?>? scope = null)
    {
        ArgumentNullException.ThrowIfNull(parse);
        return new RoleFilter(
            parse.GetValue(account),
            parse.GetValue(tenant),
            kind is null ? null : RoleFilter.ParseKind(parse.GetValue(kind)),
            scope is null ? null : parse.GetValue(scope));
    }
}
