import AppKit
import Combine
import ServiceManagement
import UserNotifications
import UsageCore

struct ServiceState {
    var snapshot: UsageSnapshot?
    var status: String?
    func stale(at now: Date, after limit: TimeInterval) -> Bool {
        status != nil || snapshot.map { now.timeIntervalSince($0.updatedAt) > limit } ?? true
    }
}

/// Refresh cadences offered in settings, in seconds.
enum RefreshInterval {
    static let watchedChoices: [TimeInterval] = [60, 120, 180, 300, 600, 900, 1800]
    static let unwatchedChoices: [TimeInterval] = [300, 600, 900, 1800, 3600]
    static func stored(_ key: String, choices: [TimeInterval], default value: TimeInterval) -> TimeInterval {
        let saved = UserDefaults.standard.double(forKey: key)
        return choices.contains(saved) ? saved : value
    }
    static func label(_ seconds: TimeInterval) -> String {
        seconds >= 3600 ? "\(Int(seconds / 3600)) hr" : "\(Int(seconds / 60)) min"
    }
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
        didSet { UserDefaults.standard.set(panelVisible, forKey: "panelVisible"); onWindowSettings?(); viewChanged() }
    }
    @Published var lowAlerts = UserDefaults.standard.object(forKey: "lowAlerts") as? Bool ?? true {
        didSet { UserDefaults.standard.set(lowAlerts, forKey: "lowAlerts") }
    }
    @Published var resetAlerts = UserDefaults.standard.object(forKey: "resetAlerts") as? Bool ?? true {
        didSet { UserDefaults.standard.set(resetAlerts, forKey: "resetAlerts") }
    }
    /// How often to refresh while the panel or menu-bar view is on screen.
    @Published var watchedInterval = RefreshInterval.stored("watchedInterval", choices: RefreshInterval.watchedChoices, default: 180) {
        didSet { UserDefaults.standard.set(watchedInterval, forKey: "watchedInterval"); reschedule() }
    }
    /// How often to refresh, for alerts only, while the panel is hidden or covered and the menu-bar view is closed.
    @Published var unwatchedInterval = RefreshInterval.stored("unwatchedInterval", choices: RefreshInterval.unwatchedChoices, default: 600) {
        didSet { UserDefaults.standard.set(unwatchedInterval, forKey: "unwatchedInterval"); reschedule() }
    }
    /// Never refresh more often unwatched than watched.
    private var effectiveUnwatchedInterval: TimeInterval { max(watchedInterval, unwatchedInterval) }
    /// Longer than the unwatched refresh interval, so a quiet reading isn't dimmed between refreshes.
    var staleAfter: TimeInterval { max(900, effectiveUnwatchedInterval + 300) }
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
    var popoverVisible = false { didSet { viewChanged() } }
    /// True while another window fully covers the desktop panel. macOS sends no occlusion updates for it.
    var panelCovered: (() -> Bool)?
    private var viewed: Bool { popoverVisible || (panelVisible && !(panelCovered?() ?? false)) }
    private var lastRefresh = Date.distantPast
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
        // Automatic refreshes keep some slack below the chosen cadence, but never poll faster than the default allows.
        let gap: TimeInterval = manual ? 30 : min(150, watchedInterval - 15)
        Task {
            await withTaskGroup(of: (ProviderID, ProviderResult).self) { group in
                group.addTask { [claude] in (.claude, await claude.refresh(minimumGap: gap, allowKeychainUI: allowKeychainUI)) }
                group.addTask { [codex] in (.codex, await codex.refresh(minimumGap: gap)) }
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
            lastRefresh = Date()
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
        // Unwatched, only alerts need readings: check less often, still waking for a known reset.
        let due = lastRefresh.addingTimeInterval(viewed ? watchedInterval : effectiveUnwatchedInterval)
        let wake = nextReset.map { max(now.addingTimeInterval(30), $0.addingTimeInterval(5)) } ?? due
        let interval = max(1, min(due, wake).timeIntervalSince(now))
        let timer = Timer(timeInterval: interval, repeats: false) { [weak self] _ in
            Task { @MainActor in self?.refresh() }
        }
        timer.tolerance = min(15, interval / 10)
        RunLoop.main.add(timer, forMode: .common)
        refreshTimer = timer
    }

    /// Applies a changed interval to the pending refresh; one in flight schedules the next itself.
    private func reschedule() {
        guard !refreshing, refreshCount > 0 else { return }
        refreshTimer?.invalidate()
        scheduleRefresh()
    }
    private func viewChanged() { updateDisplayTimer(); catchUp() }
    /// Refreshes when the widget comes back into view after a longer unwatched interval.
    private func catchUp() {
        if refreshCount > 0, Date().timeIntervalSince(lastRefresh) >= watchedInterval, viewed { refresh() }
    }
    private func updateDisplayTimer() {
        displayTimer?.invalidate(); displayTimer = nil
        guard !suspended, panelVisible || popoverVisible else { return }
        now = Date()
        let timer = Timer(timeInterval: 60, repeats: true) { [weak self] _ in
            Task { @MainActor in
                self?.now = Date()
                self?.catchUp()
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
    var menuReadings: [(provider: ProviderID, value: String)] {
        ProviderID.allCases.map { provider in
            let state = services[provider]
            let value = state?.snapshot?.primary
            let number = value.flatMap { !($0.expired(at: now)) && state?.stale(at: now, after: staleAfter) == false ? "\(Int($0.remaining.rounded(.down)))%" : nil } ?? "—"
            return (provider, number)
        }
    }
    var menuTitle: String { menuReadings.map { "\($0.provider.title) \($0.value)" }.joined(separator: " · ") }

    func writeDiagnostics(window: NSWindow) {
        Task { writeDiagnostics(window: window, claudeKeychainReads: await claude.keychainReads) }
    }
    private func writeDiagnostics(window: NSWindow, claudeKeychainReads: Int) {
        let data: [String: Any] = [
            "version": "1.2.0", "startedAt": startedAt.timeIntervalSince1970,
            "refreshCount": refreshCount, "refreshing": refreshing, "suspended": suspended,
            "windowVisible": window.isVisible, "windowLevel": window.level.rawValue,
            "mode": topmost ? "floating" : "desktop", "menuTitle": menuTitle,
            "notifications": notificationStatus, "lowAlerts": lowAlerts, "resetAlerts": resetAlerts,
            "watchedInterval": Int(watchedInterval), "unwatchedInterval": Int(effectiveUnwatchedInterval),
            "watched": viewed, "nextRefreshIn": Int(refreshTimer.map { $0.isValid ? $0.fireDate.timeIntervalSinceNow : 0 } ?? 0), "claudeKeychainReads": claudeKeychainReads,
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
