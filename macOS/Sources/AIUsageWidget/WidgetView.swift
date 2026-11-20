import AppKit
import SwiftUI
import UsageCore

struct WidgetView: View {
    @ObservedObject var model: UsageModel
    var inPopover = false
    /// The cards' natural height, measured, to decide whether they must scroll.
    @State private var cardsHeight: CGFloat = 0
    /// The measured height of everything else: header, footer, message and padding.
    @State private var chromeHeight: CGFloat = 100
    /// Room for the cards on the display.
    private var cardsLimit: CGFloat {
        max(240, (inPopover ? model.maxPopoverHeight : model.maxPanelHeight) - chromeHeight - 2)
    }
    private var scrolling: Bool { cardsHeight > cardsLimit }
    var body: some View {
        VStack(alignment: .leading, spacing: 14) {
            HStack(spacing: 10) {
                ZStack(alignment: .leading) {
                    if !inPopover { WindowDragRegion() }
                    HStack(spacing: 6) {
                        Image(systemName: "chart.bar.xaxis").font(.system(size: 12, weight: .semibold))
                        Text("AI USAGE").font(.system(size: 10, weight: .bold, design: .rounded)).tracking(1.4)
                    }.foregroundStyle(.secondary).allowsHitTesting(false)
                }.frame(maxWidth: .infinity, alignment: .leading).frame(height: 22)
                Button { model.refresh(manual: true) } label: {
                    Image(systemName: "arrow.clockwise").opacity(model.refreshing ? 0.35 : 1)
                }.disabled(model.refreshing).help("Refresh usage").accessibilityLabel("Refresh usage")
                Button {
                    model.topmost.toggle()
                    model.panelVisible = true
                } label: { Image(systemName: model.topmost ? "pin.fill" : "pin") }
                    .foregroundStyle(model.topmost ? Color.accentColor : Color.secondary)
                    .help(model.topmost ? "Place behind other windows" : "Keep above other windows")
                    .accessibilityLabel(model.topmost ? "Switch to desktop mode" : "Switch to always on top")
                Menu {
                    Toggle("Show Desktop Panel", isOn: $model.panelVisible)
                    Toggle("Always on Top", isOn: $model.topmost)
                    Button("Reposition on This Display") { model.onShow?() }
                    Divider()
                    Toggle("Launch at Login", isOn: Binding(get: { model.loginEnabled }, set: { model.setLaunchAtLogin($0) }))
                    Picker("Refresh While Visible", selection: $model.watchedInterval) {
                        ForEach(RefreshInterval.watchedChoices, id: \.self) { Text("Every " + RefreshInterval.label($0)).tag($0) }
                    }
                    Picker("Refresh While Hidden", selection: $model.unwatchedInterval) {
                        ForEach(RefreshInterval.unwatchedChoices, id: \.self) { Text("Every " + RefreshInterval.label($0)).tag($0) }
                    }
                    Toggle("Alert Below 10% (5-hour Windows)", isOn: $model.lowAlerts)
                    Toggle("Notify After Usage Resets", isOn: $model.resetAlerts)
                    Button("Send Test Notification") { model.testNotification() }
                    Button("Notification Settings…") { model.openNotificationSettings() }
                    Text(model.notificationStatus)
                    Divider()
                    Menu("ChatGPT Accounts") {
                        ForEach(model.codexAccounts) { account in
                            Toggle(accountTitle(account), isOn: Binding(get: { model.isTracked(account) },
                                                                         set: { model.setTracked(account, $0) }))
                                .disabled(account.key == nil)
                        }
                        Divider()
                        Button("Add Codex Sign-In Folder…") { model.addCodexFolder() }
                        Text("Codex Subscription Router accounts appear automatically")
                    }
                    Toggle("Show Antigravity", isOn: $model.showAntigravity).disabled(!model.antigravityInstalled)
                    if !model.antigravityInstalled { Text("Antigravity CLI not found") }
                    Button("Connect Claude…") { model.refresh(manual: true, allowKeychainUI: true) }
                    Toggle("Renew Claude Sign-In Automatically", isOn: $model.renewClaude)
                    Button("Refresh Now") { model.refresh(manual: true) }.keyboardShortcut("r").disabled(model.refreshing)
                    Divider()
                    Button("Quit AI Usage Widget") { NSApp.terminate(nil) }.keyboardShortcut("q")
                } label: { Image(systemName: "ellipsis") }
                    .menuStyle(.borderlessButton).menuIndicator(.hidden).fixedSize()
                    .help("Widget settings").accessibilityLabel("Widget settings")
            }.font(.system(size: 12, weight: .medium)).buttonStyle(.plain)

            // Always the same scroll view, so the cards keep their state when scrolling starts or stops.
            ScrollView(.vertical) {
                cards.fixedSize(horizontal: false, vertical: true)
                    .background(GeometryReader { Color.clear.preference(key: Heights.self, value: Heights(cards: $0.size.height)) })
            }
            .scrollDisabled(!scrolling)
            .frame(height: min(cardsHeight, cardsLimit))
            .background(GeometryReader { Color.clear.preference(key: Heights.self, value: Heights(slot: $0.size.height)) })
            // Shows that more follows, since macOS hides scroll bars until scrolling.
            .overlay(alignment: .bottom) {
                if scrolling {
                    LinearGradient(colors: [.clear, Self.background], startPoint: .top, endPoint: .bottom)
                        .frame(height: 18).allowsHitTesting(false)
                }
            }
            HStack(spacing: 5) {
                Image(systemName: model.topmost ? "pin.fill" : "desktopcomputer")
                Text(model.topmost ? "Always on top" : "On desktop")
                Spacer()
                if model.refreshing { Text("Refreshing…") }
                else { Text("Auto · " + RefreshInterval.label(model.watchedInterval)) }
            }.font(.system(size: 9, weight: .medium)).foregroundStyle(.tertiary)
            if let message = model.message {
                Button { model.message = nil } label: {
                    HStack { Text(message).font(.caption); Spacer(); Image(systemName: "xmark").font(.caption2) }
                }.buttonStyle(.plain).foregroundStyle(.secondary)
            }
        }
        .padding(16)
        .background(GeometryReader { Color.clear.preference(key: Heights.self, value: Heights(total: $0.size.height)) })
        // All three heights come from the same layout pass, so the chrome is the total less the cards' slot.
        .onPreferenceChange(Heights.self) { heights in
            let chrome = heights.total - heights.slot
            var changed = false
            if abs(heights.cards - cardsHeight) > 0.5 { cardsHeight = heights.cards; changed = true }
            if chrome > 0, abs(chrome - chromeHeight) > 0.5 { chromeHeight = chrome; changed = true }
            // The window follows the view's size only when the model reports a change.
            if changed { DispatchQueue.main.async { model.onUpdate?() } }
        }
        .frame(width: 300)
        .background(Self.background)
        .clipShape(RoundedRectangle(cornerRadius: 16))
        .overlay(RoundedRectangle(cornerRadius: 16).strokeBorder(Color.white.opacity(0.12), lineWidth: 1))
        .preferredColorScheme(.dark)
        .fixedSize(horizontal: false, vertical: true)
    }

    private var cards: some View {
        VStack(alignment: .leading, spacing: 14) {
            ForEach(model.sources) { source in
                if source != model.sources.first { Divider().overlay(Color.white.opacity(0.05)) }
                ProviderView(provider: source.provider, account: model.accountName(source),
                             state: model.services[source.id] ?? ServiceState(), now: model.now, staleAfter: model.staleAfter) {
                    PlanDateSection(model: model, source: source)
                }
            }
        }
    }
    private static let background = Color(red: 0.065, green: 0.082, blue: 0.105)

    private func accountTitle(_ account: CodexAccount) -> String {
        var parts = [account.name]
        if let plan = account.plan { parts.append(plan.capitalized) }
        switch account.origin {
        case .main: parts.append("Main sign-in")
        case .router: parts.append("Subscription Router")
        case .folder: if !account.signedIn { parts.append("Signed out") }
        }
        return parts.joined(separator: " · ")
    }
}

/// The cards' natural height, the height of the slot they are shown in, and the whole view's.
private struct Heights: PreferenceKey, Equatable {
    var cards: CGFloat = 0, slot: CGFloat = 0, total: CGFloat = 0
    static var defaultValue = Heights()
    static func reduce(value: inout Heights, nextValue: () -> Heights) {
        let next = nextValue()
        value = Heights(cards: max(value.cards, next.cards), slot: max(value.slot, next.slot), total: max(value.total, next.total))
    }
}

private extension ProviderID {
    var accent: Color {
        switch self {
        case .claude: return Color(red: 0.91, green: 0.58, blue: 0.43)
        case .codex: return Color(red: 0.37, green: 0.81, blue: 0.77)
        case .antigravity: return Color(red: 0.48, green: 0.62, blue: 0.98)
        }
    }
}

private struct ProviderView<PlanDate: View>: View {
    let provider: ProviderID
    /// The signed-in email for ChatGPT/Codex accounts, so several subscriptions can be told apart.
    let account: String?
    let state: ServiceState
    let now: Date
    let staleAfter: TimeInterval
    /// When the subscription renews or ends, editable in place.
    @ViewBuilder let planDate: PlanDate
    private var accent: Color { provider.accent }
    private var primary: UsageWindow? { state.snapshot?.primary }
    private var stale: Bool { state.stale(at: now, after: staleAfter) }
    /// A used-up longer limit leaves nothing in this window either.
    private func blocker(_ window: UsageWindow) -> UsageWindow? { state.snapshot?.blocker(of: window, now: now) }
    private func available(_ window: UsageWindow) -> Double { blocker(window) == nil ? window.remaining : 0 }
    /// A used-up longer limit means 0% even once this window has expired; otherwise an expired window awaits a reading.
    private func unknown(_ window: UsageWindow) -> Bool { blocker(window) == nil && window.expired(at: now) }
    private func percent(_ window: UsageWindow) -> String { unknown(window) ? "—" : "\(Int(available(window).rounded(.down)))%" }
    /// Further windows: one row each, except that a quota pool other than the headline one (an Antigravity model group)
    /// shares a single row.
    private var rows: [[UsageWindow]] {
        guard let snapshot = state.snapshot, let primary else { return [] }
        var result: [[UsageWindow]] = []
        for window in snapshot.windows where window.id != primary.id {
            if let pool = window.pool, pool != primary.pool, let index = result.firstIndex(where: { $0.first?.pool == pool }) {
                result[index].append(window)
            } else { result.append([window]) }
        }
        return result
    }
    var body: some View {
        VStack(alignment: .leading, spacing: 7) {
            HStack(spacing: 6) {
                Circle().fill(accent).frame(width: 6, height: 6)
                Text(provider.title).font(.system(size: 14, weight: .semibold)).layoutPriority(1)
                if let account {
                    Text(account).font(.system(size: 10)).foregroundStyle(.secondary).lineLimit(1).truncationMode(.middle)
                        .help(account)
                }
                Spacer(minLength: 4)
                if let plan = state.snapshot?.plan { Text(plan.capitalized).font(.system(size: 10)).foregroundStyle(.secondary).layoutPriority(1) }
            }
            if let primary {
                HStack(alignment: .firstTextBaseline, spacing: 5) {
                    Text(percent(primary))
                        .font(.system(size: 34, weight: .semibold, design: .rounded)).monospacedDigit()
                        .foregroundStyle(available(primary) < 10 && !unknown(primary) ? Color.orange : Color.primary)
                    Text("remaining").font(.system(size: 11)).foregroundStyle(.secondary)
                    Spacer(minLength: 0)
                }.opacity(stale ? 0.5 : 1)
                quotaBar(primary, height: 5)
                HStack(alignment: .top, spacing: 8) {
                    Text(primary.label).lineLimit(2)
                    Spacer(minLength: 0)
                    if let blocker = blocker(primary) {
                        Text(UsageFormatting.blocked(by: blocker, now: now)).multilineTextAlignment(.trailing).lineLimit(2)
                            .foregroundStyle(Color.orange.opacity(0.9))
                    } else {
                        Text(UsageFormatting.countdown(primary.resetsAt, now: now)).multilineTextAlignment(.trailing).lineLimit(2)
                    }
                }.font(.system(size: 10)).foregroundStyle(.secondary)
                    .help(resetHelp(primary)).accessibilityElement(children: .combine)
                // One line and a bar per further window, so cards with several limits stay short.
                ForEach(rows, id: \.first!.id) { row in
                    if row.count == 1, let window = row.first { windowRow(window) } else { poolRow(row) }
                }
            } else {
                Text("—").font(.system(size: 34, weight: .semibold, design: .rounded)).foregroundStyle(.secondary)
                Text("Subscription allowance").font(.system(size: 11)).foregroundStyle(.secondary)
            }
            if let status = state.status {
                Text(status).font(.system(size: 10)).foregroundStyle(.orange.opacity(0.9)).fixedSize(horizontal: false, vertical: true)
                    .padding(.top, 2)
            }
            // The renewal date shares the last line with the reading's age, so it adds no height.
            HStack(alignment: .firstTextBaseline, spacing: 6) {
                planDate
                Spacer(minLength: 0)
                Text(UsageFormatting.age(state.snapshot?.updatedAt, now: now))
                    .font(.system(size: 9)).foregroundStyle(.tertiary).lineLimit(1).layoutPriority(1)
            }
        }.accessibilityElement(children: .contain).accessibilityLabel(account.map { "\(provider.title) usage for \($0)" } ?? provider.title + " usage")
    }
    private func quotaBar(_ window: UsageWindow, height: CGFloat) -> some View {
        GeometryReader { geometry in
            ZStack(alignment: .leading) {
                Capsule().fill(Color.white.opacity(0.08))
                Capsule().fill(available(window) < 10 ? Color.orange : accent)
                    .frame(width: geometry.size.width * (unknown(window) ? 0 : available(window) / 100))
                    .opacity(stale ? 0.35 : 1)
            }
        }.frame(height: height)
            .accessibilityLabel(window.label).accessibilityValue(unknown(window) ? "Awaiting update" : "\(Int(available(window))) percent remaining")
    }
    private func windowRow(_ window: UsageWindow) -> some View {
        VStack(alignment: .leading, spacing: 3) {
            HStack(alignment: .firstTextBaseline, spacing: 6) {
                Text(window.label).lineLimit(1).truncationMode(.middle)
                Spacer(minLength: 4)
                Text(blocker(window).map { UsageParser.windowLabel($0.minutes, fallback: "Longer limit").lowercased() + " used up" }
                     ?? UsageFormatting.shortCountdown(window.resetsAt, now: now))
                    .font(.system(size: 9)).lineLimit(1)
                    .foregroundStyle(blocker(window) == nil ? AnyShapeStyle(.tertiary) : AnyShapeStyle(Color.orange.opacity(0.85)))
                Text(percent(window)).monospacedDigit().foregroundStyle(.primary)
            }.font(.system(size: 11)).foregroundStyle(.secondary)
            quotaBar(window, height: 3)
        }.padding(.top, 3).help(resetHelp(window)).accessibilityElement(children: .combine)
    }
    /// A quota group in one row, "Claude and GPT   5h 95% · wk 70%", with a bar for whichever is lower.
    private func poolRow(_ windows: [UsageWindow]) -> some View {
        let name = windows[0].label.components(separatedBy: " · ").first ?? windows[0].label
        let tightest = windows.min { (unknown($0) ? 101 : available($0)) < (unknown($1) ? 101 : available($1)) } ?? windows[0]
        return VStack(alignment: .leading, spacing: 3) {
            HStack(alignment: .firstTextBaseline, spacing: 6) {
                Text(name).lineLimit(1).truncationMode(.middle)
                Spacer(minLength: 4)
                ForEach(Array(windows.enumerated()), id: \.element.id) { index, window in
                    if index > 0 { Text("·").foregroundStyle(.tertiary) }
                    HStack(alignment: .firstTextBaseline, spacing: 3) {
                        Text(Self.shortName(window)).font(.system(size: 9)).foregroundStyle(.tertiary)
                        Text(percent(window)).monospacedDigit()
                            .foregroundStyle(blocker(window) != nil ? Color.orange.opacity(0.9) : Color.primary)
                    }
                }
            }.font(.system(size: 11)).foregroundStyle(.secondary)
            quotaBar(tightest, height: 3)
        }.padding(.top, 3).help(windows.map { $0.label + " · " + percent($0) + " · " + resetHelp($0) }.joined(separator: "\n"))
            .accessibilityElement(children: .combine)
    }
    private static func shortName(_ window: UsageWindow) -> String {
        switch window.minutes {
        case 300: return "5h"
        case 10080: return "wk"
        case 1440: return "day"
        case 60: return "1h"
        default: return window.label.components(separatedBy: " · ").last ?? window.label
        }
    }
    private func resetHelp(_ window: UsageWindow) -> String {
        var text = window.resetsAt.map { "Resets " + $0.formatted(date: .complete, time: .standard) }
            ?? "The provider has not supplied a reset time."
        if let blocker = blocker(window) {
            text = "\(Int(window.remaining.rounded(.down)))% of this window is left, but the \(blocker.label) limit is used up, "
                + "so none of it can be used until that resets"
                + (blocker.resetsAt.map { " " + $0.formatted(date: .complete, time: .shortened) } ?? "") + ". " + text
        }
        return text
    }
}

/// The renewal or end date at the foot of a subscription. Clicking it opens a calendar in a floating card
/// under the date, so editing never makes the panel taller.
private struct PlanDateSection: View {
    @ObservedObject var model: UsageModel
    let source: UsageSource
    @ObservedObject private var editor = PlanDatePanel.shared
    @State private var hovering = false
    @State private var anchor = ScreenAnchor.Box()
    private var editing: Bool { editor.openSource == source.id && editor.anchorWindow === anchor.view?.window }
    var body: some View {
        if let line = model.planDate(source) {
            chip(icon: "calendar", text: line.text,
                 color: line.lapsed ? Color.orange.opacity(0.9) : hovering || editing ? Color.primary : Color.secondary)
                .help(line.help).accessibilityLabel(line.text).accessibilityHint("Edit the renewal date")
        } else if model.canAddPlanDate(source) {
            chip(icon: "calendar.badge.plus", text: "Renewal date",
                 color: hovering || editing ? Color.secondary : Color(white: 1, opacity: 0.32))
                .help(addDateHelp)
        }
    }
    private var addDateHelp: String {
        switch source.provider {
        case .claude: return "Claude's sign-in doesn't report when the subscription renews. Add the date from claude.ai → Settings → Billing."
        case .antigravity: return "Antigravity doesn't report when your Google AI plan renews. Add the date from Google One → Manage plan."
        case .codex: return "Add when this subscription renews or ends."
        }
    }
    private func chip(icon: String, text: String, color: Color) -> some View {
        Button(action: toggle) {
            HStack(spacing: 4) {
                Image(systemName: icon).font(.system(size: 9, weight: .semibold))
                Text(text).lineLimit(1).truncationMode(.tail)
            }
            .font(.system(size: 10)).foregroundStyle(color)
            .padding(.horizontal, 6).padding(.vertical, 2)
            .background(Capsule().fill(Color.white.opacity(hovering || editing ? 0.1 : 0.05)))
            .contentShape(Capsule())
        }.buttonStyle(.plain).onHover { hovering = $0 }
            .background(ScreenAnchor(box: anchor))
            // Line up the chip's text with the card's left edge.
            .padding(.leading, -6)
    }
    private func toggle() {
        if editing { editor.close(); return }
        guard let view = anchor.view else { return }
        let model = model, source = source
        editor.open(source: source.id, anchor: view,
                    PlanDateEditor(accent: source.provider.accent, existing: model.planDates[source.id],
                                   reported: model.reportedPlanDate(source), now: model.now,
                                   save: { model.setPlanDate(source, $0); PlanDatePanel.shared.close() },
                                   cancel: { PlanDatePanel.shared.close() }))
    }
}

/// Finds the AppKit view behind a SwiftUI view, to place the calendar next to it on screen.
private struct ScreenAnchor: NSViewRepresentable {
    final class Box { weak var view: NSView? }
    let box: Box
    func makeNSView(context: Context) -> NSView { let view = NSView(); box.view = view; return view }
    func updateNSView(_ nsView: NSView, context: Context) { box.view = nsView }
}

/// The floating card that holds the renewal calendar. It sits above the panel and the menu-bar popover,
/// takes clicks without becoming key (like the panel), and closes on Save, Cancel, or a click elsewhere.
@MainActor
final class PlanDatePanel: ObservableObject {
    static let shared = PlanDatePanel()
    @Published private(set) var openSource: String?
    private(set) weak var anchorWindow: NSWindow?
    private var panel: WidgetPanel?
    private var monitors: [Any] = []
    /// Lets the menu-bar popover stay open while its calendar is in use.
    var onOpen: ((NSWindow) -> Void)?
    var onClose: (() -> Void)?

    func open<Content: View>(source: String, anchor: NSView, _ content: Content) {
        close()
        guard let window = anchor.window else { return }
        let card = content
            .background(Color(red: 0.085, green: 0.104, blue: 0.13))
            .clipShape(RoundedRectangle(cornerRadius: 12))
            .overlay(RoundedRectangle(cornerRadius: 12).strokeBorder(Color.white.opacity(0.14), lineWidth: 1))
            .preferredColorScheme(.dark)
        let hosting = NSHostingView(rootView: card)
        let size = hosting.fittingSize
        let chip = window.convertToScreen(anchor.convert(anchor.bounds, to: nil))
        let area = (window.screen ?? NSScreen.main)?.visibleFrame ?? .zero
        // Below the date when it fits, otherwise above it; kept on the display.
        var origin = NSPoint(x: chip.minX - 6, y: chip.minY - 6 - size.height)
        if origin.y < area.minY + 8 { origin.y = chip.maxY + 6 }
        origin.x = max(area.minX + 8, min(origin.x, area.maxX - size.width - 8))
        origin.y = max(area.minY + 8, min(origin.y, area.maxY - size.height - 8))
        let panel = WidgetPanel(contentRect: NSRect(origin: origin, size: size), styleMask: [.borderless, .nonactivatingPanel],
                                backing: .buffered, defer: false)
        panel.isOpaque = false; panel.backgroundColor = .clear; panel.hasShadow = true
        panel.hidesOnDeactivate = false; panel.isReleasedWhenClosed = false; panel.animationBehavior = .utilityWindow
        // Above the menu-bar popover, which the calendar can open from.
        panel.level = .popUpMenu
        panel.collectionBehavior = [.canJoinAllSpaces, .fullScreenAuxiliary, .ignoresCycle]
        panel.contentView = hosting
        panel.orderFrontRegardless()
        self.panel = panel; anchorWindow = window; openSource = source
        onOpen?(window)
        let events: NSEvent.EventTypeMask = [.leftMouseDown, .rightMouseDown, .otherMouseDown]
        // Clicks in other apps close it; so do clicks in the widget outside the card, except on the date itself,
        // whose button then closes it.
        if let global = NSEvent.addGlobalMonitorForEvents(matching: events, handler: { [weak self] _ in
            Task { @MainActor in self?.close() }
        }) { monitors.append(global) }
        if let local = NSEvent.addLocalMonitorForEvents(matching: events, handler: { [weak self, weak anchor] event in
            guard let self, event.window !== self.panel else { return event }
            let onChip = anchor.map { $0.window === event.window && $0.bounds.contains($0.convert(event.locationInWindow, from: nil)) } ?? false
            if !onChip { self.close() }
            return event
        }) { monitors.append(local) }
    }

    func close() {
        monitors.forEach(NSEvent.removeMonitor); monitors = []
        panel?.orderOut(nil); panel = nil
        anchorWindow = nil
        if openSource != nil { openSource = nil; onClose?() }
    }
}

/// A compact month calendar with the renewal cycle, shown in a floating card.
private struct PlanDateEditor: View {
    let accent: Color
    /// The date entered earlier, if any.
    let existing: ManualPlanDate?
    /// ChatGPT's paid-period end, which removing the entry falls back to.
    let reported: Date?
    let now: Date
    let save: (ManualPlanDate?) -> Void
    let cancel: () -> Void
    @State private var day: Date
    @State private var month: Date
    @State private var cycle: ManualPlanDate.Cycle
    private let calendar = Calendar.current

    init(accent: Color, existing: ManualPlanDate?, reported: Date?, now: Date,
         save: @escaping (ManualPlanDate?) -> Void, cancel: @escaping () -> Void) {
        self.accent = accent; self.existing = existing; self.reported = reported; self.now = now
        self.save = save; self.cancel = cancel
        let start = Calendar.current.startOfDay(for: existing?.upcoming(now: now) ?? reported ?? now)
        _day = State(initialValue: start); _month = State(initialValue: start)
        _cycle = State(initialValue: existing?.cycle ?? .monthly)
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 10) {
            cyclePicker
            VStack(spacing: 6) {
                monthHeader
                weekGrid
            }
            Text(caption).font(.system(size: 9)).foregroundStyle(.secondary).lineLimit(1)
            HStack(spacing: 8) {
                if existing != nil {
                    Button(reported == nil ? "Remove" : "Use ChatGPT's Date") { save(nil) }
                        .foregroundStyle(.secondary)
                        .help(reported == nil ? "Stop showing a renewal date" : "Show the date ChatGPT reports again")
                }
                Spacer(minLength: 0)
                Button("Cancel", action: cancel).foregroundStyle(.secondary)
                Button { save(ManualPlanDate(date: day, cycle: cycle)) } label: {
                    Text("Save").fontWeight(.semibold).foregroundStyle(.black.opacity(0.85))
                        .padding(.horizontal, 12).padding(.vertical, 4)
                        .background(Capsule().fill(accent))
                }
            }.font(.system(size: 11))
        }
        .padding(12)
        .frame(width: 252)
        .buttonStyle(.plain)
    }

    /// What saving will show, for example "Renews on the 26th of every month · next 26 Dec".
    private var caption: String {
        let text = UsageFormatting.day(day, now: now, calendar: calendar)
        let next = ManualPlanDate(date: day, cycle: cycle).upcoming(now: now, calendar: calendar)
        let later = next == day ? "" : " · next " + UsageFormatting.day(next, now: now, calendar: calendar)
        switch cycle {
        case .monthly:
            let format = NumberFormatter(); format.numberStyle = .ordinal
            let ordinal = format.string(from: NSNumber(value: calendar.component(.day, from: day))) ?? text
            return "Renews on the \(ordinal) of every month" + later
        case .yearly: return "Renews every year on " + day.formatted(.dateTime.day().month(.abbreviated)) + later
        case .ends: return (day < calendar.startOfDay(for: now) ? "Cancelled: ended " : "Cancelled: ends ") + text
        }
    }

    private var cyclePicker: some View {
        HStack(spacing: 2) {
            ForEach(ManualPlanDate.Cycle.allCases, id: \.self) { option in
                Button { cycle = option } label: {
                    Text(option.title).font(.system(size: 10, weight: cycle == option ? .semibold : .regular))
                        .frame(maxWidth: .infinity).padding(.vertical, 4)
                        .foregroundStyle(cycle == option ? Color.primary : Color.secondary)
                        .background(RoundedRectangle(cornerRadius: 6).fill(cycle == option ? Color.white.opacity(0.14) : .clear))
                        .contentShape(Rectangle())
                }.help(option.explanation).accessibilityAddTraits(cycle == option ? .isSelected : [])
            }
        }.padding(2).background(RoundedRectangle(cornerRadius: 8).fill(Color.black.opacity(0.25)))
    }

    private var monthHeader: some View {
        HStack {
            Button { step(-1) } label: { Image(systemName: "chevron.left").frame(width: 22, height: 18).contentShape(Rectangle()) }
                .accessibilityLabel("Previous month")
            Spacer()
            Button { month = day } label: {
                Text(month.formatted(.dateTime.month(.wide).year())).font(.system(size: 11, weight: .semibold))
            }.help("Go to the selected day")
            Spacer()
            Button { step(1) } label: { Image(systemName: "chevron.right").frame(width: 22, height: 18).contentShape(Rectangle()) }
                .accessibilityLabel("Next month")
        }.font(.system(size: 10, weight: .semibold)).foregroundStyle(.secondary)
    }
    private func step(_ months: Int) { month = calendar.date(byAdding: .month, value: months, to: month) ?? month }

    private var weekGrid: some View {
        let cells = MonthGrid.days(of: month, calendar: calendar)
        let today = calendar.startOfDay(for: now)
        let repeatDay = renewal(in: month)
        return VStack(spacing: 2) {
            HStack(spacing: 0) {
                ForEach(Array(MonthGrid.weekdaySymbols(calendar: calendar).enumerated()), id: \.offset) { _, symbol in
                    Text(symbol).font(.system(size: 9, weight: .medium)).foregroundStyle(.tertiary).frame(maxWidth: .infinity)
                }
            }
            ForEach(0..<cells.count / 7, id: \.self) { row in
                HStack(spacing: 0) {
                    ForEach(0..<7, id: \.self) { column in
                        if let date = cells[row * 7 + column] {
                            dayCell(date, today: today, repeats: date == repeatDay)
                        } else {
                            Color.clear.frame(maxWidth: .infinity).frame(height: 24)
                        }
                    }
                }
            }
        }
    }
    private func dayCell(_ date: Date, today: Date, repeats: Bool) -> some View {
        let selected = calendar.isDate(date, inSameDayAs: day)
        return Button { day = date } label: {
            Text("\(calendar.component(.day, from: date))")
                .font(.system(size: 11, weight: selected ? .bold : .regular)).monospacedDigit()
                .foregroundStyle(selected ? Color.black.opacity(0.85) : date < today ? Color.secondary : Color.primary)
                .frame(width: 24, height: 24)
                .background(Circle().fill(selected ? accent : .clear))
                .overlay(Circle().strokeBorder(date == today && !selected ? Color.white.opacity(0.35) : .clear, lineWidth: 1))
                .overlay(alignment: .bottom) {
                    if repeats { Circle().fill(accent).frame(width: 3, height: 3).offset(y: -1) }
                }
                .frame(maxWidth: .infinity).contentShape(Rectangle())
        }.help(date.formatted(date: .complete, time: .omitted) + (repeats ? " · renews" : ""))
            .accessibilityLabel(date.formatted(date: .complete, time: .omitted))
            .accessibilityAddTraits(selected ? .isSelected : [])
    }
    /// Where the chosen day repeats in the shown month, so a monthly or yearly cycle is visible at a glance.
    private func renewal(in month: Date) -> Date? {
        guard cycle != .ends,
              let from = calendar.dateInterval(of: .month, for: day)?.start,
              let to = calendar.dateInterval(of: .month, for: month)?.start,
              let months = calendar.dateComponents([.month], from: from, to: to).month, months > 0,
              cycle == .monthly || months % 12 == 0 else { return nil }
        return calendar.date(byAdding: .month, value: months, to: day)
    }
}

private struct WindowDragRegion: NSViewRepresentable {
    func makeNSView(context: Context) -> NSView { DragView() }
    func updateNSView(_ nsView: NSView, context: Context) {}
    final class DragView: NSView {
        override func mouseDown(with event: NSEvent) { window?.performDrag(with: event) }
        override func resetCursorRects() { addCursorRect(bounds, cursor: .openHand) }
    }
}
