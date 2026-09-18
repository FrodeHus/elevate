using System.Diagnostics.CodeAnalysis;

namespace Elevate.Core.Models;

/// <summary>Which kind of client an account authenticates with.</summary>
public enum SignInMethodKind
{
    /// <summary>Elevate's own app registration (the default, so it is the zero value).</summary>
    OwnApp,
    AzureCLI,
    AzurePowerShell,
    /// <summary>Any other public-client registration, identified by its client id.</summary>
    Custom,
}

/// <summary>
/// How an account authenticates. First-party methods need no app registration or admin consent;
/// <see cref="SignInMethodKind.Custom"/> is any other public-client registration used through the
/// same loopback browser flow. Port of the Swift <c>SignInMethod</c> enum; stored as a single
/// string ("ownApp", "azureCLI", "azurePowerShell", "ownApp:&lt;client id&gt;", or
/// "custom:&lt;client id&gt;").
/// </summary>
public readonly record struct SignInMethod
{
    public const string AzureCLIClientId = "04b07795-8ddb-461a-bbee-02f9e1bf7b46";
    public const string AzurePowerShellClientId = "1950a258-227b-4e31-a9cf-717495945fc2";

    /// <summary>Shown wherever a pinned account is used before the CLI supports it. The Windows app does.</summary>
    public const string PinnedUnsupportedMessage =
        "This account uses its own app registration, which this version of Elevate does not support yet.";

    // One field for the client id of both forms that carry one, so equality covers it.
    private readonly string? _clientId;

    private SignInMethod(SignInMethodKind kind, string? clientId)
    {
        Kind = kind;
        _clientId = clientId;
    }

    public SignInMethodKind Kind { get; }

    /// <summary>Client id of a custom registration; null for every other method.</summary>
    public string? CustomClientId => Kind == SignInMethodKind.Custom ? _clientId : null;

    /// <summary>Client id of a pinned own-app registration (stored "ownApp:&lt;id&gt;"); null otherwise.</summary>
    public string? PinnedClientId => Kind == SignInMethodKind.OwnApp ? _clientId : null;

    public bool IsPinned => PinnedClientId is not null;

    /// <summary>Whether this is an Entra app registration, the Settings one or the account's own.</summary>
    public bool IsOwnApp => Kind == SignInMethodKind.OwnApp;

    public static SignInMethod OwnApp => new(SignInMethodKind.OwnApp, null);
    public static SignInMethod AzureCLI => new(SignInMethodKind.AzureCLI, null);
    public static SignInMethod AzurePowerShell => new(SignInMethodKind.AzurePowerShell, null);

    /// <summary>A custom public-client registration. The client id must not be empty.</summary>
    public static SignInMethod Custom(string clientId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(clientId);
        return new SignInMethod(SignInMethodKind.Custom, clientId);
    }

    /// <summary>An own-app registration of the account's own, independent of the settings client id.</summary>
    public static SignInMethod PinnedApp(string clientId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(clientId);
        return new SignInMethod(SignInMethodKind.OwnApp, NormalizeClientId(clientId));
    }

    /// <summary>Trimmed and lower-cased, matching the Swift side.</summary>
    public static string NormalizeClientId(string clientId) => clientId.Trim().ToLowerInvariant();

    /// <summary>The methods offered as fixed choices; a custom one needs a client id typed by the user.</summary>
    public static IReadOnlyList<SignInMethod> BuiltIn { get; } = [OwnApp, AzureCLI, AzurePowerShell];

    public string DisplayName => Kind switch
    {
        SignInMethodKind.OwnApp => "Entra app registration",
        SignInMethodKind.AzureCLI => "Azure CLI app",
        SignInMethodKind.AzurePowerShell => "Azure PowerShell app",
        _ => "Other app (browser sign-in)",
    };

    /// <summary>Longer caption naming a pinned account's own client id.</summary>
    public string DetailedName
    {
        get
        {
            if (!IsPinned)
            {
                return DisplayName;
            }

            var id = PinnedClientId!;
            return $"{DisplayName} ({id[..Math.Min(8, id.Length)]}…)";
        }
    }

    /// <summary>Client id used through the loopback flow, or null when the own MSAL registration is used.</summary>
    public string? ClientId => Kind switch
    {
        SignInMethodKind.OwnApp => _clientId,
        SignInMethodKind.AzureCLI => AzureCLIClientId,
        SignInMethodKind.AzurePowerShell => AzurePowerShellClientId,
        _ => _clientId,
    };

    /// <summary>
    /// Whether an MSAL public client of ours serves this method: the Settings registration's
    /// provider, or — for a pinned account — the one built for its own client id.
    /// </summary>
    public bool UsesMsal => IsOwnApp;

    /// <summary>
    /// Whether the account signs in as the Azure CLI or Azure PowerShell app, whose token cache the
    /// tool itself reads: an activation through Elevate then provably leaves that tool a stale token.
    /// </summary>
    public bool SharesToolTokenCache => Kind is SignInMethodKind.AzureCLI or SignInMethodKind.AzurePowerShell;

    public bool IsCustom => Kind == SignInMethodKind.Custom;

    /// <summary>
    /// Whether the client is known to carry the Graph scope that activates Entra directory roles.
    /// Neither Microsoft first-party app is: they can list PIM schedules but
    /// <c>RoleAssignmentSchedule.ReadWrite.Directory</c> is admin-consent only. Another app is
    /// assumed capable until its token says otherwise.
    /// </summary>
    public bool IsPreauthorisedForEntraActivation =>
        Kind is SignInMethodKind.OwnApp or SignInMethodKind.Custom;

    /// <summary>One-line statement of what the method can do, for the add-account dialog and headers.</summary>
    public string? LimitationSummary => IsPreauthorisedForEntraActivation
        ? null
        : "Supports Azure resource roles only. Entra roles are neither read nor activated.";

    /// <summary>Longer explanation shown on the Entra rows and headers of an account using this method.</summary>
    public string? EntraViewOnlyReason => IsPreauthorisedForEntraActivation
        ? null
        : $"This account was added with the {DisplayName}, which supports Azure resource roles only: "
          + "Microsoft grants it no Graph PIM permissions, so Elevate does not read or activate Entra "
          + "roles for it. Add the account with an Entra app registration or another app registration for Entra roles.";

    /// <summary>The single string this method is persisted as.</summary>
    public string StorageKey => Kind switch
    {
        SignInMethodKind.OwnApp => IsPinned ? $"ownApp:{_clientId}" : "ownApp",
        SignInMethodKind.AzureCLI => "azureCLI",
        SignInMethodKind.AzurePowerShell => "azurePowerShell",
        _ => $"custom:{_clientId}",
    };

    /// <summary>Parses a stored key. Returns false for an unknown key or an empty custom or pinned client id.</summary>
    public static bool TryFromStorageKey(string? storageKey, [NotNullWhen(true)] out SignInMethod? method)
    {
        method = storageKey switch
        {
            "ownApp" => OwnApp,
            "azureCLI" => AzureCLI,
            "azurePowerShell" => AzurePowerShell,
            _ => null,
        };
        if (method is not null)
        {
            return true;
        }

        const string pinnedPrefix = "ownApp:";
        if (storageKey is not null && storageKey.StartsWith(pinnedPrefix, StringComparison.Ordinal))
        {
            var id = storageKey[pinnedPrefix.Length..];
            if (!string.IsNullOrWhiteSpace(id))
            {
                method = PinnedApp(id);
                return true;
            }

            return false;
        }

        const string prefix = "custom:";
        if (storageKey is not null && storageKey.StartsWith(prefix, StringComparison.Ordinal))
        {
            var id = storageKey[prefix.Length..];
            if (!string.IsNullOrWhiteSpace(id))
            {
                method = new SignInMethod(SignInMethodKind.Custom, id);
                return true;
            }
        }

        return false;
    }

    public override string ToString() => StorageKey;
}
