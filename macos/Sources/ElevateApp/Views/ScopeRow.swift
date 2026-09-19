import SwiftUI
import ElevateCore

/// How far a row of the Azure tab's scope tree is pushed in. The panel is narrow and a management
/// group path is long, so a step is small and the tree stops indenting after a few levels rather
/// than squeezing the text off the row — the path on the header says where you are.
enum PanelIndent {
    static let step: CGFloat = 12
    static let maxSteps = 3

    static func `for`(_ depth: Int) -> CGFloat { CGFloat(min(max(depth, 0), maxSteps)) * step }
}

/// One scope in the Azure tab's tree: a management group, subscription, resource group or resource
/// that has roles beneath it. The chevron opens it and, in select mode, the checkbox takes
/// everything under it in one press.
struct ScopeRow: View {
    @Environment(AppModel.self) private var model
    @Environment(\.accessibilityReduceMotion) private var reduceMotion
    let tenant: TenantContext
    let node: ScopeNode
    let depth: Int

    private var expanded: Bool { !model.isScopeCollapsed(tenant.id, node) }
    /// "Alpha /" — scopes folded into this one because they only passed through.
    private var ancestors: String? {
        node.ancestors.isEmpty ? nil : node.ancestors.joined(separator: " / ") + " /"
    }
    /// What a press of the checkbox would select; the count beside the name says the same.
    private var countText: String { node.roleCount == 1 ? "1 role" : "\(node.roleCount) roles" }
    private var tooltip: String {
        guard let ancestors else { return node.scope }
        return "\(ancestors) \(node.title)\n\(node.scope)"
    }

    var body: some View {
        HStack(spacing: 6) {
            if model.selectMode { subtreeCheckbox }
            // One plain button for chevron + name, as the tenant header does: a bare tap gesture
            // loses to neighbouring hit areas that stretch across the row.
            Button { withAnimation(reduceMotion ? nil : .snappy) { model.toggleScope(tenant.id, node) } } label: {
                HStack(spacing: 5) {
                    Image(systemName: "chevron.right").rotationEffect(.degrees(expanded ? 90 : 0))
                        .font(.caption2.weight(.semibold)).foregroundStyle(.secondary).frame(width: 10)
                    if let ancestors {
                        Text(ancestors).font(.caption).foregroundStyle(.tertiary).lineLimit(1).layoutPriority(-1)
                    }
                    Text(node.title).font(.caption.weight(.medium)).lineLimit(1)
                    Text(ArmScope.label(node.kind)).font(.caption2).foregroundStyle(.secondary).lineLimit(1)
                }
                .contentShape(Rectangle())
            }
            .buttonStyle(.plain)
            .accessibilityLabel(expanded ? "Collapse \(node.title)" : "Expand \(node.title)")
            Spacer(minLength: 4)
            Text(countText).font(.caption2).foregroundStyle(.secondary).fixedSize()
        }
        .help(tooltip)
        .padding(.vertical, 2)
        .padding(.leading, PanelMetrics.headerInset + PanelIndent.for(depth))
        .padding(.trailing, PanelMetrics.trailingInset)
    }

    /// Three states, as on Windows: everything under the node chosen, some of it, or none. Disabled
    /// when there is nothing left to take — all active already, or view-only.
    private var subtreeCheckbox: some View {
        let state = model.subtreeState(node)
        let empty = model.subtreeKeys(node).isEmpty
        return Button { model.toggleSubtree(node) } label: {
            Image(systemName: glyph(state))
                .font(.body)
                .foregroundStyle(state == .none ? AnyShapeStyle(.secondary) : AnyShapeStyle(Color.accentColor))
                .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
        .disabled(empty)
        .accessibilityLabel(node.roleCount == 1
            ? "Select the role under \(node.title)"
            : "Select all \(node.roleCount) roles under \(node.title)")
    }

    private func glyph(_ state: AppModel.SubtreeSelection) -> String {
        switch state {
        case .all: "checkmark.square.fill"
        case .some: "minus.square.fill"
        case .none: "square"
        }
    }
}
