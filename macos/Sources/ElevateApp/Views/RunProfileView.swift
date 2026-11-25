import SwiftUI
import ElevateCore

/// The run window: the shared run form at window width, plus the title and the re-plan hook.
struct RunProfileView: View {
    @Environment(AppModel.self) private var model
    @Environment(\.dismiss) private var dismiss
    let profileId: UUID

    private var profile: ActivationProfile? { model.profiles.first { $0.id == profileId } }
    private var finished: Bool { model.runSessions[profileId]?.finished == true }

    var body: some View {
        ProfileRunContent(profileId: profileId, compact: false, onDone: { dismiss() })
            .padding(16).frame(width: 560)
            .navigationTitle(finished ? "Ran \"\(profile?.name ?? "profile")\"" : "Run \"\(profile?.name ?? "profile")\"")
            .onAppear { model.runWindowsOpen.insert(profileId); replan() }
            .onDisappear { model.runWindowClosed(profileId) }
            // WindowGroup(for:) refocuses an existing window for the same value, so .onAppear does not
            // re-fire; re-plan when the user asks to run this profile again — not on every refocus.
            .onChange(of: model.runRequests[profileId]) { _, _ in replan() }
    }

    private func replan() {
        if model.runSessions[profileId]?.running != true { model.resetRunSession(profileId) }
    }
}
