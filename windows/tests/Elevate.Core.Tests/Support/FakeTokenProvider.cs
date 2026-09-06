using Elevate.Core.Auth;
using Elevate.Core.Models;

namespace Elevate.Core.Tests.Support;

/// <summary>Port of the Swift <c>FakeTokenProvider</c>.</summary>
public sealed class FakeTokenProvider : ITokenProvider
{
    public sealed record InteractiveCall(string TenantId, IReadOnlyList<string> Scopes, string? Claims);

    private readonly List<Identity> _storedIdentities = [];
    private readonly List<InteractiveCall> _interactiveCalls = [];
    private readonly List<string> _silentCalls = [];
    private readonly List<string> _signOutCalls = [];
    private readonly Dictionary<(string IdentityId, string TenantId), string> _tokens = [];
    private readonly Lock _gate = new();

    /// <summary>Thrown by <see cref="AccessTokenAsync"/> while set; a successful interactive acquisition clears it.</summary>
    public PimException? SilentError { get; set; }

    /// <summary>Thrown by <see cref="AcquireInteractivelyAsync"/> while set.</summary>
    public PimException? InteractiveError { get; set; }

    public IReadOnlyList<Identity> StoredIdentities
    {
        get { lock (_gate) { return [.. _storedIdentities]; } }
    }

    public IReadOnlyList<InteractiveCall> InteractiveCalls
    {
        get { lock (_gate) { return [.. _interactiveCalls]; } }
    }

    public IReadOnlyList<string> SilentCalls
    {
        get { lock (_gate) { return [.. _silentCalls]; } }
    }

    /// <summary>Identity ids passed to <see cref="SignOutAsync"/>, so a test can assert a sign-in was discarded.</summary>
    public IReadOnlyList<string> SignOutCalls
    {
        get { lock (_gate) { return [.. _signOutCalls]; } }
    }

    public void AddIdentity(Identity identity)
    {
        lock (_gate)
        {
            _storedIdentities.Add(identity);
        }
    }

    /// <summary>Sets the token returned for one (identity, tenant) pair; otherwise "token-{tenantId}" is used.</summary>
    public void SetToken(string identityId, string tenantId, string token)
    {
        lock (_gate)
        {
            _tokens[(identityId, tenantId)] = token;
        }
    }

    public Task<Identity> SignInAsync(SignInMethod method, CancellationToken ct)
    {
        var identity = new Identity("new", "new@x", "New", "home", method);
        lock (_gate)
        {
            _storedIdentities.Add(identity);
        }

        return Task.FromResult(identity);
    }

    public Task SignOutAsync(Identity identity, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(identity);
        lock (_gate)
        {
            _signOutCalls.Add(identity.Id);
            _storedIdentities.RemoveAll(i => i.Id == identity.Id);
        }

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<Identity>> IdentitiesAsync(CancellationToken ct)
        => Task.FromResult(StoredIdentities);

    public Task<string> AccessTokenAsync(
        Identity identity,
        string tenantId,
        IReadOnlyList<string> scopes,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(identity);
        lock (_gate)
        {
            _silentCalls.Add(tenantId);
        }

        if (SilentError is { } error)
        {
            return Task.FromException<string>(error);
        }

        return Task.FromResult(TokenFor(identity.Id, tenantId));
    }

    public Task<string> AcquireInteractivelyAsync(
        Identity identity,
        string tenantId,
        IReadOnlyList<string> scopes,
        string? claims,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(identity);
        lock (_gate)
        {
            _interactiveCalls.Add(new InteractiveCall(tenantId, scopes, claims));
        }

        if (InteractiveError is { } error)
        {
            return Task.FromException<string>(error);
        }

        SilentError = null;
        return Task.FromResult(TokenFor(identity.Id, tenantId));
    }

    private string TokenFor(string identityId, string tenantId)
    {
        lock (_gate)
        {
            return _tokens.TryGetValue((identityId, tenantId), out var token) ? token : $"token-{tenantId}";
        }
    }
}
