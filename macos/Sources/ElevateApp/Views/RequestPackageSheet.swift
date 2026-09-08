import SwiftUI
import ElevateCore

/// Policy choice plus justification for one package; hands off to My Access when the chosen
/// policy asks questions Elevate does not collect.
struct RequestPackageSheet: View {
    @Environment(AppModel.self) private var model
    @Environment(\.dismiss) private var dismiss
    let tenantKey: TenantKey
    let package: AccessPackage
    let onSubmitted: () -> Void

    @State private var requirements: [PolicyRequirement] = []
    @State private var selectedPolicyId: String?
    @State private var justification = ""
    @State private var loading = true
    @State private var loadError: String?
    @State private var submitting = false
    @State private var submitError: String?

    private var selected: PolicyRequirement? { requirements.first { $0.id == selectedPolicyId } ?? requirements.first }
    private var canSubmit: Bool {
        !submitting && selected != nil && !(selected?.requiresAnswers ?? false)
            && !justification.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            VStack(alignment: .leading, spacing: 3) {
                Text("Request \(package.displayName)").font(.headline)
                if let d = package.description, !d.isEmpty { Text(d).font(.caption).foregroundStyle(.secondary) }
            }
            if loading {
                ProgressView("Checking the request policy…").controlSize(.small)
            } else if let loadError {
                Label(loadError, systemImage: "exclamationmark.triangle").font(.caption).foregroundStyle(.red)
            } else if requirements.isEmpty {
                Text("No policy lets you request this package right now.").font(.caption).foregroundStyle(.secondary)
            } else if let selected, selected.requiresAnswers {
                questionsNotice(selected)
            } else {
                form
            }
            HStack {
                if submitting { ProgressView().controlSize(.small) }
                Spacer()
                Button("Cancel") { dismiss() }.keyboardShortcut(.cancelAction)
                if let selected, selected.requiresAnswers {
                    Button("Open in My Access") {
                        NSWorkspace.shared.open(AccessPackageProvider.myAccessURL(tenantId: tenantKey.tenantId, packageId: package.id))
                        dismiss()
                    }
                    .keyboardShortcut(.defaultAction).buttonStyle(.borderedProminent)
                } else {
                    Button("Submit Request") { Task { await submit() } }
                        .keyboardShortcut(.defaultAction).buttonStyle(.borderedProminent).disabled(!canSubmit)
                }
            }
        }
        .padding(18)
        .frame(width: 440)
        .task { await load() }
    }

    @ViewBuilder private var form: some View {
        if requirements.count > 1 {
            VStack(alignment: .leading, spacing: 5) {
                Text("Policy").font(.caption.weight(.semibold))
                Picker("", selection: Binding(get: { selectedPolicyId ?? requirements[0].id }, set: { selectedPolicyId = $0 })) {
                    ForEach(requirements) { r in Text(Self.policyLabel(r)).tag(r.id) }
                }
                .labelsHidden()
                Text("\(requirements.count) policies let you request this package. The policy sets the duration and who approves.")
                    .font(.caption).foregroundStyle(.secondary)
            }
        }
        VStack(alignment: .leading, spacing: 5) {
            Text("Justification").font(.caption.weight(.semibold))
            TextField("Why you need this access", text: $justification, axis: .vertical).lineLimit(3...5)
            Text("Required. Approvers see this text.").font(.caption).foregroundStyle(.secondary)
        }
        if let submitError {
            Label(submitError, systemImage: "exclamationmark.triangle").font(.caption).foregroundStyle(.red)
                .fixedSize(horizontal: false, vertical: true)
        }
    }

    private func questionsNotice(_ policy: PolicyRequirement) -> some View {
        HStack(alignment: .top, spacing: 10) {
            Image(systemName: "info.circle").foregroundStyle(.secondary)
            VStack(alignment: .leading, spacing: 3) {
                Text("This package asks questions before it can be requested.")
                Text("Policy \u{201C}\(policy.displayName)\u{201D} requires answers that Elevate does not collect. Continue in the My Access portal, then check the Requested tab here.")
                    .font(.caption).foregroundStyle(.secondary).fixedSize(horizontal: false, vertical: true)
            }
        }
        .padding(10)
        .background(Color.primary.opacity(0.04), in: RoundedRectangle(cornerRadius: 8))
    }

    static func policyLabel(_ r: PolicyRequirement) -> String {
        var parts = [r.displayName]
        if let d = r.description, !d.isEmpty { parts.append(d) }
        parts.append(r.isApprovalRequired ? "approval required" : "no approval")
        return parts.joined(separator: " · ")
    }

    private func load() async {
        loading = true
        defer { loading = false }
        do {
            requirements = try await model.packageRequirements(tenantKey, packageId: package.id)
            selectedPolicyId = requirements.first?.id
            loadError = nil
        } catch {
            loadError = (error as? PIMError)?.userMessage ?? error.localizedDescription
        }
    }

    private func submit() async {
        guard let selected else { return }
        submitting = true
        defer { submitting = false }
        do {
            try await model.requestPackage(tenantKey, packageId: package.id,
                                           policyId: requirements.count > 1 ? selected.id : nil,
                                           justification: justification.trimmingCharacters(in: .whitespacesAndNewlines))
            submitError = nil
            onSubmitted()
            dismiss()
        } catch {
            submitError = (error as? PIMError)?.userMessage ?? error.localizedDescription
        }
    }
}
