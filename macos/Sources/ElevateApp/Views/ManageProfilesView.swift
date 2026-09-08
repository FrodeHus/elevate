import SwiftUI
import ElevateCore

/// The Profiles window: every profile on the left, the selected one edited in place on the right.
/// Changes apply as they are made; Done only closes.
struct ManageProfilesView: View {
    @Environment(AppModel.self) private var model
    @Environment(\.dismiss) private var dismiss
    @State private var selected: UUID?
    @State private var search = ""

    private var filtered: [ActivationProfile] {
        model.profiles.filter { PanelFilter.matches(query: search, text: $0.name) }
    }
    private var selectedProfile: ActivationProfile? { selected.flatMap { model.profile(id: $0) } }

    var body: some View {
        HStack(spacing: 0) {
            sidebar.frame(width: 210)
            Divider()
            if let p = selectedProfile {
                ProfileEditor(profile: p).id(p.id)
            } else {
                VStack(spacing: 8) {
                    Text(model.profiles.isEmpty ? "No profiles yet" : "Select a profile")
                        .font(.headline)
                    Text("Select roles in the panel and choose \"Save as profile…\", or press + to start an empty one here.")
                        .font(.caption).foregroundStyle(.secondary).multilineTextAlignment(.center)
                        .fixedSize(horizontal: false, vertical: true)
                }
                .padding(24).frame(maxWidth: .infinity, maxHeight: .infinity)
            }
        }
        .frame(width: 680, height: 440)
        .navigationTitle("Profiles")
        .onAppear { select(model.profileToEdit ?? model.profiles.first?.id) }
        .onChange(of: model.profileToEdit) { _, id in if id != nil { select(id) } }
        .onChange(of: model.profiles.map(\.id)) { _, ids in
            if let s = selected, !ids.contains(s) { selected = ids.first }
        }
    }

    /// Reordering only makes sense over the whole list; while filtering the handles are off.
    private var moveAction: ((IndexSet, Int) -> Void)? {
        search.isEmpty ? { model.moveProfile(fromOffsets: $0, toOffset: $1) } : nil
    }

    private func select(_ id: UUID?) {
        selected = id
        if model.profileToEdit != nil { model.profileToEdit = nil }
    }

    private var sidebar: some View {
        VStack(spacing: 0) {
            HStack(spacing: 6) {
                Image(systemName: "magnifyingglass").foregroundStyle(.secondary)
                TextField("Search", text: $search).textFieldStyle(.plain)
            }
            .padding(.horizontal, 10).padding(.vertical, 8)
            Divider()
            List(selection: $selected) {
                ForEach(filtered) { p in
                    HStack(spacing: 7) {
                        Image(systemName: p.pinned ? "star.fill" : "star")
                            .font(.caption).foregroundStyle(p.pinned ? Color.orange : Color.secondary)
                            .accessibilityLabel(p.pinned ? "Pinned" : "Not pinned")
                        VStack(alignment: .leading, spacing: 1) {
                            Text(p.name).lineLimit(1)
                            Text(ProfileSummary.caption(entries: p.entries)).font(.caption2).foregroundStyle(.secondary)
                        }
                    }
                    .tag(p.id)
                }
                .onMove(perform: moveAction)
            }
            .listStyle(.sidebar)
            Divider()
            HStack {
                Button { selected = model.newProfile().id; search = "" } label: { Image(systemName: "plus") }
                    .buttonStyle(.borderless).help("New profile").accessibilityLabel("New profile")
                Spacer()
                Text(search.isEmpty ? "Drag to reorder" : "\(filtered.count) of \(model.profiles.count)")
                    .font(.caption2).foregroundStyle(.secondary)
            }
            .padding(.horizontal, 10).padding(.vertical, 6)
        }
    }
}

/// One profile's editor: name, pin, shortcut, and its roles with the durations the next run proposes.
private struct ProfileEditor: View {
    @Environment(AppModel.self) private var model
    @Environment(\.dismiss) private var dismiss
    @Environment(\.openWindow) private var openWindow
    let profile: ActivationProfile
    @State private var name: String
    @State private var pinHint: String?
    @State private var showPicker = false
    @FocusState private var nameFocused: Bool

    init(profile: ActivationProfile) {
        self.profile = profile
        _name = State(initialValue: profile.name)
    }

    private var tenantKeys: [TenantKey] {
        var seen: [TenantKey] = []
        for e in profile.entries where !seen.contains(e.roleKey.tenantKey) { seen.append(e.roleKey.tenantKey) }
        return seen
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 10) {
            HStack(spacing: 10) {
                TextField("Profile name", text: $name)
                    .textFieldStyle(.roundedBorder)
                    .focused($nameFocused)
                    .onSubmit(commitName)
                    .onChange(of: nameFocused) { _, focused in if !focused { commitName() } }
                Button("Run…") { commitName(); ProfileActions.run(profile.id, model: model, openWindow: openWindow, silentlyIfPossible: false) }
                    .disabled(profile.entries.isEmpty)
            }
            HStack(alignment: .firstTextBaseline, spacing: 18) {
                VStack(alignment: .leading, spacing: 2) {
                    Toggle("Pinned in panel", isOn: pinnedBinding).toggleStyle(.switch).controlSize(.small)
                    if let pinHint { Text(pinHint).font(.caption2).foregroundStyle(.orange) }
                }
                VStack(alignment: .leading, spacing: 2) {
                    Toggle("Runs with the global shortcut", isOn: hotKeyBinding).toggleStyle(.switch).controlSize(.small)
                    HStack(spacing: 4) {
                        if let key = model.settings.hotKey {
                            Text(key.display).font(.caption2).foregroundStyle(.secondary)
                        } else {
                            Text("No shortcut recorded.").font(.caption2).foregroundStyle(.secondary)
                        }
                        SettingsLink { Text("Settings…").font(.caption2) }.buttonStyle(.borderless)
                    }
                }
            }
            roles
            HStack {
                Button("Delete…", role: .destructive) { model.profileToDelete = profile.id }
                Spacer()
                Text("Changes save as you go").font(.caption2).foregroundStyle(.secondary)
                Button("Done") { commitName(); dismiss() }.keyboardShortcut(.defaultAction)
            }
        }
        .padding(14)
        .profileDeleteConfirmation(profile)
    }

    private var roles: some View {
        VStack(alignment: .leading, spacing: 0) {
            ScrollView {
                VStack(alignment: .leading, spacing: 8) {
                    if profile.entries.isEmpty {
                        Text("No roles yet. \"Add roles…\" picks from every account and tenant.")
                            .font(.caption).foregroundStyle(.secondary).padding(10)
                    }
                    ForEach(tenantKeys, id: \.self) { tk in
                        TenantGroup(tenantKey: tk) {
                            ForEach(profile.entries.filter { $0.roleKey.tenantKey == tk }, id: \.roleKey) { entry in
                                row(entry)
                            }
                        }
                    }
                }
                .padding(8)
            }
            Divider()
            HStack(spacing: 8) {
                Button("Add roles…") { showPicker = true }
                    .popover(isPresented: $showPicker, arrowEdge: .top) {
                        AddRolesPicker(profile: profile) { keys in
                            model.addProfileEntries(id: profile.id, keys: keys)
                            showPicker = false
                        }
                    }
                Text("Durations are what the next run proposes; you can still change them then.")
                    .font(.caption2).foregroundStyle(.secondary).lineLimit(2)
            }
            .padding(8)
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity)
        .background(.background, in: RoundedRectangle(cornerRadius: 8))
        .overlay(RoundedRectangle(cornerRadius: 8).strokeBorder(.quaternary))
    }

    private func row(_ entry: ActivationProfile.Entry) -> some View {
        let key = entry.roleKey
        let role = model.role(for: key)
        let policy = role?.policy ?? .manualDefault
        return HStack(spacing: 8) {
            VStack(alignment: .leading, spacing: 1) {
                Text(model.summaryName(for: key)).lineLimit(1)
                HStack(spacing: 4) {
                    Text(kindLabel(key.scope.kind))
                    if let detail = role?.detail { Text("·"); Text(detail).lineLimit(1) }
                    if let notes = PolicyNotes.caption(for: policy) { Text("·"); Text(notes) }
                    if role == nil { Text("·"); Text("not loaded") }
                }
                .font(.caption2).foregroundStyle(.secondary)
            }
            Spacer()
            DurationPicker(duration: durationBinding(entry, policy: policy), maximum: policy.maximumDuration)
                .labelsHidden().frame(width: 110)
            Button { model.removeProfileEntry(id: profile.id, key: key) } label: { Image(systemName: "minus.circle") }
                .buttonStyle(.borderless).help("Remove from profile")
                .accessibilityLabel("Remove \(model.summaryName(for: key))")
        }
    }

    private func kindLabel(_ kind: RoleScopeKind) -> String {
        switch kind { case .entraDirectory: "Entra"; case .azureResource: "Azure"; case .group: "Group" }
    }

    /// What the next run would propose today: the entry's own duration, else memory, else the policy.
    private func durationBinding(_ entry: ActivationProfile.Entry, policy: RolePolicy) -> Binding<Duration> {
        Binding(
            get: { min(entry.lastDuration ?? model.remembered(for: entry.roleKey)?.lastDuration ?? policy.defaultDuration, policy.maximumDuration) },
            set: { model.setProfileEntryDuration(id: profile.id, key: entry.roleKey, duration: $0) })
    }

    private var pinnedBinding: Binding<Bool> {
        Binding(get: { profile.pinned }, set: { on in
            pinHint = model.setPinned(id: profile.id, on) ? nil : "Unpin another first: \(ProfilePins.limit) is the most the row holds"
        })
    }

    private var hotKeyBinding: Binding<Bool> {
        Binding(get: { model.settings.hotKeyProfileId == profile.id },
                set: { on in model.setHotKeyProfile(on ? profile.id : nil) })
    }

    private func commitName() {
        let trimmed = name.trimmingCharacters(in: .whitespacesAndNewlines)
        if trimmed.isEmpty { name = profile.name } else if trimmed != profile.name { model.renameProfile(id: profile.id, name: trimmed) }
    }
}
