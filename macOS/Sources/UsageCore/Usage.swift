import Foundation
import CoreFoundation

public enum ProviderID: String, Codable, CaseIterable, Identifiable {
    case claude, codex
    public var id: String { rawValue }
    public var title: String { self == .claude ? "Claude" : "Codex" }
}

public struct UsageWindow: Codable, Equatable, Identifiable {
    public let id: String
    public let label: String
    public let usedPercent: Double
    public let resetsAt: Date?
    public let minutes: Int?
    public var remaining: Double { max(0, min(100, 100 - usedPercent)) }
    public func expired(at now: Date) -> Bool { resetsAt.map { $0 <= now } ?? false }

    public init(id: String, label: String, usedPercent: Double, resetsAt: Date?, minutes: Int?) {
        self.id = id; self.label = label; self.usedPercent = usedPercent
        self.resetsAt = resetsAt; self.minutes = minutes
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
                                     usedPercent: max(0, min(100, used)), resetsAt: date(value["resetsAt"]), minutes: minutes))
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
            windows.append(.init(id: key, label: label, usedPercent: max(0, min(100, used)), resetsAt: isoDate(reset), minutes: minutes))
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
    public static func age(_ date: Date?, now: Date) -> String {
        guard let date else { return "Waiting for a reading" }
        let minutes = max(0, Int(now.timeIntervalSince(date) / 60))
        if minutes < 1 { return "Updated just now" }
        if minutes < 60 { return "Updated \(minutes)m ago" }
        if minutes < 1440 { return "Updated \(minutes / 60)h ago" }
        return "Updated \(minutes / 1440)d ago"
    }
}
