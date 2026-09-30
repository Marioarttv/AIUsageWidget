import XCTest
@testable import UsageCore

final class UsageCoreTests: XCTestCase {
    let now = Date(timeIntervalSince1970: 1_800_000_000)
    func snapshot(_ used: Double, reset: Date? = nil, minutes: Int = 300, at: Date? = nil, id: String = "session") -> UsageSnapshot {
        .init(updatedAt: at ?? now, windows: [.init(id: id, label: minutes == 300 ? "5-hour session" : "Weekly", usedPercent: used, resetsAt: reset, minutes: minutes)])
    }
    func codex(_ json: String) throws -> UsageSnapshot { try UsageParser.codex(Data(json.utf8), now: now) }
    func claude(_ json: String) throws -> UsageSnapshot { try UsageParser.claude(Data(json.utf8), now: now, plan: "max") }

    func testCodexLabelsUseDurationNotPrimaryPosition() throws {
        let result = try codex(#"{"rateLimits":{"primary":{"usedPercent":14,"windowDurationMins":10080,"resetsAt":1800000010},"planType":"pro"}}"#)
        XCTAssertEqual(result.windows.first?.label, "Weekly")
        XCTAssertEqual(result.windows.first?.remaining, 86)
        XCTAssertEqual(result.plan, "pro")
    }
    func testMultipleBucketsAndZeroAreRetained() throws {
        let result = try codex(#"{"rateLimitsByLimitId":{"codex":{"primary":{"usedPercent":0,"windowDurationMins":300}},"other":{"limitName":"Other models","primary":{"usedPercent":100,"windowDurationMins":10080}}}}"#)
        XCTAssertEqual(result.windows.count, 2)
        XCTAssertEqual(result.primary?.remaining, 100)
        XCTAssertEqual(result.windows.last?.label, "Other models · Weekly")
        XCTAssertEqual(result.windows.last?.remaining, 0)
    }
    func testMissingOrBooleanPercentIsNotZero() throws {
        let result = try codex(#"{"rateLimits":{"primary":{"usedPercent":null},"secondary":{"usedPercent":false}}}"#)
        XCTAssertTrue(result.windows.isEmpty)
        XCTAssertTrue(try claude(#"{"five_hour":{"utilization":null}}"#).windows.isEmpty)
    }
    func testEmptyBucketsFallBackToLegacy() throws {
        XCTAssertEqual(try codex(#"{"rateLimitsByLimitId":{},"rateLimits":{"primary":{"usedPercent":5,"windowDurationMins":60}}}"#).windows.first?.label, "Hourly")
    }
    func testPercentagesAreClampedAndInvalidDatesAreUnknown() throws {
        let result = try codex(#"{"rateLimits":{"primary":{"usedPercent":140,"resetsAt":-1},"secondary":{"usedPercent":-5,"resetsAt":999999999999999}}}"#)
        XCTAssertEqual(result.windows.map(\.remaining), [0, 100])
        XCTAssertTrue(result.windows.allSatisfy { $0.resetsAt == nil })
    }
    func testClaudeFormatsAndModelScopedLimits() throws {
        let result = try claude(#"{"five_hour":{"utilization":52,"resets_at":"2026-09-30T07:50:00.103876+00:00"},"seven_day":{"utilization":0,"resets_at":"2026-10-03T07:00:00Z"},"seven_day_opus":null,"limits":[{"kind":"session","percent":52},{"kind":"weekly_scoped","percent":2,"scope":{"model":{"display_name":"Fable"}}},{"kind":"future_limit","percent":9}]}"#)
        XCTAssertEqual(result.windows.map(\.label), ["5-hour session", "Weekly", "Weekly · Fable"])
        XCTAssertEqual(result.windows.first?.remaining, 48)
        XCTAssertNotNil(result.windows[0].resetsAt)
        XCTAssertNotNil(result.windows[1].resetsAt)
        XCTAssertNil(result.windows[2].resetsAt)
        XCTAssertEqual(result.plan, "max")
    }
    func testClaudeFallbackLimitList() throws {
        let result = try claude(#"{"limits":[{"kind":"session","percent":40},{"kind":"weekly_all","percent":7}]}"#)
        XCTAssertEqual(result.windows.map(\.id), ["five_hour", "seven_day"])
    }
    func testExpiredWindowsStayUnknown() {
        let value = snapshot(80, reset: now).windows[0]
        XCTAssertTrue(value.expired(at: now))
        XCTAssertTrue(UsageFormatting.countdown(now, now: now).contains("awaiting update"))
        XCTAssertEqual(UsageFormatting.countdown(nil, now: now), "Reset time unavailable")
    }
    func testLowAlertUsesStrictThresholdAndOnlyFiveHours() {
        var ledger = AlertLedger()
        ledger.observe(snapshot(90, reset: now.addingTimeInterval(300)), provider: .claude, lowEnabled: true, resetEnabled: true, now: now)
        XCTAssertTrue(ledger.pending.isEmpty)
        ledger.observe(snapshot(91, reset: now.addingTimeInterval(300), at: now.addingTimeInterval(1)), provider: .claude, lowEnabled: true, resetEnabled: true, now: now.addingTimeInterval(1))
        XCTAssertEqual(ledger.pending.map(\.kind), [.low])
        var weekly = AlertLedger()
        weekly.observe(snapshot(99, reset: now.addingTimeInterval(300), minutes: 10080), provider: .codex, lowEnabled: true, resetEnabled: true, now: now)
        XCTAssertTrue(weekly.pending.isEmpty)
    }
    func testLowAlertsDeduplicateAcrossRestartAndProviderIsIndependent() throws {
        var ledger = AlertLedger()
        let first = snapshot(95, reset: now.addingTimeInterval(500))
        ledger.observe(first, provider: .claude, lowEnabled: true, resetEnabled: true, now: now)
        ledger.markDelivered(ledger.pending.map(\.id))
        ledger = try JSONDecoder().decode(AlertLedger.self, from: JSONEncoder().encode(ledger))
        let later = snapshot(97, reset: now.addingTimeInterval(500), at: now.addingTimeInterval(5))
        ledger.observe(later, provider: .claude, lowEnabled: true, resetEnabled: true, now: now.addingTimeInterval(5))
        XCTAssertTrue(ledger.pending.isEmpty)
        ledger.observe(later, provider: .codex, lowEnabled: true, resetEnabled: true, now: now.addingTimeInterval(5))
        XCTAssertEqual(ledger.pending.count, 1)
    }
    func testFirstReadingOrElapsedClockNeverInventsReset() {
        var ledger = AlertLedger()
        ledger.observe(snapshot(95, reset: now.addingTimeInterval(-5)), provider: .claude, lowEnabled: true, resetEnabled: true, now: now)
        XCTAssertTrue(ledger.pending.isEmpty)
        ledger.observe(snapshot(95, reset: now.addingTimeInterval(-5), at: now.addingTimeInterval(10)), provider: .claude, lowEnabled: true, resetEnabled: true, now: now.addingTimeInterval(10))
        XCTAssertTrue(ledger.pending.isEmpty)
    }
    func testConfirmedRolloverEvenIfNewWindowAlreadyUsedMore() {
        var ledger = AlertLedger()
        ledger.observe(snapshot(20, reset: now.addingTimeInterval(5)), provider: .codex, lowEnabled: true, resetEnabled: true, now: now)
        ledger.observe(snapshot(40, reset: now.addingTimeInterval(18000), at: now.addingTimeInterval(10)), provider: .codex, lowEnabled: true, resetEnabled: true, now: now.addingTimeInterval(10))
        XCTAssertEqual(ledger.pending.map(\.kind), [.reset])
        XCTAssertEqual(ledger.pending.first?.remaining, 60)
    }
    func testIdleWindowWithNullBoundaryCanConfirmReset() {
        var ledger = AlertLedger()
        ledger.observe(snapshot(80, reset: now.addingTimeInterval(5)), provider: .claude, lowEnabled: true, resetEnabled: true, now: now)
        ledger.observe(snapshot(0, at: now.addingTimeInterval(10)), provider: .claude, lowEnabled: true, resetEnabled: true, now: now.addingTimeInterval(10))
        XCTAssertEqual(ledger.pending.map(\.kind), [.reset])
    }
    func testNoFalseResetFromSmallCorrectionBeforeBoundary() {
        var ledger = AlertLedger()
        ledger.observe(snapshot(80, reset: now.addingTimeInterval(500)), provider: .claude, lowEnabled: true, resetEnabled: true, now: now)
        ledger.observe(snapshot(78, reset: now.addingTimeInterval(500), at: now.addingTimeInterval(10)), provider: .claude, lowEnabled: true, resetEnabled: true, now: now.addingTimeInterval(10))
        XCTAssertTrue(ledger.pending.isEmpty)
    }
    func testEarlyResetRequiresZeroAndAdvancedBoundary() {
        var ledger = AlertLedger()
        ledger.observe(snapshot(80, reset: now.addingTimeInterval(500)), provider: .codex, lowEnabled: true, resetEnabled: true, now: now)
        ledger.observe(snapshot(0, reset: now.addingTimeInterval(18000), at: now.addingTimeInterval(10)), provider: .codex, lowEnabled: true, resetEnabled: true, now: now.addingTimeInterval(10))
        XCTAssertEqual(ledger.pending.map(\.kind), [.reset])
    }
    func testWeeklyResetAndNewLowCycle() {
        var ledger = AlertLedger()
        ledger.observe(snapshot(95, reset: now.addingTimeInterval(5), minutes: 10080), provider: .claude, lowEnabled: true, resetEnabled: true, now: now)
        ledger.observe(snapshot(0, reset: now.addingTimeInterval(604800), minutes: 10080, at: now.addingTimeInterval(10)), provider: .claude, lowEnabled: true, resetEnabled: true, now: now.addingTimeInterval(10))
        XCTAssertEqual(ledger.pending.map(\.kind), [.reset])
    }
    func testPendingDeliverySurvivesRestartButExpires() throws {
        var ledger = AlertLedger()
        ledger.observe(snapshot(95, reset: now.addingTimeInterval(500)), provider: .claude, lowEnabled: true, resetEnabled: true, now: now)
        ledger = try JSONDecoder().decode(AlertLedger.self, from: JSONEncoder().encode(ledger))
        XCTAssertEqual(ledger.deliverable(now: now, lowEnabled: true, resetEnabled: true).count, 1)
        XCTAssertTrue(ledger.deliverable(now: now.addingTimeInterval(501), lowEnabled: true, resetEnabled: true).isEmpty)
    }
    func testRecoveryRemovesUnsentLowAlert() {
        var ledger = AlertLedger()
        ledger.observe(snapshot(95, reset: now.addingTimeInterval(500)), provider: .claude, lowEnabled: true, resetEnabled: true, now: now)
        ledger.observe(snapshot(80, reset: now.addingTimeInterval(500), at: now.addingTimeInterval(10)), provider: .claude, lowEnabled: true, resetEnabled: true, now: now.addingTimeInterval(10))
        XCTAssertTrue(ledger.pending.isEmpty)
    }
    func testPendingLowAlertKeepsTheLatestPercentage() {
        var ledger = AlertLedger()
        ledger.observe(snapshot(91, reset: now.addingTimeInterval(500)), provider: .claude, lowEnabled: true, resetEnabled: true, now: now)
        ledger.observe(snapshot(98, reset: now.addingTimeInterval(500), at: now.addingTimeInterval(10)), provider: .claude, lowEnabled: true, resetEnabled: true, now: now.addingTimeInterval(10))
        XCTAssertEqual(ledger.pending.count, 1)
        XCTAssertEqual(ledger.pending.first?.remaining, 2)
    }
    func testMissingResetDateAndPartialCorrectionDoNotConfirmReset() {
        var ledger = AlertLedger()
        ledger.observe(snapshot(80, reset: now.addingTimeInterval(5)), provider: .claude, lowEnabled: true, resetEnabled: true, now: now)
        ledger.observe(snapshot(70, at: now.addingTimeInterval(10)), provider: .claude, lowEnabled: true, resetEnabled: true, now: now.addingTimeInterval(10))
        XCTAssertTrue(ledger.pending.isEmpty)
    }
    func testResetTimestampJitterDoesNotDuplicateLowAlerts() {
        var ledger = AlertLedger()
        ledger.observe(snapshot(95, reset: now.addingTimeInterval(500)), provider: .codex, lowEnabled: true, resetEnabled: true, now: now)
        ledger.markDelivered(ledger.pending.map(\.id))
        ledger.observe(snapshot(96, reset: now.addingTimeInterval(501), at: now.addingTimeInterval(10)), provider: .codex, lowEnabled: true, resetEnabled: true, now: now.addingTimeInterval(10))
        XCTAssertTrue(ledger.pending.isEmpty)
    }
    func testResetTimestampJitterDoesNotInventRollover() {
        var ledger = AlertLedger()
        ledger.observe(snapshot(1, reset: now.addingTimeInterval(5)), provider: .codex, lowEnabled: true, resetEnabled: true, now: now)
        ledger.observe(snapshot(0, reset: now.addingTimeInterval(6), at: now.addingTimeInterval(5)), provider: .codex, lowEnabled: true, resetEnabled: true, now: now.addingTimeInterval(5))
        XCTAssertTrue(ledger.pending.isEmpty)
    }
    func testNewWindowCanWarnAfterPreviousWindowWasDelivered() {
        var ledger = AlertLedger()
        ledger.observe(snapshot(95, reset: now.addingTimeInterval(5)), provider: .claude, lowEnabled: true, resetEnabled: true, now: now)
        ledger.markDelivered(ledger.pending.map(\.id))
        ledger.observe(snapshot(96, reset: now.addingTimeInterval(18000), at: now.addingTimeInterval(10)), provider: .claude, lowEnabled: true, resetEnabled: true, now: now.addingTimeInterval(10))
        XCTAssertEqual(Set(ledger.pending.map { $0.kind.rawValue }), ["reset", "low"])
    }
    func testStaleAndOutOfOrderObservationsCannotAlertOrReplaceBaseline() {
        var ledger = AlertLedger()
        ledger.observe(snapshot(95, reset: now.addingTimeInterval(500), at: now.addingTimeInterval(-3600)), provider: .codex, lowEnabled: true, resetEnabled: true, now: now)
        XCTAssertTrue(ledger.pending.isEmpty)
        ledger.observe(snapshot(80, reset: now.addingTimeInterval(5)), provider: .codex, lowEnabled: true, resetEnabled: true, now: now)
        ledger.observe(snapshot(95, reset: now.addingTimeInterval(500), at: now.addingTimeInterval(-5)), provider: .codex, lowEnabled: true, resetEnabled: true, now: now)
        XCTAssertTrue(ledger.pending.isEmpty)
        ledger.observe(snapshot(0, reset: now.addingTimeInterval(18000), at: now.addingTimeInterval(10)), provider: .codex, lowEnabled: true, resetEnabled: true, now: now.addingTimeInterval(10))
        XCTAssertEqual(ledger.pending.map(\.kind), [.reset])
    }
    func testDisabledAlertsStillAdvanceBaselineWithoutRetroactiveReset() {
        var ledger = AlertLedger()
        ledger.observe(snapshot(95, reset: now.addingTimeInterval(5)), provider: .claude, lowEnabled: false, resetEnabled: false, now: now)
        ledger.observe(snapshot(0, reset: now.addingTimeInterval(18000), at: now.addingTimeInterval(10)), provider: .claude, lowEnabled: false, resetEnabled: false, now: now.addingTimeInterval(10))
        XCTAssertTrue(ledger.pending.isEmpty)
        ledger.observe(snapshot(1, reset: now.addingTimeInterval(18000), at: now.addingTimeInterval(20)), provider: .claude, lowEnabled: true, resetEnabled: true, now: now.addingTimeInterval(20))
        XCTAssertTrue(ledger.pending.isEmpty)
    }
    func testMissingWindowDoesNotEraseResetBaseline() {
        var ledger = AlertLedger()
        ledger.observe(snapshot(80, reset: now.addingTimeInterval(5)), provider: .claude, lowEnabled: true, resetEnabled: true, now: now)
        ledger.observe(.init(updatedAt: now.addingTimeInterval(3), windows: []), provider: .claude, lowEnabled: true, resetEnabled: true, now: now.addingTimeInterval(3))
        ledger.observe(snapshot(0, reset: now.addingTimeInterval(18000), at: now.addingTimeInterval(10)), provider: .claude, lowEnabled: true, resetEnabled: true, now: now.addingTimeInterval(10))
        XCTAssertEqual(ledger.pending.map(\.kind), [.reset])
    }
}
