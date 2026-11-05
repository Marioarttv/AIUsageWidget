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
                    Button("Connect Claude…") { model.refresh(manual: true, allowKeychainUI: true) }
                    Toggle("Renew Claude Sign-In Automatically", isOn: $model.renewClaude)
                    Button("Refresh Now") { model.refresh(manual: true) }.keyboardShortcut("r").disabled(model.refreshing)
                    Divider()
                    Button("Quit AI Usage Widget") { NSApp.terminate(nil) }.keyboardShortcut("q")
                } label: { Image(systemName: "ellipsis") }
                    .menuStyle(.borderlessButton).menuIndicator(.hidden).fixedSize()
                    .help("Widget settings").accessibilityLabel("Widget settings")
            }.font(.system(size: 12, weight: .medium)).buttonStyle(.plain)

            ForEach(model.sources) { source in
                if source != model.sources.first { Divider().overlay(Color.white.opacity(0.05)) }
                ProviderView(provider: source.provider, account: model.accountName(source),
                             state: model.services[source.id] ?? ServiceState(), now: model.now, staleAfter: model.staleAfter) {
                    PlanDateSection(model: model, source: source)
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
        .frame(width: 300)
        .background(Color(red: 0.065, green: 0.082, blue: 0.105))
        .clipShape(RoundedRectangle(cornerRadius: 16))
        .overlay(RoundedRectangle(cornerRadius: 16).strokeBorder(Color.white.opacity(0.12), lineWidth: 1))
        .preferredColorScheme(.dark)
        .fixedSize(horizontal: false, vertical: true)
    }

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

private extension ProviderID {
    var accent: Color { self == .claude ? Color(red: 0.91, green: 0.58, blue: 0.43) : Color(red: 0.37, green: 0.81, blue: 0.77) }
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
            planDate.padding(.top, 2)
            Text(UsageFormatting.age(state.snapshot?.updatedAt, now: now))
                .font(.system(size: 9)).foregroundStyle(.tertiary)
        }.accessibilityElement(children: .contain).accessibilityLabel(account.map { "\(provider.title) usage for \($0)" } ?? provider.title + " usage")
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

/// The renewal or end date under a subscription. Clicking it opens a calendar in place.
private struct PlanDateSection: View {
    @ObservedObject var model: UsageModel
    let source: UsageSource
    @State private var hovering = false
    private var editing: Bool { model.editingPlanDate == source.id }
    var body: some View {
        VStack(alignment: .leading, spacing: 8) {
            if let line = model.planDate(source) {
                Button(action: toggle) {
                    HStack(spacing: 5) {
                        Image(systemName: "calendar").font(.system(size: 9, weight: .semibold))
                        Text(line.text).lineLimit(1)
                        Image(systemName: editing ? "chevron.up" : "pencil").font(.system(size: 8, weight: .semibold))
                            .opacity(hovering || editing ? 0.9 : 0)
                    }.font(.system(size: 10)).foregroundStyle(line.lapsed ? Color.orange.opacity(0.9) : Color.secondary)
                        .contentShape(Rectangle())
                }.help(line.help).accessibilityLabel(line.text).accessibilityHint("Edit the renewal date")
            } else if model.canAddPlanDate(source) {
                Button(action: toggle) {
                    HStack(spacing: 5) {
                        Image(systemName: "calendar.badge.plus").font(.system(size: 9, weight: .semibold))
                        Text("Add renewal date")
                    }.font(.system(size: 10)).foregroundStyle(hovering || editing ? .secondary : .tertiary)
                        .contentShape(Rectangle())
                }.help(source.provider == .claude
                       ? "Claude's sign-in doesn't report when the subscription renews. Add the date from claude.ai → Settings → Billing."
                       : "Add when this subscription renews or ends.")
            }
            if editing {
                PlanDateEditor(accent: source.provider.accent, existing: model.planDates[source.id],
                               reported: model.reportedPlanDate(source), now: model.now,
                               save: { model.setPlanDate(source, $0) }, cancel: { model.editingPlanDate = nil })
            }
        }.buttonStyle(.plain).onHover { hovering = $0 }
    }
    private func toggle() { model.editingPlanDate = editing ? nil : source.id }
}

/// A compact month calendar with the renewal cycle, styled to match the panel.
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
        .padding(10)
        .background(RoundedRectangle(cornerRadius: 10).fill(Color.white.opacity(0.045)))
        .overlay(RoundedRectangle(cornerRadius: 10).strokeBorder(Color.white.opacity(0.08), lineWidth: 1))
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
