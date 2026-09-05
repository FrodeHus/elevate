import Foundation
import UserNotifications
import ElevateCore

final class ExpiryNotifier: NSObject, ExpiryNotifying, UNUserNotificationCenterDelegate, @unchecked Sendable {
    static let categoryId = "PIMTRAY_EXPIRY"
    static let extendAction = "PIMTRAY_EXTEND"
    static let leadTime: TimeInterval = 5 * 60
    static let expiredCategoryId = "PIMTRAY_EXPIRED"
    static let activateAgainAction = "PIMTRAY_ACTIVATE_AGAIN"
    static let expiredDelay: TimeInterval = 5

    /// Set by the app on launch; receives the role to re-activate.
    @MainActor var onExtend: ((RoleKey) -> Void)?
    /// Called when the user has refused notification permission, so the panel can explain the silence.
    @MainActor var onAuthorizationDenied: (() -> Void)?

    override init() {
        super.init()
        let center = UNUserNotificationCenter.current()
        center.delegate = self
        let extend = UNNotificationAction(identifier: Self.extendAction, title: "Extend", options: [.foreground])
        let again = UNNotificationAction(identifier: Self.activateAgainAction, title: "Activate again", options: [.foreground])
        center.setNotificationCategories([
            UNNotificationCategory(identifier: Self.categoryId, actions: [extend], intentIdentifiers: []),
            UNNotificationCategory(identifier: Self.expiredCategoryId, actions: [again], intentIdentifiers: []),
        ])
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
            let assignmentId = a.assignmentId ?? UUID().uuidString
            let name = names[a.roleKey] ?? "PIM role"
            let tenant = tenantNames[a.roleKey.tenantKey] ?? a.roleKey.tenantId
            await add(
                center,
                id: "expiry-" + assignmentId,
                title: "\(name) expires in 5 minutes",
                body: "Tenant \(tenant)",
                category: Self.categoryId,
                key: a.roleKey,
                at: end.addingTimeInterval(-Self.leadTime)
            )
            await add(
                center,
                id: "expired-" + assignmentId,
                title: "\(name) expired",
                body: "Tenant \(tenant)",
                category: Self.expiredCategoryId,
                key: a.roleKey,
                at: end.addingTimeInterval(Self.expiredDelay)
            )
        }
    }

    private func add(_ center: UNUserNotificationCenter, id: String, title: String, body: String, category: String, key: RoleKey, at fireAt: Date) async {
        let delay = fireAt.timeIntervalSinceNow
        guard delay > 1 else { return }
        let content = UNMutableNotificationContent()
        content.title = title
        content.body = body
        content.categoryIdentifier = category
        content.sound = .default
        if let data = try? JSONEncoder().encode(key) { content.userInfo = ["roleKey": String(decoding: data, as: UTF8.self)] }
        let trigger = UNTimeIntervalNotificationTrigger(timeInterval: delay, repeats: false)
        try? await center.add(UNNotificationRequest(identifier: id, content: content, trigger: trigger))
    }

    func userNotificationCenter(_ center: UNUserNotificationCenter, didReceive response: UNNotificationResponse) async {
        guard response.actionIdentifier == Self.extendAction || response.actionIdentifier == Self.activateAgainAction || response.actionIdentifier == UNNotificationDefaultActionIdentifier,
              let json = response.notification.request.content.userInfo["roleKey"] as? String,
              let key = try? JSONDecoder().decode(RoleKey.self, from: Data(json.utf8)) else { return }
        await MainActor.run { onExtend?(key) }
    }

    func userNotificationCenter(_ center: UNUserNotificationCenter, willPresent notification: UNNotification) async -> UNNotificationPresentationOptions {
        [.banner, .sound]
    }
}
