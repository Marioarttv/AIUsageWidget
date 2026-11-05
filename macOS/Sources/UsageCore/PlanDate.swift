import Foundation

/// A renewal or end date entered by hand: for Claude, whose sign-in doesn't report one, or to correct a ChatGPT date.
public struct ManualPlanDate: Codable, Equatable {
    public enum Cycle: String, Codable, CaseIterable {
        case monthly, yearly
        /// Cancelled: the plan ends on the date and doesn't renew.
        case ends
        public var title: String {
            switch self {
            case .monthly: return "Monthly"
            case .yearly: return "Yearly"
            case .ends: return "Ends"
            }
        }
        /// What choosing this cycle means, shown under the choice.
        public var explanation: String {
            switch self {
            case .monthly: return "Renews every month on this day"
            case .yearly: return "Renews every year on this day"
            case .ends: return "Cancelled: the plan ends on this day"
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

    /// Entries keyed by `UsageSource.id`, including the single Claude entry saved by earlier versions.
    public static func decodeAll(_ data: Data?, legacyClaude: Data?) -> [String: ManualPlanDate] {
        var entries = data.flatMap { try? JSONDecoder().decode([String: ManualPlanDate].self, from: $0) } ?? [:]
        if entries[UsageSource.claude.id] == nil,
           let legacy = legacyClaude.flatMap({ try? JSONDecoder().decode(ManualPlanDate.self, from: $0) }) {
            entries[UsageSource.claude.id] = legacy
        }
        return entries
    }
}

/// The days of a calendar month laid out in week rows for a date picker.
public enum MonthGrid {
    /// Six week rows of seven days, starting on the calendar's first weekday, so every month takes the same space.
    /// nil pads the cells before the 1st and after the month's last day.
    public static func days(of month: Date, calendar: Calendar = .current) -> [Date?] {
        guard let interval = calendar.dateInterval(of: .month, for: month),
              let count = calendar.range(of: .day, in: .month, for: month)?.count else { return [] }
        let first = interval.start
        let lead = (calendar.component(.weekday, from: first) - calendar.firstWeekday + 7) % 7
        var cells: [Date?] = Array(repeating: nil, count: lead)
        for offset in 0..<count { cells.append(calendar.date(byAdding: .day, value: offset, to: first)) }
        while cells.count < 42 { cells.append(nil) }
        return cells
    }
    /// Short weekday symbols in column order, starting on the calendar's first weekday.
    public static func weekdaySymbols(calendar: Calendar = .current) -> [String] {
        let symbols = calendar.veryShortStandaloneWeekdaySymbols
        return (0..<7).map { symbols[(calendar.firstWeekday - 1 + $0) % 7] }
    }
}
