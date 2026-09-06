using System.Text.Json;
using System.Text.Json.Serialization;
using CommunityToolkit.Mvvm.ComponentModel;
using Elevate.Core.Storage;

namespace Elevate.App.Services;

/// <summary>Which list the panel shows. Persisted so the flyout reopens where it was left.</summary>
public enum PanelTab
{
    Roles,
    Azure,
    Groups,
}

/// <summary>
/// User-editable configuration, in <c>%LOCALAPPDATA%\Elevate\settings.json</c>. The client id is
/// the only required value. Port of the macOS <c>AppSettings</c> (UserDefaults there).
/// </summary>
public sealed class AppSettings : ObservableObject
{
    public const string LoopbackRedirectUri = "http://localhost";

    private static readonly JsonSerializerOptions FileOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private readonly string _path;
    private string _clientId = string.Empty;
    private string _customClientId = string.Empty;
    private PanelTab _panelTab = PanelTab.Roles;
    private bool _collapsedActive;

    public AppSettings(string? directory = null)
    {
        Directory = directory ?? AppStateStore.DefaultDirectory;
        System.IO.Directory.CreateDirectory(Directory);
        _path = Path.Combine(Directory, "settings.json");
        Load();
    }

    /// <summary>The persisted shape; every field optional so an older file still loads.</summary>
    private sealed record FileModel(
        string? ClientId = null,
        string? CustomClientId = null,
        PanelTab? PanelTab = null,
        bool? CollapsedActive = null);

    public string Directory { get; }

    public string FilePath => _path;

    /// <summary>Application (client) id of the user's own app registration; empty until configured.</summary>
    public string ClientId
    {
        get => _clientId;
        set
        {
            if (SetProperty(ref _clientId, value ?? string.Empty))
            {
                OnPropertyChanged(nameof(IsConfigured));
                Save();
            }
        }
    }

    /// <summary>
    /// Last client id typed into "Custom client ID" in Add account, so the next account from the
    /// same company app needs no retyping. Not a configuration value in its own right.
    /// </summary>
    public string CustomClientId
    {
        get => _customClientId;
        set
        {
            if (SetProperty(ref _customClientId, value ?? string.Empty))
            {
                Save();
            }
        }
    }

    /// <summary>The flyout's last-used pivot.</summary>
    public PanelTab PanelTab
    {
        get => _panelTab;
        set
        {
            if (SetProperty(ref _panelTab, value))
            {
                Save();
            }
        }
    }

    /// <summary>Whether the flyout's "Active now" group is collapsed; remembered between launches.</summary>
    public bool CollapsedActive
    {
        get => _collapsedActive;
        set
        {
            if (SetProperty(ref _collapsedActive, value))
            {
                Save();
            }
        }
    }

    public bool IsConfigured => IsValidClientId(ClientId);

    /// <summary>The redirect URI the Windows broker (WAM) expects the registration to list for a client id.</summary>
    public static string BrokerRedirectUri(string clientId) =>
        $"ms-appx-web://microsoft.aad.brokerplugin/{(clientId ?? string.Empty).Trim()}";

    public static bool IsValidClientId(string? value)
    {
        var trimmed = (value ?? string.Empty).Trim();
        return Guid.TryParse(trimmed, out var guid) && guid != Guid.Empty;
    }

    private void Load()
    {
        if (!File.Exists(_path))
        {
            return;
        }

        try
        {
            var model = JsonSerializer.Deserialize<FileModel>(File.ReadAllBytes(_path), FileOptions);
            if (model is null)
            {
                return;
            }

            _clientId = model.ClientId ?? string.Empty;
            _customClientId = model.CustomClientId ?? string.Empty;
            _panelTab = model.PanelTab ?? PanelTab.Roles;
            _collapsedActive = model.CollapsedActive ?? false;
        }
        catch (JsonException)
        {
            // A corrupt settings file is not worth blocking the app for: start from defaults and
            // the next save overwrites it.
        }
    }

    private void Save()
    {
        var model = new FileModel(_clientId, _customClientId, _panelTab, _collapsedActive);
        var temp = _path + ".tmp";
        File.WriteAllBytes(temp, JsonSerializer.SerializeToUtf8Bytes(model, FileOptions));
        File.Move(temp, _path, overwrite: true);
    }
}
