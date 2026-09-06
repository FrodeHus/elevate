using Elevate.Core.Auth;
using Elevate.Core.Models;
using Elevate.Core.Networking;
using Elevate.Core.Providers;

namespace Elevate.Core.Coordination;

/// <summary>What became of one activation. Port of Swift's <c>ActivationOutcome.Result</c>.</summary>
public abstract record ActivationResult
{
    private ActivationResult()
    {
    }

    public sealed record Activated(ActiveAssignment Assignment) : ActivationResult;

    public sealed record PendingApproval(ActiveAssignment Assignment) : ActivationResult;

    public sealed record Scheduled(ActiveAssignment Assignment) : ActivationResult;

    public sealed record Failed(PimException Error) : ActivationResult;
}

public sealed record ActivationOutcome(RoleKey RoleKey, ActivationResult Result);

/// <summary>
/// Runs activations grouped by identity+tenant: groups in parallel, requests within a group in sequence.
/// Handles <see cref="PimErrorKind.InteractionRequired"/> and <see cref="PimErrorKind.ClaimsChallenge"/>
/// with one interactive prompt and one retry per request.
/// </summary>
public sealed class ActivationCoordinator
{
    private readonly Dictionary<RoleScopeKind, IPimProvider> _providers;
    private readonly ITokenProvider _tokens;

    public ActivationCoordinator(IEnumerable<IPimProvider> providers, ITokenProvider tokens)
    {
        ArgumentNullException.ThrowIfNull(providers);
        _providers = providers.ToDictionary(p => p.Kind);
        _tokens = tokens;
    }

    public IPimProvider? Provider(RoleScopeKind kind) => _providers.GetValueOrDefault(kind);

    public async Task<IReadOnlyList<ActivationOutcome>> ActivateAsync(
        IEnumerable<ActivationRequest> requests,
        IEnumerable<Identity> identities,
        Action<ActivationOutcome>? onProgress = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(requests);
        ArgumentNullException.ThrowIfNull(identities);

        var identityById = identities.ToDictionary(i => i.Id);
        var groups = requests.GroupBy(r => r.RoleKey.TenantKey).ToList();

        var batches = await Task.WhenAll(groups.Select(group => Task.Run(async () =>
        {
            var outcomes = new List<ActivationOutcome>();
            foreach (var request in group)
            {
                var outcome = await ActivateOneAsync(request, identityById.GetValueOrDefault(group.Key.IdentityId), ct)
                    .ConfigureAwait(false);
                onProgress?.Invoke(outcome);
                outcomes.Add(outcome);
            }

            return outcomes;
        }, ct))).ConfigureAwait(false);

        return [.. batches.SelectMany(b => b)];
    }

    private async Task<ActivationOutcome> ActivateOneAsync(ActivationRequest request, Identity? identity, CancellationToken ct)
    {
        if (identity is null)
        {
            return Failure(request.RoleKey, new PimException(
                PimErrorKind.Unexpected, $"Unknown identity {request.RoleKey.IdentityId}"));
        }

        if (Provider(request.RoleKey.Scope.Kind) is not { } provider)
        {
            return Failure(request.RoleKey, new PimException(
                PimErrorKind.Unexpected, $"No provider for {CaseName(request.RoleKey.Scope.Kind)}", 501));
        }

        // A role can demand a fresh MFA or a Conditional Access authentication context. The
        // service answers with a 400 and no claims header, so we choose the claims: the policy's
        // authentication context when it has one, otherwise a plain MFA re-verification.
        var fallbackClaims = request.AuthenticationContext is { } context
            ? ClaimsChallenge.AuthenticationContext(context)
            : ClaimsChallenge.MultiFactor;

        try
        {
            var assignment = await InteractionRetry.RunAsync(
                _tokens, identity, request.RoleKey.TenantId, provider.Scopes,
                () => provider.ActivateAsync(request, identity, ct), fallbackClaims, ct).ConfigureAwait(false);

            return new ActivationOutcome(request.RoleKey, assignment.Status.Kind switch
            {
                AssignmentStatusKind.PendingApproval => new ActivationResult.PendingApproval(assignment),
                AssignmentStatusKind.Scheduled => new ActivationResult.Scheduled(assignment),
                AssignmentStatusKind.Failed => new ActivationResult.Failed(
                    new PimException(PimErrorKind.Unexpected, assignment.Status.FailureReason)),
                _ => new ActivationResult.Activated(assignment),
            });
        }
        catch (PimException e) when (e.Kind is PimErrorKind.InteractionRequired or PimErrorKind.ClaimsChallenge)
        {
            // The retry already sent the user through the browser once; a second refusal means
            // that sign-in did not satisfy the role's requirement.
            var requirement = request.AuthenticationContext is { } acrs
                ? $"the Conditional Access authentication context \"{acrs}\""
                : "multi-factor authentication";
            return Failure(request.RoleKey, new PimException(
                PimErrorKind.PolicyViolation,
                $"This role requires {requirement} and the sign-in did not satisfy it. "
                + "Try again and complete the verification in the browser."));
        }
        catch (PimException e)
        {
            return Failure(request.RoleKey, e);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return Failure(request.RoleKey, new PimException(PimErrorKind.Network, e.Message));
        }
    }

    public async Task DeactivateAsync(ActiveAssignment assignment, Identity identity, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(assignment);
        var provider = RequireProvider(assignment.RoleKey.Scope.Kind);
        await InteractionRetry.RunAsync(
            _tokens, identity, assignment.RoleKey.TenantId, provider.Scopes,
            async () =>
            {
                await provider.DeactivateAsync(assignment, identity, ct).ConfigureAwait(false);
                return true;
            },
            ct: ct).ConfigureAwait(false);
    }

    public async Task CancelPendingRequestAsync(ActiveAssignment assignment, Identity identity, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(assignment);
        var provider = RequireProvider(assignment.RoleKey.Scope.Kind);
        await InteractionRetry.RunAsync(
            _tokens, identity, assignment.RoleKey.TenantId, provider.Scopes,
            async () =>
            {
                await provider.CancelPendingRequestAsync(assignment, identity, ct).ConfigureAwait(false);
                return true;
            },
            ct: ct).ConfigureAwait(false);
    }

    private IPimProvider RequireProvider(RoleScopeKind kind) =>
        Provider(kind) ?? throw new PimException(PimErrorKind.Unexpected, $"No provider for {CaseName(kind)}", 501);

    private static ActivationOutcome Failure(RoleKey key, PimException error) =>
        new(key, new ActivationResult.Failed(error));

    /// <summary>The Swift case name of a scope kind, so messages read the same on both platforms.</summary>
    private static string CaseName(RoleScopeKind kind)
    {
        var name = kind.ToString();
        return char.ToLowerInvariant(name[0]) + name[1..];
    }
}
