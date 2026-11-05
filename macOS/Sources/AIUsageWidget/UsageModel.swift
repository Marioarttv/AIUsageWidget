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
    /// Readings keyed by `UsageSource.id`.
    @Published var services: [String: ServiceState] = [:]
    /// Claude, then each tracked ChatGPT/Codex sign-in, in display order.
    @Published private(set) var sources: [UsageSource] = [.claude, .codex]
    /// Every ChatGPT/Codex sign-in found, tracked or not.
    @Published private(set) var codexAccounts: [CodexAccount] = []
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
    /// Renews Claude Code's sign-in after it lapses, instead of waiting for Claude Code to be opened.
    @Published var renewClaude = UserDefaults.standard.object(forKey: "renewClaude") as? Bool ?? true {
        didSet { UserDefaults.standard.set(renewClaude, forKey: "renewClaude") }
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
    private let claude = ClaudeProvider()
    private var codexProviders: [String: CodexProvider] = [:]
    /// Additional sign-ins the user turned on or off; others follow `CodexAccount.trackedByDefault`.
    private var codexOn = UserDefaults.standard.stringArray(forKey: "codexAccountsOn") ?? []
    private var codexOff = UserDefaults.standard.stringArray(forKey: "codexAccountsOff") ?? []
    /// Codex home folders added in settings.
    private var codexFolders = UserDefaults.standard.stringArray(forKey: "codexFolders") ?? []
    private var refreshAgain = false
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
        applyAccounts(CodexAccounts.discover(folders: codexFolders))
    }

    /// Shows tracked accounts and keeps one reader per account, reusing readers whose home is unchanged.
    private func applyAccounts(_ accounts: [CodexAccount]) {
        codexAccounts = accounts
        let tracked = CodexAccounts.tracked(accounts, on: Set(codexOn), off: Set(codexOff))
        var providers: [String: CodexProvider] = [:]
        for account in tracked {
            if let existing = codexProviders[account.id], existing.home == account.home { providers[account.id] = existing; continue }
            let signIn: String
            switch account.origin {
            case .main: signIn = "Sign in to Codex CLI to connect"
            case .router: signIn = "Sign in to this account again in Codex Subscription Router"
            case .folder: signIn = "Sign in to Codex again with this folder as CODEX_HOME"
            }
            providers[account.id] = CodexProvider(home: account.home, signIn: signIn)
        }
        codexProviders = providers
        let next = [UsageSource.claude] + tracked.map(\.source)
        if next != sources { sources = next }
        for source in sources where services[source.id] == nil {
            services[source.id] = ServiceState(snapshot: cache.snapshots[source.id], status: "Connecting…")
        }
    }

    /// The account's email, shown for ChatGPT/Codex sign-ins.
    func accountName(_ source: UsageSource) -> String? {
        guard source.provider == .codex, let account = codexAccounts.first(where: { $0.source == source }) else { return nil }
        return account.origin == .main ? account.email : account.name
    }
    /// Renewal or end dates entered by hand, keyed by `UsageSource.id`: Claude's sign-in doesn't report one,
    /// and ChatGPT's doesn't say whether the plan was cancelled.
    @Published private(set) var planDates = ManualPlanDate.decodeAll(UserDefaults.standard.data(forKey: "planDates"),
                                                                     legacyClaude: UserDefaults.standard.data(forKey: "claudePlanDate"))
    /// The subscription whose renewal date is being edited in the panel.
    @Published var editingPlanDate: String?

    struct PlanDateLine {
        let text: String
        let help: String
        /// The plan has ended or its paid period has passed.
        let lapsed: Bool
    }

    /// The renewal line under a subscription: entered by hand, otherwise ChatGPT's paid-period end.
    func planDate(_ source: UsageSource) -> PlanDateLine? {
        if let entry = planDates[source.id] {
            let next = entry.upcoming(now: now).formatted(date: .complete, time: .omitted)
            let help: String
            switch entry.cycle {
            case .monthly, .yearly: help = "Entered by hand: renews \(entry.cycle.rawValue) on \(next). Click to change."
            case .ends: help = "Entered by hand: the plan ends on \(next). Click to change."
            }
            return PlanDateLine(text: entry.label(now: now), help: help,
                                lapsed: entry.cycle == .ends && entry.upcoming(now: now) < Calendar.current.startOfDay(for: now))
        }
        guard let end = reportedPlanDate(source) else { return nil }
        return PlanDateLine(text: UsageFormatting.paidPeriod(end, now: now),
                            help: "ChatGPT reports this plan paid through " + end.formatted(date: .complete, time: .shortened)
                            + ". It renews then unless the subscription was cancelled. Click to set whether it renews or ends.",
                            lapsed: end <= now)
    }
    /// The paid-period end recorded in a ChatGPT sign-in.
    func reportedPlanDate(_ source: UsageSource) -> Date? {
        codexAccounts.first(where: { $0.source == source })?.paidUntil
    }
    /// Offers to add a date where none is known, except for free ChatGPT plans, which don't renew.
    func canAddPlanDate(_ source: UsageSource) -> Bool {
        source.provider == .claude || codexAccounts.first(where: { $0.source == source })?.plan?.lowercased() != "free"
    }

    func setPlanDate(_ source: UsageSource, _ entry: ManualPlanDate?) {
        planDates[source.id] = entry
        if let data = try? JSONEncoder().encode(planDates) { UserDefaults.standard.set(data, forKey: "planDates") }
        UserDefaults.standard.removeObject(forKey: "claudePlanDate")
        editingPlanDate = nil
        onUpdate?()
    }
    func title(_ source: UsageSource) -> String {
        accountName(source).map { source.provider.title + " · " + $0 } ?? source.provider.title
    }
    private var multipleCodexAccounts: Bool { sources.filter { $0.provider == .codex }.count > 1 }

    func isTracked(_ account: CodexAccount) -> Bool { sources.contains(account.source) }

    /// Turning off an added folder removes it from the list.
    func setTracked(_ account: CodexAccount, _ on: Bool) {
        guard let key = account.key else { return }
        if !on, account.origin == .folder, let home = account.home {
            codexFolders.removeAll { URL(fileURLWithPath: $0).standardizedFileURL.path == home }
        }
        codexOn.removeAll { $0 == key }; codexOff.removeAll { $0 == key }
        if on { codexOn.append(key) } else { codexOff.append(key) }
        saveAccountChoices()
        applyAccounts(CodexAccounts.discover(folders: codexFolders))
        if on { refresh(manual: true) }
        onUpdate?()
    }

    /// Adds a Codex home folder holding another ChatGPT sign-in, such as one made with `CODEX_HOME=… codex login`.
    func addCodexFolder() {
        let panel = NSOpenPanel()
        panel.title = "Choose a Codex Sign-In Folder"
        panel.message = "Choose a Codex home folder (it contains auth.json), such as one used with CODEX_HOME."
        panel.prompt = "Add Account"
        panel.canChooseDirectories = true; panel.canChooseFiles = false; panel.allowsMultipleSelection = false
        panel.showsHiddenFiles = true
        panel.directoryURL = FileManager.default.homeDirectoryForCurrentUser
        NSApp.activate(ignoringOtherApps: true)
        guard panel.runModal() == .OK, let url = panel.url?.standardizedFileURL else { return }
        guard let identity = CodexAccounts.identity(home: url) else {
            message = "No ChatGPT sign-in found in that folder"; onUpdate?(); return
        }
        if let existing = codexAccounts.first(where: { $0.accountID != nil && $0.accountID == identity.accountID }) {
            if existing.key != nil && !isTracked(existing) { setTracked(existing, true) }
            message = "\(existing.name) is already listed"; onUpdate?(); return
        }
        if !codexFolders.contains(url.path) { codexFolders.append(url.path) }
        let key = identity.accountID ?? url.path
        codexOff.removeAll { $0 == key }
        saveAccountChoices()
        applyAccounts(CodexAccounts.discover(folders: codexFolders))
        refresh(manual: true)
    }

    private func saveAccountChoices() {
        UserDefaults.standard.set(codexOn, forKey: "codexAccountsOn")
        UserDefaults.standard.set(codexOff, forKey: "codexAccountsOff")
        UserDefaults.standard.set(codexFolders, forKey: "codexFolders")
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
        guard !suspended else { return }
        // A newly tracked account is read as soon as the refresh in progress finishes.
        guard !refreshing else { if manual { refreshAgain = true }; return }
        refreshing = true
        refreshTimer?.invalidate()
        // Automatic refreshes keep some slack below the chosen cadence, but never poll faster than the default allows.
        let gap: TimeInterval = manual ? 30 : min(150, watchedInterval - 15)
        let renew = renewClaude
        let folders = codexFolders
        Task {
            // Picks up accounts signed in or out of Codex Subscription Router since the last refresh.
            applyAccounts(await Task.detached(priority: .utility) { CodexAccounts.discover(folders: folders) }.value)
            let codex = sources.compactMap { source in codexProviders[source.id].map { (source, $0) } }
            await withTaskGroup(of: (UsageSource, ProviderResult).self) { group in
                group.addTask { [claude] in (.claude, await claude.refresh(minimumGap: gap, allowKeychainUI: allowKeychainUI, allowRenewal: renew)) }
                for (source, provider) in codex {
                    group.addTask { (source, await provider.refresh(minimumGap: gap)) }
                }
                for await (source, result) in group {
                    now = Date()
                    switch result {
                    case .reading(let snapshot):
                        services[source.id] = ServiceState(snapshot: snapshot)
                        cache.snapshots[source.id] = snapshot
                        cache.alerts.observe(snapshot, source: source, account: multipleCodexAccounts ? accountName(source) : nil,
                                             lowEnabled: lowAlerts, resetEnabled: resetAlerts, now: now)
                    case .failure(let status): services[source.id, default: ServiceState()].status = status
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
            if refreshAgain { refreshAgain = false; refresh(manual: true) }
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
    var menuReadings: [StatusIcon.Reading] {
        sources.map { source in
            let state = services[source.id]
            let value = state?.snapshot?.primary
            let number = value.flatMap { !($0.expired(at: now)) && state?.stale(at: now, after: staleAfter) == false ? "\(Int($0.remaining.rounded(.down)))%" : nil } ?? "—"
            return StatusIcon.Reading(provider: source.provider, value: number, name: title(source))
        }
    }
    var menuTitle: String { menuReadings.map { "\($0.name) \($0.value)" }.joined(separator: " · ") }

    func writeDiagnostics(window: NSWindow) {
        Task { writeDiagnostics(window: window, claudeKeychainReads: await claude.keychainReads, claudeRenewals: await claude.renewals) }
    }
    private func writeDiagnostics(window: NSWindow, claudeKeychainReads: Int, claudeRenewals: Int) {
        let data: [String: Any] = [
            "version": "1.2.0", "startedAt": startedAt.timeIntervalSince1970,
            "refreshCount": refreshCount, "refreshing": refreshing, "suspended": suspended,
            "windowVisible": window.isVisible, "windowLevel": window.level.rawValue,
            "mode": topmost ? "floating" : "desktop", "menuTitle": menuTitle,
            "notifications": notificationStatus, "lowAlerts": lowAlerts, "resetAlerts": resetAlerts,
            "watchedInterval": Int(watchedInterval), "unwatchedInterval": Int(effectiveUnwatchedInterval),
            "watched": viewed, "nextRefreshIn": Int(refreshTimer.map { $0.isValid ? $0.fireDate.timeIntervalSinceNow : 0 } ?? 0), "claudeKeychainReads": claudeKeychainReads,
            "renewClaude": renewClaude, "claudeRenewals": claudeRenewals,
            "codexAccountsFound": codexAccounts.count,
            "services": Dictionary(uniqueKeysWithValues: sources.map { source in
                (source.id, ["windowCount": services[source.id]?.snapshot?.windows.count ?? 0,
                             "status": services[source.id]?.status ?? "connected"] as [String: Any])
            })
        ]
        if let bytes = try? JSONSerialization.data(withJSONObject: data, options: [.prettyPrinted, .sortedKeys]) {
            try? bytes.write(to: Self.dataDirectory.appendingPathComponent("diagnostics.json"), options: .atomic)
        }
    }
}
