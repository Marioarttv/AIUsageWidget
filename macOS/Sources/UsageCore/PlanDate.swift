import Foundation

/// A renewal or end date entered by hand, for subscriptions whose sign-in doesn't report one (Claude).
public struct ManualPlanDate: Codable, Equatable {
    public enum Cycle: String, Codable, CaseIterable {
        case monthly, yearly
        /// Cancelled: the plan ends on the date and doesn't renew.
        case ends
        public var title: String {
            switch self {
            case .monthly: return "Renews monthly"
            case .yearly: return "Renews yearly"
            case .ends: return "Ends on this date (cancelled)"
            }
        }
    }
    /// The renewal day as entered; renewals repeat from it.
    public var date: Date
    public var cycle: Cycle
    public init(date: Date, cycle: Cycle) { self.date = date; self.cycle = cycle }

    /// The renewal on or after today, counted from the entered day so a 31st stays the 31st where the month has one.
    public func upcoming(now: Date, calendar: Calendar = .current) -> Date {
        let anchor = calendar.startOfDay(for: date), today = calendar.startOfDay(for: now)
        guard cycle != .ends, anchor < today else { return anchor }
        let unit: Calendar.Component = cycle == .monthly ? .month : .year
        let elapsed = calendar.dateComponents([unit], from: anchor, to: today).value(for: unit) ?? 0
        for step in max(0, elapsed)...(max(0, elapsed) + 2) {
            if let next = calendar.date(byAdding: unit, value: step, to: anchor), next >= today { return next }
        }
        return anchor
    }

    /// "Renews 26 Nov", "Ends 26 Nov", or "Ended 26 Oct".
    public func label(now: Date, calendar: Calendar = .current) -> String {
        let day = upcoming(now: now, calendar: calendar)
        let text = UsageFormatting.day(day, now: now, calendar: calendar)
        if cycle != .ends { return "Renews " + text }
        return day < calendar.startOfDay(for: now) ? "Ended " + text : "Ends " + text
    }
}
