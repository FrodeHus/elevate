import SwiftUI
import AppKit
import ElevateCore

/// One row under the tab picker: a chip per pinned profile, never wrapping, and an "All N" button
/// that opens the full list. Hidden when there are no profiles at all.
struct ProfilesRow: View {
    @Environment(AppModel.self) private var model
    @State private var showAll = false

    var body: some View {
        if !model.profiles.isEmpty {
            HStack(spacing: 6) {
                let pinned = model.pinnedProfiles
                if pinned.isEmpty {
                    Text("Pin profiles to show them here").font(.caption).foregroundStyle(.secondary).lineLimit(1)
                }
                ForEach(pinned) { p in
                    ProfileChip(profile: p)
                }
                Spacer(minLength: 0)
                Button { showAll.toggle() } label: {
                    HStack(spacing: 4) {
                        Text("All \(model.profiles.count)").font(.caption.weight(.medium))
                        Image(systemName: "chevron.down").font(.system(size: 8, weight: .semibold))
                    }
                    .padding(.horizontal, 9).padding(.vertical, 4)
                    .background(.quaternary.opacity(0.6), in: Capsule())
                }
                .buttonStyle(.plain)
                .help("All profiles: search, run, pin and manage")
                .accessibilityLabel("All profiles, \(model.profiles.count)")
                .popover(isPresented: $showAll, arrowEdge: .bottom) {
                    AllProfilesPopover(dismiss: { showAll = false })
                }
            }
            .padding(.horizontal, 12).padding(.vertical, 7)
            Divider()
        }
    }
}

/// A pinned profile: click runs it, Option-click runs it silently, right-click for the rest.
struct ProfileChip: View {
    @Environment(AppModel.self) private var model
    @Environment(\.openWindow) private var openWindow
    let profile: ActivationProfile

    var body: some View {
        Button { ProfileActions.run(profile.id, model: model, openWindow: openWindow, silentlyIfPossible: NSEvent.modifierFlags.contains(.option)) } label: {
            HStack(spacing: 5) {
                Image(systemName: "bolt.fill").font(.caption2).foregroundStyle(Color.accentColor)
                Text(profile.name).font(.caption.weight(.medium)).lineLimit(1).truncationMode(.tail)
                Text(ProfileActions.shortCaption(profile)).font(.caption2).foregroundStyle(.secondary).fixedSize()
            }
            .padding(.horizontal, 9).padding(.vertical, 4)
            .background(.background, in: Capsule())
            .overlay(Capsule().strokeBorder(.quaternary))
        }
        .buttonStyle(.plain)
        .frame(maxWidth: 150)
        .help("Run \(profile.name) (\(ProfileSummary.caption(entries: profile.entries))). Option-click to run with the last reason and durations")
        .contextMenu { ProfileMenuItems(profile: profile) }
        .profileDeleteConfirmation(profile)
    }
}

/// The searchable list behind "All N": pinned profiles first, then the rest. Return runs the
/// first match; the star pins or unpins in place.
struct AllProfilesPopover: View {
    @Environment(AppModel.self) private var model
    @Environment(\.openWindow) private var openWindow
    let dismiss: () -> Void
    @State private var query = ""
    @FocusState private var searchFocused: Bool
    @State private var pinHint: String?

    private var matching: [ActivationProfile] {
        model.profiles.filter { PanelFilter.matches(query: query, text: $0.name) }
    }
    private var pinned: [ActivationProfile] { matching.filter(\.pinned) }
    private var others: [ActivationProfile] { matching.filter { !$0.pinned } }

    var body: some View {
        VStack(alignment: .leading, spacing: 0) {
            HStack(spacing: 6) {
                Image(systemName: "magnifyingglass").foregroundStyle(.secondary)
                TextField("Search profiles", text: $query)
                    .textFieldStyle(.plain)
                    .focused($searchFocused)
                    .onSubmit { if let first = matching.first { run(first, silently: NSEvent.modifierFlags.contains(.option)) } }
                    .onExitCommand { if query.isEmpty { dismiss() } else { query = "" } }
            }
            .padding(.horizontal, 10).padding(.vertical, 8)
            Divider()
            ScrollView {
                VStack(alignment: .leading, spacing: 1) {
                    if matching.isEmpty {
                        Text("No matches").font(.caption).foregroundStyle(.secondary).padding(10)
                    }
                    if !pinned.isEmpty {
                        sectionLabel("Pinned")
                        ForEach(pinned) { p in row(p) }
                    }
                    if !others.isEmpty {
                        sectionLabel(pinned.isEmpty ? "Profiles" : "All profiles")
                        ForEach(others) { p in row(p) }
                    }
                }
                .padding(6)
            }
            .frame(maxHeight: 320)
            Divider()
            HStack {
                Button("Manage profiles…") { dismiss(); ProfileActions.open(.manageProfiles, openWindow: openWindow) }
                    .buttonStyle(.borderless)
                Spacer()
                Text(pinHint ?? "⏎ runs the first match · ⌥⏎ runs it silently").font(.caption2).foregroundStyle(pinHint == nil ? Color.secondary : Color.orange)
            }
            .padding(.horizontal, 10).padding(.vertical, 8)
        }
        .frame(width: 340)
        .onAppear { searchFocused = true }
    }

    private func sectionLabel(_ text: String) -> some View {
        Text(text.uppercased()).font(.caption2.weight(.semibold)).foregroundStyle(.secondary)
            .padding(.horizontal, 6).padding(.top, 6).padding(.bottom, 2)
    }

    private func row(_ p: ActivationProfile) -> some View {
        ProfilePopoverRow(profile: p, run: { run(p, silently: $0) }, togglePin: { togglePin(p) })
    }

    private func run(_ p: ActivationProfile, silently: Bool) {
        dismiss()
        ProfileActions.run(p.id, model: model, openWindow: openWindow, silentlyIfPossible: silently)
    }

    private func togglePin(_ p: ActivationProfile) {
        if model.setPinned(id: p.id, !p.pinned) {
            pinHint = nil
        } else {
            pinHint = "Unpin one first: \(ProfilePins.limit) is the most the row holds"
        }
    }
}

private struct ProfilePopoverRow: View {
    @Environment(AppModel.self) private var model
    let profile: ActivationProfile
    let run: (Bool) -> Void
    let togglePin: () -> Void
    @State private var hovering = false

    private var activeCount: Int {
        profile.entries.filter { model.assignment(for: $0.roleKey)?.status == .active }.count
    }

    var body: some View {
        HStack(spacing: 8) {
            Button(action: togglePin) {
                Image(systemName: profile.pinned ? "star.fill" : "star")
                    .font(.caption).foregroundStyle(profile.pinned ? Color.orange : Color.secondary)
                    .frame(width: 16, height: 16).contentShape(Rectangle())
            }
            .buttonStyle(.plain)
            .help(profile.pinned ? "Unpin from the panel" : "Pin to the panel")
            .accessibilityLabel(profile.pinned ? "Unpin \(profile.name)" : "Pin \(profile.name)")
            Text(profile.name).font(.callout.weight(.medium)).lineLimit(1)
            Text(ProfileSummary.caption(entries: profile.entries)).font(.caption).foregroundStyle(.secondary).lineLimit(1)
            Spacer(minLength: 4)
            if activeCount > 0, !hovering {
                Text("\(activeCount) active").font(.caption).foregroundStyle(.green)
            }
            if hovering {
                Button("Run") { run(NSEvent.modifierFlags.contains(.option)) }.controlSize(.small)
                Menu {
                    ProfileMenuItems(profile: profile)
                } label: {
                    Image(systemName: "ellipsis.circle").font(.body)
                }
                .menuStyle(.borderlessButton).menuIndicator(.hidden).fixedSize()
                .accessibilityLabel("More actions for \(profile.name)")
            }
        }
        .padding(.horizontal, 6).padding(.vertical, 4)
        .frame(minHeight: 28)
        .background(hovering ? Color.primary.opacity(0.06) : Color.clear, in: RoundedRectangle(cornerRadius: 6))
        .contentShape(Rectangle())
        .onTapGesture { run(NSEvent.modifierFlags.contains(.option)) }
        .onHover { hovering = $0 }
        .contextMenu { ProfileMenuItems(profile: profile) }
        .profileDeleteConfirmation(profile)
    }
}

/// The menu behind a chip, a popover row and its ⋯ button: run, pin, manage, delete.
struct ProfileMenuItems: View {
    @Environment(AppModel.self) private var model
    @Environment(\.openWindow) private var openWindow
    let profile: ActivationProfile

    var body: some View {
        Button("Run…") { ProfileActions.run(profile.id, model: model, openWindow: openWindow, silentlyIfPossible: false) }
        Button("Run with last reason") { ProfileActions.run(profile.id, model: model, openWindow: openWindow, silentlyIfPossible: true) }
            .disabled(profile.lastJustification == nil)
        Divider()
        if profile.pinned {
            Button("Unpin from panel") { model.setPinned(id: profile.id, false) }
        } else {
            Button("Pin to panel") { model.setPinned(id: profile.id, true) }
                .disabled(model.pinnedProfiles.count >= ProfilePins.limit)
        }
        Button("Manage profiles…") { ProfileActions.open(.manageProfiles, openWindow: openWindow) }
        Divider()
        Button("Delete…", role: .destructive) { model.profileToDelete = profile.id }
    }
}

/// Shared entry points for running and opening, so chips, rows and menus behave identically.
enum ProfileActions {
    /// "5 · 1" for five roles and a group; "3" for roles only. The long form is the tooltip.
    static func shortCaption(_ p: ActivationProfile) -> String {
        let groups = p.entries.filter { $0.roleKey.scope.kind == .group }.count
        let roles = p.entries.count - groups
        switch (roles, groups) {
        case (_, 0): return "\(roles)"
        case (0, _): return "\(groups)"
        default: return "\(roles) · \(groups)"
        }
    }

    /// Runs a profile: silently with the remembered reason and durations when asked and possible,
    /// otherwise through the run sheet.
    @MainActor
    static func run(_ id: UUID, model: AppModel, openWindow: OpenWindowAction, silentlyIfPossible: Bool) {
        if silentlyIfPossible {
            Task { if await !model.quickRun(profileId: id) { model.requestRun(id); open(.runProfile(id), openWindow: openWindow) } }
        } else {
            model.requestRun(id); open(.runProfile(id), openWindow: openWindow)
        }
    }

    @MainActor
    static func open(_ route: PanelRoute, openWindow: OpenWindowAction) {
        openWindow(value: route)
        NSApp.activate(ignoringOtherApps: true)
    }
}

extension View {
    /// The "Delete profile?" dialog, attached to whatever view offers the menu that asks for it.
    func profileDeleteConfirmation(_ profile: ActivationProfile) -> some View {
        modifier(ProfileDeleteConfirmation(profile: profile))
    }
}

private struct ProfileDeleteConfirmation: ViewModifier {
    @Environment(AppModel.self) private var model
    let profile: ActivationProfile

    func body(content: Content) -> some View {
        content.confirmationDialog("Delete \"\(profile.name)\"?",
                                   isPresented: Binding(get: { model.profileToDelete == profile.id },
                                                        set: { if !$0, model.profileToDelete == profile.id { model.profileToDelete = nil } }),
                                   titleVisibility: .visible) {
            Button("Delete", role: .destructive) { model.deleteProfile(id: profile.id) }
            Button("Cancel", role: .cancel) {}
        } message: {
            Text("The profile and its \(ProfileSummary.caption(entries: profile.entries)) are removed. Active assignments are not changed.")
        }
    }
}
