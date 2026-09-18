import Foundation
import Testing
@testable import ElevateCore

/// The propagation probes: what each provider answers when a role is active but not yet usable.
@Suite struct EffectiveAccessTests {
    private static let globalAdmin = "62e90394-69f5-4237-9190-012177145e10"
    private let identity = Identity(id: "id1", upn: "u@contoso.com", displayName: "U", homeTenantId: "t-home")

    private func assignment(_ scope: RoleScope) -> ActiveAssignment {
        ActiveAssignment(roleKey: RoleKey(identityId: "id1", tenantId: "t1", scope: scope),
                         assignmentId: "a1", startDateTime: .now, endDateTime: nil, status: .active)
    }

    // MARK: Entra directory roles

    @Test func entraConfirmsWhenAFreshTokenCarriesTheRole() async throws {
        let tokens = FakeTokenProvider()
        await tokens.setRefreshedToken(Jwt.withRoles(Self.globalAdmin))
        let provider = EntraDirectoryProvider(http: StubHTTPClient(), tokens: tokens)

        let access = try await provider.effectiveAccess(
            assignment(.entraDirectory(roleDefinitionId: Self.globalAdmin, directoryScopeId: "/")), identity: identity)

        #expect(access == .confirmed)
        #expect(await tokens.forcedCalls == ["t1"])
    }

    @Test func entraIsNotYetWhileTheRoleIsMissingFromTheToken() async throws {
        let tokens = FakeTokenProvider()
        await tokens.setRefreshedToken(Jwt.withRoles("some-other-role"))
        let provider = EntraDirectoryProvider(http: StubHTTPClient(), tokens: tokens)

        let access = try await provider.effectiveAccess(
            assignment(.entraDirectory(roleDefinitionId: Self.globalAdmin, directoryScopeId: "/")), identity: identity)

        #expect(access == .notYet)
    }

    @Test func entraMatchesTheRoleIdWhateverItsCase() async throws {
        let tokens = FakeTokenProvider()
        await tokens.setRefreshedToken(Jwt.withRoles(Self.globalAdmin.uppercased()))
        let provider = EntraDirectoryProvider(http: StubHTTPClient(), tokens: tokens)

        let access = try await provider.effectiveAccess(
            assignment(.entraDirectory(roleDefinitionId: Self.globalAdmin, directoryScopeId: "/")), identity: identity)

        #expect(access == .confirmed)
    }

    @Test func entraCannotTellForARoleScopedToAnAdministrativeUnit() async throws {
        // wids carries tenant-wide roles only, so the role's absence would prove nothing.
        let tokens = FakeTokenProvider()
        await tokens.setRefreshedToken(Jwt.withRoles())
        let provider = EntraDirectoryProvider(http: StubHTTPClient(), tokens: tokens)

        let access = try await provider.effectiveAccess(
            assignment(.entraDirectory(roleDefinitionId: Self.globalAdmin, directoryScopeId: "/administrativeUnits/au1")),
            identity: identity)

        guard case .unknown = access else { Issue.record("expected unknown, got \(access)"); return }
        #expect(await tokens.forcedCalls.isEmpty)
    }

    @Test func entraCannotTellWhenTheProviderWillNotMintAFreshToken() async throws {
        let tokens = FakeTokenProvider(canForceRefresh: false)
        await tokens.setRefreshedToken(Jwt.withRoles(Self.globalAdmin))
        let provider = EntraDirectoryProvider(http: StubHTTPClient(), tokens: tokens)

        let access = try await provider.effectiveAccess(
            assignment(.entraDirectory(roleDefinitionId: Self.globalAdmin, directoryScopeId: "/")), identity: identity)

        guard case let .unknown(reason) = access else { Issue.record("expected unknown, got \(access)"); return }
        #expect(reason.contains("fresh token"))
    }

    @Test func entraCannotTellWhenTheTokenIsOpaque() async throws {
        let tokens = FakeTokenProvider()
        await tokens.setRefreshedToken("opaque-token")
        let provider = EntraDirectoryProvider(http: StubHTTPClient(), tokens: tokens)

        let access = try await provider.effectiveAccess(
            assignment(.entraDirectory(roleDefinitionId: Self.globalAdmin, directoryScopeId: "/")), identity: identity)

        guard case .unknown = access else { Issue.record("expected unknown, got \(access)"); return }
    }

    // MARK: Group membership

    private func checkMemberObjects(_ ids: [String]) -> Data {
        try! JSONSerialization.data(withJSONObject: ["value": ids])
    }

    @Test func groupConfirmsFromTheTokensGroupsClaimWithoutCallingGraph() async throws {
        let tokens = FakeTokenProvider()
        await tokens.setRefreshedToken(Jwt.withGroups("grp-ops"))
        let http = StubHTTPClient()
        let provider = GroupProvider(http: http, tokens: tokens)

        let access = try await provider.effectiveAccess(
            assignment(.group(groupId: "grp-ops", accessId: .member)), identity: identity)

        #expect(access == .confirmed)
        #expect(await http.requests.isEmpty)
    }

    @Test func groupIsNotYetWhenTheGroupsClaimIsMissingTheGroup() async throws {
        let tokens = FakeTokenProvider()
        await tokens.setRefreshedToken(Jwt.withGroups("grp-other"))
        let provider = GroupProvider(http: StubHTTPClient(), tokens: tokens)

        let access = try await provider.effectiveAccess(
            assignment(.group(groupId: "grp-ops", accessId: .member)), identity: identity)

        #expect(access == .notYet)
    }

    @Test func groupFallsBackToGraphWhenTheTokenCarriesNoGroupClaims() async throws {
        // The usual case: a registration that does not emit group claims.
        let tokens = FakeTokenProvider()
        await tokens.setRefreshedToken(Jwt.with(["oid": "caller"]))
        let http = StubHTTPClient()
        await http.on("POST", "checkMemberObjects", body: checkMemberObjects(["grp-ops"]))
        let provider = GroupProvider(http: http, tokens: tokens)

        let access = try await provider.effectiveAccess(
            assignment(.group(groupId: "grp-ops", accessId: .member)), identity: identity)

        #expect(access == .confirmed)
        #expect(await http.requests.count == 1)
    }

    @Test func groupIsNotYetWhenGraphDoesNotReportTheMembership() async throws {
        let tokens = FakeTokenProvider()
        await tokens.setRefreshedToken(Jwt.with(["oid": "caller"]))
        let http = StubHTTPClient()
        await http.on("POST", "checkMemberObjects", body: checkMemberObjects([]))
        let provider = GroupProvider(http: http, tokens: tokens)

        let access = try await provider.effectiveAccess(
            assignment(.group(groupId: "grp-ops", accessId: .member)), identity: identity)

        #expect(access == .notYet)
    }

    @Test func groupCannotTellForOwnership() async throws {
        let provider = GroupProvider(http: StubHTTPClient(), tokens: FakeTokenProvider())

        let access = try await provider.effectiveAccess(
            assignment(.group(groupId: "grp-ops", accessId: .owner)), identity: identity)

        guard case let .unknown(reason) = access else { Issue.record("expected unknown, got \(access)"); return }
        #expect(reason.contains("ownership"))
    }

    @Test func groupCannotTellWhenGraphRefuses() async throws {
        let tokens = FakeTokenProvider()
        await tokens.setRefreshedToken(Jwt.with(["oid": "caller"]))
        let http = StubHTTPClient()
        await http.on("POST", "checkMemberObjects", status: 403)
        let provider = GroupProvider(http: http, tokens: tokens)

        let access = try await provider.effectiveAccess(
            assignment(.group(groupId: "grp-ops", accessId: .member)), identity: identity)

        guard case .unknown = access else { Issue.record("expected unknown, got \(access)"); return }
    }

    // MARK: Azure resource roles

    private static let reader = "/subscriptions/sub1/providers/Microsoft.Authorization/roleDefinitions/rd1"

    private func definition(_ actions: [String]) -> Data {
        try! JSONSerialization.data(withJSONObject: [
            "id": Self.reader,
            "properties": ["roleName": "Reader", "permissions": [["actions": actions, "notActions": []]]],
        ])
    }

    private func permissions(_ actions: [String]) -> Data {
        try! JSONSerialization.data(withJSONObject: ["value": [["actions": actions, "notActions": []]]])
    }

    @Test func azureConfirmsWhenArmAlreadyGrantsWhatTheRoleGrants() async throws {
        let http = StubHTTPClient()
        await http.on("GET", "roleDefinitions/rd1", body: definition(["Microsoft.Compute/virtualMachines/read"]))
        await http.on("GET", "Microsoft.Authorization/permissions", body: permissions(["Microsoft.Compute/*"]))
        let provider = AzureResourceProvider(http: http, tokens: FakeTokenProvider())

        let access = try await provider.effectiveAccess(
            assignment(.azureResource(scope: "/subscriptions/sub1", roleDefinitionId: Self.reader)), identity: identity)

        #expect(access == .confirmed)
    }

    @Test func azureIsNotYetWhileArmStillGrantsLess() async throws {
        let http = StubHTTPClient()
        await http.on("GET", "roleDefinitions/rd1", body: definition(["Microsoft.Compute/virtualMachines/write"]))
        await http.on("GET", "Microsoft.Authorization/permissions", body: permissions(["*/read"]))
        let provider = AzureResourceProvider(http: http, tokens: FakeTokenProvider())

        let access = try await provider.effectiveAccess(
            assignment(.azureResource(scope: "/subscriptions/sub1", roleDefinitionId: Self.reader)), identity: identity)

        #expect(access == .notYet)
    }

    @Test func azureReadsTheProbeAtTheActivatedScope() async throws {
        let http = StubHTTPClient()
        await http.on("GET", "roleDefinitions/rd1", body: definition(["*/read"]))
        await http.on("GET", "Microsoft.Authorization/permissions", body: permissions(["*"]))
        let provider = AzureResourceProvider(http: http, tokens: FakeTokenProvider())

        _ = try await provider.effectiveAccess(
            assignment(.azureResource(scope: "/subscriptions/sub1/resourceGroups/rg1", roleDefinitionId: Self.reader)),
            identity: identity)

        let asked = await http.requests(matching: "resourceGroups/rg1/providers/Microsoft.Authorization/permissions")
        #expect(!asked.isEmpty)
    }

    @Test func azureReadsARefusalAtTheScopeAsPropagationRatherThanAnError() async throws {
        // Until the assignment reaches the scope, ARM will not even let the caller read its own permissions.
        let http = StubHTTPClient()
        await http.on("GET", "roleDefinitions/rd1", body: definition(["*/read"]))
        await http.on("GET", "Microsoft.Authorization/permissions", status: 403)
        let provider = AzureResourceProvider(http: http, tokens: FakeTokenProvider())

        let access = try await provider.effectiveAccess(
            assignment(.azureResource(scope: "/subscriptions/sub1", roleDefinitionId: Self.reader)), identity: identity)

        #expect(access == .notYet)
    }

    @Test func azureCannotTellWhenTheRoleDefinitionSaysNothing() async throws {
        let http = StubHTTPClient()
        await http.on("GET", "roleDefinitions/rd1", body: definition([]))
        await http.on("GET", "Microsoft.Authorization/permissions", body: permissions(["*"]))
        let provider = AzureResourceProvider(http: http, tokens: FakeTokenProvider())

        let access = try await provider.effectiveAccess(
            assignment(.azureResource(scope: "/subscriptions/sub1", roleDefinitionId: Self.reader)), identity: identity)

        guard case .unknown = access else { Issue.record("expected unknown, got \(access)"); return }
    }

    // MARK: Claims

    @Test func directoryRolesAreNilForAnOpaqueToken() {
        #expect(AccessTokenClaims.directoryRoles("not-a-jwt") == nil)
    }

    @Test func directoryRolesAreNilWhenTheClaimIsAbsent() {
        #expect(AccessTokenClaims.directoryRoles(Jwt.with(["oid": "x"])) == nil)
    }

    @Test func groupMembershipsAreLowerCasedSoTheComparisonDoesNotDependOnCasing() {
        #expect(AccessTokenClaims.groupMemberships(Jwt.withGroups("GRP-Ops")) == ["grp-ops"])
    }

    @Test func arrayClaimsIgnoreNonStringMembers() {
        #expect(AccessTokenClaims.directoryRoles(Jwt.with(["wids": ["a", 3, true]])) == ["a"])
    }

    @Test func propagationHintsTakeTheLongestDeadlineAcrossKinds() {
        #expect(PropagationHints.deadline([RoleScopeKind.entraDirectory, .azureResource])
            == PropagationHints.deadline(.azureResource))
    }

    @Test func propagationHintsFallBackWhenThereAreNoKinds() {
        #expect(PropagationHints.deadline([RoleScopeKind]()) == PropagationHints.deadline(.entraDirectory))
    }
}
