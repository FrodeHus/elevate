import Foundation
import ServiceManagement
import ElevateCore

@MainActor
extension AppModel {
    // MARK: Global shortcut

    /// Re-registers the global shortcut from settings. Registering unregisters first, so calling
    /// this after every Settings change cannot leave a stale hot key behind.
    func applyHotKey() {
        hotKeys.unregister()
        hotKeyError = nil
        guard let binding = settings.hotKey, settings.hotKeyProfileId != nil else {
            hotKeys.onFire = nil
            return
        }
        hotKeys.onFire = { [weak self] in
            Task { @MainActor in await self?.runShortcutProfile() }
        }
        do {
            try hotKeys.register(binding)
        } catch {
            hotKeyError = error.localizedDescription
        }
    }

    /// What the global shortcut fires: runs the bound profile, or opens the Run sheet when it
    /// needs a justification, ticket or duration. A policy change can remove the managed profile
    /// the shortcut was bound to out from under it; that is not a reason to pop a Run sheet with
    /// nothing to run, so this bails out quietly (beyond the log and the notice) when the bound id
    /// no longer resolves to anything.
    func runShortcutProfile() async {
        guard let id = settings.hotKeyProfileId else { return }
        guard profile(id: id) != nil else {
            logError("Shortcut profile is no longer available")
            notice = "The profile bound to the shortcut is no longer available"
            return
        }
        if await quickRun(profileId: id) { return }
        // Needs a justification, ticket or duration: open the Run sheet instead.
        requestRun(id)
        pendingProfileRun = id
    }

    // MARK: Diagnostics

    /// Records one user-visible failure. Called wherever a tenant error, an approval error or a
    /// failure `notice` is set — purely informational notices are not errors and are not logged.
    /// Messages are clipped so one enormous service body cannot crowd the log out.
    // internal: every feature extension logs user-visible failures here
    func logError(_ message: String) {
        let trimmed = message.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !trimmed.isEmpty else { return }
        errorLog.append(String(trimmed.prefix(300)))
    }

    /// The plain-text report behind "Copy diagnostics". Everything it carries is already visible
    /// in the app; no client id, token or secret is passed to the renderer at all.
    func diagnosticsText() -> String {
        let accounts = state.identities.map { identity in
            DiagnosticsAccount(upn: identity.upn,
                               method: identity.signInMethod.displayName,
                               tenantCount: state.tenants(for: identity.id).count)
        }
        let tenants = state.tenants.map { tenant -> DiagnosticsTenant in
            var flags: [String] = []
            if tenant.discoveryMode == .manualRoles { flags.append("manual roles") }
            if tenant.azureUnavailableReason != nil { flags.append("Azure off") }
            if tenant.groupsUnavailableReason != nil { flags.append("Groups off") }
            if tenant.entraActivation?.reason != nil { flags.append("Entra view only") }
            if tenant.lastDiscoveryError != nil { flags.append("discovery error") }
            return DiagnosticsTenant(name: tenant.displayName, id: tenant.tenantId,
                                     mode: tenant.discoveryMode.rawValue, flags: flags)
        }
        let hotKey: String? = settings.hotKey.map { binding in
            let profile = settings.hotKeyProfileId.flatMap { self.profile(id: $0) }
            return "\(binding.display) → \(profile?.name ?? "no profile")"
        }
        let input = DiagnosticsInput(appVersion: BuildInfo.version,
                                     build: BuildInfo.build,
                                     signing: BuildInfo.signingDescription,
                                     os: ProcessInfo.processInfo.operatingSystemVersionString,
                                     accounts: accounts,
                                     tenants: tenants,
                                     profiles: state.profiles.map(\.name) + managedProfiles.map { "\($0.name) (managed)" },
                                     hotKey: hotKey,
                                     managed: settings.managed.isEmpty ? nil : DiagnosticsManaged(origin: settings.managed.origin ?? "unknown",
                                                                                                  keys: settings.managed.keysInEffect.map(\.rawValue),
                                                                                                  warnings: settings.managed.warnings),
                                     errors: errorLog.entries)
        return DiagnosticsReport.render(input)
    }

    // MARK: Launch at login

    /// Whether the app is registered to start at login, read from the system every time: the user
    /// can change it in System Settings, so a stored copy would go stale behind our back.
    var launchAtLoginStatus: SMAppService.Status { SMAppService.mainApp.status }

    /// Registers or unregisters this app as a login item. Throws so the view can show the reason
    /// and put the toggle back where it was.
    func setLaunchAtLogin(_ on: Bool) throws {
        launchAtLoginError = nil
        do {
            if on {
                try SMAppService.mainApp.register()
            } else {
                try SMAppService.mainApp.unregister()
            }
        } catch {
            let message = error.localizedDescription
            launchAtLoginError = message
            logError("Launch at login: \(message)")
            throw error
        }
    }

    // MARK: Updates

    /// Asks GitHub for the latest release and compares it with the running version.
    ///
    /// The automatic call at startup is throttled to once a day; the Settings button forces a
    /// check. A release the user dismissed is never offered again, but a forced check still
    /// reports it, so "Check for updates" is never silent.
    func checkForUpdates(force: Bool = false) async {
        // An organization can turn the check off entirely; then GitHub is never contacted, not
        // even by an explicit "Check for updates" (the button is replaced by a caption anyway).
        guard !settings.updateCheckDisabled else { return }
        if !force {
            if let last = settings.lastUpdateCheck, abs(Date().timeIntervalSince(last)) < 24 * 60 * 60 { return }
            guard isOnline else { return }
        }
        do {
            let latest = try await UpdateChecker(http: http).latest()
            settings.lastUpdateCheck = .now
            guard let latest else {
                updateCheckMessage = "No releases yet"
                return
            }
            let version = latest.tag.hasPrefix("v") ? String(latest.tag.dropFirst()) : latest.tag
            guard AppVersion.isNewer(latestTag: latest.tag, current: BuildInfo.version) else {
                updateCheckMessage = "You have the latest version"
                return
            }
            updateCheckMessage = "Elevate \(version) is available"
            // updateCheckMessage is set above, before the dismissal guard below, so Settings
            // always reports what the check actually found even when the banner itself stays
            // suppressed because the user already dismissed this version.
            // Compare the normalised version, not the raw tag: whether the release is tagged
            // "1.1.0" or "v1.1.0" must not decide whether a dismissal still holds.
            guard force || settings.dismissedUpdateVersion != version else { return }
            updateAvailable = (version, latest.url)
        } catch {
            let message = (error as? PIMError)?.userMessage ?? error.localizedDescription
            updateCheckMessage = "Could not check for updates: \(message)"
            logError("Update check: \(message)")
        }
    }

    /// Hides the update banner and remembers not to raise it again for this release.
    func dismissUpdate() {
        if let update = updateAvailable {
            settings.dismissedUpdateVersion = update.version
        }
        updateAvailable = nil
    }
}
