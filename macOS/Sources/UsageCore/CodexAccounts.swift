import Foundation

/// One tracked reading: a provider and, for additional sign-ins, which account.
public struct UsageSource: Hashable, Codable, Identifiable {
    public let provider: ProviderID
    /// nil for the provider's default sign-in, otherwise a stable account key.
    public let account: String?
    public init(provider: ProviderID, account: String? = nil) { self.provider = provider; self.account = account }
    /// Default sign-ins keep the bare provider name, so earlier cached readings and alert history still match.
    public var id: String { account.map { provider.rawValue + "@" + $0 } ?? provider.rawValue }
    public static let claude = UsageSource(provider: .claude)
    public static let codex = UsageSource(provider: .codex)
}

/// A ChatGPT sign-in that Codex can read subscription limits for.
public struct CodexAccount: Equatable, Identifiable {
    public enum Origin: Equatable {
        /// Codex's own default sign-in (`CODEX_HOME` or `~/.codex`).
        case main
        /// An account slot in Codex Subscription Router.
        case router
        /// A Codex home folder added in the widget's settings.
        case folder
    }
    /// nil for the main sign-in, otherwise the ChatGPT account ID (or the folder path when it is unknown).
    public let key: String?
    /// The Codex home to read; nil uses Codex's default.
    public let home: String?
    public let email: String?
    public let plan: String?
    public let accountID: String?
    public let origin: Origin
    /// When the current paid ChatGPT period ends: the plan renews then, unless it was cancelled.
    public var paidUntil: Date? = nil
    public var source: UsageSource { UsageSource(provider: .codex, account: key) }
    public var id: String { source.id }
    public var signedIn: Bool { email != nil || accountID != nil }
    /// New additional sign-ins are tracked unless they're on a free plan or signed out.
    public var trackedByDefault: Bool {
        origin == .main || origin == .folder || (signedIn && (plan ?? "").lowercased() != "free")
    }
    public var name: String {
        if let email { return email }
        if let home { return (home as NSString).abbreviatingWithTildeInPath }
        return "Codex sign-in"
    }

    public init(key: String?, home: String?, email: String?, plan: String?, accountID: String?, origin: Origin, paidUntil: Date? = nil) {
        self.key = key; self.home = home; self.email = email; self.plan = plan; self.accountID = accountID; self.origin = origin
        self.paidUntil = paidUntil
    }
}

public enum CodexAccounts {
    public struct Identity: Equatable {
        public let email: String?
        public let plan: String?
        public let accountID: String?
        /// End of the current paid period, as ChatGPT last reported it in the sign-in.
        public var paidUntil: Date? = nil
    }

    /// Reads only the identity claims of a sign-in. Tokens are never returned or kept.
    public static func identity(home: URL) -> Identity? {
        let file = home.appendingPathComponent("auth.json")
        guard let size = (try? FileManager.default.attributesOfItem(atPath: file.path))?[.size] as? NSNumber,
              size.intValue < 256_000, let data = try? Data(contentsOf: file) else { return nil }
        return identity(authData: data)
    }

    public static func identity(authData: Data) -> Identity? {
        guard let root = (try? JSONSerialization.jsonObject(with: authData)) as? [String: Any],
              let tokens = root["tokens"] as? [String: Any] else { return nil }
        var claims: [String: Any] = [:]
        if let token = tokens["id_token"] as? String {
            let parts = token.split(separator: ".")
            if parts.count >= 2 {
                var payload = String(parts[1]).replacingOccurrences(of: "-", with: "+").replacingOccurrences(of: "_", with: "/")
                payload += String(repeating: "=", count: (4 - payload.count % 4) % 4)
                if let data = Data(base64Encoded: payload),
                   let decoded = (try? JSONSerialization.jsonObject(with: data)) as? [String: Any] { claims = decoded }
            }
        }
        let auth = claims["https://api.openai.com/auth"] as? [String: Any] ?? [:]
        func text(_ value: Any?) -> String? { (value as? String).flatMap { $0.isEmpty ? nil : $0 } }
        let identity = Identity(email: text(claims["email"]), plan: text(auth["chatgpt_plan_type"]),
                                accountID: text(auth["chatgpt_account_id"]) ?? text(tokens["account_id"]),
                                paidUntil: date(auth["chatgpt_subscription_active_until"]))
        return identity.email == nil && identity.accountID == nil ? nil : identity
    }

    /// ChatGPT writes ISO 8601 times, sometimes with microseconds.
    static func date(_ value: Any?) -> Date? {
        guard let text = value as? String, !text.isEmpty else { return nil }
        let format = ISO8601DateFormatter()
        format.formatOptions = [.withInternetDateTime, .withFractionalSeconds]
        if let date = format.date(from: text) { return date }
        format.formatOptions = [.withInternetDateTime]
        return format.date(from: text)
    }

    /// Codex homes listed in Codex Subscription Router's account state.
    public static func routerHomes(stateData: Data) -> [String] {
        guard let root = (try? JSONSerialization.jsonObject(with: stateData)) as? [String: Any],
              let accounts = root["accounts"] as? [[String: Any]] else { return [] }
        return accounts.compactMap { ($0["codexHome"] as? String).flatMap { $0.isEmpty ? nil : $0 } }
    }

    public static var defaultMainHome: URL {
        let configured = ProcessInfo.processInfo.environment["CODEX_HOME"].flatMap { $0.isEmpty ? nil : $0 }
        return configured.map { URL(fileURLWithPath: $0) }
            ?? FileManager.default.homeDirectoryForCurrentUser.appendingPathComponent(".codex")
    }
    public static var defaultRouterState: URL {
        FileManager.default.homeDirectoryForCurrentUser.appendingPathComponent(".codex-mux/state.json")
    }

    /// The main sign-in, then added folders, then signed-in router accounts, each ChatGPT account listed once.
    public static func discover(mainHome: URL = defaultMainHome, routerState: URL = defaultRouterState,
                                folders: [String]) -> [CodexAccount] {
        let main = identity(home: mainHome)
        var accounts = [CodexAccount(key: nil, home: nil, email: main?.email, plan: main?.plan, accountID: main?.accountID, origin: .main,
                                    paidUntil: main?.paidUntil)]
        var seenHomes: Set<String> = [mainHome.standardizedFileURL.path]
        func add(_ path: String, origin: CodexAccount.Origin) {
            let home = URL(fileURLWithPath: path).standardizedFileURL
            guard seenHomes.insert(home.path).inserted else { return }
            let found = identity(home: home)
            // Router slots without a sign-in are placeholders; an added folder stays listed so it can be removed.
            guard found != nil || origin == .folder else { return }
            if let id = found?.accountID, accounts.contains(where: { $0.accountID == id }) { return }
            accounts.append(CodexAccount(key: found?.accountID ?? home.path, home: home.path, email: found?.email,
                                         plan: found?.plan, accountID: found?.accountID, origin: origin,
                                         paidUntil: found?.paidUntil))
        }
        for folder in folders { add(folder, origin: .folder) }
        if let state = try? Data(contentsOf: routerState) {
            for home in routerHomes(stateData: state) { add(home, origin: .router) }
        }
        return accounts
    }

    /// Accounts to read, given the user's explicit choices; the main sign-in is always tracked.
    public static func tracked(_ accounts: [CodexAccount], on: Set<String>, off: Set<String>) -> [CodexAccount] {
        accounts.filter { account in
            guard let key = account.key else { return true }
            if on.contains(key) { return true }
            if off.contains(key) { return false }
            return account.trackedByDefault
        }
    }
}
