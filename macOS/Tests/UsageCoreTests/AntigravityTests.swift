import XCTest
@testable import UsageCore

final class AntigravityTests: XCTestCase {
    let now = ISO8601DateFormatter().date(from: "2026-11-20T16:30:00Z")!

    /// `antigravity -p /usage --output-format json`, as CLI 1.5.13 prints it (descriptions shortened).
    let sample = #"""
    {"conversation_id":"","status":"SUCCESS","response":"Gemini Models\tWeekly Limit Remaining\t100%\t2026-11-27T16:14:33Z\n","duration_seconds":0,"num_turns":0,
     "usage":{"input_tokens":0,"output_tokens":0,"total_tokens":0},
     "command":{"name":"usage","data":{"description":"Within each group, models share a weekly limit and a 5-hour limit.","groups":[
      {"name":"Gemini Models","description":"Gemini 4 Neon, Gemini 4 Hydrogen","buckets":[
        {"id":"gemini-weekly","name":"Weekly Limit Remaining","window":"weekly","remaining_fraction":0.9990500807762146,"reset_time":"2026-11-27T16:14:33Z"},
        {"id":"gemini-5h","name":"Five Hour Limit Remaining","window":"5h","remaining_fraction":0.9966751933097839,"reset_time":"2026-11-20T21:14:33Z"}]},
      {"name":"Claude and GPT models","buckets":[
        {"id":"3p-weekly","name":"Weekly Limit Remaining","window":"weekly","remaining_fraction":1,"reset_time":"2026-11-27T16:24:14Z"},
        {"id":"3p-5h","name":"Five Hour Limit Remaining","window":"5h","reset_time":"2026-11-20T21:24:14Z"}]},
      {"name":"Gemini 4 Argon","buckets":[
        {"id":"gemini-argon-wtu-weekly","name":"Gemini 4 Argon Weekly Limit","window":"weekly","remaining_fraction":1,"reset_time":"2026-11-27T16:24:14Z"},
        {"id":"off","name":"Disabled","window":"5h","remaining_fraction":1,"disabled":true}]}]}}}
    """#

    func testParsesGroupsIntoLabelledWindows() throws {
        let snapshot = try UsageParser.antigravity(Data(sample.utf8), now: now)
        XCTAssertEqual(snapshot.windows.map(\.label), ["Gemini · 5-hour session", "Gemini · Weekly", "Claude and GPT · 5-hour session",
                                                       "Claude and GPT · Weekly", "Gemini 4 Argon · Weekly"])
        XCTAssertEqual(snapshot.windows.map(\.minutes), [300, 10080, 300, 10080, 10080])
        XCTAssertEqual(snapshot.windows.map(\.pool), ["Gemini Models", "Gemini Models", "Claude and GPT models",
                                                      "Claude and GPT models", "Gemini 4 Argon"])
        XCTAssertEqual(snapshot.primary?.id, "gemini-5h", "the Gemini 5-hour limit leads the card and the menu bar")
        XCTAssertEqual(snapshot.windows[0].remaining, 99.66751933097839, accuracy: 0.0001)
        XCTAssertEqual(snapshot.windows[0].resetsAt, ISO8601DateFormatter().date(from: "2026-11-20T21:14:33Z"))
        // The CLI omits zero values: a bucket without a fraction is used up.
        XCTAssertEqual(snapshot.windows[2].remaining, 0)
    }

    func testGeminiLeadsAndUnknownBucketsAreLeftOut() throws {
        let json = #"""
        {"status":"SUCCESS","response":"Claude and GPT models\tFive Hour Limit Remaining\t42%\t2026-11-20T21:24:14Z\n",
         "command":{"data":{"groups":[
          {"name":"Claude and GPT models","buckets":[{"id":"3p-5h","name":"Five Hour Limit Remaining","window":"5h","reset_time":"2026-11-20T21:24:14Z"},
                                                     {"id":"3p-x","name":"Mystery","window":"5h"}]},
          {"name":"Gemini Models","buckets":[{"id":"g-5h","name":"Five Hour Limit Remaining","window":"5h","remaining_fraction":0.5,"reset_time":"2026-11-20T21:14:33Z"}]}]}}}
        """#
        let snapshot = try UsageParser.antigravity(Data(json.utf8), now: now)
        XCTAssertEqual(snapshot.windows.map(\.id), ["g-5h", "3p-5h"], "Gemini first; no fraction, table row or reset time: unknown")
        XCTAssertEqual(snapshot.primary?.id, "g-5h")
        XCTAssertEqual(snapshot.windows[1].remaining, 42, accuracy: 0.001, "the printed table fills in a missing fraction")
    }

    func testWindowNames() {
        XCTAssertEqual(UsageParser.antigravityMinutes("5h"), 300)
        XCTAssertEqual(UsageParser.antigravityMinutes("Weekly"), 10080)
        XCTAssertEqual(UsageParser.antigravityMinutes("daily"), 1440)
        XCTAssertEqual(UsageParser.antigravityMinutes("30m"), 30)
        XCTAssertEqual(UsageParser.antigravityMinutes("2d"), 2880)
        XCTAssertNil(UsageParser.antigravityMinutes("monthly"))
        XCTAssertNil(UsageParser.antigravityMinutes(nil))
        XCTAssertEqual(UsageParser.shortGroupName("Gemini 4 Argon"), "Gemini 4 Argon")
    }

    func testOutcomes() {
        guard case .reading(let snapshot) = AntigravityCLI.outcome(stdout: Data(sample.utf8), now: now) else { return XCTFail() }
        XCTAssertEqual(snapshot.windows.count, 5)
        let signedOut = #"{"conversation_id":"","status":"ERROR","response":"","error":"authentication failed or timed out"}"#
        // Also seen after a slow or offline renewal, so it's retried; only the CLI's login prompt means signed out.
        XCTAssertEqual(AntigravityCLI.outcome(stdout: Data(signedOut.utf8)), .failure("Antigravity didn't answer · retrying automatically"))
        let empty = #"{"status":"SUCCESS","response":"no quota summary is available for this account"}"#
        XCTAssertEqual(AntigravityCLI.outcome(stdout: Data(empty.utf8)), .failure("No Antigravity quota returned for this account"))
        XCTAssertEqual(AntigravityCLI.outcome(stdout: Data("Fetching…".utf8)), .failure("Antigravity returned an invalid response"))
        XCTAssertTrue(AntigravityCLI.asksForLogin(Data("Authentication required. Please visit the URL to log in:\n".utf8)))
        XCTAssertFalse(AntigravityCLI.asksForLogin(Data("Fetching available models...".utf8)))
    }

    func testEnvironmentCannotOpenABrowserOrUpdate() {
        let environment = AntigravityCLI.environment(["HOME": "/Users/a", "PATH": "/usr/bin:/bin", "SECRET": "x", "TMPDIR": "/tmp/x"])
        XCTAssertEqual(environment, ["HOME": "/Users/a", "TMPDIR": "/tmp/x", "PATH": "/var/empty", "AGY_CLI_DISABLE_AUTO_UPDATE": "1"])
        XCTAssertTrue(AntigravityCLI.arguments.contains("/usage"))
        XCTAssertEqual(AntigravityCLI.arguments.last, "/dev/null", "no log file per refresh")
    }

    func testFindsNativeCLIOnPathOrFromT3() throws {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent("agy-\(UUID().uuidString)")
        defer { try? FileManager.default.removeItem(at: root) }
        let home = root.appendingPathComponent("home"), bin = root.appendingPathComponent("bin")
        func binary(_ url: URL, script: Bool = false) throws {
            try FileManager.default.createDirectory(at: url.deletingLastPathComponent(), withIntermediateDirectories: true)
            try (script ? Data("#!/bin/sh\n".utf8) : Data([0xcf, 0xfa, 0xed, 0xfe, 0, 0, 0, 0])).write(to: url)
            try FileManager.default.setAttributes([.posixPermissions: 0o755], ofItemAtPath: url.path)
        }
        XCTAssertNil(AntigravityCLI.executable(home: home, path: bin.path))
        // The editor's launcher script and anything inside an app bundle don't qualify.
        try binary(bin.appendingPathComponent("antigravity"), script: true)
        let bundled = root.appendingPathComponent("Antigravity.app/Contents/Resources/bin/agy")
        try binary(bundled)
        try FileManager.default.createSymbolicLink(at: bin.appendingPathComponent("agy"), withDestinationURL: bundled)
        XCTAssertNil(AntigravityCLI.executable(home: home, path: bin.path))
        // T3 Code's managed copy, named by its install record.
        let tools = home.appendingPathComponent(".t3/tools/antigravity-acp")
            .appendingPathComponent(ProcessInfo.processInfo.machineHardwareName == "arm64" ? "darwin-arm64" : "darwin-x64")
        try FileManager.default.createDirectory(at: tools, withIntermediateDirectories: true)
        try Data(#"{"releaseId":"abc"}"#.utf8).write(to: tools.appendingPathComponent("active.json"))
        try binary(tools.appendingPathComponent("versions/abc/antigravity"))
        XCTAssertNil(AntigravityCLI.executable(home: home, path: bin.path), "an install still in progress isn't run")
        try Data(#"{"releaseId":"abc","executable":{"name":"antigravity"}}"#.utf8)
            .write(to: tools.appendingPathComponent("versions/abc/.install-complete.json"))
        XCTAssertEqual(AntigravityCLI.executable(home: home, path: bin.path)?.managedByT3, true)
        // One on PATH comes first.
        let own = root.appendingPathComponent("other/antigravity")
        try binary(own)
        XCTAssertEqual(AntigravityCLI.executable(home: home, path: own.deletingLastPathComponent().path),
                       .init(url: own.resolvingSymlinksInPath(), managedByT3: false))
        try Data(#"{"releaseId":"../abc"}"#.utf8).write(to: tools.appendingPathComponent("active.json"))
        XCTAssertNil(AntigravityCLI.executable(home: home, path: bin.path))
    }
}

/// A used-up longer limit leaves nothing in the windows it covers.
final class BlockedWindowTests: XCTestCase {
    let now = Date(timeIntervalSince1970: 1_800_000_000)
    func window(_ id: String, used: Double, minutes: Int, pool: String? = nil, reset: TimeInterval = 3600) -> UsageWindow {
        .init(id: id, label: id, usedPercent: used, resetsAt: now.addingTimeInterval(reset), minutes: minutes, pool: pool)
    }

    func testUsedUpWeeklyEndsTheSession() {
        let session = window("session", used: 0, minutes: 300), weekly = window("weekly", used: 100, minutes: 10080, reset: 86400)
        let snapshot = UsageSnapshot(updatedAt: now, windows: [session, weekly])
        XCTAssertEqual(snapshot.blocker(of: session, now: now), weekly)
        XCTAssertEqual(snapshot.available(session, now: now), 0)
        XCTAssertEqual(snapshot.available(weekly, now: now), 0)
        XCTAssertEqual(UsageFormatting.blocked(by: weekly, now: now), "Weekly used up · 1d 0h")
        // 99.5% used shows as 0% and counts as used up; a spent session never limits the weekly window.
        let nearly = UsageSnapshot(updatedAt: now, windows: [window("session", used: 100, minutes: 300), window("weekly", used: 99.5, minutes: 10080)])
        XCTAssertEqual(nearly.available(nearly.windows[0], now: now), 0)
        XCTAssertEqual(nearly.available(nearly.windows[1], now: now), 0.5, accuracy: 0.001)
        let spentSession = UsageSnapshot(updatedAt: now, windows: [window("session", used: 100, minutes: 300), window("weekly", used: 40, minutes: 10080)])
        XCTAssertEqual(spentSession.available(spentSession.windows[1], now: now), 60)
    }

    func testExpiredWeeklyAwaitsAReadingInsteadOfBlocking() {
        let session = window("session", used: 10, minutes: 300)
        let snapshot = UsageSnapshot(updatedAt: now, windows: [session, window("weekly", used: 100, minutes: 10080, reset: -5)])
        XCTAssertNil(snapshot.blocker(of: session, now: now))
        XCTAssertEqual(snapshot.available(session, now: now), 90)
    }

    func testPoolsLimitOnlyTheirOwnWindows() {
        // Antigravity: a spent Gemini week doesn't touch the Claude and GPT group.
        let gemini = window("g5", used: 0, minutes: 300, pool: "Gemini"), others = window("c5", used: 0, minutes: 300, pool: "Claude")
        let snapshot = UsageSnapshot(updatedAt: now, windows: [gemini, window("gw", used: 100, minutes: 10080, pool: "Gemini"),
                                                               others, window("cw", used: 20, minutes: 10080, pool: "Claude")])
        XCTAssertEqual(snapshot.available(gemini, now: now), 0)
        XCTAssertEqual(snapshot.available(others, now: now), 100)
    }

    func testClaudeAccountWeekCoversModelWeeksButNotTheReverse() throws {
        let spentModel = try UsageParser.claude(Data(#"{"five_hour":{"utilization":20},"seven_day":{"utilization":30},"seven_day_opus":{"utilization":100}}"#.utf8), now: now)
        XCTAssertEqual(spentModel.windows.map { spentModel.available($0, now: now) }, [80, 70, 0])
        let spentWeek = try UsageParser.claude(Data(#"{"five_hour":{"utilization":20},"seven_day":{"utilization":100},"seven_day_opus":{"utilization":10}}"#.utf8), now: now)
        XCTAssertEqual(spentWeek.windows.map { spentWeek.available($0, now: now) }, [0, 0, 0])
    }

    func testCodexBucketsAreSeparatePools() throws {
        let single = try UsageParser.codex(Data(#"{"rateLimits":{"primary":{"usedPercent":0,"windowDurationMins":300},"secondary":{"usedPercent":100,"windowDurationMins":10080}}}"#.utf8), now: now)
        XCTAssertEqual(single.available(single.primary!, now: now), 0)
        let multiple = try UsageParser.codex(Data(#"{"rateLimitsByLimitId":{"codex":{"primary":{"usedPercent":0,"windowDurationMins":300}},"other":{"primary":{"usedPercent":100,"windowDurationMins":10080}}}}"#.utf8), now: now)
        XCTAssertEqual(multiple.available(multiple.primary!, now: now), 100)
    }

    func testBlockedWindowsNeitherResetNorRunLow() {
        var ledger = AlertLedger()
        func reading(session: Double, sessionReset: TimeInterval, at: TimeInterval) -> UsageSnapshot {
            UsageSnapshot(updatedAt: now.addingTimeInterval(at), windows: [
                .init(id: "s", label: "5-hour session", usedPercent: session, resetsAt: now.addingTimeInterval(sessionReset), minutes: 300),
                .init(id: "w", label: "Weekly", usedPercent: 100, resetsAt: now.addingTimeInterval(86400), minutes: 10080)])
        }
        ledger.observe(reading(session: 95, sessionReset: 5, at: 0), provider: .codex, lowEnabled: true, resetEnabled: true, now: now)
        XCTAssertTrue(ledger.pending.isEmpty, "no low alert while the week is spent")
        ledger.observe(reading(session: 0, sessionReset: 18000, at: 10), provider: .codex, lowEnabled: true, resetEnabled: true,
                       now: now.addingTimeInterval(10))
        XCTAssertTrue(ledger.pending.isEmpty, "no reset alert for a session the spent week still blocks")
    }

    func testUnusedWindowWithMovingBoundaryIsNoReset() {
        var ledger = AlertLedger()
        let first = UsageSnapshot(updatedAt: now, windows: [.init(id: "s", label: "5-hour session", usedPercent: 0,
                                                                   resetsAt: now.addingTimeInterval(5), minutes: 300)])
        let later = UsageSnapshot(updatedAt: now.addingTimeInterval(10), windows: [.init(id: "s", label: "5-hour session", usedPercent: 0,
                                                                   resetsAt: now.addingTimeInterval(18010), minutes: 300)])
        ledger.observe(first, provider: .antigravity, lowEnabled: true, resetEnabled: true, now: now)
        ledger.observe(later, provider: .antigravity, lowEnabled: true, resetEnabled: true, now: now.addingTimeInterval(10))
        XCTAssertTrue(ledger.pending.isEmpty)
    }

    func testCachedWindowsWithoutPoolStillDecode() throws {
        let json = #"{"updatedAt":"2026-11-20T10:00:00Z","windows":[{"id":"a","label":"Weekly","usedPercent":5,"minutes":10080}]}"#
        let decoder = JSONDecoder(); decoder.dateDecodingStrategy = .iso8601
        XCTAssertNil(try decoder.decode(UsageSnapshot.self, from: Data(json.utf8)).windows.first?.pool)
    }
}

private extension ProcessInfo {
    var machineHardwareName: String {
        var info = utsname(); uname(&info)
        return withUnsafeBytes(of: &info.machine) { String(decoding: $0.prefix { $0 != 0 }, as: UTF8.self) }
    }
}
