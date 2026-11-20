import Foundation

/// Reading Antigravity's quota through its own CLI: `antigravity -p /usage --output-format json` runs no model turn,
/// and the CLI owns the Google sign-in (kept in the login Keychain) and renews it itself.
public enum AntigravityCLI {
    /// The Keychain item holding the CLI's sign-in. Only its attributes are read, to tell whether one exists.
    public static let keychainService = "gemini", keychainAccount = "antigravity"

    public struct Executable: Equatable {
        public let url: URL
        /// The copy T3 Code installs and updates for its Antigravity sessions.
        public let managedByT3: Bool
    }

    public static let arguments = ["-p", "/usage", "--output-format", "json", "--print-timeout", "25s", "--log-file", "/dev/null"]

    /// A minimal environment. PATH points at an empty folder so the CLI cannot open a browser to sign in
    /// (it looks `open` up on PATH): a lapsed sign-in must never start a login from a background refresh.
    public static func environment(_ inherited: [String: String]) -> [String: String] {
        var result = ["PATH": "/var/empty", "AGY_CLI_DISABLE_AUTO_UPDATE": "1"]
        for key in ["HOME", "USER", "LOGNAME", "TMPDIR", "LANG"] { if let value = inherited[key] { result[key] = value } }
        return result
    }

    /// The CLI on PATH or in a common install folder, then the copy T3 Code manages. App-bundle launchers,
    /// such as the Antigravity editor's `antigravity` command, and scripts are skipped: only a native CLI binary qualifies.
    public static func executable(home: URL = FileManager.default.homeDirectoryForCurrentUser,
                                  path: String? = ProcessInfo.processInfo.environment["PATH"]) -> Executable? {
        let folders = (path ?? "").split(separator: ":").map(String.init)
            + ["/opt/homebrew/bin", "/usr/local/bin", home.appendingPathComponent(".local/bin").path]
        for folder in folders where !folder.isEmpty {
            for name in ["antigravity", "agy"] {
                let url = URL(fileURLWithPath: folder).appendingPathComponent(name).resolvingSymlinksInPath()
                if nativeBinary(url) { return Executable(url: url, managedByT3: false) }
            }
        }
        #if arch(arm64)
        let platform = "darwin-arm64"
        #else
        let platform = "darwin-x64"
        #endif
        let tools = home.appendingPathComponent(".t3/tools/antigravity-acp").appendingPathComponent(platform)
        guard let active = try? Data(contentsOf: tools.appendingPathComponent("active.json")),
              let release = ((try? JSONSerialization.jsonObject(with: active)) as? [String: Any])?["releaseId"] as? String,
              !release.isEmpty, !release.contains("/"), !release.hasPrefix(".") else { return nil }
        let version = tools.appendingPathComponent("versions").appendingPathComponent(release)
        // Only a finished install: T3 writes this record last.
        guard let record = try? Data(contentsOf: version.appendingPathComponent(".install-complete.json")),
              let installed = (try? JSONSerialization.jsonObject(with: record)) as? [String: Any] else { return nil }
        let name = ((installed["executable"] as? [String: Any])?["name"] as? String)
            .flatMap { $0.isEmpty || $0.contains("/") ? nil : $0 } ?? "antigravity"
        let url = version.appendingPathComponent(name)
        return nativeBinary(url) ? Executable(url: url, managedByT3: true) : nil
    }

    /// An executable Mach-O file outside an app bundle.
    static func nativeBinary(_ url: URL) -> Bool {
        guard !url.path.contains(".app/"), FileManager.default.isExecutableFile(atPath: url.path),
              let handle = try? FileHandle(forReadingFrom: url) else { return false }
        defer { try? handle.close() }
        guard let magic = try? handle.read(upToCount: 4), magic.count == 4 else { return false }
        let known: [[UInt8]] = [[0xcf, 0xfa, 0xed, 0xfe], [0xce, 0xfa, 0xed, 0xfe], [0xca, 0xfe, 0xba, 0xbe], [0xca, 0xfe, 0xba, 0xbf]]
        return known.contains(Array(magic))
    }

    /// The CLI's prompt to sign in on stderr. It then waits for a browser login that will never come.
    public static func asksForLogin(_ stderr: Data) -> Bool {
        let text = String(decoding: stderr.suffix(16_384), as: UTF8.self).lowercased()
        return text.contains("authentication required") || text.contains("visit the url to log in")
            || text.contains("not logged into antigravity")
    }

    public enum Outcome: Equatable {
        case reading(UsageSnapshot)
        /// The CLI asked for a new login on stderr: the sign-in is missing or can't be renewed.
        case signIn
        case failure(String)
    }

    public static func outcome(stdout: Data, now: Date = Date()) -> Outcome {
        guard let root = (try? JSONSerialization.jsonObject(with: stdout)) as? [String: Any] else {
            return .failure("Antigravity returned an invalid response")
        }
        if root["status"] as? String == "SUCCESS" {
            guard let snapshot = try? UsageParser.antigravity(stdout, now: now), !snapshot.windows.isEmpty else {
                return .failure("No Antigravity quota returned for this account")
            }
            return .reading(snapshot)
        }
        // "authentication failed or timed out" also follows a slow or offline renewal, so it is retried like any failure;
        // only the CLI's own login prompt (`asksForLogin`) means a new sign-in is needed.
        let error = (root["error"] as? String ?? "").lowercased()
        if error.contains("auth") || error.contains("timed out") { return .failure("Antigravity didn't answer · retrying automatically") }
        return .failure("Antigravity refresh failed · showing last reading")
    }
}
