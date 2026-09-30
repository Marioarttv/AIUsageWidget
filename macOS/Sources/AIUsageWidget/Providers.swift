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
    func refresh(manual: Bool = false) async -> ProviderResult {
        guard Date().timeIntervalSince(lastAttempt) >= (manual ? 30 : 150) else { return .skipped }
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

    func refresh(manual: Bool = false, allowKeychainUI: Bool = false) async -> ProviderResult {
        let now = Date()
        guard now >= backoffUntil, now.timeIntervalSince(lastAttempt) >= (manual ? 30 : 150) else { return .skipped }
        let credentials = await Task.detached(priority: .utility) { ClaudeCredentials.read(allowUI: allowKeychainUI) }.value
        switch credentials {
        case .failure(let message): return .failure(message)
        case .success(let credential):
            guard credential.expiresAt.map({ $0 > now }) ?? true else { return .failure("Claude sign-in expired · open Claude Code to renew") }
            // A denied Keychain read isn't an HTTP attempt: Connect must work immediately afterwards.
            lastAttempt = Date()
            var request = URLRequest(url: URL(string: "https://api.anthropic.com/api/oauth/usage")!)
            request.setValue("Bearer " + credential.token, forHTTPHeaderField: "Authorization")
            request.setValue("oauth-2025-04-20", forHTTPHeaderField: "anthropic-beta")
            request.setValue("AIUsageWidget/1.2.0", forHTTPHeaderField: "User-Agent")
            do {
                let (data, response) = try await session.data(for: request)
                guard let http = response as? HTTPURLResponse else { return .failure("Claude returned an invalid response") }
                switch http.statusCode {
                case 401, 403: return .failure("Claude sign-in expired · open Claude Code to renew")
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
    struct Credential { let token: String; let plan: String?; let expiresAt: Date? }
    enum ReadResult { case success(Credential), failure(String) }
    static func read(allowUI: Bool) -> ReadResult {
        let configured = ProcessInfo.processInfo.environment["CLAUDE_CONFIG_DIR"].flatMap { $0.isEmpty ? nil : $0 }
        var data: Data?
        // A custom config directory has a distinct Keychain identity. The file fallback is supported here.
        if configured == nil {
            // Claude uses the legacy login Keychain. LAContext alone does not suppress its ACL
            // prompts, so bracket this one serialized read with the legacy interaction control.
            var previousInteraction = DarwinBoolean(true)
            SecKeychainGetUserInteractionAllowed(&previousInteraction)
            SecKeychainSetUserInteractionAllowed(allowUI)
            defer { SecKeychainSetUserInteractionAllowed(previousInteraction.boolValue) }
            let context = LAContext()
            context.interactionNotAllowed = !allowUI
            let query: [String: Any] = [kSecClass as String: kSecClassGenericPassword,
                kSecAttrService as String: "Claude Code-credentials", kSecReturnData as String: true,
                kSecMatchLimit as String: kSecMatchLimitOne,
                kSecUseAuthenticationContext as String: context]
            var item: CFTypeRef?
            let status = SecItemCopyMatching(query as CFDictionary, &item)
            if status == errSecSuccess { data = item as? Data }
            else if status == errSecInteractionNotAllowed || status == errSecAuthFailed || status == errSecUserCanceled {
                return .failure("Connect Claude… in the menu to allow Keychain access")
            }
        }
        if data == nil {
            let directory = configured.map { URL(fileURLWithPath: $0) } ?? FileManager.default.homeDirectoryForCurrentUser.appendingPathComponent(".claude")
            data = try? Data(contentsOf: directory.appendingPathComponent(".credentials.json"))
        }
        guard let data, let root = (try? JSONSerialization.jsonObject(with: data)) as? [String: Any],
              let oauth = root["claudeAiOauth"] as? [String: Any], let token = oauth["accessToken"] as? String, !token.isEmpty else {
            return .failure(configured == nil ? "Sign in to Claude Code to connect" : "Custom Claude config: credential file not available")
        }
        let expiry = (oauth["expiresAt"] as? Double).flatMap { $0.isFinite && $0 > 0 ? Date(timeIntervalSince1970: $0 / 1000) : nil }
        return .success(Credential(token: token, plan: oauth["subscriptionType"] as? String, expiresAt: expiry))
    }
}
