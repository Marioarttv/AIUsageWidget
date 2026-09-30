import AppKit
import Combine
import ServiceManagement
import UserNotifications
import UsageCore

struct ServiceState {
    var snapshot: UsageSnapshot?
    var status: String?
    func stale(at now: Date) -> Bool { status != nil || snapshot.map { now.timeIntervalSince($0.updatedAt) > 600 } ?? true }
}

private struct RuntimeCache: Codable {
    var snapshots: [String: UsageSnapshot] = [:]
    var alerts = AlertLedger()
}

@MainActor
final class UsageModel: ObservableObject {
    @Published var services: [ProviderID: ServiceState] = [:]
    @Published var now = Date()
    @Published var refreshing = false
    @Published var topmost = UserDefaults.standard.bool(forKey: "topmost") {
        didSet { UserDefaults.standard.set(topmost, forKey: "topmost"); onWindowSettings?() }
    }
    @Published var panelVisible = UserDefaults.standard.object(forKey: "panelVisible") as? Bool ?? true {
        didSet { UserDefaults.standard.set(panelVisible, forKey: "panelVisible"); onWindowSettings?(); updateDisplayTimer() }
    }
    @Published var lowAlerts = UserDefaults.standard.object(forKey: "lowAlerts") as? Bool ?? true {
        didSet { UserDefaults.standard.set(lowAlerts, forKey: "lowAlerts") }
    }
    @Published var resetAlerts = UserDefaults.standard.object(forKey: "resetAlerts") as? Bool ?? true {
        didSet { UserDefaults.standard.set(resetAlerts, forKey: "resetAlerts") }
    }
    @Published var loginEnabled = SMAppService.mainApp.status == .enabled
    @Published var notificationStatus = "Checking notifications…"
    @Published var message: String?
    var onWindowSettings: (() -> Void)?
    var onUpdate: (() -> Void)?
    var onShow: (() -> Void)?
    private let claude = ClaudeProvider(), codex = CodexProvider()
    let notifications = Notifications()
    private var cache = RuntimeCache()
    private var refreshTimer: Timer?
    private var displayTimer: Timer?
    private var suspended = false
    var popoverVisible = false { didSet { updateDisplayTimer() } }
    private(set) var refreshCount = 0
    private(set) var startedAt = Date()
    private var workspaceObservers: [NSObjectProtocol] = []
    static let dataDirectory = FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask)[0].appendingPathComponent("AIUsageWidget")

    init() {
        try? FileManager.default.createDirectory(at: Self.dataDirectory, withIntermediateDirectories: true,
                                               attributes: [.posixPermissions: 0o700])
        let decoder = JSONDecoder(); decoder.dateDecodingStrategy = .iso8601
        if let data = try? Data(contentsOf: Self.dataDirectory.appendingPathComponent("usage.json")),
           let saved = try? decoder.decode(RuntimeCache.self, from: data) { cache = saved }
        for provider in ProviderID.allCases {
            services[provider] = ServiceState(snapshot: cache.snapshots[provider.rawValue], status: "Connecting…")
        }
    }

    func start() {
        let center = NSWorkspace.shared.notificationCenter
        for name in [NSWorkspace.willSleepNotification, NSWorkspace.screensDidSleepNotification] {
            workspaceObservers.append(center.addObserver(forName: name, object: nil, queue: .main) { [weak self] _ in
                Task { @MainActor in self?.suspend() }
            })
        }
        for name in [NSWorkspace.didWakeNotification, NSWorkspace.screensDidWakeNotification] {
            workspaceObservers.append(center.addObserver(forName: name, object: nil, queue: .main) { [weak self] _ in
                Task { @MainActor in self?.resume() }
            })
        }
        Task {
            await updateNotificationStatus(request: true)
        }
        refresh()
        updateDisplayTimer()
    }

    func refresh(manual: Bool = false, allowKeychainUI: Bool = false) {
        guard !refreshing, !suspended else { return }
        refreshing = true
        refreshTimer?.invalidate()
        Task {
            await withTaskGroup(of: (ProviderID, ProviderResult).self) { group in
                group.addTask { [claude] in (.claude, await claude.refresh(manual: manual, allowKeychainUI: allowKeychainUI)) }
                group.addTask { [codex] in (.codex, await codex.refresh(manual: manual)) }
                for await (provider, result) in group {
                    now = Date()
                    switch result {
                    case .reading(let snapshot):
                        services[provider] = ServiceState(snapshot: snapshot)
                        cache.snapshots[provider.rawValue] = snapshot
                        cache.alerts.observe(snapshot, provider: provider, lowEnabled: lowAlerts, resetEnabled: resetAlerts, now: now)
                    case .failure(let status): services[provider, default: ServiceState()].status = status
                    case .skipped: break
                    }
                    let ids = await notifications.send(cache.alerts.deliverable(now: now, lowEnabled: lowAlerts, resetEnabled: resetAlerts))
                    cache.alerts.markDelivered(ids)
                    save()
                    onUpdate?()
                }
            }
            refreshCount += 1
            await updateNotificationStatus()
            refreshing = false
            onUpdate?()
            scheduleRefresh()
        }
    }

    private func scheduleRefresh() {
        guard !suspended else { return }
        let now = Date()
        let nextReset = services.values.flatMap { $0.snapshot?.windows ?? [] }.compactMap(\.resetsAt).filter { $0 > now }.min()
        let interval = min(180, max(30, nextReset.map { $0.timeIntervalSince(now) + 5 } ?? 180))
        let timer = Timer(timeInterval: interval, repeats: false) { [weak self] _ in
            Task { @MainActor in self?.refresh() }
        }
        timer.tolerance = min(15, interval / 10)
        RunLoop.main.add(timer, forMode: .common)
        refreshTimer = timer
    }

    private func updateDisplayTimer() {
        displayTimer?.invalidate(); displayTimer = nil
        guard !suspended, panelVisible || popoverVisible else { return }
        now = Date()
        let timer = Timer(timeInterval: 60, repeats: true) { [weak self] _ in
            Task { @MainActor in
                self?.now = Date()
                self?.onUpdate?()
            }
        }
        timer.tolerance = 10
        RunLoop.main.add(timer, forMode: .common)
        displayTimer = timer
    }
    private func suspend() { suspended = true; refreshTimer?.invalidate(); displayTimer?.invalidate() }
    private func resume() {
        guard suspended else { return }
        suspended = false; now = Date(); updateDisplayTimer(); refresh()
    }
    private func save() {
        let encoder = JSONEncoder(); encoder.dateEncodingStrategy = .iso8601
        guard let data = try? encoder.encode(cache) else { return }
        let file = Self.dataDirectory.appendingPathComponent("usage.json")
        try? data.write(to: file, options: .atomic)
        try? FileManager.default.setAttributes([.posixPermissions: 0o600], ofItemAtPath: file.path)
    }
    func updateNotificationStatus(request: Bool = false) async {
        switch await notifications.permission(request: request) {
        case .authorized, .provisional: notificationStatus = "Notifications enabled"
        case .denied: notificationStatus = "Notifications blocked in System Settings"
        default: notificationStatus = "Notifications not enabled"
        }
    }
    func testNotification() {
        Task {
            let accepted = await notifications.test()
            message = accepted ? "Test notification sent" : "Allow notifications in System Settings"
            await updateNotificationStatus()
            onUpdate?()
        }
    }
    func setLaunchAtLogin(_ enabled: Bool) {
        do {
            if enabled { try SMAppService.mainApp.register() } else { try SMAppService.mainApp.unregister() }
            loginEnabled = SMAppService.mainApp.status == .enabled
            message = SMAppService.mainApp.status == .requiresApproval ? "Approve AI Usage Widget in Login Items" : nil
        } catch { message = "Could not update Login Items in System Settings" }
    }
    func openNotificationSettings() {
        NSWorkspace.shared.open(URL(string: "x-apple.systempreferences:com.apple.Notifications-Settings.extension")!)
    }
    var menuTitle: String {
        ProviderID.allCases.map { provider in
            let state = services[provider]
            let value = state?.snapshot?.primary
            let number = value.flatMap { !($0.expired(at: now)) && state?.stale(at: now) == false ? "\(Int($0.remaining.rounded(.down)))%" : nil } ?? "—"
            return "\(provider == .claude ? "CL" : "CX") \(number)"
        }.joined(separator: "  ")
    }

    func writeDiagnostics(window: NSWindow) {
        let data: [String: Any] = [
            "version": "1.2.0", "startedAt": startedAt.timeIntervalSince1970,
            "refreshCount": refreshCount, "refreshing": refreshing, "suspended": suspended,
            "windowVisible": window.isVisible, "windowLevel": window.level.rawValue,
            "mode": topmost ? "floating" : "desktop", "menuTitle": menuTitle,
            "notifications": notificationStatus, "lowAlerts": lowAlerts, "resetAlerts": resetAlerts,
            "services": Dictionary(uniqueKeysWithValues: ProviderID.allCases.map { id in
                (id.rawValue, ["windowCount": services[id]?.snapshot?.windows.count ?? 0,
                              "status": services[id]?.status ?? "connected"] as [String: Any])
            })
        ]
        if let bytes = try? JSONSerialization.data(withJSONObject: data, options: [.prettyPrinted, .sortedKeys]) {
            try? bytes.write(to: Self.dataDirectory.appendingPathComponent("diagnostics.json"), options: .atomic)
        }
    }
}
