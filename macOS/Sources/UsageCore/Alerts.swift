import Foundation

public struct UsageAlert: Codable, Equatable, Identifiable {
    public enum Kind: String, Codable { case low, reset }
    public let id: String
    public let provider: ProviderID
    public let kind: Kind
    public let label: String
    public let remaining: Double
    public let createdAt: Date
    public let expiresAt: Date
    /// The reading's source ID; absent in alerts saved before multiple accounts.
    public var source: String? = nil
    /// The account's display name, such as its email, when a provider has several.
    public var account: String? = nil
    public var sourceID: String { source ?? provider.rawValue }
}

/// Only fresh provider observations can generate events. Wall-clock expiry alone is never a reset.
public struct AlertLedger: Codable {
    struct Observation: Codable {
        let window: UsageWindow
        let date: Date
        let unknownCycle: String
    }
    private var observations: [String: Observation] = [:]
    private var delivered: [String] = []
    public private(set) var pending: [UsageAlert] = []
    public init() {}

    public mutating func observe(_ snapshot: UsageSnapshot, provider: ProviderID, lowEnabled: Bool, resetEnabled: Bool, now: Date) {
        observe(snapshot, source: UsageSource(provider: provider), account: nil, lowEnabled: lowEnabled, resetEnabled: resetEnabled, now: now)
    }

    public mutating func observe(_ snapshot: UsageSnapshot, source: UsageSource, account: String?, lowEnabled: Bool, resetEnabled: Bool, now: Date) {
        let provider = source.provider
        pending.removeAll { $0.expiresAt <= now || ($0.kind == .low ? !lowEnabled : !resetEnabled) }
        // Cached or clock-skewed readings must not be treated as fresh network observations.
        guard abs(snapshot.updatedAt.timeIntervalSince(now)) < 120 else { return }
        for window in snapshot.windows {
            let key = source.id + ":" + window.id
            let previous = observations[key]
            guard previous == nil || snapshot.updatedAt > previous!.date else { continue }
            let reset = window.resetsAt.map { String(Int($0.timeIntervalSince1970)) }
            let recovered = previous.map { $0.window.remaining < 10 && window.remaining >= 10 } ?? false
            var didReset = false
            if let old = previous?.window, let oldReset = old.resetsAt {
                // Providers can shift the same boundary by a second between otherwise identical readings.
                let boundaryTolerance = min(60, Double(old.minutes ?? 300) * 6)
                if oldReset <= now {
                    // An advanced boundary confirms rollover even if the new quota is already partly used.
                    didReset = (window.resetsAt.map { $0.timeIntervalSince(oldReset) > boundaryTolerance && $0 > now }
                        ?? (window.usedPercent == 0 && old.usedPercent > 0))
                } else if let nextReset = window.resetsAt {
                    // An early, provider-confirmed reset (e.g. a redeemed reset credit).
                    didReset = nextReset.timeIntervalSince(oldReset) > boundaryTolerance && window.usedPercent == 0 && old.usedPercent > 0
                }
                if didReset && resetEnabled {
                    enqueue(UsageAlert(id: "\(key):reset:\(Int(oldReset.timeIntervalSince1970))", provider: provider, kind: .reset,
                                       label: window.label, remaining: window.remaining, createdAt: now, expiresAt: now.addingTimeInterval(1800),
                                       source: source.id, account: account))
                }
            }
            // Persist a stable cycle identity instead of keying low alerts to a drifting raw timestamp.
            var cycle = previous?.unknownCycle ?? reset ?? UUID().uuidString
            if didReset { cycle = reset ?? UUID().uuidString }
            else if recovered && window.resetsAt == nil && previous?.window.resetsAt == nil { cycle = UUID().uuidString }
            // Drop obsolete low alerts after recovery or a rollover before permission/delivery succeeds.
            pending.removeAll { $0.kind == .low && $0.id.hasPrefix(key + ":low:") && (recovered || didReset) }
            if lowEnabled && window.minutes == 300 && window.remaining < 10 && !window.expired(at: now) {
                enqueue(UsageAlert(id: "\(key):low:\(cycle)", provider: provider, kind: .low,
                                   label: window.label, remaining: window.remaining, createdAt: now,
                                   expiresAt: min(window.resetsAt ?? now.addingTimeInterval(1800), now.addingTimeInterval(1800)),
                                   source: source.id, account: account))
            }
            observations[key] = Observation(window: window, date: snapshot.updatedAt, unknownCycle: cycle)
        }
        // Retain absent windows through temporary partial responses, but bound long-term state.
        observations = observations.filter { now.timeIntervalSince($0.value.date) < 45 * 86400 }
    }

    private mutating func enqueue(_ event: UsageAlert) {
        guard !delivered.contains(event.id) else { return }
        if let index = pending.firstIndex(where: { $0.id == event.id }) {
            if event.kind == .low { pending[index] = event }
            return
        }
        pending.append(event)
    }
    public mutating func markDelivered(_ ids: [String]) {
        delivered.append(contentsOf: ids)
        delivered = Array(delivered.suffix(256))
        pending.removeAll { ids.contains($0.id) }
    }
    public func deliverable(now: Date, lowEnabled: Bool, resetEnabled: Bool) -> [UsageAlert] {
        pending.filter { $0.expiresAt > now && ($0.kind == .low ? lowEnabled : resetEnabled) }
    }
}
