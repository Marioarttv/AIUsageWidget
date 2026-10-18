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
    private func home(_ name: String, email: String?, plan: String = "plus", account: String) throws -> URL {
        let directory = root.appendingPathComponent(name)
        try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
        var claims: [String: Any] = ["https://api.openai.com/auth": ["chatgpt_plan_type": plan, "chatgpt_account_id": account]]
        if let email { claims["email"] = email }
        let token = "e30." + (try base64URL(claims)) + ".sig"
        let auth: [String: Any] = ["auth_mode": "chatgpt", "tokens": ["id_token": token, "access_token": "secret", "refresh_token": "secret"]]
        try JSONSerialization.data(withJSONObject: auth).write(to: directory.appendingPathComponent("auth.json"))
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
