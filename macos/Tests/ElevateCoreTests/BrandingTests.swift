import Testing
import Foundation
@testable import ElevateCore

@Suite struct BrandingTests {
    private func config(_ values: [String: Any]) -> ManagedConfiguration {
        ManagedConfiguration.load(from: DictionaryManagedSource(values))
    }

    @Test func unbrandedResolvesToNil() {
        #expect(Branding.resolve(from: .none) == nil)
        #expect(Branding.resolve(from: config(["ClientId": "11111111-2222-3333-4444-555555555555"])) == nil)
    }

    @Test func defaultStyleIsBy() {
        #expect(Branding.resolve(from: config(["OrganizationName": "Contoso"]))?.headerCaption == "by Contoso")
    }

    @Test func managedByStyleCaption() {
        let branding = Branding.resolve(from: config([
            "OrganizationName": "Contoso", "OrganizationTitleStyle": "managedBy",
        ]))
        #expect(branding?.headerCaption == "Managed by Contoso")
    }

    @Test func noneStyleSuppressesTheCaptionOnly() {
        let branding = Branding.resolve(from: config([
            "OrganizationName": "Contoso",
            "OrganizationTitleStyle": "none",
            "OrganizationSupportUrl": "https://help.contoso.com",
        ]))
        #expect(branding?.headerCaption == nil)
        #expect(branding?.firstRunLine == "Provided by Contoso.")
        #expect(branding?.hasSupport == true)
    }

    @Test func supportLinePrefersTheUrl() {
        let both = Branding.resolve(from: config([
            "OrganizationName": "Contoso",
            "OrganizationSupportUrl": "https://help.contoso.com",
            "OrganizationSupportEmail": "it@contoso.com",
        ]))
        #expect(both?.supportLine == "Need help? Contoso IT — https://help.contoso.com")

        let emailOnly = Branding.resolve(from: config([
            "OrganizationName": "Contoso", "OrganizationSupportEmail": "it@contoso.com",
        ]))
        #expect(emailOnly?.supportLine == "Need help? Contoso IT — it@contoso.com")
    }

    @Test func supportDestinationFallsBackToMailto() {
        let url = Branding.resolve(from: config([
            "OrganizationName": "Contoso", "OrganizationSupportUrl": "https://help.contoso.com",
        ]))
        #expect(url?.supportDestination?.absoluteString == "https://help.contoso.com")

        let email = Branding.resolve(from: config([
            "OrganizationName": "Contoso", "OrganizationSupportEmail": "it@contoso.com",
        ]))
        #expect(email?.supportDestination?.absoluteString == "mailto:it@contoso.com")

        #expect(Branding.resolve(from: config(["OrganizationName": "Contoso"]))?.supportDestination == nil)
    }

    @Test func diagnosticsLineNamesTheOrganizationAndHelpDesk() {
        let full = Branding.resolve(from: config([
            "OrganizationName": "Contoso",
            "OrganizationTitleStyle": "managedBy",
            "OrganizationSupportUrl": "https://help.contoso.com",
        ]))
        #expect(full?.diagnosticsLine == "Contoso (managedBy) · https://help.contoso.com")
        #expect(Branding.resolve(from: config(["OrganizationName": "Contoso"]))?.diagnosticsLine == "Contoso (by)")
    }

    @Test func noSupportMeansNoSupportLine() {
        let branding = Branding.resolve(from: config(["OrganizationName": "Contoso"]))
        #expect(branding?.hasSupport == false)
        #expect(branding?.supportLine == nil)
    }
}
