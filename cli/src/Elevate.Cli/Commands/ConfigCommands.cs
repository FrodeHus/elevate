using System.CommandLine;
using Elevate.Cli.Auth;
using Elevate.Cli.Infrastructure;
using Spectre.Console;

namespace Elevate.Cli.Commands;

/// <summary><c>config</c>: the few settings the CLI has, and where its files live.</summary>
public static class ConfigCommands
{
    private static readonly string[] Keys = ["client-id", "custom-client-id", "unprotected-cache", "token-hint"];

    /// <summary>"shown", or the accounts the stale-token hint is hidden for.</summary>
    private static string TokenHintState(CommandContext context)
    {
        var session = context.Session;
        var hidden = session.Settings.DismissedTokenHintAccounts;
        return hidden.Count == 0 ? "shown" : "hidden for " + string.Join(", ", hidden.Select(session.AccountName).Order(StringComparer.Ordinal));
    }

    public static Command Config()
    {
        var command = new Command("config", "Show the settings. 'config set <key> <value>' changes one; 'config path' prints the data directory.");
        command.SetAction((parse, _) =>
        {
            var context = CommandContext.From(parse);
            var settings = context.Session.Settings;
            if (context.Output.Json)
            {
                context.Output.WriteJson(new
                {
                    dataDirectory = context.DataDirectory,
                    clientId = settings.ClientId.Length == 0 ? null : settings.ClientId,
                    customClientId = settings.CustomClientId.Length == 0 ? null : settings.CustomClientId,
                    unprotectedCache = settings.UnprotectedCache,
                    tokenHintHiddenFor = settings.DismissedTokenHintAccounts.Select(context.Session.AccountName).Order(StringComparer.Ordinal).ToList(),
                });
                return Task.FromResult(ExitCodes.Ok);
            }

            var table = new Table().Border(TableBorder.Rounded).HideHeaders();
            table.AddColumn("Key");
            table.AddColumn("Value");
            table.AddRow("data directory", Markup.Escape(context.DataDirectory));
            table.AddRow("client-id", settings.ClientId.Length == 0 ? "[grey]not set (needed for --method own)[/]" : Markup.Escape(settings.ClientId));
            table.AddRow("custom-client-id", settings.CustomClientId.Length == 0 ? "[grey]not set[/]" : Markup.Escape(settings.CustomClientId));
            table.AddRow("unprotected-cache", settings.UnprotectedCache ? "[yellow]true (Linux: plain-file token cache)[/]" : "false");
            table.AddRow("token-hint", Markup.Escape(TokenHintState(context)));
            context.Output.Write(table);
            return Task.FromResult(ExitCodes.Ok);
        });
        command.Subcommands.Add(Set());
        command.Subcommands.Add(Get());
        command.Subcommands.Add(PathCommand());
        return command;
    }

    private static Command Set()
    {
        var key = new Argument<string>("key") { Description = "client-id, custom-client-id, unprotected-cache or token-hint." };
        var value = new Argument<string>("value") { Description = "The new value; an empty string clears it. token-hint takes on or off." };
        var yes = CommonOptions.Yes();
        var account = CommonOptions.Account();
        var command = new Command("set", "Change a setting.") { key, value, yes, account };
        command.SetAction(async (parse, ct) =>
        {
            var context = CommandContext.From(parse);
            var session = context.Session;
            var settings = session.Settings;
            var k = parse.GetValue(key)!.Trim().ToLowerInvariant();
            var v = parse.GetValue(value)!.Trim();
            switch (k)
            {
                case "token-hint":
                {
                    var show = v.ToLowerInvariant() switch
                    {
                        "on" or "true" or "show" => true,
                        "off" or "false" or "hide" => false,
                        _ => throw new CliException("token-hint takes on or off.", ExitCodes.Usage),
                    };
                    var ids = parse.GetValue(account) is { } a
                        ? [context.RequireAccount(a).Id]
                        : session.Identities.Select(i => i.Id).ToList();
                    if (ids.Count == 0)
                    {
                        throw new CliException("No account is signed in. Run 'elevate login' first.", ExitCodes.SignInRequired);
                    }

                    var hidden = settings.DismissedTokenHintAccounts.ToHashSet(StringComparer.Ordinal);
                    foreach (var id in ids)
                    {
                        if (show)
                        {
                            hidden.Remove(id);
                        }
                        else
                        {
                            hidden.Add(id);
                        }
                    }

                    settings.DismissedTokenHintAccounts = hidden;
                    context.Output.Note($"token-hint {Markup.Escape(TokenHintState(context))}.");
                    return ExitCodes.Ok;
                }

                case "client-id":
                {
                    if (v.Length > 0 && !CliSettings.IsValidClientId(v))
                    {
                        throw new CliException("The application (client) ID must be a GUID.", ExitCodes.Usage);
                    }

                    // The own-app cache is per client id, so every own-app account signs out with the change.
                    var ownApp = session.Identities.Where(i => i.SignInMethod.UsesMsal).ToList();
                    if (ownApp.Count > 0 && !string.Equals(v, settings.ClientId, StringComparison.OrdinalIgnoreCase))
                    {
                        if (!parse.GetValue(yes) && context.Output.CanPrompt
                            && !context.Output.Stderr.Confirm($"Changing the client ID signs out {ownApp.Count} own-app account{(ownApp.Count == 1 ? "" : "s")} ({Markup.Escape(string.Join(", ", ownApp.Select(i => i.Upn)))}). Continue?", defaultValue: false))
                        {
                            return ExitCodes.Ok;
                        }

                        foreach (var identity in ownApp)
                        {
                            await session.SignOutAsync(identity, ct).ConfigureAwait(false);
                        }
                    }

                    settings.ClientId = v;
                    break;
                }

                case "custom-client-id":
                    if (v.Length > 0 && !CliSettings.IsValidClientId(v))
                    {
                        throw new CliException("The application (client) ID must be a GUID.", ExitCodes.Usage);
                    }

                    settings.CustomClientId = v;
                    break;
                case "unprotected-cache":
                    if (!bool.TryParse(v, out var flag))
                    {
                        throw new CliException("unprotected-cache takes true or false.", ExitCodes.Usage);
                    }

                    settings.UnprotectedCache = flag;
                    if (flag)
                    {
                        context.Output.Warn($"On Linux the token cache is now kept in a plain file under {Markup.Escape(context.DataDirectory)}; anyone with your file access can read it. Sign in again for it to take effect.");
                    }

                    break;
                default:
                    throw new CliException($"Unknown setting '{k}'. Keys: {string.Join(", ", Keys)}.", ExitCodes.Usage);
            }

            context.Output.Note($"{k} set.");
            return ExitCodes.Ok;
        });
        return command;
    }

    private static Command Get()
    {
        var key = new Argument<string>("key") { Description = "client-id, custom-client-id, unprotected-cache or token-hint." };
        var command = new Command("get", "Print one setting's value.") { key };
        command.SetAction((parse, _) =>
        {
            var context = CommandContext.From(parse);
            var settings = context.Session.Settings;
            var text = parse.GetValue(key)!.Trim().ToLowerInvariant() switch
            {
                "client-id" => settings.ClientId,
                "custom-client-id" => settings.CustomClientId,
                "unprotected-cache" => settings.UnprotectedCache ? "true" : "false",
                "token-hint" => TokenHintState(context),
                var other => throw new CliException($"Unknown setting '{other}'. Keys: {string.Join(", ", Keys)}.", ExitCodes.Usage),
            };
            context.Output.Plain(text);
            return Task.FromResult(ExitCodes.Ok);
        });
        return command;
    }

    private static Command PathCommand()
    {
        var command = new Command("path", "Print the data directory (state.json, settings.json and the token cache).");
        command.SetAction((parse, _) =>
        {
            var context = CommandContext.From(parse);
            context.Output.Plain(context.DataDirectory);
            context.Output.Note($"Token cache: {Markup.Escape(Path.Combine(context.DataDirectory, TokenCacheStore.FileName))} (keychain / keyring on macOS and Linux).");
            return Task.FromResult(ExitCodes.Ok);
        });
        return command;
    }
}
