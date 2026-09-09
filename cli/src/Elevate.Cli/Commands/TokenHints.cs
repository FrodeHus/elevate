using Elevate.Core.Coordination;
using Elevate.Core.Support;
using Spectre.Console;

namespace Elevate.Cli.Commands;

/// <summary>
/// The stale-token line after an Azure or group activation, on stderr, once per affected account
/// and not for accounts that turned it off with <c>elevate config set token-hint off</c>.
/// </summary>
internal static class TokenHints
{
    public static void Report(CommandContext context, IEnumerable<ActivationOutcome> outcomes)
    {
        var session = context.Session;
        var dismissed = session.Settings.DismissedTokenHintAccounts;
        foreach (var id in TokenCacheHint.AffectedAccounts(outcomes))
        {
            if (dismissed.Contains(id))
            {
                continue;
            }

            var upn = session.AccountName(id);
            context.Output.Warn($"{Markup.Escape(TokenCacheHint.Message(upn))} {Markup.Escape(TokenCacheHint.Advice)}");
            context.Output.Note($"Hide this for the account: elevate config set token-hint off --account {Markup.Escape(upn)}");
        }
    }
}
