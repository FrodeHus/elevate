using System.CommandLine;
using Elevate.Cli.Auth;
using Elevate.Cli.Infrastructure;
using Elevate.Core.Models;
using Spectre.Console;

namespace Elevate.Cli.Commands;

/// <summary>
/// <c>token</c>: an access token on stdout and nothing else, so a command that does its own HTTP can
/// make one authenticated call with what is active right now. A command rather than an exported
/// variable because it refreshes (a variable is a snapshot, and a long-running child handed a stale
/// one gets a 401 with no recourse), because it is not inherited by every descendant, and because it
/// is useful outside <c>run</c>. It activates nothing: that is <c>activate</c>'s and <c>run</c>'s job.
/// </summary>
public static class TokenCommands
{
    /// <summary>The kubectl exec credential plugin contract; <c>v1</c> since Kubernetes 1.22.</summary>
    private const string ExecApiVersion = "client.authentication.k8s.io/v1";

    /// <summary>What <c>--format</c> writes on stdout.</summary>
    private enum TokenFormat
    {
        /// <summary>The token, one line, nothing else.</summary>
        Token,

        /// <summary>The token with its resource, account, tenant and expiry.</summary>
        Json,

        /// <summary>An <c>ExecCredential</c> for a kubeconfig <c>user.exec</c> block.</summary>
        Kubectl,
    }

    public static Command Token()
    {
        var resource = new Option<string?>("--resource")
        {
            Description = "Which API the token is for: arm, graph, or a resource URI such as https://vault.azure.net. Required — a token is minted for exactly one audience.",
            Required = true,
        };
        var account = new Option<string?>("--account", "-a") { Description = "Which signed-in account the token is for (part of the address or name). Default: the only one." };
        var tenant = new Option<string?>("--tenant", "-t") { Description = "Which tenant to mint it in: a tracked tenant's name, or any tenant id or domain. Default: the account's only tracked tenant, else its home tenant." };
        var format = new Option<string?>("--format")
        {
            Description = "token (default: the token alone), json (with resource, account, tenant and expiry) or kubectl (an ExecCredential for a kubeconfig exec plugin).",
        };
        var cached = new Option<bool>("--cached")
        {
            Description = "Allow a token from the cache. Faster, but one cached before an activation does not carry it; by default a token is minted fresh.",
        };
        var iKnow = new Option<bool>("--i-know")
        {
            Description = "Acknowledge the Graph scope ceiling, which --resource graph needs.",
        };
        var command = new Command(
            "token",
            "Print an access token for a resource on stdout, for a command that authenticates itself. Activates nothing.")
        {
            resource, account, tenant, format, cached, iKnow,
        };
        command.SetAction(async (parse, ct) =>
        {
            var context = CommandContext.From(parse);
            var wanted = TokenResource.Parse(parse.GetValue(resource));
            var shape = Format(parse.GetValue(format), context.Output.Json);
            RequireGraphAcknowledgement(context, wanted, parse.GetValue(iKnow));

            context.RequireSignedIn();
            var session = await context.SessionAsync(ct).ConfigureAwait(false);
            var identity = context.RequireAccount(parse.GetValue(account));
            var tenantId = ResolveTenant(context, identity, parse.GetValue(tenant));
            var token = await AcquireAsync(context, identity, tenantId, wanted, parse.GetValue(cached), ct).ConfigureAwait(false);
            var expires = TokenResource.ExpiresOn(token);

            switch (shape)
            {
                case TokenFormat.Json:
                    // The token itself; every other field is there to say which audience it is for.
                    context.Output.WriteJson(new
                    {
                        resource = wanted.Name,
                        resourceUri = wanted.Uri,
                        account = identity.Upn,
                        tenantId,
                        tenant = session.Tenant(new TenantKey(identity.Id, tenantId))?.DisplayName,
                        expiresOn = expires,
                        token,
                    });
                    break;
                case TokenFormat.Kubectl:
                    context.Output.WriteJson(new
                    {
                        apiVersion = ExecApiVersion,
                        kind = "ExecCredential",
                        status = new { expirationTimestamp = expires, token },
                    });
                    break;
                default:
                    context.Output.Plain(token);
                    break;
            }

            return ExitCodes.Ok;
        });
        return command;
    }

    /// <summary>
    /// The tenant the token is minted in: the one named, else the account's only tracked tenant, else
    /// its home tenant. Which tenant a token is for matters as much as which resource, so a default
    /// taken while several were tracked says so on stderr.
    /// </summary>
    internal static string ResolveTenant(CommandContext context, Identity identity, string? tenant)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(identity);
        var text = (tenant ?? string.Empty).Trim();
        if (text.Length > 0)
        {
            // A tenant Elevate does not track is still a tenant this account may hold a role in.
            return Guid.TryParse(text, out _) || text.Contains('.', StringComparison.Ordinal)
                ? text
                : context.RequireTenant(text, identity.Upn).TenantId;
        }

        var tracked = context.Session.Tenants.Where(t => t.IdentityId == identity.Id).ToList();
        if (tracked.Count == 1)
        {
            return tracked[0].TenantId;
        }

        if (string.IsNullOrWhiteSpace(identity.HomeTenantId))
        {
            throw new CliException($"Say which tenant the token is for with --tenant ({identity.Upn} has no home tenant recorded).", ExitCodes.Usage);
        }

        if (tracked.Count > 1)
        {
            var home = tracked.FirstOrDefault(t => t.TenantId == identity.HomeTenantId);
            context.Output.Note($"Token for the home tenant {Markup.Escape(home?.DisplayName ?? identity.HomeTenantId)}; --tenant picks another.");
        }

        return identity.HomeTenantId;
    }

    /// <summary>
    /// A token minted now, falling back to one interactive prompt when the cache cannot answer — the
    /// prompt goes to stderr, so a token on stdout stays pipeable either way. The token is never
    /// logged: not here, not in a note, not in a failure.
    /// </summary>
    internal static async Task<string> AcquireAsync(
        CommandContext context,
        Identity identity,
        string tenantId,
        TokenResource resource,
        bool cached,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(resource);
        var tokens = context.Session.Tokens;
        Task<string> Silent() => !cached && tokens is CliTokenProvider fresh
            ? fresh.FreshAccessTokenAsync(identity, tenantId, resource.Scopes, ct)
            : tokens.AccessTokenAsync(identity, tenantId, resource.Scopes, ct);

        try
        {
            return await context.Output.StatusAsync($"Getting a token for {resource.Uri}…", Silent).ConfigureAwait(false);
        }
        catch (PimException e) when (e.Kind is PimErrorKind.InteractionRequired or PimErrorKind.ClaimsChallenge)
        {
            // A role behind a Conditional Access authentication context answers with the claims to send back.
            var claims = e.Kind == PimErrorKind.ClaimsChallenge ? e.Detail : null;
            return await tokens.AcquireInteractivelyAsync(identity, tenantId, resource.Scopes, claims, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Graph is the one resource where the token is far less capable than the role that was just
    /// activated, so it is refused until the caller says they know. Widening the registration's Graph
    /// scopes to "fix" that would trade away the minimal consent posture the app registration rests on.
    /// </summary>
    internal static void RequireGraphAcknowledgement(CommandContext context, TokenResource resource, bool acknowledged)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(resource);
        if (!resource.IsGraph)
        {
            return;
        }

        if (!acknowledged)
        {
            throw new CliException($"{TokenResource.GraphCaveat} Add --i-know to get one anyway.", ExitCodes.Usage);
        }

        context.Output.Warn(Markup.Escape(TokenResource.GraphCaveat));
    }

    private static TokenFormat Format(string? format, bool json)
    {
        var text = (format ?? string.Empty).Trim().ToLowerInvariant();
        return text switch
        {
            "" => json ? TokenFormat.Json : TokenFormat.Token,
            "token" or "raw" or "plain" => TokenFormat.Token,
            "json" => TokenFormat.Json,
            "kubectl" or "kubectl-exec" or "exec" => TokenFormat.Kubectl,
            _ => throw new CliException($"Unknown format '{format}'. Use token, json or kubectl.", ExitCodes.Usage),
        };
    }
}
