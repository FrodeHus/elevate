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
    @State private var rowResults: [RoleKey: ActivationOutcome.Result] = [:]
    @State private var finished = false
    @State private var scheduleStart = false
    @State private var startAt = Date.now.addingTimeInterval(3600)
    /// Roles the user unchecked for this run only; nothing is remembered onto the profile.
    @State private var excluded: Set<RoleKey> = []

    private var profile: ActivationProfile? { model.profiles.first { $0.id == profileId } }
    private var toActivate: [ProfilePlanItem] { items.filter { $0.disposition == .activate && !excluded.contains($0.roleKey) } }
    private var needsTicket: Bool { toActivate.contains { $0.role?.policy.requiresTicket == true } }
    private var justificationRequired: Bool { toActivate.contains { $0.role?.policy.requiresJustification == true } }
    private var canSubmit: Bool {
        !running && !finished && !toActivate.isEmpty
            && (!justificationRequired || !justification.trimmingCharacters(in: .whitespaces).isEmpty)
            && (!needsTicket || !ticketNumber.trimmingCharacters(in: .whitespaces).isEmpty)
            // A start that has slipped into the past while the sheet sat open is not submittable.
            && !(scheduleStart && startAt <= Date.now)
    }
    private var groupedTenantKeys: [TenantKey] {
        var seen: [TenantKey] = []
        for i in items where !seen.contains(i.roleKey.tenantKey) { seen.append(i.roleKey.tenantKey) }
        return seen
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            VStack(alignment: .leading, spacing: 8) {
                ForEach(groupedTenantKeys, id: \.self) { tk in
                    TenantGroup(tenantKey: tk) {
                        ForEach($items) { $item in
                            if item.roleKey.tenantKey == tk { row($item) }
                        }
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
                Text("Entries already active or unchecked are skipped. Approval-required entries are requested and shown as pending.")
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
        .navigationTitle(finished ? "Ran \"\(profile?.name ?? "profile")\"" : "Run \"\(profile?.name ?? "profile")\"")
        .onAppear(perform: load)
        .onChange(of: model.progress) { _, progress in
            for item in items { if let result = progress[item.roleKey] { rowResults[item.roleKey] = result } }
        }
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
        let included = !excluded.contains(it.roleKey)
        HStack(spacing: 8) {
            if it.disposition == .activate {
                Toggle("Include \(it.role?.displayName ?? model.summaryName(for: it.roleKey))", isOn: Binding(
                    get: { included },
                    set: { on in if on { excluded.remove(it.roleKey) } else { excluded.insert(it.roleKey) } }))
                    .toggleStyle(.checkbox).labelsHidden().disabled(running || finished)
            }
            VStack(alignment: .leading, spacing: 1) {
                Text(it.role?.displayName ?? model.summaryName(for: it.roleKey))
                if let detail = it.role?.detail {
                    Text(detail).font(.caption2).foregroundStyle(.secondary).lineLimit(1).help(scopeTooltip(it.roleKey) ?? detail)
                }
            }
            .opacity(it.disposition == .activate && included ? 1 : 0.6)
            Spacer()
            switch it.disposition {
            case .activate where !included:
                Text(finished ? "unchecked · skipped" : "unchecked").font(.caption).foregroundStyle(.secondary)
            case .activate:
                // Fixed columns: the picker without its label, then the status. Both keep their width
                // even when empty so the rows line up.
                if !finished {
                    DurationPicker(duration: item.duration, maximum: it.role?.policy.maximumDuration ?? RolePolicy.manualDefault.maximumDuration)
                        .labelsHidden().frame(width: 110)
                } else {
                    Text(Countdown.label(it.duration)).font(.caption).foregroundStyle(.secondary).frame(width: 110, alignment: .trailing)
                }
                HStack(spacing: 0) { Spacer(minLength: 0); statusLabel(for: it) }.frame(width: 150)
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

    /// Azure captions are the scope's display name; the full ARM path is one hover away.
    private func scopeTooltip(_ key: RoleKey) -> String? {
        if case .azureResource(let scope, _) = key.scope { return scope }
        return nil
    }

    @ViewBuilder private func statusLabel(for it: ProfilePlanItem) -> some View {
        ActivationProgressLabel(result: model.progress[it.roleKey] ?? rowResults[it.roleKey], running: running)
        if model.progress[it.roleKey] == nil && rowResults[it.roleKey] == nil && !running {
            if let policy = it.role?.policy, let caption = PolicyNotes.caption(for: policy) {
                Label(caption, systemImage: policy.requiresApproval ? "person.badge.clock" : "lock.shield")
                    .font(.caption).lineLimit(1).help(PolicyNotes.explanation(for: policy) ?? "")
            }
        }
    }

    private func load() {
        rowResults.removeAll()
        items = model.plan(for: profileId)
        if finished || justification.isEmpty { justification = profile?.lastJustification ?? "" }
        finished = false
        // The sheet is reused: a stale toggle, a start time or unchecked roles from the last run must not carry over.
        excluded.removeAll()
        scheduleStart = false
        startAt = Date.now.addingTimeInterval(3600)
        model.clearProgress(items.map(\.roleKey))
    }

    private func submit() async {
        rowResults.removeAll()
        running = true
        let ticket = needsTicket && !ticketNumber.isEmpty ? TicketInfo(number: ticketNumber, system: ticketSystem) : nil
        // Two minutes of headroom: a start the service sees as "now" would activate immediately.
        let start: Date? = scheduleStart ? max(startAt, Date.now.addingTimeInterval(120)) : nil
        // Unchecked roles never reach the model: no request, no run entry, no remembered duration.
        let chosen = items.filter { !excluded.contains($0.roleKey) }
        let outcomes = await model.runProfile(id: profileId, items: chosen, justification: justification, ticket: ticket,
                               startDateTime: start)
        for outcome in outcomes { rowResults[outcome.roleKey] = outcome.result }
        // A role may have become active after the sheet was opened. The execution recheck skips it.
        for index in items.indices where items[index].disposition == .activate && !excluded.contains(items[index].roleKey) {
            let item = items[index]
            if !outcomes.contains(where: { $0.roleKey == item.roleKey }), let current = model.active[item.roleKey] {
                items[index] = ProfilePlanItem(roleKey: item.roleKey, role: item.role, duration: item.duration,
                    disposition: current.status == .active ? .alreadyActive : .pending)
            }
        }
        running = false
        finished = true
    }
}
