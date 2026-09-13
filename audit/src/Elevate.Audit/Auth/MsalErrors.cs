using Elevate.Core.Models;
using Microsoft.Identity.Client;

namespace Elevate.Audit.Auth;

public static class MsalErrors
{
    /// <summary>MSAL exceptions → the Core error kinds, so the rest of the tool speaks one language.</summary>
    public static PimException Map(Exception error)
    {
        ArgumentNullException.ThrowIfNull(error);
        switch (error)
        {
            case PimException pim:
                return pim;
            case MsalUiRequiredException:
                return new PimException(PimErrorKind.InteractionRequired);
            case MsalClientException client when client.ErrorCode == MsalError.AuthenticationCanceledError:
                return new PimException(PimErrorKind.SignInDeclined, "Sign-in cancelled");
            case MsalServiceException service:
            {
                var text = service.Message ?? string.Empty;
                if (text.Contains("AADSTS65001", StringComparison.Ordinal)
                    || text.Contains("AADSTS65004", StringComparison.Ordinal)
                    || text.Contains("AADSTS90094", StringComparison.Ordinal)
                    || text.Contains("consent_required", StringComparison.Ordinal))
                {
                    return new PimException(PimErrorKind.ConsentRequired, text);
                }

                if (text.Contains("AADSTS7000112", StringComparison.Ordinal)
                    || text.Contains("AADSTS700016", StringComparison.Ordinal)
                    || text.Contains("AADSTS53003", StringComparison.Ordinal)
                    || text.Contains("AADSTS500011", StringComparison.Ordinal))
                {
                    return new PimException(PimErrorKind.Forbidden, text);
                }

                return new PimException(PimErrorKind.Unexpected, text, service.StatusCode);
            }

            case MsalException msal:
                return new PimException(PimErrorKind.Unexpected, msal.Message);
            default:
                return new PimException(PimErrorKind.Network, error.Message);
        }
    }

    /// <summary>One paragraph for stderr telling the administrator what to do about a refused sign-in or read.</summary>
    public static string Explain(PimException error, string graphClientId)
    {
        ArgumentNullException.ThrowIfNull(error);
        var app = graphClientId == ClientIds.GraphDefault ? $"the {ClientIds.GraphDefaultDisplayName} app ({ClientIds.GraphDefault})" : $"client id {graphClientId}";
        var guide = "See docs/audit.md in the Elevate repository.";
        return error.Kind switch
        {
            PimErrorKind.ConsentRequired =>
                $"Consent for {app} was declined or is not permitted in this tenant. elevate-audit asks only for read scopes "
                + $"({string.Join(", ", ClientIds.GraphReadScopeNames)}). A tenant that blocks that app can pass any public client "
                + "of its own with --client-id after granting it the same read scopes. " + guide,
            PimErrorKind.Forbidden when error.Detail is { } detail && detail.Contains("AADSTS", StringComparison.Ordinal) =>
                $"Sign-in with {app} is blocked by this tenant's policy: {detail} Use --client-id with a registration your tenant allows. " + guide,
            PimErrorKind.Forbidden =>
                "The signed-in account may not list the tenant's role assignments. The scan needs a directory role that can read "
                + "PIM: Global Reader, Privileged Role Administrator or Security Reader (and Reader on the Azure scopes to audit). "
                + $"Details: {error.Detail ?? error.UserMessage}",
            PimErrorKind.SignInDeclined => "Sign-in was cancelled.",
            _ => error.UserMessage,
        };
    }
}
