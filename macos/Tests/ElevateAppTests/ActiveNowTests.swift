import AppKit
import SwiftUI
import Testing
import ElevateCore
@testable import Elevate

/// The panel's "Active now" section: rendered inside the same lazy stack the panel uses, since a
/// view-held row copy once stayed empty there and the section vanished with it.
@MainActor
struct ActiveNowTests {
    private func modelWithActiveRole() async -> AppModel {
        var state = AppState()
        state.identities = [Sample.identity()]
        state.tenants = [Sample.tenant()]
        let model = await makeModel(state: state, online: true)
        model.roles[Sample.tenantKey] = [Sample.role(Sample.entraKey, name: "Reader")]
        model.active[Sample.entraKey] = ActiveAssignment(roleKey: Sample.entraKey, assignmentId: "a1",
                                                        startDateTime: .now.addingTimeInterval(-600),
                                                        endDateTime: .now.addingTimeInterval(3600), status: .active)
        return model
    }

    /// Pixels that differ from the corner pixel, as a proxy for "something rendered".
    private func inkPixels<V: View>(_ view: V) async -> Int {
        let host = NSHostingView(rootView: view.frame(width: 360, height: 300).background(Color(nsColor: .windowBackgroundColor)))
        let window = NSWindow(contentRect: NSRect(x: 0, y: 0, width: 360, height: 300), styleMask: [.borderless], backing: .buffered, defer: false)
        window.contentView = host
        window.orderFrontRegardless()
        defer { window.orderOut(nil) }
        for _ in 0..<5 { try? await Task.sleep(for: .milliseconds(100)); host.layoutSubtreeIfNeeded() }
        guard let rep = host.bitmapImageRepForCachingDisplay(in: host.bounds) else { return -1 }
        host.cacheDisplay(in: host.bounds, to: rep)
        guard let bg = rep.colorAt(x: 0, y: 0) else { return -1 }
        var count = 0
        for y in stride(from: 0, to: rep.pixelsHigh, by: 2) {
            for x in stride(from: 0, to: rep.pixelsWide, by: 2) {
                guard let c = rep.colorAt(x: x, y: y) else { continue }
                if abs(c.redComponent - bg.redComponent) + abs(c.greenComponent - bg.greenComponent) + abs(c.blueComponent - bg.blueComponent) > 0.1 { count += 1 }
            }
        }
        return count
    }

    @Test func sectionRendersActiveRolesInsideThePanelsLazyStack() async {
        let model = await modelWithActiveRole()
        defer { cleanup(model) }
        let panelLayout = ScrollView {
            LazyVStack(alignment: .leading, spacing: 0, pinnedViews: [.sectionHeaders]) { ActiveSection() }
        }.environment(model)
        let ink = await inkPixels(panelLayout)
        #expect(ink > 50, "Active now rendered nothing for an active role")
    }

    @Test func deactivatedRowLingersBrieflyThenLeaves() async {
        let model = await modelWithActiveRole()
        defer { cleanup(model) }
        let assignment = model.active[Sample.entraKey]!
        model.active[Sample.entraKey] = nil
        model.retainDeactivatedRow(assignment)
        #expect(model.activeAssignmentsOrdered.isEmpty)
        #expect(model.activeRowsOrdered.map(\.roleKey) == [Sample.entraKey])
        // Other tabs and the search filter apply to lingering rows too.
        model.panelTab = .azure
        #expect(model.activeRowsOrdered.isEmpty)
        model.panelTab = .roles
        model.searchQuery = "billing"
        #expect(model.activeRowsOrdered.isEmpty)
        model.searchQuery = ""
        // Activating again drops the stale row at once.
        model.recentlyDeactivated[Sample.entraKey] = nil
        #expect(model.activeRowsOrdered.isEmpty)
    }

    @Test func lingeringRowExpiresOnItsOwn() async {
        let model = await modelWithActiveRole()
        defer { cleanup(model) }
        let assignment = model.active[Sample.entraKey]!
        model.active[Sample.entraKey] = nil
        model.retainDeactivatedRow(assignment)
        try? await Task.sleep(for: .seconds(3.5))
        #expect(model.recentlyDeactivated.isEmpty)
        #expect(model.activeRowsOrdered.isEmpty)
    }
}
