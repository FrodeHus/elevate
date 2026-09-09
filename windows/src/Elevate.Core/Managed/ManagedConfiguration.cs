using Elevate.Core.Models;

namespace Elevate.Core.Managed;

/// <summary>
/// Settings pushed by an organization's policy, merged from whichever <see cref="IManagedConfigurationSource"/>
/// the app is configured with. Every property is optional or defaulted so an app with no managed
/// configuration behaves exactly as it would without this type existing at all. Port of the Swift
/// <c>ManagedConfiguration</c> struct.
/// </summary>
public sealed record ManagedConfiguration
{
    public string? ClientId { get; init; }

    public bool DisableUpdateCheck { get; init; }

    public IReadOnlySet<SignInMethodKind>? AllowedSignInMethods { get; init; }

    public IReadOnlyList<string>? AllowedTenants { get; init; }

    public IReadOnlyList<string> PinnedTenants { get; init; } = [];

    /// <summary>Raw JSON text for the managed profiles document; parsed elsewhere.</summary>
    public string? ManagedProfilesDocument { get; init; }

    public Uri? ManagedProfilesUrl { get; init; }

    /// <summary>The keys that carried a valid, in-effect value, in <see cref="ManagedKeys.All"/> order.</summary>
    public IReadOnlyList<ManagedKey> KeysInEffect { get; init; } = [];

    /// <summary>Human-readable notes about values that were present but rejected.</summary>
    public IReadOnlyList<string> Warnings { get; init; } = [];

    /// <summary>The source's <see cref="IManagedConfigurationSource.Origin"/>, set only when at least one key is in effect.</summary>
    public string? Origin { get; init; }

    /// <summary>The configuration with nothing managed — equivalent to <c>new ManagedConfiguration()</c>.</summary>
    public static ManagedConfiguration None { get; } = new();

    public bool IsEmpty => KeysInEffect.Count == 0;

    /// <summary>
    /// Reads and validates every managed key from <paramref name="source"/>, collecting warnings
    /// for values that were present but rejected. Keys with no value, or with a value that fails
    /// validation, are simply absent from the result — they do not affect <see cref="KeysInEffect"/>
    /// or <see cref="Origin"/>.
    /// </summary>
    public static ManagedConfiguration Load(IManagedConfigurationSource source)
    {
        ArgumentNullException.ThrowIfNull(source);

        string? clientId = null;
        var disableUpdateCheck = false;
        HashSet<SignInMethodKind>? allowedSignInMethods = null;
        IReadOnlyList<string>? allowedTenants = null;
        IReadOnlyList<string> pinnedTenants = [];
        string? managedProfilesDocument = null;
        Uri? managedProfilesUrl = null;
        var keysInEffect = new List<ManagedKey>();
        var warnings = new List<string>();

        var rawClientId = source.String(ManagedKey.ClientId);
        if (rawClientId is not null)
        {
            var trimmed = rawClientId.Trim();
            if (Guid.TryParseExact(trimmed, "D", out var guid) && guid != Guid.Empty)
            {
                clientId = guid.ToString().ToLowerInvariant();
                keysInEffect.Add(ManagedKey.ClientId);
            }
            else
            {
                warnings.Add($"ClientId: '{rawClientId}' is not a valid GUID");
            }
        }

        var flag = source.Bool(ManagedKey.DisableUpdateCheck);
        if (flag is not null)
        {
            disableUpdateCheck = flag.Value;
            keysInEffect.Add(ManagedKey.DisableUpdateCheck);
        }

        var methodNames = source.List(ManagedKey.AllowedSignInMethods);
        if (methodNames is { Count: > 0 })
        {
            var kinds = new HashSet<SignInMethodKind>();
            foreach (var name in methodNames)
            {
                if (Enum.TryParse<SignInMethodKind>(name, ignoreCase: true, out var kind) && Enum.IsDefined(kind))
                {
                    kinds.Add(kind);
                }
                else
                {
                    warnings.Add($"AllowedSignInMethods: unknown method '{name}' ignored");
                }
            }

            if (kinds.Count > 0)
            {
                allowedSignInMethods = kinds;
                keysInEffect.Add(ManagedKey.AllowedSignInMethods);
            }
        }

        var allowedTenantsRaw = source.List(ManagedKey.AllowedTenants);
        if (allowedTenantsRaw is not null)
        {
            var cleaned = CleanTenantList(allowedTenantsRaw);
            if (cleaned.Count > 0)
            {
                allowedTenants = cleaned;
                keysInEffect.Add(ManagedKey.AllowedTenants);
            }
        }

        var pinnedTenantsRaw = source.List(ManagedKey.PinnedTenants);
        if (pinnedTenantsRaw is not null)
        {
            var cleaned = CleanTenantList(pinnedTenantsRaw);
            if (cleaned.Count > 0)
            {
                pinnedTenants = cleaned;
                keysInEffect.Add(ManagedKey.PinnedTenants);
            }
        }

        var rawProfiles = source.String(ManagedKey.ManagedProfiles);
        if (rawProfiles is not null)
        {
            var trimmed = rawProfiles.Trim();
            if (trimmed.Length > 0)
            {
                managedProfilesDocument = rawProfiles;
                keysInEffect.Add(ManagedKey.ManagedProfiles);
            }
        }

        var rawUrl = source.String(ManagedKey.ManagedProfilesUrl);
        if (rawUrl is not null)
        {
            if (Uri.TryCreate(rawUrl, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps)
            {
                managedProfilesUrl = uri;
                keysInEffect.Add(ManagedKey.ManagedProfilesUrl);
            }
            else
            {
                warnings.Add("ManagedProfilesUrl: only https URLs are accepted");
            }
        }

        return new ManagedConfiguration
        {
            ClientId = clientId,
            DisableUpdateCheck = disableUpdateCheck,
            AllowedSignInMethods = allowedSignInMethods,
            AllowedTenants = allowedTenants,
            PinnedTenants = pinnedTenants,
            ManagedProfilesDocument = managedProfilesDocument,
            ManagedProfilesUrl = managedProfilesUrl,
            KeysInEffect = keysInEffect,
            Warnings = warnings,
            Origin = keysInEffect.Count > 0 ? source.Origin : null,
        };
    }

    /// <summary>Trims, lower-cases, drops blanks, and de-duplicates while preserving order.</summary>
    private static List<string> CleanTenantList(IReadOnlyList<string> raw)
    {
        var seen = new HashSet<string>();
        var result = new List<string>();
        foreach (var entry in raw)
        {
            var cleaned = entry.Trim().ToLowerInvariant();
            if (cleaned.Length == 0 || !seen.Add(cleaned))
            {
                continue;
            }

            result.Add(cleaned);
        }

        return result;
    }
}
