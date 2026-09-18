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
            let pinned = model.pinnedProfiles
            VStack(alignment: .leading, spacing: 0) {
                HStack(spacing: 6) {
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
                // Deleting a pinned chip confirms here, spanning the row's full width, not the
                // chip's own ~150 pt column: a card that narrow wrapped the message onto four lines
                // and truncated both button titles to "Ca…"/"Del…" (found in the visual check).
                // Suppressed while the "All profiles" popover is open: a pinned profile also shows
                // there, and `ProfilePopoverRow` presents its own card for it — the row the user
                // actually clicked. Without this, deleting a pinned profile from inside the popover
                // opened both cards at once, driven by the same `model.profileToDelete`.
                .rowLevelProfileDeleteConfirmation(pinned: pinned, suppressedByPopover: showAll)
            }
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
                // No count on the chip: four names already compete for the row; the tooltip and the All list carry it.
                Text(profile.name).font(.caption.weight(.medium)).lineLimit(1).truncationMode(.tail)
            }
            .padding(.horizontal, 9).padding(.vertical, 4)
            .background(.background, in: Capsule())
            .overlay(Capsule().strokeBorder(.quaternary))
        }
        .buttonStyle(.plain)
        .frame(maxWidth: 150)
        .help("Run \(profile.name) (\(ProfileSummary.caption(entries: profile.entries))). Option-click to run with the last reason and durations")
        .contextMenu { ProfileMenuItems(profile: profile) }
        // No inline confirmation on the chip itself: see `rowLevelProfileDeleteConfirmation`, which
        // renders it at the row's full width instead of this chip's ~150 pt column.
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
        // A row's delete confirmation is rendered by that row, so filtering it out of the list
        // would take the card with it and leave `model.profileToDelete` set with nothing to
        // confirm or cancel — the pinned row's own card stays suppressed while this popover is
        // open. Typing a query that hides the profile cancels its pending delete instead.
        .onChange(of: query) { model.cancelProfileDeleteIfHidden(visible: matching) }
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
                Image(systemName: profile.source == .managed ? "building.2" : (profile.pinned ? "star.fill" : "star"))
                    .font(.caption)
                    .foregroundStyle(profile.pinned && profile.source != .managed ? Color.orange : Color.secondary)
                    .frame(width: 16, height: 16).contentShape(Rectangle())
            }
            .buttonStyle(.plain)
            // A published profile's pin is the organization's choice, so the star is a marker here.
            .disabled(profile.source == .managed)
            .help(profile.source == .managed ? "Published by your organization"
                  : (profile.pinned ? "Unpin from the panel" : "Pin to the panel"))
            .accessibilityLabel(profile.source == .managed ? "Published by your organization"
                                : (profile.pinned ? "Unpin \(profile.name)" : "Pin \(profile.name)"))
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
        .inlineProfileDeleteConfirmation(profile)
    }
}

/// The menu behind a chip, a popover row and its ⋯ button: run, pin, manage, delete.
struct ProfileMenuItems: View {
    @Environment(AppModel.self) private var model
    @Environment(\.openWindow) private var openWindow
    let profile: ActivationProfile

    var body: some View {
        if !model.profileRuns(for: profile.id).isEmpty {
            Button("Deactivate roles…") { ProfileActions.open(.deactivateProfile(profile.id), openWindow: openWindow) }
        }
        Button("Run…") { ProfileActions.run(profile.id, model: model, openWindow: openWindow, silentlyIfPossible: false) }
        Button("Run with last reason") { ProfileActions.run(profile.id, model: model, openWindow: openWindow, silentlyIfPossible: true) }
            .disabled(profile.lastJustification == nil)
        Divider()
        Button(profile.source == .managed ? "Show…" : "Edit…") { model.profileToEdit = profile.id; ProfileActions.open(.manageProfiles, openWindow: openWindow) }
        // A published profile is the organization's: it cannot be pinned, unpinned or deleted here.
        if profile.source != .managed {
            if profile.pinned {
                Button("Unpin from panel") { model.setPinned(id: profile.id, false) }
            } else {
                Button("Pin to panel") { model.setPinned(id: profile.id, true) }
                    .disabled(!model.canPinAnotherProfile)
            }
        }
        Button("Manage profiles…") { ProfileActions.open(.manageProfiles, openWindow: openWindow) }
        if profile.source != .managed {
            Divider()
            Button("Delete…", role: .destructive) { model.profileToDelete = profile.id }
        }
    }
}

/// Shared entry points for running and opening, so chips, rows and menus behave identically.
enum ProfileActions {
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
    /// For `ManageProfilesView` only: it opens in a real window, where a `confirmationDialog` works.
    func profileDeleteConfirmation(_ profile: ActivationProfile) -> some View {
        modifier(ProfileDeleteConfirmation(profile: profile))
    }

    /// Same confirmation, inline: for the "All profiles" popover row, where a `confirmationDialog`
    /// steals key focus from the MenuBarExtra window and dismisses the panel before its buttons can
    /// be clicked. See InlineConfirm.swift. The pinned chip uses `rowLevelProfileDeleteConfirmation`
    /// instead, since a chip's own column is too narrow for the card.
    func inlineProfileDeleteConfirmation(_ profile: ActivationProfile) -> some View {
        modifier(InlineProfileDeleteConfirmation(profile: profile))
    }

    /// Attached to the whole pinned-chips row instead of one chip: shows the delete confirmation
    /// for whichever pinned profile `model.profileToDelete` names, at the row's full width.
    /// `suppressedByPopover` skips it while the "All profiles" popover is open, since that popover
    /// shows the same pinned profile and presents its own card for it instead.
    func rowLevelProfileDeleteConfirmation(pinned: [ActivationProfile], suppressedByPopover: Bool) -> some View {
        modifier(RowLevelProfileDeleteConfirmation(pinned: pinned, suppressedByPopover: suppressedByPopover))
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

private struct RowLevelProfileDeleteConfirmation: ViewModifier {
    @Environment(AppModel.self) private var model
    let pinned: [ActivationProfile]
    let suppressedByPopover: Bool

    func body(content: Content) -> some View {
        if !suppressedByPopover, let target = pinned.first(where: { $0.id == model.profileToDelete }) {
            content.inlineProfileDeleteConfirmation(target)
        } else {
            content
        }
    }
}

private struct InlineProfileDeleteConfirmation: ViewModifier {
    @Environment(AppModel.self) private var model
    let profile: ActivationProfile

    func body(content: Content) -> some View {
        content.inlineConfirmation(
            "Delete \"\(profile.name)\"?",
            message: "The profile and its \(ProfileSummary.caption(entries: profile.entries)) are removed. Active assignments are not changed.",
            confirmTitle: "Delete",
            isPresented: Binding(get: { model.profileToDelete == profile.id },
                                  set: { if !$0, model.profileToDelete == profile.id { model.profileToDelete = nil } })
        ) {
            model.deleteProfile(id: profile.id)
        }
    }
}
