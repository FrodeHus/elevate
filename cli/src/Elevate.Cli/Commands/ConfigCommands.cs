using System.CommandLine;
using Elevate.Cli.Auth;
using Elevate.Cli.Infrastructure;
using Elevate.Core.Managed;
using Spectre.Console;

namespace Elevate.Cli.Commands;

/// <summary><c>config</c>: the few settings the CLI has, and where its files live.</summary>
public static class ConfigCommands
{
    private static readonly string[] Keys = ["client-id", "custom-client-id", "unprotected-cache", "token-hint"];

    private static string Label(SettingSource source) => source switch
    {
        SettingSource.Managed => "managed",
        SettingSource.User => "user",
        _ => "default",
    };

    /// <summary>Everything but client-id is the user's when it has a value, the built-in default otherwise.</summary>
    private static SettingSource StoredSource(bool isSet) => isSet ? SettingSource.User : SettingSource.Default;

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
                    sources = new
                    {
                        clientId = Label(settings.ClientIdSource),
                        customClientId = Label(StoredSource(settings.CustomClientId.Length > 0)),
                        unprotectedCache = Label(StoredSource(settings.UnprotectedCache)),
                        tokenHint = Label(StoredSource(settings.DismissedTokenHintAccounts.Count > 0)),
                    },
                });
                return Task.FromResult(ExitCodes.Ok);
            }

            var table = new Table().Border(TableBorder.Rounded);
            table.AddColumn("Key");
            table.AddColumn("Value");
            table.AddColumn("Source");
            table.AddRow("data directory", Markup.Escape(context.DataDirectory), string.Empty);
            table.AddRow("client-id", settings.ClientId.Length == 0 ? "[grey]not set (needed for --method own)[/]" : Markup.Escape(settings.ClientId), Label(settings.ClientIdSource));
            table.AddRow("custom-client-id", settings.CustomClientId.Length == 0 ? "[grey]not set[/]" : Markup.Escape(settings.CustomClientId), Label(StoredSource(settings.CustomClientId.Length > 0)));
            table.AddRow("unprotected-cache", settings.UnprotectedCache ? "[yellow]true (Linux: plain-file token cache)[/]" : "false", Label(StoredSource(settings.UnprotectedCache)));
            table.AddRow("token-hint", Markup.Escape(TokenHintState(context)), Label(StoredSource(settings.DismissedTokenHintAccounts.Count > 0)));
            context.Output.Write(table);
            if (settings.IsClientIdManaged)
            {
                context.Output.Note($"client-id: managed by your organization ({Markup.Escape(settings.Managed.Origin ?? "policy")}).");
            }

            return Task.FromResult(ExitCodes.Ok);
        });
        command.Subcommands.Add(Set());
        command.Subcommands.Add(Get());
        command.Subcommands.Add(PathCommand());
        command.Subcommands.Add(ManagedCommand());
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
                    // Refused before any prompt or sign-out: a managed client id cannot change here.
                    if (settings.IsClientIdManaged)
                    {
                        throw new CliException(CliSettings.ManagedClientIdMessage, ExitCodes.Usage);
                    }

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
            var k = parse.GetValue(key)!.Trim().ToLowerInvariant();
            var text = k switch
            {
                "client-id" => settings.ClientId,
                "custom-client-id" => settings.CustomClientId,
                "unprotected-cache" => settings.UnprotectedCache ? "true" : "false",
                "token-hint" => TokenHintState(context),
                var other => throw new CliException($"Unknown setting '{other}'. Keys: {string.Join(", ", Keys)}.", ExitCodes.Usage),
            };
            context.Output.Plain(text);
            if (k == "client-id" && settings.IsClientIdManaged)
            {
                context.Output.Note($"client-id: managed by your organization ({Markup.Escape(settings.Managed.Origin ?? "policy")})");
            }

            return Task.FromResult(ExitCodes.Ok);
        });
        return command;
    }

    /// <summary><c>config managed</c>: the policy in effect, by key name only — never a value.</summary>
    private static Command ManagedCommand()
    {
        var file = new Option<string?>("--file") { Description = "Read a managed.json from this path instead of the platform source; a dry run for testing a policy file." };
        var command = new Command("managed", "Show the managed (MDM/GPO) configuration in effect: where it comes from, which keys it sets, and anything it got wrong.") { file };
        command.SetAction(async (parse, ct) =>
        {
            var context = CommandContext.From(parse);
            ManagedConfiguration managed;
            IReadOnlyList<string> warnings;
            string? origin;
            if (parse.GetValue(file) is { Length: > 0 } path)
            {
                // A caveat that changes what the output means, so it is said even with --json.
                context.Output.Warn($"Dry run: the trust check is skipped for {Markup.Escape(path)}, so a file the real load would ignore is still read here.");
                var source = new JsonFileManagedSource(path, _ => true);
                managed = ManagedConfiguration.Load(source);
                warnings = source.Warning is { } warning ? [.. managed.Warnings, warning] : managed.Warnings;
                origin = managed.Origin ?? (warnings.Count > 0 ? source.Origin : null);
            }
            else
            {
                // The session must resolve (managed tenants, then the published profiles) before
                // ManagedProfileWarnings has anything in it: without this the ManagedProfilesUrl
                // warning is unreachable and every domain-named profile tenant looks unresolved.
                var session = await context.SessionAsync(ct).ConfigureAwait(false);
                managed = session.Settings.Managed;
                // Whatever the published profile document got wrong belongs here too.
                warnings = [.. managed.Warnings, .. session.ManagedProfileWarnings];
                origin = managed.Origin;
            }

            var keys = managed.KeysInEffect.Select(k => k.Name()).ToList();
            if (context.Output.Json)
            {
                context.Output.WriteJson(new { origin, keys, warnings });
                return ExitCodes.Ok;
            }

            if (keys.Count == 0 && warnings.Count == 0)
            {
                context.Output.Note("No managed configuration.");
                return ExitCodes.Ok;
            }

            var table = new Table().Border(TableBorder.Rounded).HideHeaders();
            table.AddColumn("What");
            table.AddColumn("Value");
            table.AddRow("origin", Markup.Escape(origin ?? "none"));
            foreach (var key in keys)
            {
                table.AddRow("key", Markup.Escape(key));
            }

            foreach (var warning in warnings)
            {
                table.AddRow("[yellow]warning[/]", Markup.Escape(warning));
            }

            context.Output.Write(table);
            return ExitCodes.Ok;
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
