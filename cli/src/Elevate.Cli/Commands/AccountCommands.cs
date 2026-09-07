using System.CommandLine;
using Elevate.Cli.Infrastructure;
using Elevate.Cli.Rendering;
using Elevate.Core.Models;
using Spectre.Console;

namespace Elevate.Cli.Commands;

/// <summary><c>login</c>, <c>logout</c> and <c>accounts</c>.</summary>
public static class AccountCommands
{
    public static Command Login()
    {
        var method = new Option<string>("--method", "-m")
        {
            Description = "own (your app registration, the default), cli (Azure CLI app), pwsh (Azure PowerShell app) or custom.",
            DefaultValueFactory = _ => "own",
        };
        var clientId = new Option<string?>("--client-id") { Description = "With --method custom: the application (client) id of the registration. Remembered for next time." };
        var command = new Command("login", "Sign in and add an account. Opens the browser, or shows a device code with --device-code.")
        {
            method, clientId,
        };
        command.SetAction(async (parse, ct) =>
        {
            var context = CommandContext.From(parse);
            var session = context.Session;
            var chosen = ParseMethod(parse.GetValue(method)!, parse.GetValue(clientId), session.Settings);
            if (chosen.UsesMsal && !session.Settings.IsConfigured)
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
            context.Output.WriteLine($"Signed in as [bold]{Markup.Escape(identity.Upn)}[/] ({Markup.Escape(chosen.DisplayName)}); "
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
            var identity = context.RequireAccount(parse.GetValue(account));
            await context.Session.SignOutAsync(identity, ct).ConfigureAwait(false);
            context.Output.Note($"Signed out {Markup.Escape(identity.Upn)}. Active assignments in Entra were not changed.");
            return ExitCodes.Ok;
        });
        return command;
    }

    public static Command Accounts()
    {
        var command = new Command("accounts", "List the signed-in accounts.");
        command.SetAction((parse, _) =>
        {
            var context = CommandContext.From(parse);
            var session = context.Session;
            if (context.Output.Json)
            {
                context.Output.WriteJson(session.Identities.Select(i => Views.Account(session, i)).ToList());
                return Task.FromResult(ExitCodes.Ok);
            }

            if (session.Identities.Count == 0)
            {
                context.Output.Plain("No accounts. Run 'elevate login' to add one.");
                return Task.FromResult(ExitCodes.Ok);
            }

            var table = new Table().Border(TableBorder.Rounded);
            table.AddColumn("Account");
            table.AddColumn("Name");
            table.AddColumn("Sign-in method");
            table.AddColumn("Home tenant");
            table.AddColumn("Tenants");
            foreach (var i in session.Identities)
            {
                var method = Markup.Escape(i.SignInMethod.DisplayName);
                if (!i.SignInMethod.IsPreauthorisedForEntraActivation)
                {
                    method += " [grey](Azure roles only)[/]";
                }

                table.AddRow(
                    Markup.Escape(i.Upn), Markup.Escape(i.DisplayName), method,
                    Markup.Escape(session.TenantName(new TenantKey(i.Id, i.HomeTenantId))),
                    session.Tenants.Count(t => t.IdentityId == i.Id).ToString(System.Globalization.CultureInfo.InvariantCulture));
            }

            context.Output.Write(table);
            return Task.FromResult(ExitCodes.Ok);
        });
        return command;
    }

    internal static SignInMethod ParseMethod(string method, string? clientId, CliSettings settings)
    {
        switch (method.Trim().ToLowerInvariant())
        {
            case "own" or "ownapp" or "own-app":
                return SignInMethod.OwnApp;
            case "cli" or "az" or "azurecli" or "azure-cli":
                return SignInMethod.AzureCLI;
            case "pwsh" or "powershell" or "azurepowershell" or "azure-powershell":
                return SignInMethod.AzurePowerShell;
            case "custom":
            {
                var id = string.IsNullOrWhiteSpace(clientId) ? settings.CustomClientId : clientId;
                if (!CliSettings.IsValidClientId(id))
                {
                    throw new CliException("--method custom needs --client-id <application id> (a GUID); the last one used is remembered.", ExitCodes.Usage);
                }

                return SignInMethod.Custom(id.Trim());
            }

            default:
                throw new CliException($"Unknown sign-in method '{method}'. Use own, cli, pwsh or custom.", ExitCodes.Usage);
        }
    }
}
