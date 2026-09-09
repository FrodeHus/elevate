using System.Runtime.Versioning;
using Microsoft.Win32;

namespace Elevate.Core.Managed;

/// <summary>
/// Abstraction over a single registry hive so <see cref="RegistryManagedSource"/> can be tested
/// off Windows. A subkey path is relative to the hive root, matching <see cref="RegistryKey.OpenSubKey(string)"/>.
/// </summary>
public interface IRegistryView
{
    /// <summary>The raw value of <paramref name="name"/> under <paramref name="subKey"/>, or null when either is absent.</summary>
    object? Value(string subKey, string name);

    /// <summary>
    /// The values of <paramref name="subKey"/>'s own value names, ordered by the value name
    /// treated as an integer (names that are not integers sort after, ordinally), or null when
    /// the subkey does not exist.
    /// </summary>
    IReadOnlyList<string>? ListValues(string subKey);
}

/// <summary>Reads a subkey of a registry hive through the real Windows registry.</summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsRegistryView(RegistryHive hive) : IRegistryView
{
    /// <inheritdoc />
    public object? Value(string subKey, string name)
    {
        using var baseKey = RegistryKey.OpenBaseKey(hive, RegistryView.Default);
        using var key = baseKey.OpenSubKey(subKey);
        return key?.GetValue(name);
    }

    /// <inheritdoc />
    public IReadOnlyList<string>? ListValues(string subKey)
    {
        using var baseKey = RegistryKey.OpenBaseKey(hive, RegistryView.Default);
        using var key = baseKey.OpenSubKey(subKey);
        if (key is null)
        {
            return null;
        }

        return key.GetValueNames()
            .OrderBy(name => int.TryParse(name, out var n) ? n : int.MaxValue)
            .ThenBy(name => name, StringComparer.Ordinal)
            .Select(name => key.GetValue(name)?.ToString() ?? string.Empty)
            .ToList();
    }
}

/// <summary>
/// Managed configuration pushed via GPO/registry policy. Each key is read from HKLM first and
/// HKCU second — the first hit wins, so a machine-wide policy takes precedence over a per-user
/// one — under <see cref="ManagedConfigurationSources.RegistryPath"/>. The reported <see cref="Origin"/>
/// is always "Windows policy"; which hive actually supplied a given value is not surfaced beyond
/// that, since <see cref="IManagedConfigurationSource"/> has no per-key origin.
/// </summary>
public sealed class RegistryManagedSource(IRegistryView machine, IRegistryView user) : IManagedConfigurationSource
{
    /// <inheritdoc />
    public string Origin => "Windows policy";

    /// <inheritdoc />
    public string? String(ManagedKey key)
    {
        var raw = RawValue(key);
        return raw is string[] multiString ? string.Join('\n', multiString) : ManagedValue.String(raw);
    }

    /// <inheritdoc />
    public bool? Bool(ManagedKey key) => ManagedValue.Bool(RawValue(key));

    /// <inheritdoc />
    public IReadOnlyList<string>? List(ManagedKey key) => ManagedValue.List(RawValue(key)) ?? RawListValues(key);

    private object? RawValue(ManagedKey key)
    {
        var name = key.Name();
        return machine.Value(ManagedConfigurationSources.RegistryPath, name)
            ?? user.Value(ManagedConfigurationSources.RegistryPath, name);
    }

    private IReadOnlyList<string>? RawListValues(ManagedKey key)
    {
        var subKey = $@"{ManagedConfigurationSources.RegistryPath}\{key.Name()}";
        return machine.ListValues(subKey) ?? user.ListValues(subKey);
    }
}
