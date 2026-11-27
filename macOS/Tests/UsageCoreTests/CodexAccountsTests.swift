import XCTest
@testable import UsageCore

final class CodexAccountsTests: XCTestCase {
    private var root: URL!

    override func setUpWithError() throws {
        root = FileManager.default.temporaryDirectory.appendingPathComponent("codex-accounts-\(UUID().uuidString)")
        try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
    }
    override func tearDownWithError() throws { try? FileManager.default.removeItem(at: root) }

    private func base64URL(_ object: [String: Any]) throws -> String {
        try JSONSerialization.data(withJSONObject: object).base64EncodedString()
            .replacingOccurrences(of: "+", with: "-").replacingOccurrences(of: "/", with: "_").replacingOccurrences(of: "=", with: "")
    }
    /// A Codex home whose auth.json carries an unsigned ID token with the given claims.
    @discardableResult
    private func home(_ name: String, email: String?, plan: String = "plus", account: String, until: String? = nil) throws -> URL {
        let directory = root.appendingPathComponent(name)
        try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
        var auth: [String: Any] = ["chatgpt_plan_type": plan, "chatgpt_account_id": account]
        if let until { auth["chatgpt_subscription_active_until"] = until }
        var claims: [String: Any] = ["https://api.openai.com/auth": auth]
        if let email { claims["email"] = email }
        let token = "e30." + (try base64URL(claims)) + ".sig"
        let file: [String: Any] = ["auth_mode": "chatgpt", "tokens": ["id_token": token, "access_token": "secret", "refresh_token": "secret"]]
        try JSONSerialization.data(withJSONObject: file).write(to: directory.appendingPathComponent("auth.json"))
        return directory
    }
    private func routerState(_ homes: [URL]) throws -> URL {
        let file = root.appendingPathComponent("state.json")
        let accounts = homes.enumerated().map { ["id": "slot\($0.offset)", "label": "Subscription", "codexHome": $0.element.path, "enabled": true] as [String: Any] }
        try JSONSerialization.data(withJSONObject: ["version": 1, "accounts": accounts, "threadOwner": [:]]).write(to: file)
        return file
    }

    func testIdentityReadsClaimsOnly() throws {
        let directory = try home("main", email: "main@example.com", account: "acct-main")
        let identity = try XCTUnwrap(CodexAccounts.identity(home: directory))
        XCTAssertEqual(identity, .init(email: "main@example.com", plan: "plus", accountID: "acct-main"))
        XCTAssertNil(CodexAccounts.identity(authData: Data(#"{"OPENAI_API_KEY":"sk-test"}"#.utf8)))
        XCTAssertNil(CodexAccounts.identity(home: root.appendingPathComponent("missing")))
    }

    func testPaidPeriodEndIsReadForEachAccount() throws {
        let main = try home("main", email: "main@example.com", account: "acct-main", until: "2026-11-17T10:25:07+00:00")
        let second = try home("router-second", email: "second@example.com", account: "acct-second", until: "2026-10-28T05:23:28.123456+00:00")
        let none = try home("router-none", email: "none@example.com", account: "acct-none")
        let accounts = CodexAccounts.discover(mainHome: main, routerState: try routerState([second, none]), folders: [])
        XCTAssertEqual(accounts.map { $0.paidUntil?.timeIntervalSince1970.rounded() },
                       [1_794_911_107, 1_793_165_008, nil])
    }

    func testPaidPeriodLabelSaysRenewsOrEnds() {
        var calendar = Calendar(identifier: .gregorian)
        calendar.timeZone = TimeZone(identifier: "UTC")!
        let end = Date(timeIntervalSince1970: 1_794_911_107) // 17 Nov 2026
        XCTAssertTrue(UsageFormatting.paidPeriod(end, now: end.addingTimeInterval(-86_400), calendar: calendar).hasPrefix("Renews or ends "))
        XCTAssertTrue(UsageFormatting.paidPeriod(end, now: end.addingTimeInterval(86_400), calendar: calendar).hasPrefix("Paid until "))
        // The year is shown only when it isn't the current one.
        XCTAssertFalse(UsageFormatting.paidPeriod(end, now: end.addingTimeInterval(-90 * 86_400), calendar: calendar).contains("2026"))
        XCTAssertTrue(UsageFormatting.paidPeriod(end, now: end.addingTimeInterval(-365 * 86_400), calendar: calendar).contains("2026"))
    }

    func testManualClaudeDateRollsForwardUnlessItEnds() {
        var calendar = Calendar(identifier: .gregorian)
        calendar.timeZone = TimeZone(identifier: "UTC")!
        func day(_ y: Int, _ m: Int, _ d: Int, hour: Int = 0) -> Date {
            calendar.date(from: DateComponents(year: y, month: m, day: d, hour: hour))!
        }
        let monthly = ManualPlanDate(date: day(2026, 1, 31), cycle: .monthly)
        // Short months fall back to their last day; later months keep the 31st.
        XCTAssertEqual(monthly.upcoming(now: day(2026, 2, 10), calendar: calendar), day(2026, 2, 28))
        XCTAssertEqual(monthly.upcoming(now: day(2026, 3, 1), calendar: calendar), day(2026, 3, 31))
        XCTAssertEqual(monthly.upcoming(now: day(2026, 11, 5, hour: 20), calendar: calendar), day(2026, 11, 30))
        // On the renewal day itself it still shows that day.
        XCTAssertEqual(monthly.upcoming(now: day(2026, 3, 31, hour: 23), calendar: calendar), day(2026, 3, 31))
        // A future date is kept as entered.
        XCTAssertEqual(monthly.upcoming(now: day(2025, 12, 1), calendar: calendar), day(2026, 1, 31))

        let yearly = ManualPlanDate(date: day(2024, 6, 26), cycle: .yearly)
        XCTAssertEqual(yearly.upcoming(now: day(2026, 11, 5), calendar: calendar), day(2027, 6, 26))
        XCTAssertTrue(yearly.label(now: day(2026, 11, 5), calendar: calendar).hasPrefix("Renews "))

        let ends = ManualPlanDate(date: day(2026, 11, 26), cycle: .ends)
        XCTAssertEqual(ends.upcoming(now: day(2026, 12, 30), calendar: calendar), day(2026, 11, 26))
        XCTAssertTrue(ends.label(now: day(2026, 11, 5), calendar: calendar).hasPrefix("Ends "))
        XCTAssertTrue(ends.label(now: day(2026, 11, 27), calendar: calendar).hasPrefix("Ended "))
        // Saved settings decode back to the same date.
        let data = try! JSONEncoder().encode(yearly)
        XCTAssertEqual(try! JSONDecoder().decode(ManualPlanDate.self, from: data), yearly)
    }

    func testEarlierClaudeDateMovesIntoPerSubscriptionDates() throws {
        let claude = ManualPlanDate(date: Date(timeIntervalSince1970: 1_800_000_000), cycle: .monthly)
        let legacy = try JSONEncoder().encode(claude)
        XCTAssertEqual(ManualPlanDate.decodeAll(nil, legacyClaude: legacy), ["claude": claude])
        // A newer entry wins over the earlier setting.
        let ends = ManualPlanDate(date: claude.date, cycle: .ends)
        let saved = try JSONEncoder().encode(["claude": ends, "codex@acct": claude])
        XCTAssertEqual(ManualPlanDate.decodeAll(saved, legacyClaude: legacy), ["claude": ends, "codex@acct": claude])
        XCTAssertEqual(ManualPlanDate.decodeAll(nil, legacyClaude: nil), [:])
    }

    func testMonthGridStartsOnTheFirstWeekdayAndKeepsSixRows() {
        var calendar = Calendar(identifier: .gregorian)
        calendar.timeZone = TimeZone(identifier: "UTC")!
        calendar.firstWeekday = 2 // Monday
        // 1 November 2026 is a Sunday: six blank cells before it in a Monday-first week.
        let november = calendar.date(from: DateComponents(year: 2026, month: 11, day: 17))!
        let cells = MonthGrid.days(of: november, calendar: calendar)
        XCTAssertEqual(cells.count, 42)
        XCTAssertEqual(cells.prefix(6).compactMap { $0 }.count, 0)
        XCTAssertEqual(cells[6], calendar.date(from: DateComponents(year: 2026, month: 11, day: 1)))
        XCTAssertEqual(cells.compactMap { $0 }.count, 30)
        XCTAssertEqual(MonthGrid.weekdaySymbols(calendar: calendar).first, calendar.veryShortStandaloneWeekdaySymbols[1])
        calendar.firstWeekday = 1 // Sunday
        XCTAssertEqual(MonthGrid.days(of: november, calendar: calendar).first, cells[6])
    }

    func testRouterAccountsAreAddedOnceAndPlaceholdersSkipped() throws {
        let main = try home("main", email: "main@example.com", account: "acct-main")
        let routerPrimary = try home("router-primary", email: "main@example.com", account: "acct-main")
        let second = try home("router-second", email: "second@example.com", account: "acct-second")
        let free = try home("router-free", email: "free@example.com", plan: "free", account: "acct-free")
        let placeholder = root.appendingPathComponent("router-empty")
        try FileManager.default.createDirectory(at: placeholder, withIntermediateDirectories: true)
        let state = try routerState([routerPrimary, placeholder, second, free])

        let accounts = CodexAccounts.discover(mainHome: main, routerState: state, folders: [])
        XCTAssertEqual(accounts.map(\.email), ["main@example.com", "second@example.com", "free@example.com"])
        XCTAssertEqual(accounts.map(\.origin), [.main, .router, .router])
        XCTAssertNil(accounts[0].key)
        XCTAssertEqual(accounts[0].source, .codex)
        XCTAssertEqual(accounts[1].source.id, "codex@acct-second")
        XCTAssertEqual(accounts[1].home, second.standardizedFileURL.path)

        // Paid accounts are tracked by default; free ones only when turned on.
        XCTAssertEqual(CodexAccounts.tracked(accounts, on: [], off: []).map(\.email), ["main@example.com", "second@example.com"])
        XCTAssertEqual(CodexAccounts.tracked(accounts, on: ["acct-free"], off: ["acct-second"]).map(\.email), ["main@example.com", "free@example.com"])
        // The main sign-in can't be turned off.
        XCTAssertEqual(CodexAccounts.tracked(accounts, on: [], off: ["acct-main"]).first?.origin, .main)
    }

    func testAddedFoldersComeBeforeRouterAndStayListedWhenSignedOut() throws {
        let main = try home("main", email: "main@example.com", account: "acct-main")
        let folder = try home("folder", email: "second@example.com", account: "acct-second")
        let router = try home("router-second", email: "second@example.com", account: "acct-second")
        let empty = root.appendingPathComponent("empty-folder")
        try FileManager.default.createDirectory(at: empty, withIntermediateDirectories: true)
        let accounts = CodexAccounts.discover(mainHome: main, routerState: try routerState([router]), folders: [folder.path, empty.path])
        XCTAssertEqual(accounts.map(\.origin), [.main, .folder, .folder])
        XCTAssertEqual(accounts[1].home, folder.standardizedFileURL.path)
        XCTAssertFalse(accounts[2].signedIn)
        XCTAssertEqual(accounts[2].key, empty.standardizedFileURL.path)
    }

    func testMissingRouterStateLeavesTheMainSignIn() {
        let accounts = CodexAccounts.discover(mainHome: root.appendingPathComponent("none"),
                                              routerState: root.appendingPathComponent("none.json"), folders: [])
        XCTAssertEqual(accounts.count, 1)
        XCTAssertEqual(accounts.first?.origin, .main)
        XCTAssertNil(accounts.first?.email)
    }

    func testAlertsAreIndependentPerAccountAndKeepMainHistory() {
        let now = Date(timeIntervalSince1970: 1_800_000_000)
        func low(_ at: Date) -> UsageSnapshot {
            .init(updatedAt: at, windows: [.init(id: "codex:primary", label: "5-hour session", usedPercent: 95, resetsAt: now.addingTimeInterval(900), minutes: 300)])
        }
        var ledger = AlertLedger()
        let second = UsageSource(provider: .codex, account: "acct-second")
        ledger.observe(low(now), source: .codex, account: "main@example.com", lowEnabled: true, resetEnabled: true, now: now)
        ledger.observe(low(now), source: second, account: "second@example.com", lowEnabled: true, resetEnabled: true, now: now)
        XCTAssertEqual(ledger.pending.count, 2)
        XCTAssertEqual(Set(ledger.pending.map(\.sourceID)), ["codex", "codex@acct-second"])
        XCTAssertEqual(Set(ledger.pending.compactMap(\.account)), ["main@example.com", "second@example.com"])
        // The main account's alert IDs match those saved before accounts were added.
        var legacy = AlertLedger()
        legacy.observe(low(now), provider: .codex, lowEnabled: true, resetEnabled: true, now: now)
        XCTAssertTrue(ledger.pending.contains { $0.id == legacy.pending.first?.id })
    }

    func testAlertsSavedBeforeAccountsStillDecode() throws {
        let json = #"{"id":"codex:x:low:1","provider":"codex","kind":"low","label":"5-hour session","remaining":5,"createdAt":0,"expiresAt":100}"#
        let alert = try JSONDecoder().decode(UsageAlert.self, from: Data(json.utf8))
        XCTAssertNil(alert.account)
        XCTAssertEqual(alert.sourceID, "codex")
    }
}
