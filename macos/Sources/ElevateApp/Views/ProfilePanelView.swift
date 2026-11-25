import SwiftUI
import ElevateCore

/// A profile's run form in the panel's main column. The header is `PanelHeader`'s; the content is
/// the one the run window shares. `ProfileRunContent` owns `markRunSeen` and the Reason focus.
struct ProfilePanelView: View {
    @Environment(AppModel.self) private var model
    let profileId: UUID

    var body: some View {
        ScrollView {
            ProfileRunContent(profileId: profileId, compact: true, onDone: nil)
                .padding(12)
                // A new identity per profile, so selecting another one re-runs onAppear and refocuses Reason.
                .id(profileId)
        }
    }
}
