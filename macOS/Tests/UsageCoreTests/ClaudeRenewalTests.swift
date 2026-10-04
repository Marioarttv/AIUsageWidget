import XCTest
@testable import UsageCore

final class ClaudeRenewalTests: XCTestCase {
    let now = Date(timeIntervalSince1970: 1_800_000_000)
    let stored = Data(#"{"mcpOAuth":{"server":{"accessToken":"m"}},"claudeAiOauth":{"accessToken":"old-a","refreshToken":"old-r","expiresAt":1799999000000,"refreshTokenExpiresAt":1900000000000,"scopes":["user:inference","user:profile"],"subscriptionType":"pro","rateLimitTier":"default"}}"#.utf8)
    func object(_ data: Data?) throws -> [String: Any] { try XCTUnwrap(JSONSerialization.jsonObject(with: XCTUnwrap(data)) as? [String: Any]) }

    func testRequestUsesStoredScopesAndClaudeCodeClient() throws {
        let body = try object(ClaudeRenewal.request(stored: stored, now: now))
        XCTAssertEqual(body["grant_type"] as? String, "refresh_token")
        XCTAssertEqual(body["refresh_token"] as? String, "old-r")
        XCTAssertEqual(body["scope"] as? String, "user:inference user:profile")
        XCTAssertEqual(body["client_id"] as? String, ClaudeRenewal.defaultClientID)
        let custom = Data(#"{"claudeAiOauth":{"refreshToken":"r","scopes":["user:profile"],"clientId":"custom"}}"#.utf8)
        XCTAssertEqual(try object(ClaudeRenewal.request(stored: custom, now: now))["client_id"] as? String, "custom")
    }
    func testNoRequestWithoutUsableRefreshToken() {
        XCTAssertNil(ClaudeRenewal.request(stored: Data(#"{"claudeAiOauth":{"accessToken":"a","scopes":["user:profile"]}}"#.utf8), now: now))
        XCTAssertNil(ClaudeRenewal.request(stored: Data(#"{"claudeAiOauth":{"refreshToken":"r","scopes":[]}}"#.utf8), now: now))
        XCTAssertNil(ClaudeRenewal.request(stored: Data(#"{"claudeAiOauth":{"refreshToken":"r","scopes":["s"],"refreshTokenExpiresAt":1799999999000}}"#.utf8), now: now))
        XCTAssertNil(ClaudeRenewal.request(stored: Data("not json".utf8), now: now))
    }
    func testMergeReplacesTokensAndKeepsEverythingElse() throws {
        let response = Data(#"{"access_token":"new-a","refresh_token":"new-r","expires_in":28800,"refresh_token_expires_in":7776000,"scope":"user:inference user:profile user:sessions:claude_code"}"#.utf8)
        let root = try object(ClaudeRenewal.merge(stored: stored, response: response, now: now))
        let oauth = try XCTUnwrap(root["claudeAiOauth"] as? [String: Any])
        XCTAssertEqual(oauth["accessToken"] as? String, "new-a")
        XCTAssertEqual(oauth["refreshToken"] as? String, "new-r")
        XCTAssertEqual((oauth["expiresAt"] as? NSNumber)?.int64Value, 1_800_028_800_000)
        XCTAssertEqual((oauth["refreshTokenExpiresAt"] as? NSNumber)?.int64Value, 1_807_776_000_000)
        XCTAssertEqual(oauth["scopes"] as? [String], ["user:inference", "user:profile", "user:sessions:claude_code"])
        XCTAssertEqual(oauth["subscriptionType"] as? String, "pro")
        XCTAssertEqual(oauth["rateLimitTier"] as? String, "default")
        XCTAssertNotNil(root["mcpOAuth"])
    }
    func testMergeKeepsRefreshTokenWhenNotRotatedAndRejectsBadResponses() throws {
        let merged = ClaudeRenewal.merge(stored: stored, response: Data(#"{"access_token":"new-a","expires_in":60}"#.utf8), now: now)
        XCTAssertEqual(ClaudeRenewal.refreshToken(try XCTUnwrap(merged)), "old-r")
        XCTAssertEqual((try object(merged)["claudeAiOauth"] as? [String: Any])?["scopes"] as? [String], ["user:inference", "user:profile"])
        XCTAssertNil(ClaudeRenewal.merge(stored: stored, response: Data(#"{"error":"invalid_grant"}"#.utf8), now: now))
        XCTAssertNil(ClaudeRenewal.merge(stored: stored, response: Data(#"{"access_token":"a","expires_in":0}"#.utf8), now: now))
        XCTAssertNil(ClaudeRenewal.merge(stored: Data("{}".utf8), response: Data(#"{"access_token":"a","expires_in":60}"#.utf8), now: now))
    }

    func temporaryClaudeDirectory() throws -> URL {
        let base = FileManager.default.temporaryDirectory.appendingPathComponent("aiw-lock-\(UUID().uuidString)")
        let directory = base.appendingPathComponent(".claude")
        try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
        addTeardownBlock { try? FileManager.default.removeItem(at: base) }
        return directory
    }
    func testLockMatchesClaudeCodePathsAndExcludesOthers() async throws {
        let directory = try temporaryClaudeDirectory()
        let paths = ClaudeRefreshLock.paths(claudeDirectory: directory)
        XCTAssertTrue(paths.current.hasSuffix("/.claude/.oauth_refresh.lock"))
        XCTAssertTrue(paths.legacy.hasSuffix("/.claude.lock"))
        let held = await ClaudeRefreshLock.acquire(claudeDirectory: directory, attempts: 1)
        XCTAssertNotNil(held)
        XCTAssertTrue(FileManager.default.fileExists(atPath: paths.current))
        XCTAssertTrue(FileManager.default.fileExists(atPath: paths.legacy))
        let second = await ClaudeRefreshLock.acquire(claudeDirectory: directory, attempts: 1)
        XCTAssertNil(second)
        held?.release()
        XCTAssertFalse(FileManager.default.fileExists(atPath: paths.current))
        XCTAssertFalse(FileManager.default.fileExists(atPath: paths.legacy))
        let again = await ClaudeRefreshLock.acquire(claudeDirectory: directory, attempts: 1)
        XCTAssertNotNil(again)
        again?.release()
    }
    func testBusyLegacyLockBlocksAndAbandonedLockIsTakenOver() async throws {
        let directory = try temporaryClaudeDirectory()
        let paths = ClaudeRefreshLock.paths(claudeDirectory: directory)
        try FileManager.default.createDirectory(atPath: paths.legacy, withIntermediateDirectories: false)
        let blocked = await ClaudeRefreshLock.acquire(claudeDirectory: directory, attempts: 1)
        XCTAssertNil(blocked)
        XCTAssertFalse(FileManager.default.fileExists(atPath: paths.current), "the current lock is given back")
        try FileManager.default.setAttributes([.modificationDate: Date().addingTimeInterval(-120)], ofItemAtPath: paths.legacy)
        let taken = await ClaudeRefreshLock.acquire(claudeDirectory: directory, attempts: 1)
        XCTAssertNotNil(taken)
        taken?.release()
    }

    /// Writes a scratch Keychain item, never Claude Code's. Opt in with AIW_KEYCHAIN_TESTS=1.
    func testSecurityToolRoundTripOnScratchItem() throws {
        try XCTSkipUnless(ProcessInfo.processInfo.environment["AIW_KEYCHAIN_TESTS"] == "1", "Keychain test is opt-in")
        let service = "AIUsageWidget-test-\(UUID().uuidString)"
        addTeardownBlock { _ = SecurityTool.delete(service: service) }
        XCTAssertNil(SecurityTool.read(service: service))
        XCTAssertTrue(SecurityTool.write(service: service, account: NSUserName(), data: stored))
        XCTAssertEqual(SecurityTool.account(service: service), NSUserName())
        XCTAssertEqual(SecurityTool.read(service: service).flatMap(ClaudeRenewal.refreshToken), "old-r")
        let renewed = try XCTUnwrap(ClaudeRenewal.merge(stored: stored, response: Data(#"{"access_token":"new-a","refresh_token":"new-r","expires_in":60}"#.utf8), now: now))
        XCTAssertTrue(SecurityTool.write(service: service, account: NSUserName(), data: renewed))
        XCTAssertEqual(SecurityTool.read(service: service).flatMap(ClaudeRenewal.refreshToken), "new-r")
        // A payload over the `security -i` line limit takes the argument path.
        let large = try JSONSerialization.data(withJSONObject: ["claudeAiOauth": ["refreshToken": "big", "pad": String(repeating: "x", count: 3000)]])
        XCTAssertTrue(SecurityTool.write(service: service, account: NSUserName(), data: large))
        XCTAssertEqual(SecurityTool.read(service: service).flatMap(ClaudeRenewal.refreshToken), "big")
    }
}
