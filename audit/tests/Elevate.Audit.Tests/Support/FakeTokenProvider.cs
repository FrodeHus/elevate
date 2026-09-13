using Elevate.Core.Auth;
using Elevate.Core.Models;

namespace Elevate.Audit.Tests.Support;

/// <summary>Hands out a fixed bearer token; the stub HTTP client never checks it.</summary>
public sealed class FakeTokenProvider : ITokenProvider
{
    public Task<Identity> SignInAsync(SignInMethod method, CancellationToken ct = default) => Task.FromResult(TestIdentity.Alex);
    public Task SignOutAsync(Identity identity, CancellationToken ct = default) => Task.CompletedTask;
    public Task<IReadOnlyList<Identity>> IdentitiesAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<Identity>>([TestIdentity.Alex]);
    public Task<string> AccessTokenAsync(Identity identity, string tenantId, IReadOnlyList<string> scopes, CancellationToken ct = default) => Task.FromResult("token");
    public Task<string> AcquireInteractivelyAsync(Identity identity, string tenantId, IReadOnlyList<string> scopes, string? claims, CancellationToken ct = default) => Task.FromResult("token");
}
