import SwiftUI
import ElevateCore

struct ActivationView: View {
    @Environment(AppModel.self) private var model
    @Environment(\.dismiss) private var dismiss
    let keys: [RoleKey]

    struct Item: Identifiable {
        let role: EligibleRole
        var duration: Duration
        var id: RoleKey { role.key }
    }

    @State private var items: [Item] = []
    @State private var justification = ""
    @State private var ticketNumber = ""
    @State private var ticketSystem = ""
    @State private var running = false

    private var isBulk: Bool { keys.count > 1 }
    private var needsTicket: Bool { items.contains { $0.role.policy.requiresTicket } }
    private var justificationRequired: Bool { items.contains { $0.role.policy.requiresJustification } }
    private var canSubmit: Bool {
        !running && !items.isEmpty
            && (!justificationRequired || !justification.trimmingCharacters(in: .whitespaces).isEmpty)
            && (!needsTicket || !ticketNumber.trimmingCharacters(in: .whitespaces).isEmpty)
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            Text(isBulk ? "Activate \(keys.count) roles" : (items.first?.role.displayName ?? "Activate role")).font(.title3.weight(.semibold))
            if isBulk { bulkTable } else { singleDuration }
            TextField("Reason", text: $justification, axis: .vertical).lineLimit(2...4)
            if needsTicket {
                HStack {
                    TextField("Ticket number", text: $ticketNumber)
                    TextField("Ticket system", text: $ticketSystem)
                }
            }
            HStack {
                Spacer()
                Button("Cancel") { dismiss() }.keyboardShortcut(.cancelAction)
                Button(isBulk ? "Activate all" : "Activate") { Task { await submit() } }
                    .keyboardShortcut(.defaultAction).buttonStyle(.borderedProminent).disabled(!canSubmit)
            }
        }
        .padding(16)
        .frame(width: isBulk ? 560 : 380)
        .onAppear(perform: load)
    }

    private var singleDuration: some View {
        Group {
            if let item = items.first {
                if case .failed(let error)? = model.progress[item.role.key] {
                    Label(error.userMessage, systemImage: "exclamationmark.triangle")
                        .font(.caption).foregroundStyle(.red).textSelection(.enabled)
                        .fixedSize(horizontal: false, vertical: true)
                }
                DurationPicker(duration: Binding(get: { items[0].duration }, set: { items[0].duration = $0 }), maximum: item.role.policy.maximumDuration)
                if item.role.policy.requiresApproval { Label("This role requires approval", systemImage: "person.badge.clock").font(.caption) }
            }
        }
    }

    private var bulkTable: some View {
        VStack(alignment: .leading, spacing: 4) {
            ForEach(groupedTenantKeys, id: \.self) { tk in
                Text("\(model.identity(tk.identityId)?.upn ?? tk.identityId) · \(model.tenant(tk)?.displayName ?? tk.tenantId)")
                    .font(.caption.weight(.semibold)).foregroundStyle(.secondary).padding(.top, 4)
                ForEach($items) { $item in
                    if item.role.key.tenantKey == tk {
                        HStack {
                            Text(item.role.displayName)
                            Spacer()
                            DurationPicker(duration: $item.duration, maximum: item.role.policy.maximumDuration).frame(width: 150)
                            approvalLabel(for: item.role).frame(width: 80)
                            progressLabel(for: item.role.key).frame(width: 120, alignment: .trailing)
                        }
                    }
                }
            }
        }
    }

    private var groupedTenantKeys: [TenantKey] {
        var seen: [TenantKey] = []
        for i in items where !seen.contains(i.role.key.tenantKey) { seen.append(i.role.key.tenantKey) }
        return seen
    }

    @ViewBuilder private func approvalLabel(for role: EligibleRole) -> some View {
        if role.policy.requiresApproval {
            Label("approval", systemImage: "person.badge.clock").font(.caption)
        } else {
            EmptyView()
        }
    }

    @ViewBuilder private func progressLabel(for key: RoleKey) -> some View {
        switch model.progress[key] {
        case .activated: Label("Active", systemImage: "checkmark.circle.fill").foregroundStyle(.green).font(.caption)
        case .pendingApproval: Label("Pending", systemImage: "clock").foregroundStyle(.yellow).font(.caption)
        case .failed(let e): Text(e.userMessage).foregroundStyle(.red).font(.caption).lineLimit(1).help(e.userMessage)
        case nil: running ? AnyView(ProgressView().controlSize(.small)) : AnyView(EmptyView())
        }
    }

    private func load() {
        items = keys.compactMap { key in
            guard let role = model.role(for: key) else { return nil }
            let remembered = model.remembered(for: key)?.lastDuration
            let d = min(remembered ?? role.policy.defaultDuration, role.policy.maximumDuration)
            return Item(role: role, duration: d)
        }
        justification = keys.compactMap { model.remembered(for: $0)?.justification }.first ?? ""
        model.clearProgress(keys)
    }

    private func submit() async {
        running = true
        let ticket = needsTicket && !ticketNumber.isEmpty ? TicketInfo(number: ticketNumber, system: ticketSystem) : nil
        let requests = items.map {
            ActivationRequest(roleKey: $0.role.key, duration: $0.duration, justification: justification, ticket: ticket,
                              authenticationContext: $0.role.policy.authenticationContext)
        }
        await model.activate(requests)
        running = false
        let allOk = requests.allSatisfy { if case .failed = model.progress[$0.roleKey] { false } else { true } }
        if allOk { dismiss() }
    }
}

/// 30-minute steps from 30 minutes up to `maximum`.
struct DurationPicker: View {
    @Binding var duration: Duration
    let maximum: Duration

    private var options: [Duration] {
        let maxMinutes = max(30, Int(maximum.components.seconds / 60))
        return stride(from: 30, through: maxMinutes, by: 30).map { .seconds($0 * 60) }
    }

    var body: some View {
        Picker("Duration", selection: $duration) {
            ForEach(options, id: \.self) { d in Text(label(d)).tag(d) }
        }
        .onAppear { if !options.contains(duration) { duration = nearestOption(to: duration) } }
    }

    /// Snaps to the closest 30-minute step, never past the policy maximum.
    private func nearestOption(to d: Duration) -> Duration {
        let options = options
        guard let first = options.first, let last = options.last else { return .seconds(1800) }
        let minutes = Double(d.components.seconds) / 60
        let snapped = Int((minutes / 30).rounded()) * 30
        return .seconds(min(max(snapped * 60, Int(first.components.seconds)), Int(last.components.seconds)))
    }

    private func label(_ d: Duration) -> String {
        let m = Int(d.components.seconds / 60)
        return m % 60 == 0 ? "\(m / 60) h" : "\(m / 60) h \(m % 60) min"
    }
}
