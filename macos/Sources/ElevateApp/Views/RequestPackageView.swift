import SwiftUI
import ElevateCore

/// Policy choice plus justification for one package, in the panel's main view; hands off to My
/// Access when the chosen policy asks questions Elevate does not collect.
struct RequestPackageView: View {
    @Environment(AppModel.self) private var model
    let tenantKey: TenantKey
    let package: AccessPackage
    var availableHeight: CGFloat = PanelMetrics.maxHeight

    @State private var requirements: [PolicyRequirement] = []
    @State private var selectedPolicyId: String?
    @State private var justification = ""
    @State private var loading = true
    @State private var contentHeight: CGFloat = 0
    @State private var loadError: String?
    @State private var submitting = false
    @State private var submitError: String?

    private var selected: PolicyRequirement? { requirements.first { $0.id == selectedPolicyId } ?? requirements.first }
    private var canSubmit: Bool {
        !submitting && selected != nil && !(selected?.requiresAnswers ?? false)
            && !justification.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty
    }

    var body: some View {
        // Sized from the measured content, as in the role list: a ScrollView reports no ideal height here.
        ScrollView { content }
            .frame(height: min(max(contentHeight, 44), max(availableHeight, 44)))
            .task { await load() }
    }

    private var content: some View {
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
            } else {
                if requirements.count > 1 { policyPicker }
                if let selected, selected.requiresAnswers {
                    questionsNotice(selected)
                } else {
                    form
                }
            }
            HStack {
                if submitting { ProgressView().controlSize(.small) }
                Spacer()
                Button("Cancel") { model.endPackageRequest() }
                if let selected, selected.requiresAnswers {
                    Button("Open in My Access") {
                        NSWorkspace.shared.open(AccessPackageProvider.myAccessURL(tenantId: tenantKey.tenantId, packageId: package.id))
                        model.endPackageRequest()
                    }
                    .keyboardShortcut(.defaultAction).buttonStyle(.borderedProminent)
                } else {
                    Button("Submit Request") { Task { await submit() } }
                        .keyboardShortcut(.defaultAction).buttonStyle(.borderedProminent).disabled(!canSubmit)
                }
            }
        }
        .padding(12)
        .onGeometryChange(for: CGFloat.self) { $0.size.height } action: { contentHeight = $0 }
    }

    private var policyPicker: some View {
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

    @ViewBuilder private var form: some View {
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
            model.accessPackagesTab = .requested
            model.endPackageRequest()
        } catch {
            submitError = (error as? PIMError)?.userMessage ?? error.localizedDescription
        }
    }
}
