namespace Elevate.Core.Models;

/// <summary>Port of the Swift <c>PIMError</c> cases.</summary>
public enum PimErrorKind
{
    /// <summary>Tenant has not consented to a required scope (Graph 403 or AADSTS65001).</summary>
    ConsentRequired,
    /// <summary>The service refused the call (HTTP 403) for an account admin consent cannot help.</summary>
    Forbidden,
    /// <summary>The user dismissed or timed out the interactive sign-in a tenant needed.</summary>
    SignInDeclined,
    /// <summary>Silent token acquisition failed; the caller must run an interactive flow.</summary>
    InteractionRequired,
    /// <summary>Resource returned a claims challenge; the detail is the decoded claims JSON.</summary>
    ClaimsChallenge,
    NotEligible,
    PolicyViolation,
    PendingApproval,
    Network,
    Unexpected,
}

/// <summary>Port of the Swift <c>PIMError</c> enum, including its <c>userMessage</c>.</summary>
public sealed class PimException : Exception
{
    public PimException(PimErrorKind kind, string? detail = null, int status = 0)
        : base(MessageFor(kind, detail, status))
    {
        Kind = kind;
        Detail = detail;
        Status = status;
    }

    public PimErrorKind Kind { get; }

    public string? Detail { get; }

    /// <summary>HTTP status for <see cref="PimErrorKind.Unexpected"/>; 0 marks "not an HTTP failure".</summary>
    public int Status { get; }

    /// <summary>The message shown to the user.</summary>
    public string UserMessage => Message;

    private static string MessageFor(PimErrorKind kind, string? detail, int status)
    {
        var text = detail ?? string.Empty;
        return kind switch
        {
            PimErrorKind.ConsentRequired => "Admin consent required for this tenant",
            PimErrorKind.Forbidden => $"Not permitted: {text}",
            PimErrorKind.SignInDeclined => "Sign-in for this tenant was not completed; press Refresh to try again",
            PimErrorKind.InteractionRequired => "Sign in again",
            PimErrorKind.ClaimsChallenge => "Multi-factor authentication required",
            PimErrorKind.NotEligible => "Not eligible for this role",
            PimErrorKind.PolicyViolation => text,
            PimErrorKind.PendingApproval => "Awaiting approval",
            PimErrorKind.Network => $"Network error: {text}",
            // status 0 is our own marker for "not an HTTP failure": the body is the message.
            _ => status == 0
                ? (text.Length == 0 ? "Unexpected error" : text)
                : (text.Length == 0
                    ? $"Unexpected response ({status})"
                    : $"Unexpected response ({status}): {text[..Math.Min(300, text.Length)]}"),
        };
    }
}
