import Testing
@testable import ElevateCore

/// Port of the C# `ArmScopeTests`.
struct ArmScopeTests {
    let mg = "/providers/Microsoft.Management/managementGroups/platform"
    let sub = "/subscriptions/11111111-1111-1111-1111-111111111111"
    var rg: String { sub + "/resourceGroups/prod-rg" }
    var vm: String { rg + "/providers/Microsoft.Compute/virtualMachines/web01" }

    @Test func segmentsReadsSubscriptionResourceGroupAndResource() {
        let segments = ArmScope.segments(vm)
        #expect(segments.map(\.kind) == [.subscription, .resourceGroup, .resource])
        #expect(segments.map(\.name) == ["11111111-1111-1111-1111-111111111111", "prod-rg", "web01"])
        #expect(segments.map(\.scope) == [sub, rg, vm])
    }

    @Test func segmentsReadsAManagementGroupAsOneStep() {
        let segments = ArmScope.segments(mg)
        #expect(segments.count == 1)
        #expect(segments.first == ArmScopeSegment(kind: .managementGroup, name: "platform", scope: mg))
    }

    @Test func segmentsReadsAResourceDirectlyUnderASubscription() {
        let segments = ArmScope.segments(sub + "/providers/Microsoft.Network/virtualNetworks/hub")
        #expect(segments.map(\.kind) == [.subscription, .resource])
    }

    @Test func segmentsReadsChildResourcesAsStepsOfTheirOwn() {
        let scope = vm + "/extensions/monitor"
        let segments = ArmScope.segments(scope)
        #expect(segments.map(\.name) == ["11111111-1111-1111-1111-111111111111", "prod-rg", "web01", "monitor"])
        #expect(segments.last?.scope == scope)
    }

    @Test func segmentsIsEmptyForTheRoot() {
        for scope: String? in [nil, "", "/", "   "] {
            #expect(ArmScope.segments(scope).isEmpty, "scope \(scope ?? "nil")")
        }
    }

    @Test func segmentsKeepsAnUnfamiliarPathAsOneStepRatherThanDroppingIt() {
        let segments = ArmScope.segments("/tenants/contoso/widgets/one")
        #expect(segments.count == 1)
        #expect(segments.first?.kind == .unknown)
    }

    @Test func segmentsIgnoresCaseInThePathKeywords() {
        #expect(ArmScope.segments("/SUBSCRIPTIONS/abc/RESOURCEGROUPS/rg").map(\.kind) == [.subscription, .resourceGroup])
    }

    @Test func isAtOrUnderComparesStepByStep() {
        #expect(ArmScope.isAtOrUnder(vm, ancestor: sub))
        #expect(ArmScope.isAtOrUnder(vm, ancestor: rg))
        #expect(ArmScope.isAtOrUnder(vm, ancestor: vm))
        #expect(ArmScope.isAtOrUnder(vm, ancestor: "/"))
        #expect(!ArmScope.isAtOrUnder(rg, ancestor: vm))
        #expect(!ArmScope.isAtOrUnder(sub, ancestor: rg))
        #expect(!ArmScope.isAtOrUnder(mg, ancestor: sub))
    }

    @Test func isAtOrUnderDoesNotTreatANamePrefixAsAnAncestor() {
        // "/subscriptions/abc" must not swallow "/subscriptions/abcdef".
        #expect(!ArmScope.isAtOrUnder("/subscriptions/abcdef", ancestor: "/subscriptions/abc"))
    }

    @Test func isAtOrUnderIgnoresCase() {
        #expect(ArmScope.isAtOrUnder(vm, ancestor: rg.uppercased()))
    }

    @Test func hasSegmentNamedMatchesAStepsNameOrItsWholeScope() {
        #expect(ArmScope.hasSegmentNamed(vm, name: "prod-rg"))
        #expect(ArmScope.hasSegmentNamed(vm, name: "PROD-RG"))
        #expect(ArmScope.hasSegmentNamed(vm, name: "web01"))
        #expect(ArmScope.hasSegmentNamed(vm, name: rg))
        #expect(ArmScope.hasSegmentNamed(vm, name: rg + "/"))
        #expect(ArmScope.hasSegmentNamed(mg, name: "platform"))
        // A partial name is not a step.
        #expect(!ArmScope.hasSegmentNamed(vm, name: "prod"))
        #expect(!ArmScope.hasSegmentNamed(vm, name: ""))
    }

    @Test func matchesPatternLetsStarCrossSlashes() {
        #expect(ArmScope.matches(pattern: "/subscriptions/*", text: sub))
        #expect(ArmScope.matches(pattern: "/subscriptions/*", text: rg))
        #expect(ArmScope.matches(pattern: "/subscriptions/*", text: vm))
        #expect(!ArmScope.matches(pattern: "/subscriptions/*", text: mg))
        #expect(ArmScope.matches(pattern: "*/resourceGroups/prod-*", text: vm))
        #expect(!ArmScope.matches(pattern: "*/resourceGroups/prod-*", text: sub))
        #expect(ArmScope.matches(pattern: "*managementGroups*", text: mg))
    }

    @Test func matchesPatternMatchesTheWholeStringSoAPrefixAloneIsNotEnough() {
        #expect(!ArmScope.matches(pattern: "/subscriptions", text: sub))
    }

    @Test func looksLikePatternIsAboutTheStar() {
        #expect(ArmScope.looksLikePattern("prod*"))
        #expect(ArmScope.looksLikePattern("*"))
        #expect(!ArmScope.looksLikePattern("prod"))
        #expect(!ArmScope.looksLikePattern(nil))
    }

    @Test func displayNameDropsTheKindTheCaptionAppends() {
        #expect(ArmScope.displayName(detail: "Pay-As-You-Go · subscription") == "Pay-As-You-Go")
        #expect(ArmScope.displayName(detail: "prod-rg · resource group") == "prod-rg")
        #expect(ArmScope.displayName(detail: "just a name") == "just a name")
        #expect(ArmScope.displayName(detail: "") == nil)
        #expect(ArmScope.displayName(detail: nil) == nil)
    }

    @Test func tailKeepsTheLeafAndDropsWhatIsAbove() {
        #expect(ArmScope.tail(vm) == "…/prod-rg/web01")
        #expect(ArmScope.tail(vm, steps: 1) == "…/web01")
        #expect(ArmScope.tail(sub) == sub)
        #expect(ArmScope.tail("/") == "/")
    }
}
