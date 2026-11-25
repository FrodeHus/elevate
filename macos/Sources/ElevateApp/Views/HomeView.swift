import SwiftUI
import ElevateCore

/// Home: what needs a decision, what is active, and a way into the full role list.
struct HomeView: View {
    @Environment(AppModel.self) private var model
    var availableHeight: CGFloat = PanelMetrics.maxHeight
    @State private var contentHeight: CGFloat = 0

    var body: some View {
        // Sized from the measured content, as in the role list: a ScrollView reports no ideal height here.
        ScrollView {
            LazyVStack(alignment: .leading, spacing: 0, pinnedViews: [.sectionHeaders]) {
                ApprovalsSection()
                ActiveSection(showsHero: true)
                browseRow
            }
            .onGeometryChange(for: CGFloat.self) { $0.size.height } action: { contentHeight = $0 }
        }
        .frame(height: min(max(contentHeight, 44), max(availableHeight, 44)))
    }

    private var browseRow: some View {
        let roles = model.roles.values.reduce(0) { $0 + $1.count }
        let tenants = model.state.tenants.count
        return Button { model.browsing = true } label: {
            HStack {
                VStack(alignment: .leading, spacing: 1) {
                    Text("Browse all roles").font(.body)
                    Text("\(roles) in \(tenants) \(tenants == 1 ? "tenant" : "tenants")").font(.caption).foregroundStyle(.secondary)
                }
                Spacer()
                Image(systemName: "chevron.right").font(.caption.weight(.semibold)).foregroundStyle(.secondary)
            }
            .padding(.horizontal, PanelMetrics.headerInset).padding(.vertical, 8)
            .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
    }
}
