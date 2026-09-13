using FluentAssertions;

namespace Elevate.Audit.Tests.Support;

/// <summary>Golden-file comparison. Set ELEVATE_AUDIT_UPDATE_GOLDEN=1 to rewrite the expected files instead of comparing.</summary>
public static class Golden
{
    public static void Check(string repoRelativePath, string actual)
    {
        var path = Path.Combine(Repo.Root, repoRelativePath.Replace('/', Path.DirectorySeparatorChar));
        var normalized = actual.Replace("\r\n", "\n", StringComparison.Ordinal);
        if (Environment.GetEnvironmentVariable("ELEVATE_AUDIT_UPDATE_GOLDEN") == "1")
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, normalized);
            return;
        }

        File.Exists(path).Should().BeTrue($"{repoRelativePath} is missing; run: ELEVATE_AUDIT_UPDATE_GOLDEN=1 dotnet test audit/Elevate.Audit.sln");
        var expected = File.ReadAllText(path).Replace("\r\n", "\n", StringComparison.Ordinal);
        normalized.Should().Be(expected, $"{repoRelativePath} is stale; regenerate with: ELEVATE_AUDIT_UPDATE_GOLDEN=1 dotnet test audit/Elevate.Audit.sln");
    }
}
