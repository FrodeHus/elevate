import SwiftUI
import ElevateCore

/// The Approve/Deny sheet for one pending approval request, opened from `ApprovalRow`.
struct DecisionView: View {
    @Environment(AppModel.self) private var model
    @Environment(\.dismiss) private var dismiss
    let requestId: String
    let approve: Bool

    @State private var justification = ""
    @State private var running = false

    /// The live row: it disappears once decided elsewhere or dropped by a refresh. Looked up in the
    /// unfiltered set so an active panel search never hides the request the sheet is showing.
    private var request: ApprovalRequest? { model.approval(id: requestId) }

    /// Denying a request has to say why; approving may be wordless.
    private var canSubmit: Bool {
        !running && (approve || !justification.trimmingCharacters(in: .whitespaces).isEmpty)
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            if let request {
                Text(approve ? "Approve request" : "Deny request").font(.title3.weight(.semibold))
                details(request)
                TextField("Justification", text: $justification, axis: .vertical).lineLimit(2...4)
                if let error = model.approvalErrors[requestId] {
                    Label(error, systemImage: "exclamationmark.triangle")
                        .font(.caption).foregroundStyle(.red).textSelection(.enabled)
                        .fixedSize(horizontal: false, vertical: true)
                }
                HStack {
                    if running { ProgressView().controlSize(.small) }
                    Spacer()
                    Button("Cancel") { dismiss() }.keyboardShortcut(.cancelAction)
                    Button(approve ? "Approve" : "Deny") { Task { await submit(request) } }
                        .keyboardShortcut(.defaultAction)
                        .buttonStyle(.borderedProminent)
                        .disabled(!canSubmit)
                }
            } else {
                Text("This request is no longer pending.")
                HStack { Spacer(); Button("Close") { dismiss() }.keyboardShortcut(.cancelAction) }
            }
        }
        .padding(16)
        .frame(width: 420)
        // The routed window is reused across requests, so the field is seeded per request rather
        // than on every appearance — retyping is not thrown away when the panel redraws.
        .task(id: requestId) {
            justification = model.settings.lastApprovalJustification
            running = false
        }
    }

    @ViewBuilder private func details(_ r: ApprovalRequest) -> some View {
        VStack(alignment: .leading, spacing: 3) {
            LabeledContent("Requester") { Text(r.requesterName) }
            LabeledContent("Role") { Text(r.scopeCaption.map { "\(r.targetName) · \($0)" } ?? r.targetName) }
            LabeledContent("Tenant") { Text(model.tenant(r.tenantKey)?.displayName ?? r.tenantKey.tenantId) }
            if let d = r.requestedDuration { LabeledContent("Duration") { Text(Countdown.label(d)) } }
            LabeledContent("Reason") { Text(r.justification ?? "No reason given") }
        }
        .font(.callout)
        .textSelection(.enabled)
    }

    private func submit(_ request: ApprovalRequest) async {
        running = true
        let ok = await model.decide(request, approve: approve, justification: justification)
        running = false
        if ok { dismiss() }
    }
}
