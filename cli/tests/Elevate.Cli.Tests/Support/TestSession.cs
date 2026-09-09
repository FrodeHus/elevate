using Elevate.Cli.Commands;
using Elevate.Cli.Infrastructure;
using Elevate.Cli.Session;
using Elevate.Core.Managed;
using Elevate.Core.Models;
using Elevate.Core.Networking;
using Elevate.Core.Storage;

namespace Elevate.Cli.Tests.Support;

/// <summary>A session over a temp directory with one account, one tenant and fake providers.</summary>
public sealed class TestSession : IDisposable
{
    public static readonly Identity Account = new("id1", "alex@contoso.com", "Alex", "t1");

    public static readonly TenantContext Tenant = new("id1", "t1", "Contoso", TenantSource.Home);

    /// <param name="managed">
    /// The managed configuration the settings — and any command run against this directory — see.
    /// </param>
    /// <param name="http">
    /// The HTTP client the session talks to; the default answers nothing, so a test that needs an
    /// unauthenticated lookup (tenant resolution) passes a <see cref="StubHttpClient"/>.
    /// </param>
    public TestSession(ManagedConfiguration? managed = null, IHttpClient? http = null)
    {
        CommandContext.ManagedOverride = managed;
        CommandContext.HttpOverride = http;
        Directory = Path.Combine(Path.GetTempPath(), "elevate-cli-tests", Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(Directory);
        Store = new AppStateStore(Directory);
        Settings = new CliSettings(Directory, managed ?? ManagedConfiguration.None);
        Tokens = new FakeTokenProvider();
        Entra = new FakeProvider(RoleScopeKind.EntraDirectory);
        Azure = new FakeProvider(RoleScopeKind.AzureResource);
        Groups = new FakeProvider(RoleScopeKind.Group);
        EntraApprovals = new FakeApprovalProvider(RoleScopeKind.EntraDirectory);
        Packages = new FakeAccessPackageProvider();
        Session = new ElevateSession(Store, Settings, Tokens, http ?? new NoHttpClient(), [Entra, Azure, Groups], [EntraApprovals], Packages);
        Session.Load();
        Session.State.Identities.Add(Account);
        Session.State.UpsertTenant(Tenant);
        Session.Persist();
    }

    public string Directory { get; }

    public AppStateStore Store { get; }

    public CliSettings Settings { get; }

    public FakeTokenProvider Tokens { get; }

    public FakeProvider Entra { get; }

    public FakeProvider Azure { get; }

    public FakeProvider Groups { get; }

    public FakeApprovalProvider EntraApprovals { get; }

    public FakeAccessPackageProvider Packages { get; }

    public ElevateSession Session { get; }

    public static RoleKey EntraKey(string roleDefinitionId, string tenant = "t1") =>
        new("id1", tenant, new EntraDirectoryScope(roleDefinitionId, "/"));

    public static EligibleRole Role(string roleDefinitionId, string name, RolePolicy? policy = null) =>
        new(EntraKey(roleDefinitionId), name, RoleSource.Discovered, policy ?? RolePolicy.ManualDefault);

    public void Dispose()
    {
        CommandContext.ManagedOverride = null;
        CommandContext.HttpOverride = null;
        try
        {
            System.IO.Directory.Delete(Directory, recursive: true);
        }
        catch (IOException)
        {
            // Temp dir cleanup is best effort.
        }
    }
}
