using Elevate.Core.Managed;
using Elevate.Core.Models;
using FluentAssertions;

namespace Elevate.Core.Tests.Managed;

public class ManagedPolicyTests
{
    private static ManagedConfiguration Allowing(IReadOnlySet<SignInMethodKind>? kinds)
        => new() { AllowedSignInMethods = kinds };

    [Fact]
    public void NoAllowListAllowsEveryMethod()
    {
        var config = Allowing(null);

        foreach (var method in SignInMethod.BuiltIn.Append(SignInMethod.Custom("abc")))
        {
            ManagedPolicy.IsAllowed(method, config).Should().BeTrue();
        }
    }

    [Fact]
    public void AllowListRefusesMethodsOutsideIt()
    {
        var config = Allowing(new HashSet<SignInMethodKind> { SignInMethodKind.OwnApp });

        ManagedPolicy.IsAllowed(SignInMethod.OwnApp, config).Should().BeTrue();
        ManagedPolicy.IsAllowed(SignInMethod.AzureCLI, config).Should().BeFalse();
        ManagedPolicy.IsAllowed(SignInMethod.AzurePowerShell, config).Should().BeFalse();
        ManagedPolicy.IsAllowed(SignInMethod.Custom("abc"), config).Should().BeFalse();
    }

    [Fact]
    public void CustomInTheListAllowsAnyClientId()
    {
        var config = Allowing(new HashSet<SignInMethodKind> { SignInMethodKind.Custom });

        ManagedPolicy.IsAllowed(SignInMethod.Custom("abc"), config).Should().BeTrue();
        ManagedPolicy.IsAllowed(SignInMethod.Custom("11111111-2222-3333-4444-555555555555"), config).Should().BeTrue();
        ManagedPolicy.IsAllowed(SignInMethod.OwnApp, config).Should().BeFalse();
    }

    [Fact]
    public void EmptyAllowListAllowsNothing()
        => ManagedPolicy.IsAllowed(SignInMethod.OwnApp, Allowing(new HashSet<SignInMethodKind>())).Should().BeFalse();

    [Fact]
    public void NoTenantAllowListAllowsEveryTenant()
        => ManagedPolicy.IsTenantAllowed("11111111-2222-3333-4444-555555555555", null).Should().BeTrue();

    [Fact]
    public void TenantMatchIsCaseInsensitive()
    {
        var allowed = new HashSet<string> { "11111111-2222-3333-4444-AAAAAAAAAAAA" };

        ManagedPolicy.IsTenantAllowed("11111111-2222-3333-4444-aaaaaaaaaaaa", allowed).Should().BeTrue();
        ManagedPolicy.IsTenantAllowed("11111111-2222-3333-4444-AAAAAAAAAAAA", allowed).Should().BeTrue();
    }

    [Fact]
    public void TenantOutsideTheListIsRefused()
    {
        ManagedPolicy.IsTenantAllowed("t-other", new HashSet<string> { "t-home" }).Should().BeFalse();
        ManagedPolicy.IsTenantAllowed("t-home", new HashSet<string>()).Should().BeFalse();
    }
}
