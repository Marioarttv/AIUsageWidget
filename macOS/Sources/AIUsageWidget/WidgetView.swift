import AppKit
import SwiftUI
import UsageCore

struct WidgetView: View {
    @ObservedObject var model: UsageModel
    var inPopover = false
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
                    Button("Connect Claude…") { model.refresh(manual: true, allowKeychainUI: true) }
                    Button("Refresh Now") { model.refresh(manual: true) }.keyboardShortcut("r").disabled(model.refreshing)
                    Divider()
                    Button("Quit AI Usage Widget") { NSApp.terminate(nil) }.keyboardShortcut("q")
                } label: { Image(systemName: "ellipsis") }
                    .menuStyle(.borderlessButton).menuIndicator(.hidden).fixedSize()
                    .help("Widget settings").accessibilityLabel("Widget settings")
            }.font(.system(size: 12, weight: .medium)).buttonStyle(.plain)

            ForEach(ProviderID.allCases) { provider in
                if provider == .codex { Divider().overlay(Color.white.opacity(0.05)) }
                ProviderView(provider: provider, state: model.services[provider] ?? ServiceState(), now: model.now,
                             staleAfter: model.staleAfter)
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
        .frame(width: 300)
        .background(Color(red: 0.065, green: 0.082, blue: 0.105))
        .clipShape(RoundedRectangle(cornerRadius: 16))
        .overlay(RoundedRectangle(cornerRadius: 16).strokeBorder(Color.white.opacity(0.12), lineWidth: 1))
        .preferredColorScheme(.dark)
        .fixedSize(horizontal: false, vertical: true)
    }
}

private struct ProviderView: View {
    let provider: ProviderID
    let state: ServiceState
    let now: Date
    let staleAfter: TimeInterval
    private var accent: Color { provider == .claude ? Color(red: 0.91, green: 0.58, blue: 0.43) : Color(red: 0.37, green: 0.81, blue: 0.77) }
    private var primary: UsageWindow? { state.snapshot?.primary }
    private var stale: Bool { state.stale(at: now, after: staleAfter) }
    var body: some View {
        VStack(alignment: .leading, spacing: 7) {
            HStack(spacing: 6) {
                Circle().fill(accent).frame(width: 6, height: 6)
                Text(provider.title).font(.system(size: 14, weight: .semibold))
                Spacer()
                if let plan = state.snapshot?.plan { Text(plan.capitalized).font(.system(size: 10)).foregroundStyle(.secondary) }
            }
            if let primary {
                HStack(alignment: .firstTextBaseline, spacing: 5) {
                    Text(primary.expired(at: now) ? "—" : "\(Int(primary.remaining.rounded(.down)))%")
                        .font(.system(size: 34, weight: .semibold, design: .rounded)).monospacedDigit()
                        .foregroundStyle(primary.remaining < 10 && !primary.expired(at: now) ? Color.orange : Color.primary)
                    Text("remaining").font(.system(size: 11)).foregroundStyle(.secondary)
                    Spacer(minLength: 0)
                }.opacity(stale ? 0.5 : 1)
                quotaBar(primary, height: 5)
                HStack(alignment: .top, spacing: 8) {
                    Text(primary.label).lineLimit(2)
                    Spacer(minLength: 0)
                    Text(UsageFormatting.countdown(primary.resetsAt, now: now)).multilineTextAlignment(.trailing).lineLimit(2)
                }.font(.system(size: 10)).foregroundStyle(.secondary)
                    .help(resetHelp(primary)).accessibilityElement(children: .combine)
                ForEach(state.snapshot?.windows.filter { $0.id != primary.id } ?? []) { window in
                    VStack(alignment: .leading, spacing: 4) {
                        HStack {
                            Text(window.label).lineLimit(2)
                            Spacer(minLength: 4)
                            Text(window.expired(at: now) ? "—" : "\(Int(window.remaining.rounded(.down)))%")
                                .monospacedDigit().foregroundStyle(.primary)
                        }.font(.system(size: 11)).foregroundStyle(.secondary)
                        quotaBar(window, height: 3)
                        Text(UsageFormatting.countdown(window.resetsAt, now: now))
                            .font(.system(size: 9)).foregroundStyle(.tertiary)
                    }.padding(.top, 4).help(resetHelp(window))
                }
            } else {
                Text("—").font(.system(size: 34, weight: .semibold, design: .rounded)).foregroundStyle(.secondary)
                Text("Subscription allowance").font(.system(size: 11)).foregroundStyle(.secondary)
            }
            if let status = state.status {
                Text(status).font(.system(size: 10)).foregroundStyle(.orange.opacity(0.9)).fixedSize(horizontal: false, vertical: true)
                    .padding(.top, 2)
            }
            Text(UsageFormatting.age(state.snapshot?.updatedAt, now: now))
                .font(.system(size: 9)).foregroundStyle(.tertiary)
        }.accessibilityElement(children: .contain).accessibilityLabel(provider.title + " usage")
    }
    private func quotaBar(_ window: UsageWindow, height: CGFloat) -> some View {
        GeometryReader { geometry in
            ZStack(alignment: .leading) {
                Capsule().fill(Color.white.opacity(0.08))
                Capsule().fill(window.remaining < 10 ? Color.orange : accent)
                    .frame(width: geometry.size.width * (window.expired(at: now) ? 0 : window.remaining / 100))
                    .opacity(stale ? 0.35 : 1)
            }
        }.frame(height: height)
            .accessibilityLabel(window.label).accessibilityValue(window.expired(at: now) ? "Awaiting update" : "\(Int(window.remaining)) percent remaining")
    }
    private func resetHelp(_ window: UsageWindow) -> String {
        guard let reset = window.resetsAt else { return "The provider has not supplied a reset time." }
        return "Resets " + reset.formatted(date: .complete, time: .standard)
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
