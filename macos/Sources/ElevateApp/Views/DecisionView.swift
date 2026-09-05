import SwiftUI
import ElevateCore

/// The Approve/Deny sheet. Placeholder: the details, justification field and buttons land with the
/// approvals UI; the route exists now so `AppModel.decide` has a window to be driven from.
struct DecisionView: View {
    @Environment(AppModel.self) private var model
    @Environment(\.dismiss) private var dismiss
    let requestId: String
    let approve: Bool

    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            Text(approve ? "Approve request" : "Deny request").font(.title3.weight(.semibold))
            Text(model.approvalsOrdered.first { $0.id == requestId }?.targetName ?? requestId)
                .foregroundStyle(.secondary)
            HStack { Spacer(); Button("Close") { dismiss() }.keyboardShortcut(.cancelAction) }
        }
        .padding(16)
        .frame(width: 380)
    }
}
