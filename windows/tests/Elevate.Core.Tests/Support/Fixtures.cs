using System.Text;

namespace Elevate.Core.Tests.Support;

/// <summary>Reads the JSON fixtures copied next to the test assembly. Port of the Swift <c>Fixtures</c>.</summary>
public static class Fixtures
{
    public static byte[] Data(string name)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", name.EndsWith(".json", StringComparison.Ordinal) ? name : name + ".json");
        return File.ReadAllBytes(path);
    }

    public static string Text(string name) => Encoding.UTF8.GetString(Data(name));
}
