using System.Text.Json;
using Elevate.Audit.Infrastructure;
using Elevate.Audit.Model;

namespace Elevate.Audit.Rendering;

/// <summary>`--save-snapshot` / `--from-snapshot`. The kind marker keeps a report from being mistaken for a snapshot.</summary>
public static class SnapshotFile
{
    public static void Save(Snapshot snapshot, string path)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        File.WriteAllText(path, JsonSerializer.Serialize(snapshot, AuditJson.Options) + "\n");
    }

    public static Snapshot Load(string path)
    {
        if (!File.Exists(path))
        {
            throw new AuditException($"Snapshot file not found: {path}");
        }

        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("kind", out var kind) || kind.GetString() != Snapshot.KindMarker)
        {
            throw root.ValueKind == JsonValueKind.Object && root.TryGetProperty("findings", out _)
                ? new AuditException($"{path} is a report, not a snapshot. Re-run the scan with --save-snapshot to produce one.")
                : new AuditException($"{path} is not an elevate-audit snapshot.");
        }

        return root.Deserialize<Snapshot>(AuditJson.Options) ?? throw new AuditException($"{path} is empty.");
    }
}
