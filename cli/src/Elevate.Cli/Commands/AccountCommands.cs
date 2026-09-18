using System.CommandLine;
using Elevate.Cli.Infrastructure;
using Elevate.Cli.Rendering;
using Elevate.Cli.Session;
using Elevate.Core.Managed;
using Elevate.Core.Models;
using Spectre.Console;

namespace Elevate.Cli.Commands;

/// <summary><c>login</c>, <c>logout</c> and <c>accounts</c>.</summary>
public static class AccountCommands
{
    public static Command Login()
    {
        var method = new Option<string?>("--method", "-m")
        {
            Description = "own (your app registration), cli (Azure CLI app), pwsh (Azure PowerShell app) or custom; the first one your organization permits is the default.",
        };
        var clientId = new Option<string?>("--client-id")
        {
            Description = "The application (client) id of the registration to sign in with. With --method own it gives this "
                + "account a registration of its own instead of the configured one; with --method custom it names the other "
                + "app, and is remembered for next time.",
        };
        var command = new Command("login", "Sign in and add an account. Opens the browser, or shows a device code with --device-code.")
        {
            method, clientId,
        };
        command.SetAction(async (parse, ct) =>
        {
            var context = CommandContext.From(parse);
            var session = await context.SessionAsync(ct).ConfigureAwait(false);
            var chosen = ParseMethod(parse.GetValue(method) ?? ElevateSession.DefaultMethodName(session.Settings.Managed), parse.GetValue(clientId), session.Settings);
            if (chosen == SignInMethod.OwnApp && !session.Settings.IsConfigured)
            {
                throw new CliException(
                    "No client ID is configured for the own-app method. Run 'elevate config set client-id <application id>' first, "
                    + "or sign in with '--method cli' for Azure resource roles only.", ExitCodes.Usage);
            }

            if (chosen.LimitationSummary is { } limitation)
            {
                context.Output.Warn(Markup.Escape(limitation));
            }

            var identity = await session.AddAccountAsync(chosen, ct).ConfigureAwait(false);
            var homeKey = new TenantKey(identity.Id, identity.HomeTenantId);
            await context.Output.StatusAsync($"Reading roles in {session.TenantName(homeKey)}…", () => session.RefreshTenantAsync(homeKey, ct)).ConfigureAwait(false);
            if (context.Output.Json)
            {
                context.Output.WriteJson(Views.Account(session, identity));
                return ExitCodes.Ok;
            }

            var roles = session.Roles.GetValueOrDefault(homeKey)?.Count ?? 0;
            context.Output.WriteLine($"Signed in as [bold]{Markup.Escape(identity.Upn)}[/] ({Markup.Escape(chosen.DetailedName)}); "
                + $"{roles} eligible role{(roles == 1 ? "" : "s")} in {Markup.Escape(session.TenantName(homeKey))}.");
            if (session.TenantErrors.TryGetValue(homeKey, out var error))
            {
                context.Output.Warn(Markup.Escape(error));
            }

            context.Output.Note("Other tenants: 'elevate tenants discover --add' or 'elevate tenants add <domain>'.");
            return ExitCodes.Ok;
        });
        return command;
    }

    public static Command Logout()
    {
        var account = new Argument<string?>("account") { Description = "Part of the address; not needed with one account.", Arity = ArgumentArity.ZeroOrOne };
        var command = new Command("logout", "Sign an account out and forget its tenants, profile entries and remembered reasons.") { account };
        command.SetAction(async (parse, ct) =>
        {
            var context = CommandContext.From(parse);
            var session = await context.SessionAsync(ct).ConfigureAwait(false);
            var identity = context.RequireAccount(parse.GetValue(account));
            await session.SignOutAsync(identity, ct).ConfigureAwait(false);
            context.Output.Note($"Signed out {Markup.Escape(identity.Upn)}. Active assignments in Entra were not changed.");
            return ExitCodes.Ok;
        });
        return command;
    }

    public static Command Accounts()
    {
        var command = new Command("accounts", "List the signed-in accounts.");
        command.SetAction(async (parse, ct) =>
        {
            var context = CommandContext.From(parse);
            var session = await context.SessionAsync(ct).ConfigureAwait(false);
            if (context.Output.Json)
            {
                context.Output.WriteJson(session.Identities.Select(i => Views.Account(session, i)).ToList());
                return ExitCodes.Ok;
            }

            if (session.Identities.Count == 0)
            {
                context.Output.Plain("No accounts. Run 'elevate login' to add one.");
                return ExitCodes.Ok;
            }

            var table = new Table().Border(TableBorder.Rounded);
            table.AddColumn("Account");
            table.AddColumn("Name");
            table.AddColumn("Sign-in method");
            table.AddColumn("Home tenant");
            table.AddColumn("Tenants");
            table.AddColumn("Flags");
            foreach (var i in session.Identities)
            {
                var method = Markup.Escape(i.SignInMethod.DetailedName);
                if (!i.SignInMethod.IsPreauthorisedForEntraActivation)
                {
                    method += " [grey](Azure roles only)[/]";
                }

                table.AddRow(
                    Markup.Escape(i.Upn), Markup.Escape(i.DisplayName), method,
                    Markup.Escape(session.TenantName(new TenantKey(i.Id, i.HomeTenantId))),
                    session.Tenants.Count(t => t.IdentityId == i.Id).ToString(System.Globalization.CultureInfo.InvariantCulture),
                    session.IsMethodAllowed(i.SignInMethod) ? "[grey]—[/]" : "[yellow]not permitted[/]");
            }

            context.Output.Write(table);
            return ExitCodes.Ok;
        });
        command.Subcommands.Add(SetClientId());
        return command;
    }

    /// <summary>
    /// <c>accounts set-client-id</c>: moves one account onto an Entra app registration — its own,
    /// or the one in settings — keeping its tenants, roles and profiles.
    /// </summary>
    private static Command SetClientId()
    {
        var account = new Argument<string>("account") { Description = "Part of the address of the account to move." };
        var clientId = new Argument<string?>("client-id")
        {
            Description = "The registration's application (client) ID, or 'settings' for the configured one.",
            Arity = ArgumentArity.ZeroOrOne,
        };
        var fromSettings = new Option<bool>("--from-settings") { Description = "Use the client ID from 'elevate config', instead of one of this account's own." };
        var command = new Command(
            "set-client-id",
            "Move an account to another Entra app registration, keeping its tenants, roles and profiles. "
            + "Asks you to sign in with the new registration; nothing changes until you do.")
        {
            account, clientId, fromSettings,
        };
        command.SetAction(async (parse, ct) =>
        {
            var context = CommandContext.From(parse);
            var session = await context.SessionAsync(ct).ConfigureAwait(false);
            var identity = context.RequireAccount(parse.GetValue(account));
            var raw = parse.GetValue(clientId)?.Trim();
            var settingsTarget = parse.GetValue(fromSettings) || string.Equals(raw, "settings", StringComparison.OrdinalIgnoreCase);
            if (settingsTarget && raw is { Length: > 0 } && !string.Equals(raw, "settings", StringComparison.OrdinalIgnoreCase))
            {
                throw new CliException("Give a client ID or --from-settings, not both.", ExitCodes.Usage);
            }

            SignInMethod target;
            if (settingsTarget)
            {
                target = SignInMethod.OwnApp;
            }
            else if (CliSettings.IsValidClientId(raw))
            {
                target = SignInMethod.PinnedApp(raw!);
            }
            else
            {
                throw new CliException(
                    "Give the registration's application (client) ID as a GUID, or --from-settings for the configured one.", ExitCodes.Usage);
            }

            if (target == identity.SignInMethod)
            {
                context.Output.Note($"{Markup.Escape(identity.Upn)} already uses {Markup.Escape(target.DetailedName)}.");
                return ExitCodes.Ok;
            }

            var moved = await session.ChangeRegistrationAsync(identity, target, ct).ConfigureAwait(false);
            await context.Output.StatusAsync(
                $"Reading roles for {moved.Upn}…",
                () => session.RefreshAsync(session.Tenants.Where(t => t.IdentityId == moved.Id).Select(t => t.Key).ToList(), ct)).ConfigureAwait(false);
            if (context.Output.Json)
            {
                context.Output.WriteJson(Views.Account(session, moved));
                return ExitCodes.Ok;
            }

            context.Output.WriteLine(
                $"[bold]{Markup.Escape(moved.Upn)}[/] now uses {Markup.Escape(target.DetailedName)}; its tenants, roles and profiles were kept.");
            return ExitCodes.Ok;
        });
        return command;
    }

    internal static SignInMethod ParseMethod(string method, string? clientId, CliSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        switch (method.Trim().ToLowerInvariant())
        {
            case "own" or "ownapp" or "own-app":
            {
                // No --client-id means the registration in settings; one given pins this account to
                // its own, which the organization may have taken out of the user's hands.
                if (string.IsNullOrWhiteSpace(clientId))
                {
                    return Permitted(SignInMethod.OwnApp, "own", settings);
                }

                if (!CliSettings.IsValidClientId(clientId))
                {
                    throw new CliException("--client-id must be the registration's application (client) ID, a GUID.", ExitCodes.Usage);
                }

                if (settings.IsClientIdManaged)
                {
                    throw new CliException(
                        $"{CliSettings.ManagedClientIdMessage} Sign in with '--method own' and no --client-id to use it.", ExitCodes.Usage);
                }

                return Permitted(SignInMethod.PinnedApp(clientId), "own", settings);
            }

            case "cli" or "az" or "azurecli" or "azure-cli":
                return Permitted(SignInMethod.AzureCLI, "cli", settings);
            case "pwsh" or "powershell" or "azurepowershell" or "azure-powershell":
                return Permitted(SignInMethod.AzurePowerShell, "pwsh", settings);
            case "custom":
            {
                var id = string.IsNullOrWhiteSpace(clientId) ? settings.CustomClientId : clientId;
                if (!CliSettings.IsValidClientId(id))
                {
                    throw new CliException("--method custom needs --client-id <application id> (a GUID); the last one used is remembered.", ExitCodes.Usage);
                }

                return Permitted(SignInMethod.Custom(id.Trim()), "custom", settings);
            }

            default:
                throw new CliException($"Unknown sign-in method '{method}'. Use own, cli, pwsh or custom.", ExitCodes.Usage);
        }
    }

    /// <summary>The method itself, or the refusal naming the ones the organization does permit.</summary>
    private static SignInMethod Permitted(SignInMethod method, string name, CliSettings settings) =>
        ManagedPolicy.IsAllowed(method, settings.Managed) ? method : throw ElevateSession.DisallowedMethod(name, settings.Managed);
}
