import SwiftUI
import ElevateCore

/// Per-tenant access packages: what can be requested, what is pending, held or declined.
struct AccessPackagesView: View {
    @Environment(AppModel.self) private var model
    @Environment(\.dismiss) private var dismiss
    let tenantKey: TenantKey

    enum Tab: String, CaseIterable, Identifiable {
        case available = "Available", requested = "Requested", assigned = "Assigned", declined = "Declined"
        var id: String { rawValue }
    }

    /// One searchable line: a package, a request or an assignment.
    struct Row: Identifiable, Hashable {
        let id: String
        let name: String
        let detail: String?
    }

    @State private var tab: Tab = .available
    @State private var search = ""
    @State private var packages: [AccessPackage] = []
    @State private var packagesError: String?
    @State private var loadingPackages = false
    @State private var requesting: AccessPackage?
    @State private var cancelling: Set<String> = []
    @State private var actionError: String?

    private var tenantName: String { model.tenant(tenantKey)?.displayName ?? tenantKey.tenantId }
    private var snapshot: AccessPackageSnapshot { model.accessPackageSnapshot(tenantKey) ?? AccessPackageSnapshot() }
    private var consentError: String? {
        let pollError = model.accessPackageErrors[tenantKey]
        if pollError == PIMError.consentRequired.userMessage { return pollError }
        if packagesError == PIMError.consentRequired.userMessage { return packagesError }
        return nil
    }

    static func filtered(_ rows: [Row], query: String) -> [Row] {
        guard PanelFilter.isActive(query) else { return rows }
        return rows.filter { PanelFilter.matches(query: query, text: $0.name) || ($0.detail.map { PanelFilter.matches(query: query, text: $0) } ?? false) }
    }

    /// What (if anything) to show in place of the rows: nil when there are rows to show, the
    /// tab's own empty-collection caption when there is nothing to filter, or a "no matches"
    /// caption when the search narrowed a non-empty collection down to nothing.
    static func emptyCaption(total: Int, filtered: Int, query: String, emptyText: String) -> String? {
        guard filtered == 0 else { return nil }
        guard total > 0, PanelFilter.isActive(query) else { return emptyText }
        return "No matches for \u{201C}\(query.trimmingCharacters(in: .whitespacesAndNewlines))\u{201D}."
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            Text("Packages you can request through Entra entitlement management. Approvals and delivery happen in Entra.")
                .font(.caption).foregroundStyle(.secondary)
            Picker("", selection: $tab) { ForEach(Tab.allCases) { Text($0.rawValue).tag($0) } }
                .pickerStyle(.segmented).labelsHidden()
            TextField("Search packages", text: $search)
            if let consentError {
                VStack(alignment: .leading, spacing: 6) {
                    Label("Access packages are not permitted in this tenant.", systemImage: "info.circle")
                    Text(consentError).font(.caption).foregroundStyle(.secondary)
                    if let url = model.adminConsentURL(identityId: tenantKey.identityId, tenantId: tenantKey.tenantId) {
                        Button("Open admin consent link…") { NSWorkspace.shared.open(url) }
                    }
                }
                Spacer()
            } else {
                content
            }
            if let actionError {
                Label(actionError, systemImage: "exclamationmark.triangle").font(.caption).foregroundStyle(.red)
                    .fixedSize(horizontal: false, vertical: true)
            } else if let pollError = model.accessPackageErrors[tenantKey], pollError != PIMError.consentRequired.userMessage {
                Label(pollError, systemImage: "exclamationmark.triangle").font(.caption).foregroundStyle(.red)
                    .fixedSize(horizontal: false, vertical: true)
            }
            HStack(spacing: 8) {
                Button { Task { await reload() } } label: { Image(systemName: "arrow.clockwise") }
                    .accessibilityLabel("Refresh")
                Text(updatedCaption).font(.caption).foregroundStyle(.secondary)
                if loadingPackages || !model.accessPackagesPolling.isDisjoint(with: [tenantKey]) { ProgressView().controlSize(.small) }
                Spacer()
                Button("Close") { dismiss() }.keyboardShortcut(.cancelAction)
            }
        }
        .padding(16)
        .frame(width: 560, height: 520)
        .navigationTitle("Access packages in \(tenantName)")
        .task(id: tenantKey) { await reload() }
        .onChange(of: tab) { _, _ in Task { await model.pollAccessPackages(tenantKey) } }
        .sheet(item: $requesting) { package in
            RequestPackageSheet(tenantKey: tenantKey, package: package) {
                tab = .requested
            }
            .environment(model)
        }
    }

    @ViewBuilder private var content: some View {
        switch tab {
        case .available: availableList
        case .requested: requestedList
        case .assigned: assignedList
        case .declined: declinedList
        }
    }

    // MARK: Available

    private var availableRows: [(Row, AccessPackage)] {
        let rows = packages.map { Row(id: $0.id, name: $0.displayName, detail: $0.description) }
        let byId = Dictionary(packages.map { ($0.id, $0) }, uniquingKeysWith: { a, _ in a })
        return Self.filtered(rows, query: search).compactMap { row in byId[row.id].map { (row, $0) } }
    }

    private var availableList: some View {
        List {
            if let packagesError, consentError == nil {
                Text(packagesError).font(.caption).foregroundStyle(.red)
            } else if !loadingPackages, let caption = Self.emptyCaption(
                total: packages.count, filtered: availableRows.count, query: search,
                emptyText: "No access packages are available for you to request."
            ) {
                Text(caption).font(.caption).foregroundStyle(.secondary)
            }
            ForEach(availableRows, id: \.0.id) { row, package in
                HStack(alignment: .top, spacing: 10) {
                    VStack(alignment: .leading, spacing: 2) {
                        Text(package.displayName)
                        if let d = package.description, !d.isEmpty { Text(d).font(.caption).foregroundStyle(.secondary).lineLimit(2) }
                    }
                    Spacer()
                    if let state = existingState(for: package.id) {
                        stateLabel(state.text, color: state.color)
                    } else {
                        Button("Request") { requesting = package }.buttonStyle(.borderedProminent).controlSize(.small)
                    }
                }
                .padding(.vertical, 2)
            }
        }
    }

    /// A pending request or delivered assignment for this package, shown instead of Request.
    private func existingState(for packageId: String) -> (text: String, color: Color)? {
        if let r = snapshot.requests.first(where: { $0.packageId == packageId && $0.state.isOpen }) {
            return (Self.label(r.state), Self.color(r.state))
        }
        if snapshot.assignments.contains(where: { $0.packageId == packageId && $0.state == .delivered }) {
            return ("assigned", .green)
        }
        return nil
    }

    // MARK: Requested

    private var requestedList: some View {
        let open = snapshot.requests.filter { $0.state.isOpen }.sorted { ($0.createdAt ?? .distantPast) > ($1.createdAt ?? .distantPast) }
        let rows = Self.filtered(open.map { Row(id: $0.id, name: $0.packageName, detail: $0.justification) }, query: search)
        let byId = Dictionary(open.map { ($0.id, $0) }, uniquingKeysWith: { a, _ in a })
        return List {
            if let caption = Self.emptyCaption(total: open.count, filtered: rows.count, query: search, emptyText: "No requests in progress.") {
                Text(caption).font(.caption).foregroundStyle(.secondary)
            }
            ForEach(rows) { row in
                if let r = byId[row.id] {
                    HStack(alignment: .top, spacing: 10) {
                        VStack(alignment: .leading, spacing: 2) {
                            HStack(spacing: 6) { Text(r.packageName); stateLabel(Self.label(r.state), color: Self.color(r.state)) }
                            Text(Self.requestCaption(r)).font(.caption).foregroundStyle(.secondary).lineLimit(2)
                        }
                        Spacer()
                        if r.state.isCancellable {
                            Button(cancelling.contains(r.id) ? "Cancelling…" : "Cancel request") { Task { await cancel(r) } }
                                .controlSize(.small).disabled(cancelling.contains(r.id))
                        }
                    }
                    .padding(.vertical, 2)
                }
            }
        }
    }

    // MARK: Assigned

    private var assignedList: some View {
        let held = snapshot.assignments.filter { $0.state == .delivered }
        let rows = Self.filtered(held.map { Row(id: $0.id, name: $0.packageName, detail: $0.policyName) }, query: search)
        let byId = Dictionary(held.map { ($0.id, $0) }, uniquingKeysWith: { a, _ in a })
        return List {
            if let caption = Self.emptyCaption(total: held.count, filtered: rows.count, query: search, emptyText: "No access packages are assigned to you.") {
                Text(caption).font(.caption).foregroundStyle(.secondary)
            }
            ForEach(rows) { row in
                if let a = byId[row.id] {
                    let soon = Self.expiresSoon(a)
                    HStack(alignment: .top, spacing: 10) {
                        VStack(alignment: .leading, spacing: 2) {
                            HStack(spacing: 6) {
                                Text(a.packageName)
                                stateLabel(soon ? "expires soon" : "delivered", color: soon ? .orange : .green)
                            }
                            Text(Self.assignmentCaption(a)).font(.caption).foregroundStyle(.secondary)
                        }
                        Spacer()
                        if soon, let package = packages.first(where: { $0.id == a.packageId }) {
                            Button("Request again") { requesting = package }.controlSize(.small)
                        }
                    }
                    .padding(.vertical, 2)
                }
            }
        }
    }

    // MARK: Declined

    private var declinedList: some View {
        let ended = snapshot.requests.filter { $0.state.isDeclined }.sorted { ($0.completedAt ?? $0.createdAt ?? .distantPast) > ($1.completedAt ?? $1.createdAt ?? .distantPast) }
        let rows = Self.filtered(ended.map { Row(id: $0.id, name: $0.packageName, detail: $0.justification) }, query: search)
        let byId = Dictionary(ended.map { ($0.id, $0) }, uniquingKeysWith: { a, _ in a })
        return List {
            if let caption = Self.emptyCaption(total: ended.count, filtered: rows.count, query: search, emptyText: "No denied, failed or canceled requests.") {
                Text(caption).font(.caption).foregroundStyle(.secondary)
            }
            ForEach(rows) { row in
                if let r = byId[row.id] {
                    HStack(alignment: .top, spacing: 10) {
                        VStack(alignment: .leading, spacing: 2) {
                            HStack(spacing: 6) { Text(r.packageName); stateLabel(Self.label(r.state), color: Self.color(r.state)) }
                            Text(Self.declinedCaption(r)).font(.caption).foregroundStyle(.secondary).lineLimit(2)
                        }
                        Spacer()
                        if r.state != .canceled, let package = packages.first(where: { $0.id == r.packageId }) {
                            Button("Request again") { requesting = package }.controlSize(.small)
                        }
                    }
                    .padding(.vertical, 2)
                }
            }
        }
    }

    // MARK: Helpers

    private func stateLabel(_ text: String, color: Color) -> some View {
        HStack(spacing: 4) {
            Circle().fill(color).frame(width: 7, height: 7)
            Text(text).font(.caption).foregroundStyle(.secondary)
        }
        .accessibilityElement(children: .ignore)
        .accessibilityLabel(text)
    }

    static func label(_ s: AccessPackageRequestState) -> String {
        switch s {
        case .submitted: "submitted"
        case .pendingApproval: "pending approval"
        case .delivering: "delivering"
        case .delivered: "delivered"
        case .deliveryFailed: "delivery failed"
        case .denied: "denied"
        case .scheduled: "scheduled"
        case .canceled: "canceled"
        case .partiallyDelivered: "partially delivered"
        case .unknown: "unknown"
        }
    }

    static func color(_ s: AccessPackageRequestState) -> Color {
        switch s {
        case .submitted, .pendingApproval, .scheduled: .yellow
        case .delivering, .delivered, .partiallyDelivered: .green
        case .denied, .deliveryFailed: .red
        case .canceled, .unknown: .gray
        }
    }

    static func expiresSoon(_ a: AccessPackageAssignment, now: Date = .now) -> Bool {
        guard let end = a.expiresAt else { return false }
        return end.timeIntervalSince(now) < 7 * 24 * 3600
    }

    static func requestCaption(_ r: AccessPackageRequest) -> String {
        var parts: [String] = []
        if let d = r.createdAt { parts.append("Requested " + d.formatted(date: .abbreviated, time: .shortened)) }
        if let j = r.justification, !j.isEmpty { parts.append("\u{201C}\(j)\u{201D}") }
        return parts.joined(separator: " · ")
    }

    static func assignmentCaption(_ a: AccessPackageAssignment) -> String {
        var parts = [a.expiresAt.map { "Expires " + $0.formatted(date: .abbreviated, time: .omitted) } ?? "No expiry"]
        if let p = a.policyName { parts.append("Policy: \(p)") }
        return parts.joined(separator: " · ")
    }

    static func declinedCaption(_ r: AccessPackageRequest) -> String {
        var parts: [String] = []
        if let d = r.completedAt ?? r.createdAt {
            let verb = r.state == .canceled ? "Canceled" : r.state == .deliveryFailed ? "Failed" : "Decided"
            parts.append("\(verb) " + d.formatted(date: .abbreviated, time: .shortened))
        }
        if r.state == .denied, let j = r.justification, !j.isEmpty { parts.append("Your reason: \u{201C}\(j)\u{201D}") }
        if r.state != .denied, let s = r.status, !s.isEmpty { parts.append(s) }
        return parts.joined(separator: " · ")
    }

    private var updatedCaption: String {
        guard let at = model.accessPackagesPolledAt(tenantKey) else { return "Not updated yet" }
        return "Updated " + at.formatted(.relative(presentation: .named))
    }

    private func reload() async {
        actionError = nil
        loadingPackages = true
        defer { loadingPackages = false }
        async let poll: Void = model.pollAccessPackages(tenantKey)
        do {
            packages = try await model.requestablePackages(tenantKey)
            packagesError = nil
        } catch {
            packagesError = (error as? PIMError)?.userMessage ?? error.localizedDescription
        }
        await poll
    }

    private func cancel(_ r: AccessPackageRequest) async {
        cancelling.insert(r.id)
        defer { cancelling.remove(r.id) }
        do {
            try await model.cancelPackageRequest(tenantKey, requestId: r.id)
            actionError = nil
        } catch {
            actionError = "Could not cancel \(r.packageName): \((error as? PIMError)?.userMessage ?? error.localizedDescription)"
        }
    }
}
