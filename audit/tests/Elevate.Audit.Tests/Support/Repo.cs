namespace Elevate.Audit.Tests.Support;

public static class Repo
{
    /// <summary>The repository root: the nearest ancestor of the test binary that contains audit/Elevate.Audit.sln.</summary>
    public static string Root { get; } = Find();

    private static string Find()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "audit", "Elevate.Audit.sln")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        throw new InvalidOperationException("Repository root not found above " + AppContext.BaseDirectory);
    }
}
