import Testing
import Foundation
@testable import ElevateCore

@Suite struct ManagedConfigurationTests {
    @Test func emptySourceIsEmpty() {
        let c = ManagedConfiguration.load(from: DictionaryManagedSource([:]))
        #expect(c.isEmpty); #expect(c.clientId == nil); #expect(c.disableUpdateCheck == false)
        #expect(c.allowedSignInMethods == nil); #expect(c.allowedTenants == nil); #expect(c.pinnedTenants.isEmpty)
    }
    @Test func clientIdIsValidatedAndLowercased() {
        let c = ManagedConfiguration.load(from: DictionaryManagedSource(["ClientId": " 11111111-2222-3333-4444-555555555555 "]))
        #expect(c.clientId == "11111111-2222-3333-4444-555555555555")
        #expect(c.keysInEffect == [.clientId])
        let bad = ManagedConfiguration.load(from: DictionaryManagedSource(["ClientId": "not-a-guid"]))
        #expect(bad.clientId == nil); #expect(bad.keysInEffect.isEmpty)
        #expect(bad.warnings.contains { $0.hasPrefix("ClientId:") })
        let zero = ManagedConfiguration.load(from: DictionaryManagedSource(["ClientId": "00000000-0000-0000-0000-000000000000"]))
        #expect(zero.clientId == nil)
    }
    @Test func disableUpdateCheckAcceptsBoolAndStrings() {
        #expect(ManagedConfiguration.load(from: DictionaryManagedSource(["DisableUpdateCheck": true])).disableUpdateCheck)
        #expect(ManagedConfiguration.load(from: DictionaryManagedSource(["DisableUpdateCheck": "true"])).disableUpdateCheck)
        #expect(ManagedConfiguration.load(from: DictionaryManagedSource(["DisableUpdateCheck": 1])).disableUpdateCheck)
        let off = ManagedConfiguration.load(from: DictionaryManagedSource(["DisableUpdateCheck": false]))
        #expect(!off.disableUpdateCheck); #expect(off.keysInEffect == [.disableUpdateCheck])
    }
    @Test func allowedMethodsParseCaseInsensitivelyAndDropUnknown() {
        let c = ManagedConfiguration.load(from: DictionaryManagedSource(["AllowedSignInMethods": ["OwnApp", "azurecli", "saml"]]))
        #expect(c.allowedSignInMethods == [.ownApp, .azureCLI])
        #expect(c.warnings == ["AllowedSignInMethods: unknown method 'saml' ignored"])
        let csv = ManagedConfiguration.load(from: DictionaryManagedSource(["AllowedSignInMethods": "ownApp, custom"]))
        #expect(csv.allowedSignInMethods == [.ownApp, .custom])
        let empty = ManagedConfiguration.load(from: DictionaryManagedSource(["AllowedSignInMethods": []]))
        #expect(empty.allowedSignInMethods == nil); #expect(!empty.keysInEffect.contains(.allowedSignInMethods))
        let allUnknown = ManagedConfiguration.load(from: DictionaryManagedSource(["AllowedSignInMethods": ["saml"]]))
        #expect(allUnknown.allowedSignInMethods == nil)
    }
    @Test func tenantListsKeepEntriesTrimmedAndDeduplicated() {
        let c = ManagedConfiguration.load(from: DictionaryManagedSource(["AllowedTenants": [" contoso.com ", "contoso.com", ""], "PinnedTenants": ["Fabrikam.com"]]))
        #expect(c.allowedTenants == ["contoso.com"]); #expect(c.pinnedTenants == ["fabrikam.com"])
        #expect(c.keysInEffect == [.allowedTenants, .pinnedTenants])
    }
    @Test func profilesUrlMustBeHttps() {
        let ok = ManagedConfiguration.load(from: DictionaryManagedSource(["ManagedProfilesUrl": "https://example.com/p.json"]))
        #expect(ok.managedProfilesUrl?.absoluteString == "https://example.com/p.json")
        let http = ManagedConfiguration.load(from: DictionaryManagedSource(["ManagedProfilesUrl": "http://example.com/p.json"]))
        #expect(http.managedProfilesUrl == nil); #expect(http.warnings == ["ManagedProfilesUrl: only https URLs are accepted"])
    }
    @Test func profilesDocumentIsKeptRaw() {
        let c = ManagedConfiguration.load(from: DictionaryManagedSource(["ManagedProfiles": "{\"version\":1,\"profiles\":[]}"]))
        #expect(c.managedProfilesDocument == "{\"version\":1,\"profiles\":[]}"); #expect(c.keysInEffect == [.managedProfiles])
        let blank = ManagedConfiguration.load(from: DictionaryManagedSource(["ManagedProfiles": "  "]))
        #expect(blank.managedProfilesDocument == nil)
    }
    @Test func originIsRecorded() {
        let c = ManagedConfiguration.load(from: DictionaryManagedSource(["ClientId": "11111111-2222-3333-4444-555555555555"], origin: "unit"))
        #expect(c.origin == "unit")
        #expect(ManagedConfiguration.load(from: DictionaryManagedSource([:], origin: "unit")).origin == nil)
    }
    @Test func managedPreferencesReadsOnlyForcedKeys() {
        let suite = "elevate-managed-\(UUID().uuidString)"
        let defaults = UserDefaults(suiteName: suite)!
        defer { defaults.removePersistentDomain(forName: suite) }
        defaults.set("11111111-2222-3333-4444-555555555555", forKey: "ClientId")
        // A value the user wrote is not forced, so the source must not return it.
        #expect(ManagedPreferences(defaults: defaults).string(.clientId) == nil)
        #expect(ManagedPreferences(defaults: defaults).origin == "managed preferences")
    }
    @Test func signInMethodKinds() {
        #expect(SignInMethod.ownApp.kind == .ownApp); #expect(SignInMethod.custom(clientId: "x").kind == .custom)
        #expect(SignInMethodKind(rawValue: "azurePowerShell") == .azurePowerShell)
    }
}
