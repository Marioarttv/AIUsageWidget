import AppKit
import SwiftUI
import Combine
import UsageCore

final class WidgetPanel: NSPanel {
    override var canBecomeKey: Bool { false }
    override var canBecomeMain: Bool { false }
}

@MainActor
final class AppDelegate: NSObject, NSApplicationDelegate, NSWindowDelegate, NSPopoverDelegate {
    private var model: UsageModel!
    private var panel: WidgetPanel!
    private var hosting: NSHostingView<WidgetView>!
    private var statusItem: NSStatusItem!
    private let popover = NSPopover()
    private var subscriptions = Set<AnyCancellable>()
    private var resizing = false
    private var statusTitle: String?
    private var positionSave: DispatchWorkItem?
    private let commandName = Notification.Name("com.mariomanzocco.aiusagewidget.command")

    func applicationDidFinishLaunching(_ notification: Notification) {
        NSApp.setActivationPolicy(.accessory)
        // Ignore SIGPIPE from an unexpectedly terminated CLI without crashing the widget.
        signal(SIGPIPE, SIG_IGN)
        model = UsageModel()
        panel = WidgetPanel(contentRect: NSRect(x: 0, y: 0, width: 300, height: 400),
                            styleMask: [.borderless, .nonactivatingPanel], backing: .buffered, defer: false)
        panel.title = "AI Usage Widget"
        panel.isOpaque = false
        panel.backgroundColor = .clear
        panel.hasShadow = true
        panel.hidesOnDeactivate = false
        panel.isReleasedWhenClosed = false
        panel.isMovableByWindowBackground = false
        panel.animationBehavior = .none
        panel.delegate = self
        hosting = NSHostingView(rootView: WidgetView(model: model))
        hosting.sizingOptions = [.intrinsicContentSize]
        panel.contentView = hosting
        statusItem = NSStatusBar.system.statusItem(withLength: NSStatusItem.variableLength)
        if let button = statusItem.button {
            button.target = self; button.action = #selector(statusClicked(_:))
            button.sendAction(on: [.leftMouseUp, .rightMouseUp])
            button.imagePosition = .imageOnly
            button.toolTip = "AI Usage · Claude and Codex remaining allowance\nClick for allowance and reset times."
            button.setAccessibilityLabel("AI Usage Widget menu bar")
        }
        popover.behavior = .transient
        popover.animates = false
        popover.delegate = self
        popover.contentViewController = NSHostingController(rootView: WidgetView(model: model, inPopover: true))
        model.onWindowSettings = { [weak self] in self?.applyWindowSettings() }
        model.onUpdate = { [weak self] in self?.updatePresentation() }
        model.onShow = { [weak self] in self?.reposition() }
        model.panelCovered = { [weak self] in self?.panelCovered() ?? false }
        model.notifications.onOpen = { [weak self] in self?.showPopover() }
        model.objectWillChange.sink { [weak self] _ in
            DispatchQueue.main.async { self?.updatePresentation() }
        }.store(in: &subscriptions)
        updatePresentation()
        restorePosition()
        applyWindowSettings()
        NotificationCenter.default.addObserver(self, selector: #selector(screensChanged), name: NSApplication.didChangeScreenParametersNotification, object: nil)
        DistributedNotificationCenter.default().addObserver(self, selector: #selector(command(_:)), name: commandName, object: nil)
        model.start()
    }

    private func updatePresentation() {
        guard panel != nil else { return }
        let title = model.menuTitle
        if title != statusTitle, let button = statusItem.button {
            statusTitle = title
            button.image = StatusIcon.image(model.menuReadings)
            button.setAccessibilityTitle(title)
        }
        let height = ceil(hosting.fittingSize.height)
        if height > 0 && abs(height - panel.frame.height) > 1 {
            resizing = true
            let top = panel.frame.maxY
            panel.setFrame(NSRect(x: panel.frame.minX, y: top - height, width: 300, height: height), display: true)
            clampToDisplay()
            resizing = false
        }
        if popover.isShown, let controller = popover.contentViewController {
            popover.contentSize = controller.view.fittingSize
        }
    }
    private func applyWindowSettings() {
        guard panel != nil else { return }
        panel.isFloatingPanel = model.topmost
        // isFloatingPanel changes the default level; set our explicit level afterwards.
        panel.level = model.topmost ? .floating : NSWindow.Level(rawValue: Int(CGWindowLevelForKey(.desktopIconWindow)) + 1)
        panel.collectionBehavior = model.topmost ? [.canJoinAllSpaces, .fullScreenAuxiliary, .ignoresCycle]
            : [.canJoinAllSpaces, .stationary, .ignoresCycle]
        if model.panelVisible { panel.orderFrontRegardless() } else { panel.orderOut(nil) }
    }
    private func restorePosition() {
        let defaults = UserDefaults.standard
        if defaults.object(forKey: "panelX") != nil {
            panel.setFrameOrigin(NSPoint(x: defaults.double(forKey: "panelX"), y: defaults.double(forKey: "panelY")))
            clampToDisplay()
        } else { positionOnCurrentDisplay() }
    }
    private func positionOnCurrentDisplay() {
        let screen = NSScreen.screens.first { NSMouseInRect(NSEvent.mouseLocation, $0.frame, false) } ?? NSScreen.main
        guard let area = screen?.visibleFrame else { return }
        panel.setFrameOrigin(NSPoint(x: area.maxX - panel.frame.width - 24, y: area.maxY - panel.frame.height - 24))
        clampToDisplay()
    }
    private func clampToDisplay() {
        let frame = panel.frame
        let best = NSScreen.screens.max { a, b in
            let aa = a.visibleFrame.intersection(frame), bb = b.visibleFrame.intersection(frame)
            return (aa.isNull ? 0 : aa.width * aa.height) < (bb.isNull ? 0 : bb.width * bb.height)
        } ?? NSScreen.main
        guard let area = best?.visibleFrame else { return }
        let x = max(area.minX + 8, min(frame.minX, area.maxX - frame.width - 8))
        let y = max(area.minY + 8, min(frame.minY, area.maxY - frame.height - 8))
        if x != frame.minX || y != frame.minY { panel.setFrameOrigin(NSPoint(x: x, y: y)) }
    }
    func windowDidMove(_ notification: Notification) {
        guard !resizing else { return }
        positionSave?.cancel()
        let work = DispatchWorkItem { [weak self] in
            guard let self else { return }
            UserDefaults.standard.set(self.panel.frame.minX, forKey: "panelX")
            UserDefaults.standard.set(self.panel.frame.minY, forKey: "panelY")
        }
        positionSave = work
        DispatchQueue.main.asyncAfter(deadline: .now() + 0.4, execute: work)
    }
    @objc private func screensChanged() { clampToDisplay() }
    /// Whether one visible window above the panel contains it entirely. Uses window bounds only.
    private func panelCovered() -> Bool {
        guard panel.isVisible,
              let windows = CGWindowListCopyWindowInfo([.optionOnScreenOnly], kCGNullWindowID) as? [[String: Any]] else { return false }
        func bounds(_ window: [String: Any]) -> CGRect? {
            (window[kCGWindowBounds as String] as? NSDictionary).flatMap { CGRect(dictionaryRepresentation: $0) }
        }
        // The list runs front to back, so only windows before the panel are above it.
        guard let index = windows.firstIndex(where: { ($0[kCGWindowNumber as String] as? Int) == panel.windowNumber }),
              let own = bounds(windows[index]) else { return false }
        return windows[..<index].contains { window in
            let layer = window[kCGWindowLayer as String] as? Int ?? -1
            let alpha = window[kCGWindowAlpha as String] as? Double ?? 0
            return layer >= 0 && layer < Int(CGWindowLevelForKey(.mainMenuWindow)) && alpha > 0.5
                && bounds(window)?.contains(own) == true
        }
    }
    private func reposition() { model.panelVisible = true; positionOnCurrentDisplay(); applyWindowSettings() }

    @objc private func statusClicked(_ sender: NSStatusBarButton) {
        if NSApp.currentEvent?.type == .rightMouseUp {
            let menu = NSMenu()
            let show = menu.addItem(withTitle: model.panelVisible ? "Hide Desktop Panel" : "Show Desktop Panel", action: #selector(togglePanel), keyEquivalent: "")
            show.target = self
            let pin = menu.addItem(withTitle: "Always on Top", action: #selector(togglePin), keyEquivalent: "")
            pin.target = self; pin.state = model.topmost ? .on : .off
            menu.addItem(.separator())
            let quit = menu.addItem(withTitle: "Quit AI Usage Widget", action: #selector(quit), keyEquivalent: "q"); quit.target = self
            statusItem.menu = menu; statusItem.button?.performClick(nil); statusItem.menu = nil
        } else if popover.isShown { popover.performClose(nil) }
        else { showPopover() }
    }
    @objc private func togglePanel() { model.panelVisible.toggle() }
    @objc private func togglePin() { model.topmost.toggle(); model.panelVisible = true }
    @objc private func quit() { NSApp.terminate(nil) }
    private func showPopover() {
        guard let button = statusItem.button else { return }
        popover.show(relativeTo: button.bounds, of: button, preferredEdge: .minY)
        model.popoverVisible = true
        Task { await model.updateNotificationStatus() }
    }
    func popoverDidClose(_ notification: Notification) { model.popoverVisible = false }
    func applicationShouldHandleReopen(_ sender: NSApplication, hasVisibleWindows flag: Bool) -> Bool { showPopover(); return false }
    func applicationWillTerminate(_ notification: Notification) {
        positionSave?.cancel()
        UserDefaults.standard.set(panel.frame.minX, forKey: "panelX")
        UserDefaults.standard.set(panel.frame.minY, forKey: "panelY")
    }

    @objc private func command(_ notification: Notification) {
        switch notification.object as? String {
        case "show": showPopover()
        case "diagnostics": model.writeDiagnostics(window: panel)
        default: break
        }
    }
}
