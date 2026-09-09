using System.Text.Json;

namespace Elevate.Core.Managed;

/// <summary>
/// Managed configuration read from a JSON file on disk — the fallback used on platforms without a
/// registry-backed policy store (macOS/Linux ports of the Windows app, and local testing). The
/// file is trusted only when it, and its parent directory, are not writable by anyone but their
/// owner, so a non-admin user cannot plant a fake policy file.
/// </summary>
public sealed class JsonFileManagedSource : IManagedConfigurationSource
{
    /// <summary>The path <see cref="ManagedConfigurationSources.Default"/> reads on non-Windows platforms.</summary>
    public const string DefaultPath = "/etc/elevate/managed.json";

    private readonly IManagedConfigurationSource? _inner;

    /// <summary>
    /// Creates a source backed by <paramref name="path"/>. Nothing is read until construction:
    /// a missing file leaves every getter returning null with no warning; an untrusted or
    /// malformed file leaves every getter returning null and sets <see cref="Warning"/>.
    /// </summary>
    /// <param name="isTrusted">
    /// Decides whether the file at a given path should be trusted. Defaults to a check that the
    /// file is not world-writable and its parent directory is not world-writable (on Windows the
    /// check always passes, since this source is never the platform default there).
    /// </param>
    public JsonFileManagedSource(string path, Func<string, bool>? isTrusted = null)
    {
        ArgumentNullException.ThrowIfNull(path);
        Origin = path;
        isTrusted ??= DefaultIsTrusted;

        if (!File.Exists(path))
        {
            return;
        }

        if (!isTrusted(path))
        {
            Warning = $"{path}: ignored because it is not owned by root or is writable by others";
            return;
        }

        try
        {
            var json = File.ReadAllText(path);
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                Warning = $"{path}: ignored because its contents are not a JSON object";
                return;
            }

            var values = new Dictionary<string, object?>();
            foreach (var property in document.RootElement.EnumerateObject())
            {
                values[property.Name] = property.Value.Clone();
            }

            _inner = new DictionaryManagedSource(values, path);
        }
        catch (JsonException)
        {
            Warning = $"{path}: ignored because it could not be parsed as JSON";
        }
    }

    /// <summary>Set when the file exists but was ignored (untrusted) or failed to parse.</summary>
    public string? Warning { get; }

    /// <inheritdoc />
    public string Origin { get; }

    /// <inheritdoc />
    public string? String(ManagedKey key) => _inner?.String(key);

    /// <inheritdoc />
    public bool? Bool(ManagedKey key) => _inner?.Bool(key);

    /// <inheritdoc />
    public IReadOnlyList<string>? List(ManagedKey key) => _inner?.List(key);

    private static bool DefaultIsTrusted(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            return true;
        }

        try
        {
            var fileMode = File.GetUnixFileMode(path);
            if (fileMode.HasFlag(UnixFileMode.OtherWrite))
            {
                return false;
            }

            var directory = Path.GetDirectoryName(Path.GetFullPath(path));
            if (directory is not null && File.GetUnixFileMode(directory).HasFlag(UnixFileMode.OtherWrite))
            {
                return false;
            }

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
