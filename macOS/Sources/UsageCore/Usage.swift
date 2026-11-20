import Foundation
import CoreFoundation

public enum ProviderID: String, Codable, CaseIterable, Identifiable {
    case claude, codex, antigravity
    public var id: String { rawValue }
    public var title: String {
        switch self {
        case .claude: return "Claude"
        case .codex: return "Codex"
        case .antigravity: return "Antigravity"
        }
    }
}

public struct UsageWindow: Codable, Equatable, Identifiable {
    public let id: String
    public let label: String
    public let usedPercent: Double
    public let resetsAt: Date?
    public let minutes: Int?
    /// The quota these windows share, such as one Codex limit bucket or one Antigravity model group.
    /// nil is the whole account: such a window limits every other window.
    public let pool: String?
    public var remaining: Double { max(0, min(100, 100 - usedPercent)) }
    public func expired(at now: Date) -> Bool { resetsAt.map { $0 <= now } ?? false }
    /// Shown as 0% in whole numbers.
    var usedUp: Bool { remaining < 1 }

    public init(id: String, label: String, usedPercent: Double, resetsAt: Date?, minutes: Int?, pool: String? = nil) {
        self.id = id; self.label = label; self.usedPercent = usedPercent
        self.resetsAt = resetsAt; self.minutes = minutes; self.pool = pool
    }
}

public struct UsageSnapshot: Codable, Equatable {
    public let updatedAt: Date
    public let windows: [UsageWindow]
    public let plan: String?
    public init(updatedAt: Date, windows: [UsageWindow], plan: String? = nil) {
        self.updatedAt = updatedAt; self.windows = windows; self.plan = plan
    }
    public var primary: UsageWindow? {
        windows.min { ($0.minutes ?? Int.max) < ($1.minutes ?? Int.max) }
    }

    /// The used-up limit that leaves nothing in `window` either: a longer window of the same quota (a spent weekly
    /// limit also ends the 5-hour session), or an account-wide window for a model-specific one. When several apply,
    /// the one that resets last decides. Expired windows await a new reading and limit nothing.
    public func blocker(of window: UsageWindow, now: Date) -> UsageWindow? {
        guard let minutes = window.minutes else { return nil }
        return windows.filter { other in
            guard other.id != window.id, other.usedUp, !other.expired(at: now), let length = other.minutes,
                  other.pool == nil || other.pool == window.pool else { return false }
            return length > minutes || (length == minutes && other.pool == nil && window.pool != nil)
        }.max { ($0.resetsAt ?? .distantFuture) < ($1.resetsAt ?? .distantFuture) }
    }
    /// What can actually be used of `window`: 0 while a longer limit is used up.
    public func available(_ window: UsageWindow, now: Date) -> Double {
        blocker(of: window, now: now) == nil ? window.remaining : 0
    }
}

public enum UsageParser {
    public static func windowLabel(_ minutes: Int?, fallback: String) -> String {
        guard let minutes, minutes > 0 else { return fallback }
        switch minutes {
        case 300: return "5-hour session"
        case 10080: return "Weekly"
        case 1440: return "Daily"
        case 60: return "Hourly"
        default:
            if minutes % 1440 == 0 { return "\(minutes / 1440)-day window" }
            if minutes % 60 == 0 { return "\(minutes / 60)-hour window" }
            return "\(minutes)-minute window"
        }
    }

    private static func number(_ value: Any?) -> Double? {
        guard let n = value as? NSNumber, CFGetTypeID(n) != CFBooleanGetTypeID(), n.doubleValue.isFinite else { return nil }
        return n.doubleValue
    }
    private static func date(_ value: Any?) -> Date? {
        guard let seconds = number(value), seconds > 0, seconds < 253402300800 else { return nil }
        return Date(timeIntervalSince1970: seconds)
    }
    private static func isoDate(_ value: Any?) -> Date? {
        guard let string = value as? String else { return nil }
        let format = ISO8601DateFormatter()
        format.formatOptions = [.withInternetDateTime, .withFractionalSeconds]
        if let date = format.date(from: string) { return date }
        format.formatOptions = [.withInternetDateTime]
        return format.date(from: string)
    }

    public static func codex(_ data: Data, now: Date = Date()) throws -> UsageSnapshot {
        let root = try JSONSerialization.jsonObject(with: data) as? [String: Any] ?? [:]
        var windows: [UsageWindow] = []
        var plan: String?
        func add(_ bucket: [String: Any], id: String, multiple: Bool) {
            plan = plan ?? bucket["planType"] as? String
            for key in ["primary", "secondary"] {
                guard let value = bucket[key] as? [String: Any], let used = number(value["usedPercent"]) else { continue }
                let raw = number(value["windowDurationMins"])
                let minutes = raw.flatMap { $0 > 0 && $0 < 10_000_000 ? Int($0) : nil }
                let name = bucket["limitName"] as? String ?? id
                let label = windowLabel(minutes, fallback: key == "primary" ? "Current window" : "Additional window")
                windows.append(.init(id: "\(id):\(key)", label: multiple ? "\(name) · \(label)" : label,
                                     usedPercent: max(0, min(100, used)), resetsAt: date(value["resetsAt"]), minutes: minutes,
                                     pool: multiple ? id : nil))
            }
        }
        if let buckets = root["rateLimitsByLimitId"] as? [String: Any] {
            for key in buckets.keys.sorted() {
                if let bucket = buckets[key] as? [String: Any] { add(bucket, id: key, multiple: buckets.count > 1) }
            }
        }
        if windows.isEmpty, let bucket = root["rateLimits"] as? [String: Any] {
            add(bucket, id: bucket["limitId"] as? String ?? "codex", multiple: false)
        }
        return UsageSnapshot(updatedAt: now, windows: windows, plan: plan)
    }

    public static func claude(_ data: Data, now: Date = Date(), plan: String? = nil) throws -> UsageSnapshot {
        let root = try JSONSerialization.jsonObject(with: data) as? [String: Any] ?? [:]
        var windows: [UsageWindow] = []
        func add(_ key: String, _ label: String, _ minutes: Int, _ used: Any?, _ reset: Any?) {
            guard let used = number(used), !windows.contains(where: { $0.id == key || $0.label == label }) else { return }
            // The session and the overall weekly limit cover the account; a model's weekly limit covers that model.
            let pool = key == "five_hour" || key == "seven_day" ? nil : key
            windows.append(.init(id: key, label: label, usedPercent: max(0, min(100, used)), resetsAt: isoDate(reset),
                                 minutes: minutes, pool: pool))
        }
        for (key, label) in [("five_hour", "5-hour session"), ("seven_day", "Weekly"), ("seven_day_opus", "Weekly · Opus"), ("seven_day_sonnet", "Weekly · Sonnet")] {
            if let value = root[key] as? [String: Any] { add(key, label, key == "five_hour" ? 300 : 10080, value["utilization"], value["resets_at"]) }
        }
        for value in root["limits"] as? [[String: Any]] ?? [] {
            switch value["kind"] as? String {
            case "session": add("five_hour", "5-hour session", 300, value["percent"], value["resets_at"])
            case "weekly_all": add("seven_day", "Weekly", 10080, value["percent"], value["resets_at"])
            case "weekly_scoped":
                if let scope = value["scope"] as? [String: Any], let model = scope["model"] as? [String: Any],
                   let name = model["display_name"] as? String, !name.isEmpty {
                    add("seven_day_" + name.lowercased(), "Weekly · " + name, 10080, value["percent"], value["resets_at"])
                }
            default: break
            }
        }
        return UsageSnapshot(updatedAt: now, windows: windows, plan: plan)
    }
}

extension UsageParser {
    /// Antigravity CLI's `/usage` command in JSON print mode: model groups, each with a 5-hour and a weekly bucket.
    public static func antigravity(_ data: Data, now: Date = Date()) throws -> UsageSnapshot {
        let root = try JSONSerialization.jsonObject(with: data) as? [String: Any] ?? [:]
        let command = root["command"] as? [String: Any]
        // Gemini first, so its 5-hour limit leads the card and the menu bar whatever order the CLI uses.
        let groups = ((command?["data"] as? [String: Any])?["groups"] as? [[String: Any]] ?? []).enumerated().sorted { a, b in
            let x = (a.element["name"] as? String ?? "").lowercased().hasPrefix("gemini models")
            let y = (b.element["name"] as? String ?? "").lowercased().hasPrefix("gemini models")
            return x != y ? x : a.offset < b.offset
        }.map(\.element)
        // The plain-text table the CLI prints alongside: "Group\tBucket\t97%\tReset".
        var printed: [String: Double] = [:]
        for line in (root["response"] as? String ?? "").split(separator: "\n") {
            let cells = line.split(separator: "\t", omittingEmptySubsequences: false).map { $0.trimmingCharacters(in: .whitespaces) }
            guard cells.count >= 3, cells[2].hasSuffix("%"), let value = Double(cells[2].dropLast()) else { continue }
            printed[cells[0] + "\t" + cells[1]] = value
        }
        var windows: [UsageWindow] = []
        for (index, group) in groups.enumerated() {
            let groupName = (group["name"] as? String).flatMap { $0.isEmpty ? nil : $0 } ?? "Group \(index + 1)"
            let short = shortGroupName(groupName)
            // Shortest window first within each group, so a group reads "5-hour session", then "Weekly".
            let buckets = (group["buckets"] as? [[String: Any]] ?? []).enumerated().sorted { a, b in
                let x = antigravityMinutes(a.element["window"] as? String) ?? Int.max
                let y = antigravityMinutes(b.element["window"] as? String) ?? Int.max
                return x != y ? x < y : a.offset < b.offset
            }.map(\.element)
            for bucket in buckets {
                guard bucket["disabled"] as? Bool != true else { continue }
                let minutes = antigravityMinutes(bucket["window"] as? String)
                let name = (bucket["name"] as? String).flatMap { $0.isEmpty ? nil : $0 }
                let id = (bucket["id"] as? String).flatMap { $0.isEmpty ? nil : $0 }
                    ?? groupName + ":" + (bucket["window"] as? String ?? name ?? String(windows.count))
                guard !windows.contains(where: { $0.id == id }) else { continue }
                // The CLI leaves out zero values, so a bucket without a fraction is used up, as its printed table
                // confirms. Without either, or a reset time, the bucket is unknown and left out.
                let printedFraction = name.flatMap { printed[groupName + "\t" + $0] }.map { $0 / 100 }
                let reset = isoDate(bucket["reset_time"])
                guard let fraction = number(bucket["remaining_fraction"]).map({ max(0, min(1, $0)) })
                        ?? printedFraction.map({ max(0, min(1, $0)) }) ?? (reset != nil ? 0 : nil) else { continue }
                windows.append(.init(id: id, label: short + " · " + windowLabel(minutes, fallback: name ?? "Limit"),
                                     usedPercent: (1 - fraction) * 100, resetsAt: reset,
                                     minutes: minutes, pool: groupName))
            }
        }
        return UsageSnapshot(updatedAt: now, windows: windows)
    }

    /// "Gemini Models" → "Gemini", "Claude and GPT models" → "Claude and GPT".
    static func shortGroupName(_ name: String) -> String {
        let trimmed = name.trimmingCharacters(in: .whitespaces)
        guard trimmed.lowercased().hasSuffix(" models") else { return trimmed }
        return String(trimmed.dropLast(" models".count))
    }

    /// "5h", "weekly", "daily", "30m", "2d", "1w".
    static func antigravityMinutes(_ window: String?) -> Int? {
        guard let text = window?.lowercased().trimmingCharacters(in: .whitespaces), !text.isEmpty else { return nil }
        switch text {
        case "weekly", "week": return 10080
        case "daily", "day": return 1440
        case "hourly", "hour": return 60
        default: break
        }
        guard let unit = text.last, let count = Int(text.dropLast()), count > 0, count < 100_000 else { return nil }
        switch unit {
        case "m": return count
        case "h": return count * 60
        case "d": return count * 1440
        case "w": return count * 10080
        default: return nil
        }
    }
}

public enum UsageFormatting {
    public static func countdown(_ reset: Date?, now: Date) -> String {
        guard let reset else { return "Reset time unavailable" }
        let seconds = reset.timeIntervalSince(now)
        guard seconds > 0 else { return "Reset reached · awaiting update" }
        let minutes = Int(ceil(seconds / 60))
        if minutes >= 1440 { return "Resets in \(minutes / 1440)d \((minutes % 1440) / 60)h" }
        if minutes >= 60 { return "Resets in \(minutes / 60)h \(minutes % 60)m" }
        return "Resets in \(max(1, minutes))m"
    }
    /// The end of a paid ChatGPT period. The sign-in doesn't say whether the plan will renew or was cancelled.
    public static func paidPeriod(_ end: Date, now: Date, calendar: Calendar = .current) -> String {
        let text = day(end, now: now, calendar: calendar)
        return end > now ? "Renews or ends " + text : "Paid until " + text
    }
    /// "17 Nov", with the year only when it isn't the current one.
    public static func day(_ date: Date, now: Date, calendar: Calendar = .current) -> String {
        let sameYear = calendar.component(.year, from: date) == calendar.component(.year, from: now)
        var style = Date.FormatStyle.dateTime.day().month(.abbreviated)
        style.timeZone = calendar.timeZone
        return date.formatted(sameYear ? style : style.year())
    }
    /// Why a window shows 0%, short enough for one line: "Weekly used up · 3d 4h".
    public static func blocked(by window: UsageWindow, now: Date) -> String {
        UsageParser.windowLabel(window.minutes, fallback: "Longer limit") + " used up · " + shortCountdown(window.resetsAt, now: now)
    }
    /// A short countdown for compact rows: "3d 4h", "2h 5m", "12m".
    public static func shortCountdown(_ reset: Date?, now: Date) -> String {
        guard let reset else { return "—" }
        let seconds = reset.timeIntervalSince(now)
        guard seconds > 0 else { return "awaiting update" }
        let minutes = Int(ceil(seconds / 60))
        if minutes >= 1440 { return "\(minutes / 1440)d \((minutes % 1440) / 60)h" }
        if minutes >= 60 { return "\(minutes / 60)h \(minutes % 60)m" }
        return "\(max(1, minutes))m"
    }
    public static func age(_ date: Date?, now: Date) -> String {
        guard let date else { return "Waiting for a reading" }
        let minutes = max(0, Int(now.timeIntervalSince(date) / 60))
        if minutes < 1 { return "Updated just now" }
        if minutes < 60 { return "Updated \(minutes)m ago" }
        if minutes < 1440 { return "Updated \(minutes / 60)h ago" }
        return "Updated \(minutes / 1440)d ago"
    }
}
