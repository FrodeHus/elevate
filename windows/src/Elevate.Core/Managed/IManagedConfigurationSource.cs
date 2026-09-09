namespace Elevate.Core.Managed;

/// <summary>
/// Something that can supply raw managed-configuration values by key, without knowing anything
/// about how those values are validated or combined. <see cref="ManagedConfiguration.Load"/> does
/// the validation; a source is just a typed lookup.
/// </summary>
public interface IManagedConfigurationSource
{
    /// <summary>The raw string value for <paramref name="key"/>, or null when absent.</summary>
    string? String(ManagedKey key);

    /// <summary>The raw boolean value for <paramref name="key"/>, or null when absent or not coercible.</summary>
    bool? Bool(ManagedKey key);

    /// <summary>The raw list value for <paramref name="key"/>, or null when absent or not coercible.</summary>
    IReadOnlyList<string>? List(ManagedKey key);

    /// <summary>A human-readable description of where these values came from.</summary>
    string Origin { get; }
}
