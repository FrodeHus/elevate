using Elevate.Core.Support;
using FluentAssertions;

namespace Elevate.Core.Tests;

public class ArmScopeTests
{
    private const string Mg = "/providers/Microsoft.Management/managementGroups/platform";
    private const string Sub = "/subscriptions/11111111-1111-1111-1111-111111111111";
    private const string Rg = Sub + "/resourceGroups/prod-rg";
    private const string Vm = Rg + "/providers/Microsoft.Compute/virtualMachines/web01";

    [Fact]
    public void Segments_ReadsSubscriptionResourceGroupAndResource()
    {
        var segments = ArmScope.Segments(Vm);
        segments.Select(s => s.Kind).Should().Equal(
            ArmScopeKind.Subscription, ArmScopeKind.ResourceGroup, ArmScopeKind.Resource);
        segments.Select(s => s.Name).Should().Equal("11111111-1111-1111-1111-111111111111", "prod-rg", "web01");
        segments.Select(s => s.Scope).Should().Equal(Sub, Rg, Vm);
    }

    [Fact]
    public void Segments_ReadsAManagementGroupAsOneStep()
    {
        var segments = ArmScope.Segments(Mg);
        segments.Should().ContainSingle();
        segments[0].Should().Be(new ArmScopeSegment(ArmScopeKind.ManagementGroup, "platform", Mg));
    }

    [Fact]
    public void Segments_ReadsAResourceDirectlyUnderASubscription()
    {
        var scope = Sub + "/providers/Microsoft.Network/virtualNetworks/hub";
        ArmScope.Segments(scope).Select(s => s.Kind).Should().Equal(ArmScopeKind.Subscription, ArmScopeKind.Resource);
    }

    [Fact]
    public void Segments_ReadsChildResourcesAsStepsOfTheirOwn()
    {
        var scope = Vm + "/extensions/monitor";
        var segments = ArmScope.Segments(scope);
        segments.Select(s => s.Name).Should().Equal("11111111-1111-1111-1111-111111111111", "prod-rg", "web01", "monitor");
        segments[^1].Scope.Should().Be(scope);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("/")]
    public void Segments_IsEmptyForTheRoot(string? scope) => ArmScope.Segments(scope).Should().BeEmpty();

    [Fact]
    public void Segments_KeepsAnUnfamiliarPathAsOneStepRatherThanDroppingIt()
    {
        var segments = ArmScope.Segments("/tenants/contoso/widgets/one");
        segments.Should().ContainSingle();
        segments[0].Kind.Should().Be(ArmScopeKind.Unknown);
    }

    [Fact]
    public void Segments_IgnoresCaseInThePathKeywords()
    {
        var segments = ArmScope.Segments("/SUBSCRIPTIONS/abc/RESOURCEGROUPS/rg");
        segments.Select(s => s.Kind).Should().Equal(ArmScopeKind.Subscription, ArmScopeKind.ResourceGroup);
    }

    [Theory]
    [InlineData(Vm, Sub, true)]
    [InlineData(Vm, Rg, true)]
    [InlineData(Vm, Vm, true)]
    [InlineData(Rg, Vm, false)]
    [InlineData(Sub, Rg, false)]
    [InlineData(Mg, Sub, false)]
    [InlineData(Vm, "/", true)]
    public void IsAtOrUnder_ComparesStepByStep(string scope, string ancestor, bool expected) =>
        ArmScope.IsAtOrUnder(scope, ancestor).Should().Be(expected);

    [Fact]
    public void IsAtOrUnder_DoesNotTreatANamePrefixAsAnAncestor()
    {
        // "/subscriptions/abc" must not swallow "/subscriptions/abcdef".
        ArmScope.IsAtOrUnder("/subscriptions/abcdef", "/subscriptions/abc").Should().BeFalse();
    }

    [Fact]
    public void IsAtOrUnder_IgnoresCase() =>
        ArmScope.IsAtOrUnder(Vm, Rg.ToUpperInvariant()).Should().BeTrue();

    [Theory]
    [InlineData(Vm, "prod-rg", true)]
    [InlineData(Vm, "PROD-RG", true)]
    [InlineData(Vm, "web01", true)]
    [InlineData(Vm, "prod", false)]
    [InlineData(Vm, Rg, true)]
    [InlineData(Vm, Rg + "/", true)]
    [InlineData(Mg, "platform", true)]
    [InlineData(Vm, "", false)]
    public void HasSegmentNamed_MatchesAStepsNameOrItsWholeScope(string scope, string name, bool expected) =>
        ArmScope.HasSegmentNamed(scope, name).Should().Be(expected);

    [Theory]
    [InlineData("/subscriptions/*", Sub, true)]
    [InlineData("/subscriptions/*", Rg, true)]
    [InlineData("/subscriptions/*", Vm, true)]
    [InlineData("/subscriptions/*", Mg, false)]
    [InlineData("*/resourceGroups/prod-*", Vm, true)]
    [InlineData("*/resourceGroups/prod-*", Sub, false)]
    [InlineData("*managementGroups*", Mg, true)]
    public void MatchesPattern_LetsStarCrossSlashes(string pattern, string scope, bool expected) =>
        ArmScope.MatchesPattern(pattern, scope).Should().Be(expected);

    [Fact]
    public void MatchesPattern_MatchesTheWholeStringSoAPrefixAloneIsNotEnough() =>
        ArmScope.MatchesPattern("/subscriptions", Sub).Should().BeFalse();

    [Theory]
    [InlineData("prod*", true)]
    [InlineData("*", true)]
    [InlineData("prod", false)]
    [InlineData(null, false)]
    public void LooksLikePattern_IsAboutTheStar(string? term, bool expected) =>
        ArmScope.LooksLikePattern(term).Should().Be(expected);

    [Theory]
    [InlineData("Pay-As-You-Go · subscription", "Pay-As-You-Go")]
    [InlineData("prod-rg · resource group", "prod-rg")]
    [InlineData("just a name", "just a name")]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void DisplayName_DropsTheKindTheCaptionAppends(string? detail, string? expected) =>
        ArmScope.DisplayName(detail).Should().Be(expected);

    [Fact]
    public void Tail_KeepsTheLeafAndDropsWhatIsAbove()
    {
        ArmScope.Tail(Vm).Should().Be("…/prod-rg/web01");
        ArmScope.Tail(Vm, 1).Should().Be("…/web01");
        ArmScope.Tail(Sub).Should().Be(Sub);
        ArmScope.Tail("/").Should().Be("/");
    }
}
