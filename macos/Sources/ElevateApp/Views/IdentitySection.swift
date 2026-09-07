import SwiftUI
import ElevateCore

/// Full account row shown between accounts. With `soleTenant` set the account has exactly one
/// tenant, so this row also carries that tenant's name, pills, active count and menu items, and
/// the tenant header is not shown at all.
struct IdentityHeader: View {
    @Environment(AppModel.self) private var model
    @Environment(\.openWindow) private var openWindow
    let identity: Identity
    var soleTenant: TenantContext? = nil

    private var activeCount: Int {
        guard let t = soleTenant else { return 0 }
        return model.roles(for: t.id, tab: model.panelTab).filter { model.assignment(for: $0.key)?.status == .active }.count
    }

    @State private var signingIn = false
    @State private var confirmSignOut = false
    @State private var confirmRemoveTenant = false
    @Environment(\.accessibilityReduceMotion) private var reduceMotion

    /// The person's name leads; the UPN is the caption. A name survives the row's width where a
    /// UPN middle-truncates to "ops.....com" as soon as a pill or button joins the line.
    private var primaryName: String {
        let name = identity.displayName.trimmingCharacters(in: .whitespaces)
        return name.isEmpty ? identity.upn : name
    }

    private var caption: String {
        guard let t = soleTenant else { return identity.upn }
        return t.source == .home ? "\(identity.upn) · \(t.displayName) · home" : "\(identity.upn) · \(t.displayName)"
    }

    private var signInHelp: String {
        "\(identity.upn), signed in with \(identity.signInMethod.displayName)"
    }

    private static let signInNeededHelp =
        "The saved sign-in for this account is gone. Sign in again to keep its tenants and roles, or sign out to remove it."

    var body: some View {
        let expanded = !model.collapsedIdentities.contains(identity.id)
        let needsSignIn = model.needsSignIn(identity.id)
        HStack(spacing: 8) {
            Button { withAnimation(reduceMotion ? nil : .snappy) { model.toggleIdentity(identity.id) } } label: {
                HStack(spacing: 8) {
                    Image(systemName: "chevron.right").rotationEffect(.degrees(expanded ? 90 : 0))
                        .font(.caption.weight(.semibold)).foregroundStyle(.secondary).frame(width: 12)
                    Image(systemName: "person.crop.circle.fill").foregroundStyle(Color.accentColor)
                        .help(signInHelp)
                    VStack(alignment: .leading, spacing: 0) {
                        Text(primaryName).font(.subheadline.weight(.semibold)).lineLimit(1)
                        Text(caption).font(.caption).foregroundStyle(.secondary).lineLimit(1).truncationMode(.middle)
                    }
                }
                .contentShape(Rectangle())
            }
            .buttonStyle(.plain)
            .accessibilityLabel(expanded ? "Collapse account \(primaryName)" : "Expand account \(primaryName)")
            .accessibilityHint(signInHelp)
            HStack(spacing: 6) {
                // Each tenant header carries its own status glyph; the account-level badge only covers the
                // moment before any tenant is known.
                if model.tenants(for: identity.id).isEmpty, let reason = identity.signInMethod.entraViewOnlyReason {
                    ViewOnlyBadge(reason: reason)
                }
                if needsSignIn {
                    // A glyph, not a pill: the Sign in button beside it already says what to do, and
                    // a pill here squeezed the name out of the row.
                    Image(systemName: "exclamationmark.triangle.fill")
                        .font(.caption).foregroundStyle(.red)
                        .help(Self.signInNeededHelp)
                        .accessibilityLabel("Sign-in needed: \(Self.signInNeededHelp)")
                } else if let t = soleTenant {
                    TenantPills(tenant: t)
                }
            }
            Spacer(minLength: 8)
            if needsSignIn {
                // The retry lives on the row itself: the flagged account is exactly the one the
                // user must act on, and a menu item alone is easy to miss.
                if signingIn {
                    ProgressView().controlSize(.mini)
                } else {
                    Button("Sign in") { retry() }
                        .controlSize(.small)
                        .help("Sign in again as \(identity.upn)")
                }
            } else if activeCount > 0 {
                Text("\(activeCount) active").font(.caption).foregroundStyle(.secondary)
            }
            HeaderMenu(label: "Account actions") {
                Text(signInHelp)
                Divider()
                if needsSignIn {
                    Button("Sign in again") { retry() }.disabled(signingIn)
                    Divider()
                }
                Button("Discover tenants…") { open(.discoverTenants(identity.id)) }
                Button("Add tenant…") { open(.addTenant(identity.id)) }
                if let t = soleTenant {
                    Divider()
                    TenantMenuItems(tenant: t, confirmRemove: $confirmRemoveTenant)
                }
                Divider()
                Button("Sign out…", role: .destructive) { confirmSignOut = true }
            }
        }
        .confirmationDialog("Sign out \(identity.upn)?", isPresented: $confirmSignOut, titleVisibility: .visible) {
            Button("Sign out", role: .destructive) { model.signOut(identity) }
            Button("Cancel", role: .cancel) {}
        } message: {
            Text(signOutMessage)
        }
        .modifier(SoleTenantRemoval(tenant: soleTenant, isPresented: $confirmRemoveTenant))
        .padding(.horizontal, PanelMetrics.headerInset)
        .padding(.vertical, 7)
        .frame(maxWidth: .infinity, alignment: .leading)
        // A neutral fill keeps the primary text at full contrast in both appearances; the accent
        // survives as the glyph and a left edge, which is enough to tell accounts from tenants.
        // Opaque under the tint: this row pins while the list scrolls, and a translucent fill let
        // the rows beneath show through it in a Liquid Glass window.
        .background {
            ZStack {
                Color(nsColor: .windowBackgroundColor)
                Rectangle().fill(.quaternary.opacity(0.5))
            }
        }
        .overlay(alignment: .leading) { Rectangle().fill(Color.accentColor).frame(width: 3) }
        .overlay(alignment: .bottom) { Divider() }
    }

    private var signOutMessage: String {
        let n = model.tenants(for: identity.id).count
        let tenants = n == 1 ? "its tenant" : "its \(n) tenants"
        return "Elevate forgets \(tenants), configured roles and profile entries for this account. Active assignments in Entra are not changed. You can add the account again later."
    }

    private func open(_ route: PanelRoute) {
        openWindow(value: route)
        NSApp.activate(ignoringOtherApps: true)
    }

    private func retry() {
        guard !signingIn else { return }
        signingIn = true
        Task {
            await model.retrySignIn(identity)
            signingIn = false
        }
    }
}

/// Attaches the remove-tenant confirmation only when the account row stands in for its sole tenant.
private struct SoleTenantRemoval: ViewModifier {
    let tenant: TenantContext?
    @Binding var isPresented: Bool
    func body(content: Content) -> some View {
        if let tenant {
            content.removeTenantConfirmation(tenant, isPresented: $isPresented)
        } else {
            content
        }
    }
}

/// Small tinted capsule for a status the user should notice but not read at length; the full
/// text is the tooltip. Used for "Azure roles only" and for a failed discovery.
struct StatusPill: View {
    let text: String
    var tint: Color = .orange
    let help: String
    var body: some View {
        Text(text).font(.caption2.weight(.medium))
            .padding(.horizontal, 5).padding(.vertical, 1)
            .background(tint.opacity(0.2), in: Capsule())
            .foregroundStyle(.primary)
            .help(help)
            .accessibilityLabel("\(text): \(help)")
    }
}

/// "Azure roles only" pill, same on account headers and tenant headers.
struct ViewOnlyBadge: View {
    let reason: String
    var body: some View { StatusPill(text: "Azure roles only", help: reason) }
}

/// The "…" menu at the trailing edge of a header. Pinned to a fixed width with its indicator
/// hidden so its click target is the glyph alone and never the rest of the row.
struct HeaderMenu<Content: View>: View {
    let label: String
    @ViewBuilder let content: () -> Content
    var body: some View {
        Menu(content: content) { Image(systemName: "ellipsis.circle") }
            .menuStyle(.borderlessButton)
            .menuIndicator(.hidden)
            .frame(width: 22, height: 20)
            .fixedSize()
            .accessibilityLabel(label)
    }
}
