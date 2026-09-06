using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Elevate.Core.Storage;

/// <summary>
/// Reads and writes <c>state.json</c>. Writes are atomic (temp file plus move) and carry a
/// generation, so a save computed from older state cannot overwrite a newer one.
/// </summary>
public sealed class AppStateStore
{
    private readonly Lock _gate = new();
    private readonly string _filePath;

    /// <summary>Generation of the newest state written; out-of-order saves are dropped.</summary>
    private ulong _lastAppliedGeneration;
    private ulong _nextGeneration;

    public AppStateStore(string? directory = null)
    {
        Directory = directory ?? DefaultDirectory;
        System.IO.Directory.CreateDirectory(Directory);
        _filePath = Path.Combine(Directory, "state.json");
    }

    /// <summary><c>%LOCALAPPDATA%\Elevate</c> on Windows; the platform equivalent elsewhere.</summary>
    public static string DefaultDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Elevate");

    public string Directory { get; }

    public string FilePath => _filePath;

    /// <summary>The saved state, or an empty one when nothing has been written yet.</summary>
    public AppState Load()
    {
        lock (_gate)
        {
            if (!File.Exists(_filePath))
            {
                return new AppState();
            }

            var bytes = File.ReadAllBytes(_filePath);
            return JsonSerializer.Deserialize<AppState>(bytes, Json.Options) ?? new AppState();
        }
    }

    /// <summary>
    /// Moves an unreadable <c>state.json</c> aside so the next save cannot silently destroy it.
    /// Returns the backup path, or null when there was no file to move.
    /// </summary>
    public string? QuarantineCorruptFile()
    {
        lock (_gate)
        {
            if (!File.Exists(_filePath))
            {
                return null;
            }

            var backup = Path.Combine(Directory, "state.json.bak");
            File.Move(_filePath, backup, overwrite: true);
            return backup;
        }
    }

    public void Save(AppState state)
    {
        lock (_gate)
        {
            _nextGeneration += 1;
            SaveLocked(state, _nextGeneration);
        }
    }

    /// <summary>Writes <paramref name="state"/> unless a newer generation has already been written.</summary>
    public void Save(AppState state, ulong generation)
    {
        lock (_gate)
        {
            SaveLocked(state, generation);
        }
    }

    private void SaveLocked(AppState state, ulong generation)
    {
        if (generation < _lastAppliedGeneration)
        {
            return;
        }

        var temp = _filePath + ".tmp";
        File.WriteAllBytes(temp, Encode(state));
        File.Move(temp, _filePath, overwrite: true);
        _lastAppliedGeneration = generation;
        _nextGeneration = Math.Max(_nextGeneration, generation);
    }

    /// <summary>
    /// Pretty-printed with sorted keys, matching the macOS encoder's
    /// <c>[.prettyPrinted, .sortedKeys]</c> output so the same file is produced on both platforms.
    /// </summary>
    internal static byte[] Encode(AppState state)
    {
        var node = JsonSerializer.SerializeToNode(state, Json.Options);
        var sorted = Sorted(node);
        var text = sorted?.ToJsonString(new JsonSerializerOptions(Json.Options) { WriteIndented = true }) ?? "{}";
        return Encoding.UTF8.GetBytes(text);
    }

    private static JsonNode? Sorted(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject o:
            {
                var result = new JsonObject();
                foreach (var (key, value) in o.ToList().OrderBy(p => p.Key, StringComparer.Ordinal))
                {
                    o.Remove(key);
                    result[key] = Sorted(value);
                }

                return result;
            }

            case JsonArray a:
            {
                var items = a.ToList();
                var result = new JsonArray();
                foreach (var item in items)
                {
                    a.Remove(item);
                    result.Add(Sorted(item));
                }

                return result;
            }

            default:
                return node;
        }
    }
}
