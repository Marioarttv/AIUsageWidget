import AppKit
import UsageCore

let arguments = CommandLine.arguments
if arguments.contains("--check") {
    signal(SIGPIPE, SIG_IGN)
    Task {
        async let claude = ClaudeProvider().refresh()
        async let codex = CodexProvider().refresh()
        var report: [String: Any] = [:]
        var ok = true
        for (id, result) in await [(ProviderID.claude, claude), (.codex, codex)] {
            switch result {
            case .reading(let snapshot): report[id.rawValue] = ["connected": true, "windowCount": snapshot.windows.count]
            case .failure(let status): report[id.rawValue] = ["connected": false, "status": status]; ok = false
            case .skipped: report[id.rawValue] = ["connected": false, "status": "Skipped"]; ok = false
            }
        }
        if let data = try? JSONSerialization.data(withJSONObject: report, options: [.prettyPrinted, .sortedKeys]) {
            FileHandle.standardOutput.write(data); FileHandle.standardOutput.write(Data([10]))
        }
        exit(ok ? 0 : 1)
    }
    RunLoop.main.run()
} else {
    let bundleID = Bundle.main.bundleIdentifier ?? "com.mariomanzocco.aiusagewidget"
    let other = NSRunningApplication.runningApplications(withBundleIdentifier: bundleID).first { $0.processIdentifier != getpid() }
    if arguments.contains("--diagnostics") || other != nil {
        DistributedNotificationCenter.default().postNotificationName(Notification.Name("com.mariomanzocco.aiusagewidget.command"),
            object: arguments.contains("--diagnostics") ? "diagnostics" : "show", userInfo: nil, deliverImmediately: true)
        exit(0)
    }
    MainActor.assumeIsolated {
        let app = NSApplication.shared
        let delegate = AppDelegate()
        app.delegate = delegate
        withExtendedLifetime(delegate) { app.run() }
    }
}
