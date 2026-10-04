import Foundation
import Security
import LocalAuthentication
import Darwin
import UsageCore

enum ProviderResult {
    case reading(UsageSnapshot)
    case failure(String)
    case skipped
}

enum ProviderError: Error { case unavailable, timedOut, invalidResponse, authentication, disconnected }

actor CodexProvider {
    private var lastAttempt = Date.distantPast
    func refresh(minimumGap: TimeInterval = 150) async -> ProviderResult {
        guard Date().timeIntervalSince(lastAttempt) >= minimumGap else { return .skipped }
        lastAttempt = Date()
        return await Task.detached(priority: .utility) {
            do {
                let snapshot = try UsageParser.codex(CodexReader.read())
                return snapshot.windows.isEmpty ? .failure("No subscription windows returned") : .reading(snapshot)
            } catch ProviderError.unavailable { return .failure("Install and sign in to Codex CLI") }
            catch ProviderError.authentication { return .failure("Sign in to Codex CLI to connect") }
            catch ProviderError.timedOut { return .failure("Refresh timed out · retrying automatically") }
            catch { return .failure("Refresh failed · showing last reading") }
        }.value
    }
}

/// The native CLI exists only for a refresh. No Node runtime, shell, model turn, or persistent server.
enum CodexReader {
    static func executable() -> URL? {
        let home = FileManager.default.homeDirectoryForCurrentUser.path
        let paths = (ProcessInfo.processInfo.environment["PATH"] ?? "").split(separator: ":").map(String.init)
            + ["/opt/homebrew/bin", "/usr/local/bin", home + "/.local/bin"]
        #if arch(arm64)
        let platform = "darwin-arm64", triple = "aarch64-apple-darwin"
        #else
        let platform = "darwin-x64", triple = "x86_64-apple-darwin"
        #endif
        for path in paths {
            let command = URL(fileURLWithPath: path).appendingPathComponent("codex").resolvingSymlinksInPath()
            guard FileManager.default.isExecutableFile(atPath: command.path) else { continue }
            if command.pathExtension == "js" {
                let root = command.deletingLastPathComponent().deletingLastPathComponent()
                for suffix in ["node_modules/@openai/codex-\(platform)/vendor/\(triple)/bin/codex", "vendor/\(triple)/bin/codex", "vendor/\(triple)/codex/codex"] {
                    let native = root.appendingPathComponent(suffix)
                    if FileManager.default.isExecutableFile(atPath: native.path) { return native }
                }
            } else { return command }
        }
        return nil
    }

    static func read() throws -> Data {
        guard let executable = executable() else { throw ProviderError.unavailable }
        let process = Process(), input = Pipe(), output = Pipe()
        process.executableURL = executable
        process.arguments = ["app-server", "--listen", "stdio://"]
        process.currentDirectoryURL = FileManager.default.homeDirectoryForCurrentUser
        process.standardInput = input
        process.standardOutput = output
        // Never persist the server's diagnostics, account data, or authentication material.
        process.standardError = FileHandle.nullDevice
        try process.run()
        defer {
            try? input.fileHandleForWriting.close()
            if process.isRunning {
                process.terminate()
                let end = Date().addingTimeInterval(1)
                while process.isRunning && Date() < end { usleep(10_000) }
                if process.isRunning { kill(process.processIdentifier, SIGKILL) }
            }
            process.waitUntilExit()
            try? output.fileHandleForReading.close()
        }
        let deadline = Date().addingTimeInterval(30)
        var buffer = Data()
        func send(_ message: [String: Any]) throws {
            var data = try JSONSerialization.data(withJSONObject: message)
            data.append(10)
            try input.fileHandleForWriting.write(contentsOf: data)
        }
        func response(_ id: Int) throws -> Data {
            var total = 0
            while Date() < deadline {
                while let newline = buffer.firstIndex(of: 10) {
                    let line = Data(buffer[..<newline]); buffer.removeSubrange(...newline)
                    guard let message = (try? JSONSerialization.jsonObject(with: line)) as? [String: Any], message["id"] as? Int == id else { continue }
                    if let error = message["error"] as? [String: Any] {
                        let text = (error["message"] as? String ?? "").lowercased()
                        if text.contains("auth") || text.contains("sign in") || text.contains("logged in") { throw ProviderError.authentication }
                        throw ProviderError.invalidResponse
                    }
                    guard let result = message["result"] else { throw ProviderError.invalidResponse }
                    return try JSONSerialization.data(withJSONObject: result)
                }
                var descriptor = pollfd(fd: output.fileHandleForReading.fileDescriptor, events: Int16(POLLIN), revents: 0)
                let ready = poll(&descriptor, 1, 500)
                if ready < 0 { if errno == EINTR { continue }; throw ProviderError.disconnected }
                if ready == 0 { continue }
                var bytes = [UInt8](repeating: 0, count: 8192)
                let count = Darwin.read(descriptor.fd, &bytes, bytes.count)
                guard count > 0 else { throw ProviderError.disconnected }
                total += count
                guard total <= 2_000_000 else { throw ProviderError.invalidResponse }
                buffer.append(contentsOf: bytes.prefix(count))
            }
            throw ProviderError.timedOut
        }
        try send(["id": 1, "method": "initialize", "params": ["clientInfo": ["name": "ai_usage_widget", "title": "AI Usage Widget", "version": "1.2.0"]]])
        _ = try response(1)
        try send(["method": "initialized", "params": [:]])
        try send(["id": 2, "method": "account/rateLimits/read", "params": [:]])
        return try response(2)
    }
}

actor ClaudeProvider {
    private var lastAttempt = Date.distantPast
    private var backoffUntil = Date.distantPast
    private let session: URLSession = {
        let config = URLSessionConfiguration.ephemeral
        config.timeoutIntervalForRequest = 20
        config.timeoutIntervalForResource = 25
        config.httpCookieStorage = nil
        config.urlCache = nil
        return URLSession(configuration: config)
    }()

    /// Kept in memory only, until it nears expiry or Claude rejects it, so refreshes skip the Keychain read.
    private var credential: ClaudeCredentials.Credential?
    private(set) var keychainReads = 0
    private(set) var renewals = 0
    /// The expired token whose renewal Claude refused; a new login replaces it.
    private var renewalRefused: String?
    private var renewalRetryAfter = Date.distantPast
    private static let expired = "Claude sign-in expired · open Claude Code to renew"

    func refresh(minimumGap: TimeInterval = 150, allowKeychainUI: Bool = false, allowRenewal: Bool = false) async -> ProviderResult {
        let now = Date()
        guard now >= backoffUntil, now.timeIntervalSince(lastAttempt) >= minimumGap else { return .skipped }
        var fromKeychain = false
        if allowKeychainUI || !(credential?.valid(at: now.addingTimeInterval(60)) ?? false) {
            if let failure = await readCredential(allowUI: allowKeychainUI) { return failure }
            fromKeychain = true
        }
        // Claude Code renews ahead of expiry while it runs, so an expired item means nothing else is renewing it.
        if allowRenewal, let lapsed = credential, lapsed.renewable, !lapsed.valid(at: now), let failure = await renew(lapsed) { return failure }
        guard let current = credential, current.valid(at: now) else { return .failure(Self.expired) }
        // A denied Keychain read isn't an HTTP attempt: Connect must work immediately afterwards.
        lastAttempt = Date()
        if let result = await fetch(current) { return result }
        // Rejected: Claude Code may have renewed the sign-in since it was cached.
        if !fromKeychain, await readCredential(allowUI: false) == nil, let renewed = credential, renewed.token != current.token,
           renewed.valid(at: Date()), let result = await fetch(renewed) { return result }
        credential = nil
        return .failure(Self.expired)
    }

    /// Replaces the cached credential; returns a failure to report, or nil on success.
    private func readCredential(allowUI: Bool) async -> ProviderResult? {
        keychainReads += 1
        switch await Task.detached(priority: .utility, operation: { ClaudeCredentials.read(allowUI: allowUI) }).value {
        case .failure(let message): credential = nil; return .failure(message)
        case .success(let read): credential = read; return nil
        }
    }

    /// Replaces an expired credential with a renewed one; returns a failure to report, or nil to continue.
    private func renew(_ lapsed: ClaudeCredentials.Credential) async -> ProviderResult? {
        let ended = ProviderResult.failure("Claude sign-in ended · sign in to Claude Code again")
        if renewalRefused == lapsed.token { return ended }
        guard Date() >= renewalRetryAfter else { return nil }
        switch await ClaudeCredentials.renew(lapsedToken: lapsed.token, session: session) {
        case .renewed(let fresh): credential = fresh; renewals += 1; return nil
        case .refused: renewalRefused = lapsed.token; return ended
        case .unavailable: renewalRetryAfter = Date().addingTimeInterval(300); return nil
        }
    }

    /// Returns nil when Claude rejects the token.
    private func fetch(_ credential: ClaudeCredentials.Credential) async -> ProviderResult? {
        var request = URLRequest(url: URL(string: "https://api.anthropic.com/api/oauth/usage")!)
        request.setValue("Bearer " + credential.token, forHTTPHeaderField: "Authorization")
        request.setValue("oauth-2025-04-20", forHTTPHeaderField: "anthropic-beta")
        request.setValue("AIUsageWidget/1.2.0", forHTTPHeaderField: "User-Agent")
        do {
            let (data, response) = try await session.data(for: request)
            guard let http = response as? HTTPURLResponse else { return .failure("Claude returned an invalid response") }
            switch http.statusCode {
            case 401, 403: return nil
            case 429:
                let seconds = Self.retryDelay(http.value(forHTTPHeaderField: "Retry-After"), now: Date())
                backoffUntil = Date().addingTimeInterval(seconds)
                return .failure("Claude usage busy · retrying later")
            case 200: break
            default: return .failure("Claude unavailable · retrying automatically")
            }
            guard data.count < 1_000_000 else { return .failure("Claude returned an invalid response") }
            let snapshot = try UsageParser.claude(data, plan: credential.plan)
            return snapshot.windows.isEmpty ? .failure("No Claude subscription windows returned") : .reading(snapshot)
        } catch { return .failure("Claude refresh failed · showing last reading") }
    }

    private static func retryDelay(_ value: String?, now: Date) -> TimeInterval {
        if let value, let seconds = Double(value), seconds.isFinite { return max(60, seconds) }
        let format = DateFormatter()
        format.locale = Locale(identifier: "en_US_POSIX")
        format.dateFormat = "EEE, dd MMM yyyy HH:mm:ss zzz"
        if let value, let date = format.date(from: value) { return max(60, date.timeIntervalSince(now)) }
        return 600
    }
}

private enum ClaudeCredentials {
    static let service = "Claude Code-credentials"
    struct Credential {
        let token: String; let plan: String?; let expiresAt: Date?
        /// Read from Claude Code's default Keychain item, which the widget may renew.
        var renewable = false
        func valid(at date: Date) -> Bool { expiresAt.map { $0 > date } ?? true }
    }
    enum ReadResult { case success(Credential), failure(String) }
    enum RenewResult { case renewed(Credential), refused, unavailable }

    static func read(allowUI: Bool) -> ReadResult {
        let configured = ProcessInfo.processInfo.environment["CLAUDE_CONFIG_DIR"].flatMap { $0.isEmpty ? nil : $0 }
        var data: Data?
        // A custom config directory has a distinct Keychain identity. The file fallback is supported here.
        // Claude Code writes the item with /usr/bin/security, so that tool is on its ACL and reads silently.
        // Claude Code rewrites the item on token renewal, so an "Always Allow" granted to this app doesn't last.
        if configured == nil { data = SecurityTool.read(service: service) }
        if configured == nil, data == nil {
            // Claude uses the legacy login Keychain. LAContext alone does not suppress its ACL
            // prompts, so bracket this one serialized read with the legacy interaction control.
            var previousInteraction = DarwinBoolean(true)
            SecKeychainGetUserInteractionAllowed(&previousInteraction)
            SecKeychainSetUserInteractionAllowed(allowUI)
            defer { SecKeychainSetUserInteractionAllowed(previousInteraction.boolValue) }
            let context = LAContext()
            context.interactionNotAllowed = !allowUI
            let query: [String: Any] = [kSecClass as String: kSecClassGenericPassword,
                kSecAttrService as String: service, kSecReturnData as String: true,
                kSecMatchLimit as String: kSecMatchLimitOne,
                kSecUseAuthenticationContext as String: context]
            var item: CFTypeRef?
            let status = SecItemCopyMatching(query as CFDictionary, &item)
            if status == errSecSuccess { data = item as? Data }
            else if status == errSecInteractionNotAllowed || status == errSecAuthFailed || status == errSecUserCanceled {
                return .failure("Connect Claude… in the menu to allow Keychain access")
            }
        }
        let fromKeychain = data != nil
        if data == nil {
            let directory = configured.map { URL(fileURLWithPath: $0) } ?? FileManager.default.homeDirectoryForCurrentUser.appendingPathComponent(".claude")
            data = try? Data(contentsOf: directory.appendingPathComponent(".credentials.json"))
        }
        guard let data, let credential = parse(data, renewable: fromKeychain) else {
            return .failure(configured == nil ? "Sign in to Claude Code to connect" : "Custom Claude config: credential file not available")
        }
        return .success(credential)
    }

    private static func parse(_ data: Data, renewable: Bool) -> Credential? {
        guard let root = (try? JSONSerialization.jsonObject(with: data)) as? [String: Any],
              let oauth = root["claudeAiOauth"] as? [String: Any], let token = oauth["accessToken"] as? String, !token.isEmpty else { return nil }
        let expiry = (oauth["expiresAt"] as? Double).flatMap { $0.isFinite && $0 > 0 ? Date(timeIntervalSince1970: $0 / 1000) : nil }
        return Credential(token: token, plan: oauth["subscriptionType"] as? String, expiresAt: expiry, renewable: renewable)
    }

    /// Renews the default Keychain item under Claude Code's refresh lock, re-checking it once the lock is held.
    static func renew(lapsedToken: String, session: URLSession) async -> RenewResult {
        guard ProcessInfo.processInfo.environment["CLAUDE_CONFIG_DIR"].map({ $0.isEmpty }) ?? true else { return .unavailable }
        let directory = FileManager.default.homeDirectoryForCurrentUser.appendingPathComponent(".claude")
        guard let lock = await ClaudeRefreshLock.acquire(claudeDirectory: directory) else { return .unavailable }
        defer { lock.release() }
        guard let stored = SecurityTool.read(service: service), let current = parse(stored, renewable: true) else { return .unavailable }
        // Claude Code renewed it, or a new login replaced it, while the widget waited.
        if current.token != lapsedToken { return current.valid(at: Date()) ? .renewed(current) : .unavailable }
        guard let body = ClaudeRenewal.request(stored: stored, now: Date()) else { return .refused }
        var request = URLRequest(url: ClaudeRenewal.tokenURL)
        request.httpMethod = "POST"
        request.httpBody = body
        request.setValue("application/json", forHTTPHeaderField: "Content-Type")
        request.setValue("AIUsageWidget/1.2.0", forHTTPHeaderField: "User-Agent")
        guard let (data, response) = try? await session.data(for: request), let http = response as? HTTPURLResponse else { return .unavailable }
        // A spent or revoked refresh token needs a new login; retrying it can't help.
        if [400, 401, 403].contains(http.statusCode) { return .refused }
        guard http.statusCode == 200, data.count < 100_000,
              let merged = ClaudeRenewal.merge(stored: stored, response: data, now: Date()),
              let fresh = parse(merged, renewable: true) else { return .unavailable }
        // The old refresh token is spent now; the widget keeps reading even if the item can't be updated.
        _ = save(merged, replacing: ClaudeRenewal.refreshToken(stored))
        return .renewed(fresh)
    }

    /// Writes renewed tokens unless something else saved newer ones meanwhile, confirmed by reading back.
    private static func save(_ merged: Data, replacing previous: String?) -> Bool {
        guard let account = SecurityTool.account(service: service) else { return false }
        let renewed = ClaudeRenewal.refreshToken(merged)
        for attempt in 0..<3 {
            if attempt > 0 { usleep(UInt32(100_000 * attempt)) }
            guard let stored = SecurityTool.read(service: service) else { continue }
            let latest = ClaudeRenewal.refreshToken(stored)
            if latest == renewed { return true }
            if latest != previous { return false }
            _ = SecurityTool.write(service: service, account: account, data: merged)
        }
        return SecurityTool.read(service: service).flatMap(ClaudeRenewal.refreshToken) == renewed
    }
}
