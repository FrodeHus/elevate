import SwiftUI
import ElevateCore

struct DeactivateProfileView: View {
    @Environment(AppModel.self) private var model
    @Environment(\.dismiss) private var dismiss
    let profileId: UUID
    @State private var selectedRun: UUID?
    @State private var running = false
    @State private var attempted = false
    /// Roles the user unchecked for this pass only; they stay in the run for a later pass.
    @State private var excluded: Set<RoleKey> = []

    private var runs: [ProfileRun] { model.profileRunHistory(for: profileId) }
    private var run: ProfileRun? { runs.first { $0.id == selectedRun } }
    private var chosen: Set<RoleKey> {
        Set((run?.entries ?? []).filter { !$0.completed && !excluded.contains($0.assignment.roleKey) }.map(\.assignment.roleKey))
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            Text("Deactivate roles").font(.headline)
            Text("Choose a run. Only assignments created by that run are affected; roles it skipped remain active.")
                .font(.caption).foregroundStyle(.secondary)
            Picker("Run", selection: $selectedRun) {
                ForEach(runs) { run in
                    Text("\(run.profileName) · \(run.startedAt.formatted(date: .abbreviated, time: .standard))")
                        .tag(Optional(run.id))
                }
            }.disabled(running)
            ScrollView {
                VStack(alignment: .leading, spacing: 12) {
                    if let run {
                        ForEach(run.entries, id: \.assignment.roleKey) { entry in
                            let included = !excluded.contains(entry.assignment.roleKey)
                            HStack(alignment: .top) {
                                if !entry.completed {
                                    Toggle("Include \(entry.displayName)", isOn: Binding(
                                        get: { included },
                                        set: { on in if on { excluded.remove(entry.assignment.roleKey) } else { excluded.insert(entry.assignment.roleKey) } }))
                                        .toggleStyle(.checkbox).labelsHidden().disabled(running)
                                }
                                VStack(alignment: .leading, spacing: 3) {
                                    Text(entry.displayName)
                                    Text("\(model.identity(entry.assignment.roleKey.identityId)?.upn ?? entry.assignment.roleKey.identityId) · \(model.tenant(entry.assignment.roleKey.tenantKey)?.displayName ?? entry.assignment.roleKey.tenantId)")
                                        .font(.caption2).foregroundStyle(.secondary)
                                    let shared = model.sharedProfileNames(for: entry.assignment.roleKey, excluding: profileId)
                                    if !shared.isEmpty {
                                        Text("Also used by \(shared); deactivation removes that shared access.")
                                            .font(.caption).foregroundStyle(.orange)
                                    }
                                }
                                .opacity(included || entry.completed ? 1 : 0.6)
                                Spacer()
                                if !included, !entry.completed {
                                    Text("Unchecked · kept").font(.caption).foregroundStyle(.secondary)
                                } else if attempted, let phase = model.profileDeactivationProgress[run.id]?[entry.assignment.roleKey] {
                                    DeactivationProgressLabel(phase: phase)
                                } else if entry.completed {
                                    Text("Already handled").font(.caption).foregroundStyle(.secondary)
                                } else {
                                    TimelineView(.periodic(from: .now, by: 1)) { context in
                                        preview(entry, now: context.date)
                                    }
                                }
                            }
                        }
                    }
                }
            }.frame(maxHeight: 360)
            HStack {
                Button("Done") { dismiss() }.disabled(running)
                Spacer()
                Button(attempted ? "Retry remaining roles" : "Deactivate \(chosen.count)") {
                    guard let id = selectedRun else { return }
                    let only = chosen
                    running = true; attempted = true
                    Task { await model.deactivateProfileRun(id, only: only); running = false }
                }
                .buttonStyle(.borderedProminent)
                .disabled(running || !model.isOnline || chosen.isEmpty)
            }
        }
        .padding(16).frame(width: 620)
        .onAppear { selectedRun = runs.first(where: { $0.entries.contains(where: { !$0.completed }) })?.id ?? runs.first?.id }
        .onChange(of: selectedRun) { _, _ in attempted = false; excluded.removeAll() }
    }
    @ViewBuilder private func preview(_ entry: ProfileRun.Entry, now: Date) -> some View {
        if let current = model.active[entry.assignment.roleKey] {
            switch entry.disposition(current: current, now: now) {
            case .ready: Text("Active · ready").font(.caption).foregroundStyle(.secondary)
            case .completed: Text("Already handled").font(.caption).foregroundStyle(.secondary)
            case .blocked(let reason): Text(reason).font(.caption).foregroundStyle(.orange)
            case .skipped(let reason): Text(reason).font(.caption).foregroundStyle(.secondary)
            }
        } else {
            Text("Will verify assignment").font(.caption).foregroundStyle(.secondary)
        }
    }

}
