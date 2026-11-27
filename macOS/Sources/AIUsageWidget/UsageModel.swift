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
    /// Claude, then each tracked ChatGPT/Codex sign-in, then Antigravity, in display order.
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
    /// Shows Antigravity whenever its CLI is installed.
    @Published var showAntigravity = UserDefaults.standard.object(forKey: "showAntigravity") as? Bool ?? true {
        didSet {
            UserDefaults.standard.set(showAntigravity, forKey: "showAntigravity")
            applySources()
            if showAntigravity { refresh(manual: true) }
            onUpdate?()
        }
    }
    /// Whether the Antigravity CLI was found at the last check.
    @Published private(set) var antigravityInstalled = false
    /// Renews Claude Code's sign-in after it lapses, instead of waiting for Claude Code to be opened.
    /// Off for new installations; installations from before 1.3.0 keep it on (see `init`).
    @Published var renewClaude = UserDefaults.standard.object(forKey: "renewClaude") as? Bool ?? false {
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
    private let antigravity = AntigravityProvider(workspace: UsageModel.dataDirectory.appendingPathComponent("antigravity-workspace"))
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
    nonisolated static let dataDirectory = FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask)[0].appendingPathComponent("AIUsageWidget")

    /// `demo` shows built-in sample readings for screenshots, without reading sign-ins, the disk or the network.
    init(demo: Bool = false) {
        if demo { applyDemo(); return }
        // Renewal used to be on by default. An existing installation (it has saved readings) keeps that choice.
        if UserDefaults.standard.object(forKey: "renewClaude") == nil {
            let existing = FileManager.default.fileExists(atPath: Self.dataDirectory.appendingPathComponent("usage.json").path)
            UserDefaults.standard.set(existing, forKey: "renewClaude")
            renewClaude = existing
        }
        try? FileManager.default.createDirectory(at: Self.dataDirectory, withIntermediateDirectories: true,
                                               attributes: [.posixPermissions: 0o700])
        let decoder = JSONDecoder(); decoder.dateDecodingStrategy = .iso8601
        if let data = try? Data(contentsOf: Self.dataDirectory.appendingPathComponent("usage.json")),
           let saved = try? decoder.decode(RuntimeCache.self, from: data) { cache = saved }
        antigravityInstalled = AntigravityCLI.executable() != nil
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
        applySources()
    }

    private func applySources() {
        let tracked = CodexAccounts.tracked(codexAccounts, on: Set(codexOn), off: Set(codexOff))
        let next = [UsageSource.claude] + tracked.map(\.source) + (showAntigravity && antigravityInstalled ? [.antigravity] : [])
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
        let through = end.formatted(date: .complete, time: .shortened)
        let help = end > now
            ? "ChatGPT reports this plan paid through \(through). It renews then unless the subscription was cancelled."
            : "ChatGPT last reported this plan paid through \(through) and hasn't recorded a renewal yet. "
                + "The date updates when Codex renews the sign-in."
        return PlanDateLine(text: UsageFormatting.paidPeriod(end, now: now), help: help + " Click to set whether it renews or ends.",
                            lapsed: end <= now)
    }
    /// The paid-period end recorded in a ChatGPT sign-in.
    func reportedPlanDate(_ source: UsageSource) -> Date? {
        codexAccounts.first(where: { $0.source == source })?.paidUntil
    }
    /// Offers to add a date where none is known, except for free ChatGPT plans, which don't renew.
    func canAddPlanDate(_ source: UsageSource) -> Bool {
        source.provider != .codex || codexAccounts.first(where: { $0.source == source })?.plan?.lowercased() != "free"
    }

    func setPlanDate(_ source: UsageSource, _ entry: ManualPlanDate?) {
        planDates[source.id] = entry
        if let data = try? JSONEncoder().encode(planDates) { UserDefaults.standard.set(data, forKey: "planDates") }
        UserDefaults.standard.removeObject(forKey: "claudePlanDate")
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
            // Picks up accounts signed in or out of Codex Subscription Router, and an installed or removed
            // Antigravity CLI, since the last refresh.
            let (accounts, installed) = await Task.detached(priority: .utility) {
                (CodexAccounts.discover(folders: folders), AntigravityCLI.executable() != nil)
            }.value
            antigravityInstalled = installed
            applyAccounts(accounts)
            let codex = sources.compactMap { source in codexProviders[source.id].map { (source, $0) } }
            let readAntigravity = sources.contains(.antigravity)
            await withTaskGroup(of: (UsageSource, ProviderResult).self) { group in
                group.addTask { [claude] in (.claude, await claude.refresh(minimumGap: gap, allowKeychainUI: allowKeychainUI, allowRenewal: renew)) }
                if readAntigravity {
                    group.addTask { [antigravity] in (.antigravity, await antigravity.refresh(minimumGap: gap, manual: manual)) }
                }
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
    /// What can be used now of a reading's shortest window: 0 under a used-up longer limit (even once the window itself
    /// has expired), otherwise nil when unknown, expired or stale.
    private func menuValue(_ source: UsageSource) -> Double? {
        guard let state = services[source.id], let snapshot = state.snapshot, let primary = snapshot.primary,
              !state.stale(at: now, after: staleAfter) else { return nil }
        if snapshot.blocker(of: primary, now: now) != nil { return 0 }
        return primary.expired(at: now) ? nil : primary.remaining
    }
    private static func percent(_ value: Double?) -> String { value.map { "\(Int($0.rounded(.down)))%" } ?? "—" }

    /// One mark and number per provider. Several ChatGPT accounts share one number: the average of what each
    /// has left, which is the share of their combined allowance left when the plans are the same size.
    var menuReadings: [StatusIcon.Reading] {
        var providers: [ProviderID] = []
        for source in sources where !providers.contains(source.provider) { providers.append(source.provider) }
        return providers.map { provider in
            let group = sources.filter { $0.provider == provider }
            let values = group.compactMap(menuValue)
            let value = values.isEmpty ? nil : values.reduce(0, +) / Double(values.count)
            let name = group.count > 1 ? "\(provider.title) (\(group.count) accounts)" : title(group[0])
            return StatusIcon.Reading(provider: provider, value: Self.percent(value), name: name)
        }
    }
    /// The menu-bar tooltip: each reading, with every account under a merged one.
    var menuDetails: [String] {
        menuReadings.flatMap { reading -> [String] in
            let group = sources.filter { $0.provider == reading.provider }
            guard group.count > 1 else { return ["\(reading.name): \(reading.value)"] }
            return ["\(reading.name), average: \(reading.value)"]
                + group.map { "   \(accountName($0) ?? title($0)): \(Self.percent(menuValue($0)))" }
        }
    }
    var menuTitle: String { menuReadings.map { "\($0.name) \($0.value)" }.joined(separator: " · ") }
    /// The tallest the panel and the menu-bar view may be on their displays; taller cards scroll.
    @Published var maxPanelHeight: CGFloat = .infinity
    @Published var maxPopoverHeight: CGFloat = .infinity

    func writeDiagnostics(window: NSWindow) {
        Task {
            let cli = await antigravity.lastExecutable, lapsed = await antigravity.lapsedSince
            var antigravity: [String: Any] = ["installed": antigravityInstalled, "shown": showAntigravity]
            if let cli { antigravity["executable"] = cli.url.path; antigravity["managedByT3"] = cli.managedByT3 }
            if let lapsed { antigravity["signInExpiredSince"] = lapsed.timeIntervalSince1970 }
            writeDiagnostics(window: window, claudeKeychainReads: await claude.keychainReads, claudeRenewals: await claude.renewals,
                             antigravity: antigravity)
        }
    }
    private func writeDiagnostics(window: NSWindow, claudeKeychainReads: Int, claudeRenewals: Int, antigravity: [String: Any]) {
        let data: [String: Any] = [
            "version": "1.3.0", "startedAt": startedAt.timeIntervalSince1970,
            "refreshCount": refreshCount, "refreshing": refreshing, "suspended": suspended,
            "windowVisible": window.isVisible, "windowLevel": window.level.rawValue,
            "mode": topmost ? "floating" : "desktop", "menuTitle": menuTitle,
            "notifications": notificationStatus, "lowAlerts": lowAlerts, "resetAlerts": resetAlerts,
            "watchedInterval": Int(watchedInterval), "unwatchedInterval": Int(effectiveUnwatchedInterval),
            "watched": viewed, "nextRefreshIn": Int(refreshTimer.map { $0.isValid ? $0.fireDate.timeIntervalSinceNow : 0 } ?? 0), "claudeKeychainReads": claudeKeychainReads,
            "renewClaude": renewClaude, "claudeRenewals": claudeRenewals,
            "codexAccountsFound": codexAccounts.count, "antigravity": antigravity,
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

extension UsageModel {
    /// Fictional readings for `--render-demo`: Claude, two ChatGPT accounts (one with a used-up week) and Antigravity.
    fileprivate func applyDemo() {
        let now = Date()
        func at(_ hours: Double) -> Date { now.addingTimeInterval(hours * 3600) }
        func window(_ id: String, _ label: String, used: Double, hours: Double, minutes: Int, pool: String? = nil) -> UsageWindow {
            UsageWindow(id: id, label: label, usedPercent: used, resetsAt: at(hours), minutes: minutes, pool: pool)
        }
        let main = CodexAccount(key: nil, home: nil, email: "you@example.com", plan: "pro", accountID: "demo-main",
                                origin: .main, paidUntil: at(24 * 17))
        let second = CodexAccount(key: "demo-second", home: nil, email: "team@example.com", plan: "plus", accountID: "demo-second",
                                  origin: .router, paidUntil: at(24 * 9))
        codexAccounts = [main, second]
        antigravityInstalled = true
        sources = [.claude, main.source, second.source, .antigravity]
        let updated = now.addingTimeInterval(-60)
        services = [
            UsageSource.claude.id: ServiceState(snapshot: UsageSnapshot(updatedAt: updated, windows: [
                window("five_hour", "5-hour session", used: 28, hours: 2.6, minutes: 300),
                window("seven_day", "Weekly", used: 41, hours: 24 * 3 + 5, minutes: 10080),
                window("seven_day_opus", "Weekly · Opus", used: 63, hours: 24 * 3 + 5, minutes: 10080, pool: "seven_day_opus"),
            ], plan: "Max"), status: nil),
            main.source.id: ServiceState(snapshot: UsageSnapshot(updatedAt: updated, windows: [
                window("codex:primary", "5-hour session", used: 12, hours: 4.1, minutes: 300),
                window("codex:secondary", "Weekly", used: 37, hours: 24 * 5 + 2, minutes: 10080),
            ], plan: "pro"), status: nil),
            second.source.id: ServiceState(snapshot: UsageSnapshot(updatedAt: updated, windows: [
                window("codex:primary", "5-hour session", used: 20, hours: 1.2, minutes: 300),
                window("codex:secondary", "Weekly", used: 100, hours: 24 * 1 + 7, minutes: 10080),
            ], plan: "plus"), status: nil),
            UsageSource.antigravity.id: ServiceState(snapshot: UsageSnapshot(updatedAt: updated, windows: [
                window("gemini-5h", "Gemini · 5-hour session", used: 9, hours: 3.4, minutes: 300, pool: "Gemini Models"),
                window("gemini-weekly", "Gemini · Weekly", used: 22, hours: 24 * 6, minutes: 10080, pool: "Gemini Models"),
                window("3p-5h", "Claude and GPT · 5-hour session", used: 5, hours: 3.4, minutes: 300, pool: "Claude and GPT models"),
                window("3p-weekly", "Claude and GPT · Weekly", used: 30, hours: 24 * 6, minutes: 10080, pool: "Claude and GPT models"),
            ]), status: nil),
        ]
        var day = DateComponents(); day.day = 26
        let renewal = Calendar.current.nextDate(after: now, matching: day, matchingPolicy: .nextTime) ?? at(24 * 20)
        planDates = [UsageSource.claude.id: ManualPlanDate(date: renewal, cycle: .monthly)]
        self.now = now
    }
}

