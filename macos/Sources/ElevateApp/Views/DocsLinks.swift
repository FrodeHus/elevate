import SwiftUI

/// The user guides on GitHub that the setup panel and Settings point to.
enum DocsLinks {
    static let gettingStarted = URL(string: "https://github.com/FrodeHus/elevate/blob/main/docs/getting-started.md#2-choose-how-to-sign-in")!
    static let appRegistration = URL(string: "https://github.com/FrodeHus/elevate/blob/main/docs/entra-app-registration.md")!
}

/// A caption-sized row of the two guide links, separated by a middle dot.
struct DocsLinksRow: View {
    var body: some View {
        HStack(spacing: 4) {
            Link("Getting started", destination: DocsLinks.gettingStarted)
            Text("·").foregroundStyle(.tertiary)
            Link("App registration guide", destination: DocsLinks.appRegistration)
        }
        .font(.caption)
    }
}
