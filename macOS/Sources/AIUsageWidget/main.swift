import AppKit
import UsageCore

let arguments = CommandLine.arguments
if arguments.contains("--check") {
    signal(SIGPIPE, SIG_IGN)
    Task {
        // Checks Claude, every tracked ChatGPT/Codex sign-in (using the widget's saved account choices), and Antigravity.
        let defaults = UserDefaults.standard
        let accounts = CodexAccounts.tracked(CodexAccounts.discover(folders: defaults.stringArray(forKey: "codexFolders") ?? []),
                                             on: Set(defaults.stringArray(forKey: "codexAccountsOn") ?? []),
                                             off: Set(defaults.stringArray(forKey: "codexAccountsOff") ?? []))
        let results = await withTaskGroup(of: (String, String?, ProviderResult).self) { group in
            group.addTask { ("claude", nil, await ClaudeProvider().refresh()) }
            // Antigravity is checked when its CLI is installed and the widget shows it.
            if defaults.object(forKey: "showAntigravity") as? Bool ?? true, AntigravityCLI.executable() != nil {
                let workspace = UsageModel.dataDirectory.appendingPathComponent("antigravity-workspace")
                group.addTask { ("antigravity", nil, await AntigravityProvider(workspace: workspace).refresh()) }
            }
            for account in accounts {
                group.addTask { (account.source.id, account.email, await CodexProvider(home: account.home).refresh()) }
            }
            return await group.reduce(into: []) { $0.append($1) }
        }
        var report: [String: Any] = [:]
        var ok = true
        for (id, account, result) in results {
            var entry: [String: Any]
            switch result {
            case .reading(let snapshot): entry = ["connected": true, "windowCount": snapshot.windows.count]
            case .failure(let status): entry = ["connected": false, "status": status]; ok = false
            case .skipped: entry = ["connected": false, "status": "Skipped"]; ok = false
            }
            if let account { entry["account"] = account }
            report[id] = entry
        }
        if let data = try? JSONSerialization.data(withJSONObject: report, options: [.prettyPrinted, .sortedKeys]) {
            FileHandle.standardOutput.write(data); FileHandle.standardOutput.write(Data([10]))
        }
        exit(ok ? 0 : 1)
    }
    RunLoop.main.run()
} else if let index = arguments.firstIndex(of: "--render-demo") {
    let folder = URL(fileURLWithPath: arguments.dropFirst(index + 1).first ?? ".", isDirectory: true)
    MainActor.assumeIsolated {
        _ = NSApplication.shared
        NSApp.setActivationPolicy(.prohibited)
        do { try DemoRender.run(folder: folder); exit(0) } catch {
            FileHandle.standardError.write(Data("render-demo: \(error)\n".utf8)); exit(1)
        }
    }
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
