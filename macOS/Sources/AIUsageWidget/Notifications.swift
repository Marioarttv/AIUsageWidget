import AppKit
import UserNotifications
import CryptoKit
import UsageCore

@MainActor
final class Notifications: NSObject, UNUserNotificationCenterDelegate {
    private let center = UNUserNotificationCenter.current()
    var onOpen: (() -> Void)?
    override init() { super.init(); center.delegate = self }

    func permission(request: Bool = false) async -> UNAuthorizationStatus {
        let settings = await center.notificationSettings()
        if request && settings.authorizationStatus == .notDetermined {
            _ = try? await center.requestAuthorization(options: [.alert, .sound])
            return await center.notificationSettings().authorizationStatus
        }
        return settings.authorizationStatus
    }

    func send(_ events: [UsageAlert]) async -> [String] {
        let status = await permission()
        guard status == .authorized || status == .provisional else { return [] }
        var accepted: [String] = []
        for provider in ProviderID.allCases {
            for kind in [UsageAlert.Kind.low, .reset] {
                let group = events.filter { $0.provider == provider && $0.kind == kind }
                guard !group.isEmpty else { continue }
                let content = UNMutableNotificationContent()
                content.title = kind == .low ? "\(provider.title): less than 10% remaining" : "\(provider.title) usage reset"
                content.body = group.map { event in
                    kind == .low ? "\(event.label): \(Int(event.remaining.rounded(.down)))% left."
                        : "\(event.label) has reset · \(Int(event.remaining.rounded(.down)))% remaining."
                }.joined(separator: "\n")
                content.sound = .default
                content.threadIdentifier = provider.rawValue
                let digest = SHA256.hash(data: Data(group.map(\.id).sorted().joined(separator: "|").utf8))
                let identifier = digest.map { String(format: "%02x", $0) }.joined()
                do {
                    try await center.add(UNNotificationRequest(identifier: identifier, content: content, trigger: nil))
                    accepted.append(contentsOf: group.map(\.id))
                } catch { /* Keep events pending for the next successful refresh. */ }
            }
        }
        return accepted
    }

    func test() async -> Bool {
        let status = await permission(request: true)
        guard status == .authorized || status == .provisional else { return false }
        let content = UNMutableNotificationContent()
        content.title = "AI Usage Widget notifications are ready"
        content.body = "You’ll be notified below 10% remaining in a 5-hour window and after a confirmed usage reset."
        content.sound = .default
        do {
            try await center.add(UNNotificationRequest(identifier: "notification-test", content: content, trigger: nil))
            return true
        } catch { return false }
    }

    nonisolated func userNotificationCenter(_ center: UNUserNotificationCenter, willPresent notification: UNNotification,
        withCompletionHandler completionHandler: @escaping (UNNotificationPresentationOptions) -> Void) {
        completionHandler([.banner, .list, .sound])
    }
    nonisolated func userNotificationCenter(_ center: UNUserNotificationCenter, didReceive response: UNNotificationResponse,
        withCompletionHandler completionHandler: @escaping () -> Void) {
        Task { @MainActor in self.onOpen?() }
        completionHandler()
    }
}
