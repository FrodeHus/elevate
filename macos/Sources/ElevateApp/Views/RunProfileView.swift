import SwiftUI
import ElevateCore

struct RunProfileView: View {
    @Environment(AppModel.self) private var model
    @Environment(\.dismiss) private var dismiss
    let profileId: UUID
    @State private var items: [ProfilePlanItem] = []
    @State private var justification = ""
    @State private var ticketNumber = ""
    @State private var ticketSystem = ""
    @State private var running = false
    @State private var finished = false
    @State private var scheduleStart = false
    @State private var startAt = Date.now.addingTimeInterval(3600)

    private var profile: ActivationProfile? { model.profiles.first { $0.id == profileId } }
    private var toActivate: [ProfilePlanItem] { items.filter { $0.disposition == .activate } }
    private var needsTicket: Bool { toActivate.contains { $0.role?.policy.requiresTicket == true } }
    private var justificationRequired: Bool { toActivate.contains { $0.role?.policy.requiresJustification == true } }
    private var canSubmit: Bool {
        !running && !finished && !toActivate.isEmpty
            && (!justificationRequired || !justification.trimmingCharacters(in: .whitespaces).isEmpty)
            && (!needsTicket || !ticketNumber.trimmingCharacters(in: .whitespaces).isEmpty)
    }
    private var groupedTenantKeys: [TenantKey] {
        var seen: [TenantKey] = []
        for i in items where !seen.contains(i.roleKey.tenantKey) { seen.append(i.roleKey.tenantKey) }
        return seen
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            HStack(spacing: 6) {
                Image(systemName: "bolt.fill").foregroundStyle(Color.accentColor)
                Text(finished ? "Ran \"\(profile?.name ?? "profile")\"" : "Run \"\(profile?.name ?? "profile")\"").font(.title3.weight(.semibold))
            }
            VStack(alignment: .leading, spacing: 4) {
                ForEach(groupedTenantKeys, id: \.self) { tk in
                    Text("\(model.identity(tk.identityId)?.upn ?? tk.identityId) · \(model.tenant(tk)?.displayName ?? tk.tenantId)")
                        .font(.caption.weight(.semibold)).foregroundStyle(.secondary).padding(.top, 4)
                    ForEach($items) { $item in
                        if item.roleKey.tenantKey == tk { row($item) }
                    }
                }
            }
            if !finished {
                startAtRow
                TextField("Reason", text: $justification, axis: .vertical).lineLimit(2...4)
                if needsTicket {
                    HStack { TextField("Ticket number", text: $ticketNumber); TextField("Ticket system", text: $ticketSystem) }
                }
            }
            HStack(alignment: .firstTextBaseline) {
                Text("Entries already active are skipped. Approval-required entries are requested and shown as pending.")
                    .font(.caption).foregroundStyle(.secondary).fixedSize(horizontal: false, vertical: true)
                Spacer()
                if finished {
                    Button("Done") { dismiss() }.keyboardShortcut(.defaultAction).buttonStyle(.borderedProminent)
                } else {
                    Button("Cancel") { dismiss() }.keyboardShortcut(.cancelAction).disabled(running)
                    Button("Activate \(toActivate.count)") { Task { await submit() } }
                        .keyboardShortcut(.defaultAction).buttonStyle(.borderedProminent).disabled(!canSubmit)
                }
            }
        }
        .padding(16).frame(width: 560)
        .onAppear(perform: load)
        // WindowGroup(for:) refocuses an existing window for the same value, so .onAppear does not
        // re-fire; re-plan when the user asks to run this profile again — not on every refocus.
        .onChange(of: model.runRequests[profileId]) { _, _ in
            if !running { load() }
        }
    }

    /// The picker's lower bound is "now", so it is recomputed on every render rather than captured once.
    @ViewBuilder private var startAtRow: some View {
        let notBefore = Date.now
        HStack(spacing: 8) {
            Toggle("Start at", isOn: $scheduleStart)
            if scheduleStart {
                DatePicker("", selection: $startAt, in: notBefore..., displayedComponents: [.date, .hourAndMinute])
                    .labelsHidden()
            }
        }
    }

    @ViewBuilder private func row(_ item: Binding<ProfilePlanItem>) -> some View {
        let it = item.wrappedValue
        HStack(spacing: 8) {
            Text(it.role?.displayName ?? model.summaryName(for: it.roleKey)).opacity(it.disposition == .activate ? 1 : 0.6)
            Spacer()
            switch it.disposition {
            case .activate:
                if !finished {
                    DurationPicker(duration: item.duration, maximum: it.role?.policy.maximumDuration ?? RolePolicy.manualDefault.maximumDuration).frame(width: 150)
                } else {
                    Text(Countdown.label(it.duration)).font(.caption).foregroundStyle(.secondary).frame(width: 150, alignment: .trailing)
                }
                statusLabel(for: it).frame(width: 130, alignment: .trailing)
            case .alreadyActive: Text("already active · skipped").font(.caption).foregroundStyle(.secondary)
            case .pending: Text("pending · skipped").font(.caption).foregroundStyle(.secondary)
            case .notEligible: Text("not eligible · skipped").font(.caption).foregroundStyle(.orange)
            case .notLoaded:
                HStack(spacing: 4) {
                    ProgressView().controlSize(.small)
                    Text("loading…").font(.caption).foregroundStyle(.secondary)
                }
            }
        }
    }

    @ViewBuilder private func statusLabel(for it: ProfilePlanItem) -> some View {
        switch model.progress[it.roleKey] {
        case .activated: Label("Active", systemImage: "checkmark.circle.fill").foregroundStyle(.green).font(.caption)
        case .scheduled: Label("Scheduled", systemImage: "calendar").foregroundStyle(.blue).font(.caption)
        case .pendingApproval: Label("Pending", systemImage: "clock").foregroundStyle(.yellow).font(.caption)
        case .failed(let e): Text(e.userMessage).foregroundStyle(.red).font(.caption).lineLimit(1).help(e.userMessage)
        case nil:
            if running { ProgressView().controlSize(.small) }
            else if it.role?.policy.requiresApproval == true { Label("approval", systemImage: "person.badge.clock").font(.caption) }
            else if it.role?.policy.requiresMFA == true { Text("MFA").font(.caption).foregroundStyle(.secondary) }
        }
    }

    private func load() {
        items = model.plan(for: profileId)
        if finished || justification.isEmpty { justification = profile?.lastJustification ?? "" }
        finished = false
        model.clearProgress(items.map(\.roleKey))
    }

    private func submit() async {
        running = true
        let ticket = needsTicket && !ticketNumber.isEmpty ? TicketInfo(number: ticketNumber, system: ticketSystem) : nil
        await model.runProfile(id: profileId, items: items, justification: justification, ticket: ticket,
                               startDateTime: scheduleStart ? startAt : nil)
        running = false
        finished = true
    }
}
