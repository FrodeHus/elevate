import SwiftUI

/// Chrome shared by every pinned section header: insets, material background, bottom divider.
struct PinnedHeaderChrome: ViewModifier {
    func body(content: Content) -> some View {
        content
            .padding(.leading, PanelMetrics.headerInset)
            .padding(.trailing, PanelMetrics.trailingInset)
            .padding(.vertical, 6)
            .frame(maxWidth: .infinity, alignment: .leading)
            .background(.regularMaterial)
            .overlay(alignment: .bottom) { Divider() }
    }
}

extension View {
    func pinnedHeaderChrome() -> some View {
        modifier(PinnedHeaderChrome())
    }
}

/// Generic single-line pinned section header: a chevron button that toggles collapse state, a
/// title with an optional tinted count, room for extra leading content, then trailing content
/// flush to the right. `ActiveHeader` and `ApprovalsHeader` are thin wrappers over this.
struct PinnedSectionHeader<Leading: View, Trailing: View>: View {
    let title: String
    /// Lowercase phrase used in the accessibility label, e.g. "active now" for the title "Active now".
    let accessibilityName: String
    let count: Int?
    let tint: Color
    let expanded: Bool
    let onToggle: () -> Void
    @ViewBuilder var leading: () -> Leading
    @ViewBuilder var trailing: () -> Trailing

    var body: some View {
        HStack(spacing: 6) {
            Button(action: onToggle) {
                HStack(spacing: 6) {
                    Image(systemName: "chevron.right").rotationEffect(.degrees(expanded ? 90 : 0))
                        .font(.caption.weight(.semibold)).foregroundStyle(.secondary).frame(width: 12)
                    Text(title).font(.subheadline.weight(.semibold))
                    if let count { Text("\(count)").font(.caption).foregroundStyle(tint) }
                }
                .contentShape(Rectangle())
            }
            .buttonStyle(.plain)
            .accessibilityLabel(expanded ? "Collapse \(accessibilityName)" : "Expand \(accessibilityName)")
            leading()
            Spacer()
            trailing()
        }
        .pinnedHeaderChrome()
    }
}

extension PinnedSectionHeader where Leading == EmptyView, Trailing == EmptyView {
    init(title: String, accessibilityName: String, count: Int?, tint: Color, expanded: Bool, onToggle: @escaping () -> Void) {
        self.init(title: title, accessibilityName: accessibilityName, count: count, tint: tint,
                  expanded: expanded, onToggle: onToggle, leading: { EmptyView() }, trailing: { EmptyView() })
    }
}
