using Elevate.Audit.Auth;
using Elevate.Core.Models;
using Elevate.Core.Providers;

namespace Elevate.Audit.Tests.Support;

public static class TestIdentity
{
    public const string TenantId = "11111111-1111-1111-1111-111111111111";

    public static readonly Identity Alex = new("home-1", "alex.rivera@contoso.com", "Alex Rivera", TenantId, SignInMethod.Custom(ClientIds.GraphDefault));

    public static GraphTransport Graph(StubHttpClient stub) => new(stub, new FakeTokenProvider());

    public static GraphTransport Arm(StubHttpClient stub) => new(stub, new FakeTokenProvider(), GraphTransport.MapArmError);
}
