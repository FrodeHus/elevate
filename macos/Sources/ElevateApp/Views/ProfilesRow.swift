import SwiftUI
import ElevateCore

/// One chip per saved profile under the tab picker; hidden when there are none.
struct ProfilesRow: View {
    @Environment(AppModel.self) private var model
    @Environment(\.openWindow) private var openWindow

    var body: some View {
        if !model.profiles.isEmpty {
            VStack(alignment: .leading, spacing: 6) {
                HStack {
                    Text("PROFILES").font(.caption2.weight(.semibold)).foregroundStyle(.secondary)
                    Spacer()
                    Button("Manage…") { open(.manageProfiles) }.buttonStyle(.plain).font(.caption).foregroundStyle(Color.accentColor)
                }
                FlowLayout(spacing: 6) {
                    ForEach(model.profiles) { p in
                        Button { model.requestRun(p.id); open(.runProfile(p.id)) } label: {
                            HStack(spacing: 5) {
                                Image(systemName: "bolt.fill").font(.caption2).foregroundStyle(Color.accentColor)
                                Text(p.name).font(.caption.weight(.medium)).lineLimit(1)
                                Text(ProfileSummary.caption(entries: p.entries)).font(.caption2).foregroundStyle(.secondary)
                            }
                            .frame(maxWidth: 220)
                            .padding(.horizontal, 9).padding(.vertical, 4)
                            .background(.background, in: Capsule())
                            .overlay(Capsule().strokeBorder(.quaternary))
                        }
                        .buttonStyle(.plain)
                        .help("Run \(p.name)")
                    }
                }
            }
            .padding(.horizontal, 12).padding(.vertical, 8)
            Divider()
        }
    }

    private func open(_ route: PanelRoute) {
        openWindow(value: route)
        NSApp.activate(ignoringOtherApps: true)
    }
}

/// Minimal wrapping layout for chips.
struct FlowLayout: Layout {
    var spacing: CGFloat = 6

    func sizeThatFits(proposal: ProposedViewSize, subviews: Subviews, cache: inout ()) -> CGSize {
        let width = proposal.width.flatMap { $0.isFinite ? $0 : nil } ?? (PanelMetrics.width - 24)
        var x: CGFloat = 0, y: CGFloat = 0, rowHeight: CGFloat = 0
        for s in subviews {
            let size = s.sizeThatFits(.unspecified)
            if x + size.width > width, x > 0 { x = 0; y += rowHeight + spacing; rowHeight = 0 }
            x += size.width + spacing
            rowHeight = max(rowHeight, size.height)
        }
        return CGSize(width: width, height: y + rowHeight)
    }

    func placeSubviews(in bounds: CGRect, proposal: ProposedViewSize, subviews: Subviews, cache: inout ()) {
        var x = bounds.minX, y = bounds.minY, rowHeight: CGFloat = 0
        for s in subviews {
            let size = s.sizeThatFits(.unspecified)
            if x + size.width > bounds.maxX, x > bounds.minX { x = bounds.minX; y += rowHeight + spacing; rowHeight = 0 }
            s.place(at: CGPoint(x: x, y: y), proposal: ProposedViewSize(size))
            x += size.width + spacing
            rowHeight = max(rowHeight, size.height)
        }
    }
}
