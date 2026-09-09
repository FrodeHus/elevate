import Foundation
import Testing
import ElevateCore
@testable import Elevate

/// Profiles an organization publishes: how they join the user's own, what the app refuses to
/// change about them, and how the published document is fetched, cached and merged.
@Suite @MainActor
struct AppModelManagedProfilesTests {
    /// A GUID tenant needs no lookup at all, so these tests work offline.
    static let tenantId = "aaaaaaaa-0000-0000-0000-000000000001"
    static var tenantKey: TenantKey { TenantKey(identityId: Sample.identityId, tenantId: tenantId) }
    static var entraKey: RoleKey { Sample.key(.entraDirectory(roleDefinitionId: "role-def", directoryScopeId: "/"), tenantId: tenantId) }
    /// The key the resolver builds for a group nobody is eligible for: a placeholder.
    static var missingGroupKey: RoleKey { Sample.key(.group(groupId: "SRE on-call", accessId: .member), tenantId: tenantId) }
    static var managedId: UUID { ManagedProfileSet.profileId(slug: "prod-incident") }

    /// One published profile naming an eligible Entra role and a group nobody has.
    static func document(tenant: String = tenantId, name: String = "Prod incident") -> String {
        """
        {"version": 1, "profiles": [
          {"id": "prod-incident", "name": "\(name)", "reason": "Incident response", "pinned": true,
           "roles": [
             {"kind": "entraDirectory", "tenant": "\(tenant)", "role": "Global Reader", "duration": "PT2H"},
             {"kind": "group", "tenant": "\(tenant)", "group": "SRE on-call", "access": "member"}
           ]}
        ]}
        """
    }

    private static func managed(_ values: [String: Any]) -> ManagedConfiguration {
        ManagedConfiguration.load(from: DictionaryManagedSource(values))
    }

    /// A model with one account, one tenant and one eligible Entra role in it.
    private func loadedModel(document: String? = Self.document(), url: String? = nil,
                             http: StubHTTPClient = StubHTTPClient(), online: Bool = false,
                             directory: URL? = nil) async -> AppModel {
        var state = AppState()
        state.identities = [Sample.identity()]
        state.tenants = [Sample.tenant(tenantId: Self.tenantId)]
        var values: [String: Any] = [:]
        if let document { values["ManagedProfiles"] = document }
        if let url { values["ManagedProfilesUrl"] = url }
        let model = await makeModel(state: state, http: http, online: online,
                                    managed: Self.managed(values), directory: directory)
        model.roles[Self.tenantKey] = [Sample.role(Self.entraKey, name: "Global Reader")]
        return model
    }

    @Test func inlineProfilesJoinTheUsersOwnAndAreReadOnly() async {
        let model = await loadedModel()
        defer { cleanup(model) }
        let mine = model.saveProfile(name: "Ops", keys: [Self.entraKey])!

        #expect(model.profiles.map(\.name) == ["Ops", "Prod incident"])
        #expect(model.profiles.map(\.source) == [.user, .managed])
        let managed = model.profile(id: Self.managedId)
        #expect(managed?.name == "Prod incident")
        #expect(managed?.lastJustification == "Incident response")
        #expect(model.isManagedProfile(Self.managedId))
        #expect(!model.isManagedProfile(mine.id))

        // The managed pin comes first and does not count against the limit.
        for i in 0..<ProfilePins.limit {
            let p = model.saveProfile(name: "P\(i)", keys: [Self.entraKey])!
            #expect(model.setPinned(id: p.id, true))
        }
        #expect(model.pinnedProfiles.map(\.name) == ["Prod incident", "P0", "P1", "P2", "P3"])
        #expect(model.pinnedProfiles.first?.id == Self.managedId)

        // Every mutator leaves a managed profile — and the saved state — exactly as it was.
        model.renameProfile(id: Self.managedId, name: "Mine now")
        model.updateProfile(id: Self.managedId, keys: [])
        model.addProfileEntries(id: Self.managedId, keys: [Sample.azureKey])
        model.removeProfileEntry(id: Self.managedId, key: Self.entraKey)
        model.setProfileEntryDuration(id: Self.managedId, key: Self.entraKey, duration: .seconds(60))
        #expect(!model.setPinned(id: Self.managedId, false))
        model.deleteProfile(id: Self.managedId)
        #expect(model.profile(id: Self.managedId)?.name == "Prod incident")
        #expect(model.profile(id: Self.managedId)?.entries.count == 2)
        #expect(model.profile(id: Self.managedId)?.pinned == true)
        #expect(!model.state.profiles.contains { $0.source == .managed })
        #expect(model.state.profiles.map(\.name) == ["Ops", "P0", "P1", "P2", "P3"])

        // The global shortcut may be bound to it, and diagnostics say where it came from.
        model.setHotKeyProfile(Self.managedId)
        #expect(model.settings.hotKeyProfileId == Self.managedId)
        #expect(model.diagnosticsText().contains("Prod incident (managed)"))
    }

    @Test func planMarksTheUnmatchedRoleNotEligible() async {
        let model = await loadedModel()
        defer { cleanup(model) }

        let items = model.plan(for: Self.managedId)
        #expect(items.map(\.roleKey) == [Self.entraKey, Self.missingGroupKey])
        #expect(items.first?.disposition == .activate)
        #expect(items.first?.duration == .seconds(7200))
        // The tenant is loaded, so the placeholder is "not eligible" rather than "not loaded".
        #expect(items.last?.disposition == .notEligible)
    }

    @Test func runningAManagedProfileActivatesAndSavesNothing() async {
        let http = StubHTTPClient()
        await http.on("GET", "/me?", body: Data(#"{"id":"user-obj-1"}"#.utf8))
        await http.on("POST", "roleAssignmentScheduleRequests", status: 201, body: Data("""
        {"id": "req-1", "status": "Provisioned", "roleDefinitionId": "role-def", "directoryScopeId": "/",
         "scheduleInfo": {"startDateTime": "2026-09-04T09:00:00Z",
                          "expiration": {"type": "afterDuration", "duration": "PT2H"}}}
        """.utf8))
        let model = await loadedModel(http: http, online: true)
        defer { cleanup(model) }

        let outcomes = await model.runProfile(id: Self.managedId, items: model.plan(for: Self.managedId),
                                              justification: "Incident response", ticket: nil)
        #expect(outcomes.count == 1)
        #expect(model.active[Self.entraKey]?.assignmentId == "req-1")
        // Nothing about the run is written onto the profile, and none of it reaches state.json.
        #expect(model.state.profiles.isEmpty)
        #expect(model.profile(id: Self.managedId)?.lastJustification == "Incident response")
    }

    @Test func profileTenantsAreResolvedLikeTheOtherManagedEntries() async {
        let http = StubHTTPClient()
        await http.on("GET", "contoso.com/v2.0/.well-known/openid-configuration",
                      body: Data(#"{"issuer":"https://login.microsoftonline.com/\#(Self.tenantId)/v2.0"}"#.utf8))
        let model = await loadedModel(document: Self.document(tenant: "contoso.com"), http: http, online: true)
        defer { cleanup(model) }

        #expect(model.managedTenantIds["contoso.com"] == Self.tenantId)
        #expect(model.profiles.map(\.name) == ["Prod incident"])
        #expect(model.profile(id: Self.managedId)?.entries.map(\.roleKey) == [Self.entraKey, Self.missingGroupKey])
        #expect(model.managedProfileWarnings.isEmpty)
    }

    @Test func aBadInlineDocumentIsOneWarningAndNoProfiles() async {
        let model = await loadedModel(document: "{ nope")
        defer { cleanup(model) }

        #expect(model.profiles.isEmpty)
        #expect(model.managedProfileWarnings.count == 1)
        #expect(model.managedProfileWarnings.first?.hasPrefix("ManagedProfiles: ") == true)
        // Diagnostics carries every managed warning, as the Windows report does.
        #expect(model.diagnosticsText().contains("ManagedProfiles: "))
    }

    @Test func aUserProfileCannotTakeAPublishedProfilesName() async {
        let model = await loadedModel()
        defer { cleanup(model) }

        #expect(model.saveProfile(name: "  prod incident  ", keys: [Self.entraKey]) == nil)
        #expect(model.notice == "'Prod incident' is published by your organization and cannot be changed.")
        #expect(model.state.profiles.isEmpty)

        let mine = model.saveProfile(name: "Ops", keys: [Self.entraKey])!
        model.notice = nil
        model.renameProfile(id: mine.id, name: "PROD INCIDENT")
        #expect(model.profile(id: mine.id)?.name == "Ops")
        #expect(model.notice == "'Prod incident' is published by your organization and cannot be changed.")
    }

    @Test func pinnedManagedProfilesDoNotUseUpTheUsersPinSlots() async {
        let model = await loadedModel()
        defer { cleanup(model) }
        // The document's one profile is pinned already, and it must not cost a slot.
        #expect(model.pinnedProfiles.contains { $0.id == Self.managedId })
        #expect(model.canPinAnotherProfile)

        for i in 0..<(ProfilePins.limit - 1) {
            let p = model.saveProfile(name: "P\(i)", keys: [Self.entraKey])!
            #expect(model.setPinned(id: p.id, true))
            #expect(model.canPinAnotherProfile)
        }
        // The fourth user pin fills the row; the managed pin still doesn't count.
        let last = model.saveProfile(name: "Last", keys: [Self.entraKey])!
        #expect(model.setPinned(id: last.id, true))
        #expect(!model.canPinAnotherProfile)
    }

    @Test func aVanishedManagedProfileLeavesTheShortcutQuiet() async {
        // No managed document at all: the id the shortcut was bound to resolves to nothing,
        // as if the organization's policy had since removed the profile.
        let model = await loadedModel(document: nil)
        defer { cleanup(model) }
        model.setHotKeyProfile(Self.managedId)
        #expect(model.profile(id: Self.managedId) == nil)

        await model.runShortcutProfile()

        #expect(model.pendingProfileRun == nil)
        #expect(model.notice == "The profile bound to the shortcut is no longer available")
        #expect(model.errorLog.entries.map(\.message).contains("Shortcut profile is no longer available"))
    }

    @Test func theUrlDocumentIsFetchedCachedMergedAndThrottled() async {
        let http = StubHTTPClient()
        await http.on("GET", "profiles.json", body: Data(Self.document(name: "Fetched incident").utf8))
        let model = await loadedModel(document: Self.document(name: "Inline incident"),
                                      url: "https://example.com/profiles.json", http: http, online: true)
        defer { cleanup(model) }

        #expect(model.fetchedProfileSet?.profiles.map(\.name) == ["Fetched incident"])
        #expect(model.managedProfilesFetchedAt != nil)
        // Same slug in both documents: the fetched one wins.
        #expect(model.profiles.map(\.name) == ["Fetched incident"])
        #expect(model.managedProfileWarnings.isEmpty)
        let cache = model.managedProfilesCacheURL
        #expect(FileManager.default.fileExists(atPath: cache.path))

        // Within 24 hours nothing is fetched again.
        await model.refreshManagedProfiles()
        #expect(await http.requests(matching: "profiles.json").count == 1)
        // Forcing it does fetch.
        await model.refreshManagedProfiles(force: true)
        #expect(await http.requests(matching: "profiles.json").count == 2)
    }

    @Test func aFailedFetchKeepsTheCachedDocumentAndWarns() async {
        let directory = URL(fileURLWithPath: NSTemporaryDirectory())
            .appendingPathComponent("elevate-tests-\(UUID().uuidString)", isDirectory: true)
        try? FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
        try? Data(Self.document(name: "Cached incident").utf8)
            .write(to: directory.appendingPathComponent("managed-profiles.json"))

        let http = StubHTTPClient()
        await http.on("GET", "profiles.json", status: 500)
        let model = await loadedModel(document: nil, url: "https://example.com/profiles.json",
                                      http: http, online: true, directory: directory)
        defer { cleanup(model) }

        #expect(model.profiles.map(\.name) == ["Cached incident"])
        #expect(model.managedProfileWarnings.count == 1)
        #expect(model.managedProfileWarnings.first?.hasPrefix("ManagedProfilesUrl: ") == true)
        #expect(model.managedProfilesFetchedAt == nil)
    }

    @Test func aHangingFetchIsBoundedAndLeavesTheCachedDocumentStanding() async {
        let directory = URL(fileURLWithPath: NSTemporaryDirectory())
            .appendingPathComponent("elevate-tests-\(UUID().uuidString)", isDirectory: true)
        try? FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
        try? Data(Self.document(name: "Cached incident").utf8)
            .write(to: directory.appendingPathComponent("managed-profiles.json"))

        let http = StubHTTPClient()
        await http.hang("GET", "profiles.json")
        let clock = ContinuousClock()
        let start = clock.now
        let model = await loadedModel(document: nil, url: "https://example.com/profiles.json",
                                      http: http, online: true, directory: directory)
        let elapsed = clock.now - start
        defer { cleanup(model) }

        // Well under `URLSession`'s own 60 s default, which this deadline exists to pre-empt.
        #expect(elapsed < .seconds(20))
        #expect(model.profiles.map(\.name) == ["Cached incident"])
        #expect(model.managedProfileWarnings == ["ManagedProfilesUrl: timed out after 5 s"])
        #expect(model.managedProfilesFetchedAt == nil)
    }
}
