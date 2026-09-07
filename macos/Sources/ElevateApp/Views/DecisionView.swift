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
        .navigationTitle(approve ? "Approve request" : "Deny request")
        // The window is keyed by route value (one window per requestId/approve pair), so this
        // runs once per window rather than on every appearance — retyping is not thrown away
        // when the panel redraws.
        .task(id: requestId) {
            justification = model.settings.lastApprovalJustification
            running = false
        }
    }

    @ViewBuilder private func details(_ r: ApprovalRequest) -> some View {
        // A grid, not stacked LabeledContent: the labels line up in one column and the values on one edge.
        Grid(alignment: .leadingFirstTextBaseline, horizontalSpacing: 10, verticalSpacing: 4) {
            detailRow("Requester", r.requesterName)
            detailRow("Role", r.scopeCaption.map { "\(r.targetName) · \($0)" } ?? r.targetName)
            detailRow("Tenant", model.tenant(r.tenantKey)?.displayName ?? r.tenantKey.tenantId)
            if let d = r.requestedDuration { detailRow("Duration", Countdown.label(d)) }
            detailRow("Reason", r.justification ?? "No reason given")
        }
        .font(.callout)
        .textSelection(.enabled)
    }

    private func detailRow(_ label: String, _ value: String) -> some View {
        GridRow {
            Text(label).foregroundStyle(.secondary).gridColumnAlignment(.trailing)
            Text(value).fixedSize(horizontal: false, vertical: true)
        }
    }

    private func submit(_ request: ApprovalRequest) async {
        running = true
        let ok = await model.decide(request, approve: approve, justification: justification)
        running = false
        if ok { dismiss() }
    }
}
