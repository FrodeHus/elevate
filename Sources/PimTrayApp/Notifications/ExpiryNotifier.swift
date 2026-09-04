import Foundation
import UserNotifications
import PimTrayCore

final class ExpiryNotifier: NSObject, ExpiryNotifying, UNUserNotificationCenterDelegate, @unchecked Sendable {
    static let categoryId = "PIMTRAY_EXPIRY"
    static let extendAction = "PIMTRAY_EXTEND"
    static let leadTime: TimeInterval = 5 * 60

    /// Set by the app on launch; receives the role to re-activate.
    @MainActor var onExtend: ((RoleKey) -> Void)?
    /// Called when the user has refused notification permission, so the panel can explain the silence.
    @MainActor var onAuthorizationDenied: (() -> Void)?

    override init() {
        super.init()
        let center = UNUserNotificationCenter.current()
        center.delegate = self
        let extend = UNNotificationAction(identifier: Self.extendAction, title: "Extend", options: [.foreground])
        center.setNotificationCategories([UNNotificationCategory(identifier: Self.categoryId, actions: [extend], intentIdentifiers: [])])
        center.requestAuthorization(options: [.alert, .sound]) { [weak self] granted, _ in
            guard !granted else { return }
            Task { @MainActor in self?.onAuthorizationDenied?() }
        }
    }

    func reschedule(assignments: [ActiveAssignment], names: [RoleKey: String], tenantNames: [TenantKey: String]) async {
        let center = UNUserNotificationCenter.current()
        center.removeAllPendingNotificationRequests()
        for a in assignments where a.status == .active {
            guard let end = a.endDateTime else { continue }
            let fireAt = end.addingTimeInterval(-Self.leadTime)
            let delay = fireAt.timeIntervalSinceNow
            guard delay > 1 else { continue }
            let content = UNMutableNotificationContent()
            content.title = "\(names[a.roleKey] ?? "PIM role") expires in 5 minutes"
            content.body = "Tenant \(tenantNames[a.roleKey.tenantKey] ?? a.roleKey.tenantId)"
            content.categoryIdentifier = Self.categoryId
            content.sound = .default
            if let data = try? JSONEncoder().encode(a.roleKey) { content.userInfo = ["roleKey": String(decoding: data, as: UTF8.self)] }
            let trigger = UNTimeIntervalNotificationTrigger(timeInterval: delay, repeats: false)
            let id = "expiry-" + (a.assignmentId ?? UUID().uuidString)
            try? await center.add(UNNotificationRequest(identifier: id, content: content, trigger: trigger))
        }
    }

    func userNotificationCenter(_ center: UNUserNotificationCenter, didReceive response: UNNotificationResponse) async {
        guard response.actionIdentifier == Self.extendAction || response.actionIdentifier == UNNotificationDefaultActionIdentifier,
              let json = response.notification.request.content.userInfo["roleKey"] as? String,
              let key = try? JSONDecoder().decode(RoleKey.self, from: Data(json.utf8)) else { return }
        await MainActor.run { onExtend?(key) }
    }

    func userNotificationCenter(_ center: UNUserNotificationCenter, willPresent notification: UNNotification) async -> UNNotificationPresentationOptions {
        [.banner, .sound]
    }
}
