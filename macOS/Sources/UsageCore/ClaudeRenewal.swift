import Foundation
import Darwin

/// Renews Claude Code's sign-in the way Claude Code itself does, so readings continue after its 8-hour
/// access token lapses while Claude Code is closed. The renewed tokens go back into Claude Code's own item.
public enum ClaudeRenewal {
    public static let tokenURL = URL(string: "https://platform.claude.com/v1/oauth/token")!
    /// Claude Code's public OAuth client, used when the stored sign-in names none.
    public static let defaultClientID = "9d1c250a-e61b-44d9-88ed-5944d1962f5e"

    /// The refresh request body for a stored credentials item, or nil when it can't be renewed without a new login.
    public static func request(stored: Data, now: Date) -> Data? {
        guard let oauth = oauth(stored), let refresh = oauth["refreshToken"] as? String, !refresh.isEmpty,
              let scopes = oauth["scopes"] as? [String], !scopes.isEmpty else { return nil }
        if let ends = oauth["refreshTokenExpiresAt"] as? Double, ends > 0, Date(timeIntervalSince1970: ends / 1000) <= now { return nil }
        let body: [String: Any] = ["grant_type": "refresh_token", "refresh_token": refresh,
                                   "client_id": oauth["clientId"] as? String ?? defaultClientID, "scope": scopes.joined(separator: " ")]
        return try? JSONSerialization.data(withJSONObject: body)
    }

    /// The stored item with the renewed tokens; every other field is preserved. Nil for an unusable response.
    public static func merge(stored: Data, response: Data, now: Date) -> Data? {
        guard var root = (try? JSONSerialization.jsonObject(with: stored)) as? [String: Any],
              var oauth = root["claudeAiOauth"] as? [String: Any],
              let reply = (try? JSONSerialization.jsonObject(with: response)) as? [String: Any],
              let access = reply["access_token"] as? String, !access.isEmpty,
              let lifetime = reply["expires_in"] as? Double, lifetime.isFinite, lifetime > 0 else { return nil }
        let millis = (now.timeIntervalSince1970 * 1000).rounded()
        oauth["accessToken"] = access
        // The server rotates the refresh token; the old one stops working once this is saved.
        if let refresh = reply["refresh_token"] as? String, !refresh.isEmpty { oauth["refreshToken"] = refresh }
        oauth["expiresAt"] = Int64(millis + lifetime * 1000)
        if let scope = reply["scope"] as? String {
            let scopes = scope.split(whereSeparator: \.isWhitespace).map(String.init)
            if !scopes.isEmpty { oauth["scopes"] = scopes }
        }
        if let ends = reply["refresh_token_expires_in"] as? Double, ends.isFinite, ends > 0 {
            oauth["refreshTokenExpiresAt"] = Int64(millis + ends * 1000)
        }
        root["claudeAiOauth"] = oauth
        return try? JSONSerialization.data(withJSONObject: root, options: [.withoutEscapingSlashes])
    }

    /// The refresh token in a stored item, used to detect a renewal by someone else before writing.
    public static func refreshToken(_ stored: Data) -> String? { oauth(stored)?["refreshToken"] as? String }

    private static func oauth(_ stored: Data) -> [String: Any]? {
        ((try? JSONSerialization.jsonObject(with: stored)) as? [String: Any])?["claudeAiOauth"] as? [String: Any]
    }
}

/// Claude Code's cross-process refresh lock. Claude Code holds `.oauth_refresh.lock` in its config directory, plus a
/// legacy `<config dir>.lock`, as proper-lockfile directories; holding both keeps it from renewing at the same time,
/// which would spend the same refresh token twice and sign the user out.
public final class ClaudeRefreshLock {
    private let paths: [String]
    private let heartbeat: DispatchSourceTimer

    private init(paths: [String]) {
        self.paths = paths
        // proper-lockfile treats a lock untouched for 60 seconds as abandoned; keep it fresh like Claude Code does.
        heartbeat = DispatchSource.makeTimerSource(queue: .global(qos: .utility))
        heartbeat.schedule(deadline: .now() + 5, repeating: 5)
        heartbeat.setEventHandler { for path in paths { utimes(path, nil) } }
        heartbeat.resume()
    }

    public static func paths(claudeDirectory: URL) -> (current: String, legacy: String) {
        let directory = claudeDirectory.standardizedFileURL.path
        let resolved = claudeDirectory.resolvingSymlinksInPath().standardizedFileURL.path
        return (directory + "/.oauth_refresh.lock", resolved + ".lock")
    }

    /// Nil while Claude Code holds the lock, after a few short waits.
    public static func acquire(claudeDirectory: URL, attempts: Int = 5, staleAfter: TimeInterval = 60) async -> ClaudeRefreshLock? {
        let (current, legacy) = paths(claudeDirectory: claudeDirectory)
        for attempt in 0..<attempts {
            if attempt > 0 { try? await Task.sleep(nanoseconds: UInt64.random(in: 1_000_000_000...2_000_000_000)) }
            guard take(current, staleAfter: staleAfter) else { continue }
            switch takeLegacy(legacy, staleAfter: staleAfter) {
            case .taken: return ClaudeRefreshLock(paths: [legacy, current])
            case .unsupported: return ClaudeRefreshLock(paths: [current])
            case .busy: rmdir(current)
            }
        }
        return nil
    }

    public func release() {
        heartbeat.cancel()
        for path in paths { rmdir(path) }
    }

    private static func take(_ path: String, staleAfter: TimeInterval) -> Bool {
        if mkdir(path, 0o755) == 0 { return true }
        guard errno == EEXIST, abandoned(path, staleAfter: staleAfter) else { return false }
        rmdir(path)
        return mkdir(path, 0o755) == 0
    }

    private enum LegacyResult { case taken, busy, unsupported }
    /// Claude Code tolerates a legacy lock it can't create, but never one another process holds.
    private static func takeLegacy(_ path: String, staleAfter: TimeInterval) -> LegacyResult {
        if take(path, staleAfter: staleAfter) { return .taken }
        return FileManager.default.fileExists(atPath: path) ? .busy : .unsupported
    }

    private static func abandoned(_ path: String, staleAfter: TimeInterval) -> Bool {
        var info = stat()
        guard stat(path, &info) == 0 else { return false }
        let modified = Date(timeIntervalSince1970: TimeInterval(info.st_mtimespec.tv_sec))
        return Date().timeIntervalSince(modified) > staleAfter
    }
}

/// `/usr/bin/security` is on the ACL of Claude Code's item because Claude Code writes it with that tool,
/// so reading and writing through it never prompts and keeps the item's access unchanged.
public enum SecurityTool {
    public static func read(service: String) -> Data? {
        guard let data = run(["find-generic-password", "-s", service, "-w"]), data.count < 100_000 else { return nil }
        return data
    }

    /// The item's account name, so an update replaces it instead of adding a second item.
    public static func account(service: String) -> String? {
        guard let data = run(["find-generic-password", "-s", service]), let text = String(data: data, encoding: .utf8) else { return nil }
        for line in text.split(separator: "\n") where line.contains("\"acct\"<blob>=\"") {
            guard let start = line.range(of: "<blob>=\"")?.upperBound, line.hasSuffix("\"") else { continue }
            return String(line[start..<line.index(before: line.endIndex)])
        }
        return nil
    }

    /// When an item was last written (its `mdat` attribute), or nil when there is no such item.
    /// Reads attributes only, never the secret.
    public static func modified(service: String, account: String) -> String? {
        guard let data = run(["find-generic-password", "-s", service, "-a", account]),
              let text = String(data: data, encoding: .utf8) else { return nil }
        return text.split(separator: "\n").first { $0.contains("\"mdat\"<timedate>=") }
            .map { $0.trimmingCharacters(in: .whitespaces) } ?? ""
    }

    /// Writes as Claude Code does: hex payload on stdin, so the secret never appears in a process listing.
    public static func write(service: String, account: String, data: Data) -> Bool {
        guard !service.contains("\""), !account.contains("\"") else { return false }
        let hex = data.map { String(format: "%02x", $0) }.joined()
        let command = "add-generic-password -U -a \"\(account)\" -s \"\(service)\" -X \"\(hex)\"\n"
        // `security -i` truncates longer input lines; Claude Code falls back to arguments the same way.
        if command.utf8.count <= 4032 { return run(["-i"], input: Data(command.utf8)) != nil }
        return run(["add-generic-password", "-U", "-a", account, "-s", service, "-X", hex]) != nil
    }

    public static func delete(service: String) -> Bool { run(["delete-generic-password", "-s", service]) != nil }

    private static func run(_ arguments: [String], input: Data? = nil) -> Data? {
        let process = Process(), output = Pipe(), stdin = Pipe()
        process.executableURL = URL(fileURLWithPath: "/usr/bin/security")
        process.arguments = arguments
        process.standardInput = input == nil ? FileHandle.nullDevice : stdin
        process.standardOutput = output
        process.standardError = FileHandle.nullDevice
        guard (try? process.run()) != nil else { return nil }
        let timer = DispatchWorkItem { if process.isRunning { process.terminate() } }
        DispatchQueue.global(qos: .utility).asyncAfter(deadline: .now() + 10, execute: timer)
        if let input {
            try? stdin.fileHandleForWriting.write(contentsOf: input)
            try? stdin.fileHandleForWriting.close()
        }
        let data = output.fileHandleForReading.readDataToEndOfFile()
        process.waitUntilExit()
        timer.cancel()
        try? output.fileHandleForReading.close()
        guard process.terminationReason == .exit, process.terminationStatus == 0 else { return nil }
        return data
    }
}
