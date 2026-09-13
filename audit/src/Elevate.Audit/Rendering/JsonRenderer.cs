using System.Text.Json;
using Elevate.Audit.Model;

namespace Elevate.Audit.Rendering;

public static class JsonRenderer
{
    public static string Render(AuditReport report) => JsonSerializer.Serialize(report, AuditJson.Options) + "\n";
}
