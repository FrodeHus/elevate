import SwiftUI
import ElevateCore

/// The run form for one profile, rendered from its `ProfileRunSession` in the app model. The run
/// window hosts it at 560 pt; the panel hosts it compact at 380 pt. `onDone` non-nil draws Cancel and Done.
struct ProfileRunContent: View {
    @Environment(AppModel.self) private var model
    let profileId: UUID
    let compact: Bool
    var onDone: (() -> Void)?
    @FocusState private var reasonFocused: Bool

    var body: some View {
        if let session = model.runSessions[profileId] {
            content(session)
        } else {
            // Creating the session writes to the model, which must not happen while rendering.
            Color.clear.frame(height: 1).onAppear { _ = model.runSession(for: profileId) }
        }
    }

    private func content(_ session: ProfileRunSession) -> some View {
        @Bindable var session = session
        let tenantKeys = groupedTenantKeys(session)
        return VStack(alignment: .leading, spacing: 12) {
            VStack(alignment: .leading, spacing: 8) {
                ForEach(tenantKeys, id: \.self) { tk in
                    TenantGroup(tenantKey: tk) {
                        ForEach($session.items) { $item in
                            if item.roleKey.tenantKey == tk { row($item, session: session) }
                        }
                    }
                }
            }
            if !session.finished {
                startAtRow(session)
                TextField("Reason", text: $session.justification, axis: .vertical).lineLimit(2...4)
                    .focused($reasonFocused)
                    .onSubmit { Task { await model.submitRun(profileId) } }
                if session.needsTicket {
                    HStack { TextField("Ticket number", text: $session.ticketNumber); TextField("Ticket system", text: $session.ticketSystem) }
                }
            }
            buttons(session)
        }
        .onAppear {
            reasonFocused = true
            if session.finished { model.markRunSeen(profileId) }
        }
        .onChange(of: session.finished) { _, finished in if finished { model.markRunSeen(profileId) } }
    }

    private func groupedTenantKeys(_ session: ProfileRunSession) -> [TenantKey] {
        var seen: [TenantKey] = []
        for i in session.items where !seen.contains(i.roleKey.tenantKey) { seen.append(i.roleKey.tenantKey) }
        return seen
    }

    @ViewBuilder private func buttons(_ session: ProfileRunSession) -> some View {
        let caption = Text("Entries already active or unchecked are skipped. Approval-required entries are requested and shown as pending.")
            .font(.caption).foregroundStyle(.secondary).fixedSize(horizontal: false, vertical: true)
        let activate = Button("Activate \(session.toActivate.count)") { Task { await model.submitRun(profileId) } }
            .buttonStyle(.borderedProminent).disabled(!session.canSubmit)
        if compact {
            VStack(alignment: .leading, spacing: 8) {
                caption
                HStack {
                    Spacer()
                    if let onDone { doneOrCancel(session, onDone) }
                    if !session.finished { activate.keyboardShortcut(.defaultAction) }
                }
            }
        } else {
            HStack(alignment: .firstTextBaseline) {
                caption
                Spacer()
                if let onDone { doneOrCancel(session, onDone) }
                if !session.finished { activate.keyboardShortcut(.defaultAction) }
            }
        }
    }

    @ViewBuilder private func doneOrCancel(_ session: ProfileRunSession, _ onDone: @escaping () -> Void) -> some View {
        if session.finished {
            Button("Done", action: onDone).keyboardShortcut(.defaultAction).buttonStyle(.borderedProminent)
        } else {
            Button("Cancel", action: onDone).keyboardShortcut(.cancelAction).disabled(session.running)
        }
    }

    /// The picker's lower bound is "now", so it is recomputed on every render rather than captured once.
    @ViewBuilder private func startAtRow(_ session: ProfileRunSession) -> some View {
        @Bindable var session = session
        let notBefore = Date.now
        HStack(spacing: 8) {
            Toggle("Start at", isOn: $session.scheduleStart)
            if session.scheduleStart {
                DatePicker("", selection: $session.startAt, in: notBefore..., displayedComponents: [.date, .hourAndMinute])
                    .labelsHidden()
            }
        }
    }

    @ViewBuilder private func row(_ item: Binding<ProfilePlanItem>, session: ProfileRunSession) -> some View {
        let it = item.wrappedValue
        let included = !session.excluded.contains(it.roleKey)
        let dimmed = !(it.disposition == .activate && included)
        HStack(alignment: compact ? .top : .center, spacing: 8) {
            if it.disposition == .activate {
                Toggle("Include \(it.role?.displayName ?? model.summaryName(for: it.roleKey))", isOn: Binding(
                    get: { included },
                    set: { on in if on { session.excluded.remove(it.roleKey) } else { session.excluded.insert(it.roleKey) } }))
                    .toggleStyle(.checkbox).labelsHidden().disabled(session.running || session.finished)
            } else {
                // Skipped rows have nothing to choose; reserve the checkbox width so names line up.
                Toggle("", isOn: .constant(false)).toggleStyle(.checkbox).labelsHidden().hidden()
            }
            if compact {
                // The duration sits at the end of the name's line; the detail and any status share the line below.
                VStack(alignment: .leading, spacing: 1) {
                    HStack(alignment: .firstTextBaseline, spacing: 8) {
                        RoleNameLabel(it.role?.displayName ?? model.summaryName(for: it.roleKey), isPrivileged: model.isPrivileged(it.roleKey)).opacity(dimmed ? 0.6 : 1)
                        Spacer(minLength: 0)
                        if it.disposition == .activate && included { durationControl(item, session: session) }
                    }
                    HStack(spacing: 8) {
                        if let detail = it.role?.detail {
                            Text(detail).font(.caption2).foregroundStyle(.secondary).lineLimit(1).help(scopeTooltip(it.roleKey) ?? detail)
                                .opacity(dimmed ? 0.6 : 1)
                        }
                        trailing(item, session: session, included: included)
                    }
                }
            } else {
                nameBlock(it, dimmed: dimmed)
                Spacer()
                trailing(item, session: session, included: included)
            }
        }
    }

    private func nameBlock(_ it: ProfilePlanItem, dimmed: Bool) -> some View {
        VStack(alignment: .leading, spacing: 1) {
            RoleNameLabel(it.role?.displayName ?? model.summaryName(for: it.roleKey), isPrivileged: model.isPrivileged(it.roleKey))
            if let detail = it.role?.detail {
                Text(detail).font(.caption2).foregroundStyle(.secondary).lineLimit(1).help(scopeTooltip(it.roleKey) ?? detail)
            }
        }
        .opacity(dimmed ? 0.6 : 1)
    }

    @ViewBuilder private func trailing(_ item: Binding<ProfilePlanItem>, session: ProfileRunSession, included: Bool) -> some View {
        let it = item.wrappedValue
        switch it.disposition {
        case .activate where !included:
            Text(session.finished ? "unchecked · skipped" : "unchecked").font(.caption).foregroundStyle(.secondary)
        case .activate:
            // Window: fixed columns, so rows line up even when a column is empty. Panel: natural widths under the name.
            if !compact { durationControl(item, session: session) }
            if compact {
                statusLabel(for: it, running: session.running, rowResult: session.rowResults[it.roleKey])
            } else {
                HStack(spacing: 0) { Spacer(minLength: 0); statusLabel(for: it, running: session.running, rowResult: session.rowResults[it.roleKey]) }
                    .frame(width: 150)
            }
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

    /// The editable duration, or the chosen one once the run is over. The panel's row puts it on the name's line.
    @ViewBuilder private func durationControl(_ item: Binding<ProfilePlanItem>, session: ProfileRunSession) -> some View {
        let it = item.wrappedValue
        if !session.finished {
            DurationPicker(duration: item.duration, maximum: it.role?.policy.maximumDuration ?? RolePolicy.manualDefault.maximumDuration, compact: compact)
                .labelsHidden().frame(width: compact ? nil : 110)
        } else {
            Text(Countdown.label(it.duration)).font(.caption).foregroundStyle(.secondary)
                .frame(width: compact ? nil : 110, alignment: .trailing)
        }
    }

    /// Azure captions are the scope's display name; the full ARM path is one hover away.
    private func scopeTooltip(_ key: RoleKey) -> String? {
        if case .azureResource(let scope, _) = key.scope { return scope }
        return nil
    }

    @ViewBuilder private func statusLabel(for it: ProfilePlanItem, running: Bool, rowResult: ActivationOutcome.Result?) -> some View {
        let result = model.progress[it.roleKey] ?? rowResult
        ActivationProgressLabel(result: result, running: running)
        if result == nil && !running {
            if let policy = it.role?.policy, let caption = PolicyNotes.caption(for: policy) {
                Label(caption, systemImage: policy.requiresApproval ? "person.badge.clock" : "lock.shield")
                    .font(.caption).lineLimit(1).help(PolicyNotes.explanation(for: policy) ?? "")
            }
        }
    }
}
