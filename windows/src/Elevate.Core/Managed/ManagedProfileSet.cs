using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Elevate.Core.Models;
using Elevate.Core.Support;

namespace Elevate.Core.Managed;

/// <summary>A managed profile document was rejected; the message names the profile and field.</summary>
public sealed class ManagedProfileException(string message) : Exception(message);

/// <summary>
/// The organization-published profile document (design §7.1). Roles are named by tenant and role
/// rather than bound to account ids, so one document serves every machine;
/// <see cref="ManagedProfileResolver"/> turns the set into <see cref="ActivationProfile"/>s against
/// the accounts that are actually signed in. Port of the Swift <c>ManagedProfileSet</c>.
/// </summary>
public sealed record ManagedProfileSet
{
    public ManagedProfileSet(IEnumerable<Profile>? profiles = null) => Profiles = [.. profiles ?? []];

    /// <summary>
    /// One role in a managed profile; which fields matter depends on <paramref name="Kind"/>.
    /// The Swift core calls it <c>ManagedProfileSet.Role</c>; a nested record may not carry a
    /// member of its own name, and the document's field is <c>role</c>.
    /// </summary>
    /// <param name="Tenant">A tenant id or a verified domain, exactly as configured.</param>
    /// <param name="Role">Entra: display name or role template id. Azure: display name or role definition GUID.</param>
    /// <param name="Scope">Azure only: the ARM scope, compared case-insensitively.</param>
    /// <param name="DirectoryScope">Entra only; defaults to "/".</param>
    /// <param name="Group">Group only: display name or object id.</param>
    /// <param name="Access">Group only; defaults to <see cref="GroupAccess.Member"/>.</param>
    /// <param name="Duration">Proposed duration for the entry, capped by the role's policy as usual.</param>
    public sealed record RoleSpec(
        RoleScopeKind Kind,
        string Tenant,
        string? Role = null,
        string? Scope = null,
        string DirectoryScope = "/",
        string? Group = null,
        GroupAccess Access = GroupAccess.Member,
        TimeSpan? Duration = null);

    /// <summary>
    /// One published profile. <see cref="Id"/> is the administrator's slug; <see cref="ProfileId"/>
    /// is the UUID every machine derives from it, so a hot-key binding survives a reinstall.
    /// </summary>
    public sealed record Profile
    {
        public Profile(string id, string name, string? reason = null, bool pinned = false, IEnumerable<RoleSpec>? roles = null)
        {
            Id = id;
            Name = name;
            Reason = reason;
            Pinned = pinned;
            Roles = [.. roles ?? []];
        }

        public string Id { get; }

        public string Name { get; }

        public string? Reason { get; }

        public bool Pinned { get; }

        public IReadOnlyList<RoleSpec> Roles { get; }

        public Guid ProfileId => ManagedProfileSet.ProfileId(Id);

        public bool Equals(Profile? other) =>
            other is not null
            && Id == other.Id
            && Name == other.Name
            && Reason == other.Reason
            && Pinned == other.Pinned
            && Roles.SequenceEqual(other.Roles);

        public override int GetHashCode() => HashCode.Combine(Id, Name, Reason, Pinned, Roles.Count);
    }

    public IReadOnlyList<Profile> Profiles { get; }

    public static ManagedProfileSet Empty { get; } = new();

    /// <summary>The only document version this build understands.</summary>
    public const int Version = 1;

    /// <summary>The slug an administrator gives a profile; the derived UUID depends on it.</summary>
    private static readonly Regex Slug = new("^[a-z0-9-]{1,64}$", RegexOptions.CultureInvariant);

    /// <summary>RFC 4122's DNS namespace, so the derived ids are reproducible outside this app.</summary>
    private static ReadOnlySpan<byte> DnsNamespace =>
        [0x6b, 0xa7, 0xb8, 0x10, 0x9d, 0xad, 0x11, 0xd1, 0x80, 0xb4, 0x00, 0xc0, 0x4f, 0xd4, 0x30, 0xc8];

    /// <summary>
    /// UUID v5 of <c>managed-profile:&lt;slug&gt;</c> in the DNS namespace: stable across machines
    /// and installs, and identical to the Swift core's.
    /// </summary>
    public static Guid ProfileId(string slug)
    {
        var name = Encoding.UTF8.GetBytes("managed-profile:" + slug);
        var input = new byte[DnsNamespace.Length + name.Length];
        DnsNamespace.CopyTo(input);
        name.CopyTo(input, DnsNamespace.Length);

        var hash = SHA1.HashData(input);
        var bytes = hash[..16];
        bytes[6] = (byte)((bytes[6] & 0x0F) | 0x50);   // version 5
        bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80);   // RFC 4122 variant
        return new Guid(bytes, bigEndian: true);
    }

    /// <summary>Profiles in <paramref name="other"/> replace same-id ones in place; new ones are appended in order.</summary>
    public ManagedProfileSet Merged(ManagedProfileSet other)
    {
        ArgumentNullException.ThrowIfNull(other);

        var replacements = new Dictionary<string, Profile>(StringComparer.Ordinal);
        foreach (var profile in other.Profiles)
        {
            replacements[profile.Id] = profile;   // a later duplicate wins, as it does in Swift
        }

        var existing = Profiles.Select(p => p.Id).ToHashSet(StringComparer.Ordinal);
        return new ManagedProfileSet(
        [
            .. Profiles.Select(p => replacements.TryGetValue(p.Id, out var replacement) ? replacement : p),
            .. other.Profiles.Where(p => !existing.Contains(p.Id)),
        ]);
    }

    /// <summary>
    /// Parses the §7.1 document. Unknown fields are ignored; every shape error names the profile and
    /// the field, so an administrator can fix the document from the message alone.
    /// </summary>
    /// <exception cref="ManagedProfileException">The document is not a valid profile set.</exception>
    public static ManagedProfileSet Parse(string text)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(text);
        }
        catch (JsonException)
        {
            throw new ManagedProfileException("not a JSON object");
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw new ManagedProfileException("not a JSON object");
            }

            if (root.TryGetProperty("version", out var version) && !IsSupportedVersion(version))
            {
                throw new ManagedProfileException($"version {Describe(version)} is not supported");
            }

            var profiles = new List<Profile>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var index = 0;
            foreach (var item in Array(root, "profiles"))
            {
                index += 1;
                if (item.ValueKind != JsonValueKind.Object)
                {
                    throw new ManagedProfileException($"profile {index}: not a JSON object");
                }

                var profile = ParseProfile(item, index);
                if (!seen.Add(profile.Id))
                {
                    throw new ManagedProfileException($"profile '{profile.Id}': duplicate id");
                }

                profiles.Add(profile);
            }

            return new ManagedProfileSet(profiles);
        }
    }

    private static Profile ParseProfile(JsonElement element, int index)
    {
        var id = Text(element, "id");
        if (string.IsNullOrEmpty(id))
        {
            throw new ManagedProfileException($"profile {index}: id is required");
        }

        if (!Slug.IsMatch(id))
        {
            throw new ManagedProfileException($"profile '{id}': id must match [a-z0-9-]{{1,64}}");
        }

        var name = Text(element, "name");
        if (string.IsNullOrEmpty(name))
        {
            throw new ManagedProfileException($"profile '{id}': name is required");
        }

        var roles = new List<RoleSpec>();
        var roleIndex = 0;
        foreach (var item in Array(element, "roles"))
        {
            roleIndex += 1;
            if (item.ValueKind != JsonValueKind.Object)
            {
                throw new ManagedProfileException($"profile '{id}' role {roleIndex}: not a JSON object");
            }

            roles.Add(ParseRole(item, id, roleIndex));
        }

        var pinned = element.TryGetProperty("pinned", out var flag) && flag.ValueKind == JsonValueKind.True;
        return new Profile(id, name, Text(element, "reason"), pinned, roles);
    }

    private static RoleSpec ParseRole(JsonElement element, string profile, int index)
    {
        ManagedProfileException Fail(string message) =>
            new($"profile '{profile}' role {index}: {message}");

        var rawKind = Text(element, "kind");
        if (string.IsNullOrEmpty(rawKind))
        {
            throw Fail("kind is required");
        }

        var kind = rawKind switch
        {
            "entraDirectory" => RoleScopeKind.EntraDirectory,
            "azureResource" => RoleScopeKind.AzureResource,
            "group" => RoleScopeKind.Group,
            _ => throw Fail($"unknown kind '{rawKind}'"),
        };

        var tenant = Text(element, "tenant");
        if (string.IsNullOrEmpty(tenant))
        {
            throw Fail("tenant is required");
        }

        TimeSpan? duration = null;
        if (Text(element, "duration") is { } rawDuration)
        {
            duration = Iso8601Duration.Parse(rawDuration) ?? throw Fail($"'{rawDuration}' is not a valid duration");
        }

        var access = GroupAccess.Member;
        if (Text(element, "access") is { } rawAccess)
        {
            access = rawAccess switch
            {
                "member" => GroupAccess.Member,
                "owner" => GroupAccess.Owner,
                _ => throw Fail($"unknown access '{rawAccess}'"),
            };
        }

        var role = Text(element, "role");
        var scope = Text(element, "scope");
        var group = Text(element, "group");
        switch (kind)
        {
            case RoleScopeKind.EntraDirectory when string.IsNullOrEmpty(role):
                throw Fail("role is required");
            case RoleScopeKind.AzureResource when string.IsNullOrEmpty(role):
                throw Fail("role is required");
            case RoleScopeKind.AzureResource when string.IsNullOrEmpty(scope):
                throw Fail("scope is required for azureResource");
            case RoleScopeKind.Group when string.IsNullOrEmpty(group):
                throw Fail("group is required");
            default:
                break;
        }

        return new RoleSpec(kind, tenant, role, scope, Text(element, "directoryScope") ?? "/", group, access, duration);
    }

    /// <summary>JSON strings only: a number or bool in a string field is a shape error, not a value.</summary>
    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static IEnumerable<JsonElement> Array(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Array
            ? value.EnumerateArray()
            : [];

    private static bool IsSupportedVersion(JsonElement version) =>
        version.ValueKind == JsonValueKind.Number && version.TryGetInt32(out var number) && number == Version;

    private static string Describe(JsonElement version) =>
        version.ValueKind == JsonValueKind.Number && version.TryGetInt32(out var number)
            ? number.ToString(CultureInfo.InvariantCulture)
            : version.ToString();

    public bool Equals(ManagedProfileSet? other) => other is not null && Profiles.SequenceEqual(other.Profiles);

    public override int GetHashCode() => Profiles.Count;
}
