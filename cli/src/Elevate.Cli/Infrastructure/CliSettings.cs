using System.Text.Json;
using System.Text.Json.Nodes;
using Elevate.Core.Managed;

namespace Elevate.Cli.Infrastructure;

/// <summary>Where a setting's value came from.</summary>
public enum SettingSource
{
    /// <summary>Pushed by the organization's managed configuration; the stored value is ignored.</summary>
    Managed,

    /// <summary>Stored in <c>settings.json</c> by the user.</summary>
    User,

    /// <summary>Not set anywhere; the built-in default applies.</summary>
    Default,
}

/// <summary>
/// <c>settings.json</c> in the data directory. Only the values the CLI understands are exposed;
/// every other key in the file is kept as it was, so pointing <c>--data-dir</c> at the Windows
/// app's directory never strips that app's own settings.
/// </summary>
public sealed class CliSettings
{
    private static readonly JsonSerializerOptions Pretty = new() { WriteIndented = true };

    private readonly string _path;
    private JsonObject _root;

    /// <param name="managed">
    /// The organization's managed configuration; the platform default source when omitted.
    /// </param>
    public CliSettings(string directory, ManagedConfiguration? managed = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(directory);
        Directory.CreateDirectory(directory);
        _path = Path.Combine(directory, "settings.json");
        _root = Load(_path);
        Managed = managed ?? ManagedConfiguration.Load(ManagedConfigurationSources.Default());
    }

    public string FilePath => _path;

    /// <summary>The managed configuration in effect; <see cref="ManagedConfiguration.IsEmpty"/> when there is none.</summary>
    public ManagedConfiguration Managed { get; }

    /// <summary>Whether the client id comes from managed configuration and cannot be changed here.</summary>
    public bool IsClientIdManaged => Managed.ClientId is { Length: > 0 };

    /// <summary>Whether the organization has turned the update check off.</summary>
    public bool UpdateCheckDisabled => Managed.DisableUpdateCheck;

    public SettingSource ClientIdSource => IsClientIdManaged
        ? SettingSource.Managed
        : Text("clientId").Length > 0 ? SettingSource.User : SettingSource.Default;

    /// <summary>
    /// Application (client) id of the user's own app registration; empty until configured. A
    /// managed client id wins over the stored one, and refuses to be changed.
    /// </summary>
    public string ClientId
    {
        get => Managed.ClientId ?? Text("clientId");
        set
        {
            if (IsClientIdManaged)
            {
                throw new InvalidOperationException(ManagedClientIdMessage);
            }

            Set("clientId", value.Trim());
        }
    }

    /// <summary>The one message used wherever a managed client id blocks a change.</summary>
    public const string ManagedClientIdMessage = "client-id is managed by your organization.";

    /// <summary>The custom client id used last, so the next <c>login --method custom</c> needs no id.</summary>
    public string CustomClientId
    {
        get => Text("customClientId");
        set => Set("customClientId", value.Trim());
    }

    /// <summary>The justification given with the last approval decision; prefilled next time.</summary>
    public string LastApprovalJustification
    {
        get => Text("lastApprovalJustification");
        set => Set("lastApprovalJustification", value);
    }

    /// <summary>
    /// Linux only: keep the token cache in a plain file instead of the keyring. For SSH sessions
    /// and containers, where no keyring daemon is reachable. Off unless set explicitly.
    /// </summary>
    public bool UnprotectedCache
    {
        get => _root["unprotectedCache"]?.GetValue<bool>() ?? false;
        set => Set("unprotectedCache", value);
    }

    public DateTimeOffset? LastUpdateCheck
    {
        get => _root["lastUpdateCheck"] is { } node && DateTimeOffset.TryParse(node.GetValue<string>(), out var d) ? d : null;
        set => Set("lastUpdateCheck", value?.ToString("O"));
    }

    public string? LatestKnownVersion
    {
        get => _root["latestKnownVersion"]?.GetValue<string>();
        set => Set("latestKnownVersion", value);
    }

    /// <summary>
    /// Ids of the accounts for which the stale-token hint after an Azure or group activation is
    /// hidden. Same key as the Windows app's settings, so a shared data directory shares the choice.
    /// </summary>
    public IReadOnlySet<string> DismissedTokenHintAccounts
    {
        get => _root["dismissedTokenHintAccounts"] is JsonArray array
            ? array.Select(n => n?.GetValue<string>()).OfType<string>().ToHashSet(StringComparer.Ordinal)
            : new HashSet<string>(StringComparer.Ordinal);
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            Set("dismissedTokenHintAccounts", value.Count == 0 ? null : new JsonArray([.. value.Order(StringComparer.Ordinal).Select(v => (JsonNode)v)]));
        }
    }

    public bool IsConfigured => IsValidClientId(ClientId);

    public static bool IsValidClientId(string? value)
    {
        var trimmed = (value ?? string.Empty).Trim();
        return Guid.TryParse(trimmed, out var guid) && guid != Guid.Empty;
    }

    private string Text(string key) => _root[key]?.GetValue<string>() ?? string.Empty;

    private void Set(string key, JsonNode? value)
    {
        if (value is null)
        {
            _root.Remove(key);
        }
        else
        {
            _root[key] = value;
        }

        Save();
    }

    private static JsonObject Load(string path)
    {
        if (!File.Exists(path))
        {
            return [];
        }

        try
        {
            return JsonNode.Parse(File.ReadAllBytes(path)) as JsonObject ?? [];
        }
        catch (JsonException)
        {
            // A corrupt settings file is not worth blocking the CLI for: start from defaults and
            // the next save overwrites it.
            return [];
        }
    }

    private void Save()
    {
        var temp = _path + ".tmp";
        File.WriteAllText(temp, _root.ToJsonString(Pretty));
        File.Move(temp, _path, overwrite: true);
    }
}
