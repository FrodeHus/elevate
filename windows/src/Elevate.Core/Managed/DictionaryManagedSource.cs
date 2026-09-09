using System.Globalization;
using System.Text.Json;

namespace Elevate.Core.Managed;

/// <summary>
/// A managed configuration source backed by an in-memory dictionary — used in tests, and as the
/// shape a JSON-decoded payload takes internally.
/// </summary>
public sealed class DictionaryManagedSource(IReadOnlyDictionary<string, object?> values, string origin = "test") : IManagedConfigurationSource
{
    /// <inheritdoc />
    public string Origin { get; } = origin;

    /// <inheritdoc />
    public string? String(ManagedKey key) => ManagedValue.String(RawValue(key));

    /// <inheritdoc />
    public bool? Bool(ManagedKey key) => ManagedValue.Bool(RawValue(key));

    /// <inheritdoc />
    public IReadOnlyList<string>? List(ManagedKey key) => ManagedValue.List(RawValue(key));

    private object? RawValue(ManagedKey key) => values.TryGetValue(key.Name(), out var value) ? value : null;
}

/// <summary>
/// Shared coercions used by every managed configuration source: a string from a string, a number,
/// or a JSON scalar/object; a bool from a <see cref="bool"/>, an integer, or the strings
/// "true"/"1" (and their false counterparts); a list from a string array, another string
/// enumerable, a JSON array, or a comma-separated string.
/// </summary>
internal static class ManagedValue
{
    public static string? String(object? value) => value switch
    {
        string s => s,
        int i => i.ToString(CultureInfo.InvariantCulture),
        long l => l.ToString(CultureInfo.InvariantCulture),
        JsonElement { ValueKind: JsonValueKind.String } e => e.GetString(),
        JsonElement { ValueKind: JsonValueKind.Number } e => e.GetRawText(),
        JsonElement { ValueKind: JsonValueKind.Object or JsonValueKind.Array } e => JsonSerializer.Serialize(e),
        _ => null,
    };

    public static bool? Bool(object? value) => value switch
    {
        bool b => b,
        int i => i != 0,
        long l => l != 0,
        string s when IsTrue(s) => true,
        string s when IsFalse(s) => false,
        JsonElement { ValueKind: JsonValueKind.True } => true,
        JsonElement { ValueKind: JsonValueKind.False } => false,
        JsonElement { ValueKind: JsonValueKind.Number } e => e.TryGetInt64(out var n) ? n != 0 : null,
        JsonElement { ValueKind: JsonValueKind.String } e when IsTrue(e.GetString()) => true,
        JsonElement { ValueKind: JsonValueKind.String } e when IsFalse(e.GetString()) => false,
        _ => null,
    };

    public static IReadOnlyList<string>? List(object? value) => value switch
    {
        string[] a => a,
        IEnumerable<string> e => e.ToList(),
        string s => s.Split(',').Select(part => part.Trim()).ToList(),
        JsonElement { ValueKind: JsonValueKind.Array } e => e.EnumerateArray()
            .Select(ItemToString)
            .Where(s => s is not null)
            .Select(s => s!)
            .ToList(),
        _ => null,
    };

    private static string? ItemToString(JsonElement item) => item.ValueKind switch
    {
        JsonValueKind.String => item.GetString(),
        JsonValueKind.Null or JsonValueKind.Undefined => null,
        _ => item.GetRawText(),
    };

    private static bool IsTrue(string? s) => s is not null && (s.Equals("true", StringComparison.OrdinalIgnoreCase) || s == "1");

    private static bool IsFalse(string? s) => s is not null && (s.Equals("false", StringComparison.OrdinalIgnoreCase) || s == "0");
}
