# AI Usage Widget for macOS

## Install and run

Requires macOS 13+, Xcode to build, Codex CLI signed in with a ChatGPT subscription, and Claude Code signed in with a Claude subscription. The build targets the current Mac's architecture.

```sh
./macOS/scripts/install.sh
```

The installer puts the app in `~/Applications/AI Usage Widget.app` and opens it. Quit the app before reinstalling. The app is a menu-bar utility and does not add a Dock icon. Reopening it from Finder opens its menu-bar view. Only one instance runs at a time.

The build uses an existing Apple Development signing identity when available, without creating certificates or downloading profiles. Override it with `AI_WIDGET_SIGNING_IDENTITY`; use `-` for ad-hoc local signing. A stable signing identity is recommended to retain Keychain access across rebuilds. This local build is not notarized for public distribution.

## Display and controls

- **Desktop mode** is the default: the panel sits above the desktop icons and below application windows. It appears on desktop Spaces without appearing in the application switcher.
- **Pin** switches to a floating panel above normal windows. Full-screen behavior depends on the active Space and macOS; system/security surfaces retain their own ordering.
- **Drag the AI USAGE title** to reposition the panel. The position and display mode persist. Disconnecting a display brings an offscreen panel back onto an available display.
- **The Claude and ChatGPT marks in the menu bar** show Claude / Codex remaining allowance for the shortest reported window. With several ChatGPT accounts tracked, the ChatGPT mark is followed by one number per account, in panel order; hover over the menu-bar item to see which email each number belongs to. Click for the complete widget; right-click for quick show/hide, pin, and quit commands. Unknown, expired, or failed readings show a dash in the menu bar.
- **⋯** contains show/hide, reposition, launch at login, refresh intervals, both alert toggles, a test notification, notification settings, ChatGPT account choices, Claude connection access, automatic Claude sign-in renewal, and the Claude renewal date.
- **Claude Renewal Date…** in ⋯: Claude's sign-in doesn't report billing, so enter the date from claude.ai → Settings → Billing and choose *Renews monthly*, *Renews yearly*, or *Ends on this date (cancelled)*. The Claude section then shows “Renews 26 Nov” or “Ends 26 Nov”; a renewing date moves on to the next month or year by itself. **Remove Date** in the same dialog hides it. The date is kept only in the widget's settings.
- **Refresh While Visible** (1–30 minutes, default 3) sets how often readings refresh while the panel or menu-bar view is on screen; the panel footer shows it. **Refresh While Hidden** (5–60 minutes, default 10) applies while the panel is hidden or fully covered; it is never shorter than the visible interval. Intervals under three minutes poll Claude's rate-limited usage endpoint more often and are more likely to meet its backoff.
- **↻** refreshes manually. Requests still respect the provider's minimum interval and rate-limit backoff.
- Hover over a reset time for the exact local date and time. Expired windows show “awaiting update” instead of inventing a full allowance. Failed readings retain visibly dimmed last-known data with an explanation.

## Multiple ChatGPT accounts

The panel shows one Codex section per tracked ChatGPT account, labeled with the account's email and plan. Under each account, “Renews or ends 17 Nov” shows when the current paid period ends, as ChatGPT last recorded it in that sign-in; hover for the exact time. The sign-in doesn't say whether the plan was cancelled, so the date is when it either renews or ends, and it updates when Codex renews the sign-in. Each account has its own readings, low-allowance alerts, and reset notifications; notification titles name the email when more than one account is tracked.

- **Main sign-in:** the Codex CLI's own sign-in (`CODEX_HOME` or `~/.codex`). It is always shown.
- **Codex Subscription Router:** every signed-in account in `~/.codex-mux/state.json` is found automatically, including accounts disabled for routing. Router slots without a sign-in are skipped, and an account that matches one already listed appears once. Paid accounts are shown by default; free-plan accounts are listed but off until you turn them on.
- **Codex sign-in folders:** **⋯ → ChatGPT Accounts → Add Codex Sign-In Folder…** adds any Codex home that contains `auth.json`, for example one signed in with `CODEX_HOME=~/.codex-second codex login`. Turning an added folder off removes it.

Use the checkmarks in **⋯ → ChatGPT Accounts** to show or hide each account. New router sign-ins are picked up at the next refresh.

Each extra account is read by starting the Codex CLI with `CODEX_HOME` set to that account's folder, exactly as for the main sign-in. To list accounts, the widget reads only the email, plan, account ID, and paid-period end from each `auth.json`; it never stores or logs tokens. The CLI may renew an account's sign-in as usual while reading it, as Codex Subscription Router does.

## Notifications

Allow notifications on first launch. Use **Send Test Notification** to verify the delivery path. If permission was denied, enable the app in **System Settings → Notifications**. Focus, notification style, and screen-sharing settings can affect banners and sound.

The two toggles are independent and enabled by default:

1. **Below 10%:** only windows whose reported duration is five hours qualify. Exactly 10% remaining does not trigger a warning. Already-low windows warn on first connection; subsequent polls and app restarts do not repeat that warning for the same window.
2. **Usage reset:** all tracked windows qualify, including weekly and model-specific weekly limits. The app compares fresh provider observations. An advanced reset boundary after the old boundary has passed confirms rollover, even if the new window is already partly used. A return to an idle zero-use window without a reset date can also confirm rollover. An early reset requires zero use and an advanced boundary. The app never spends a reset credit.

Reset messages for multiple windows on the same provider are grouped when detected together. Clicking a notification opens the menu-bar view. A first reading establishes a baseline without a historical reset alert. Notification history and baselines survive restart; delayed, unsent events expire to avoid stale alerts.

Small changes in a provider's reported reset timestamp are treated as jitter within the same window, so they do not duplicate low-allowance alerts or imply a new reset.

Detection happens at the next successful refresh while the Mac is awake: at the **Refresh While Visible** interval (default three minutes) while the panel or menu-bar view is on screen, and at the **Refresh While Hidden** interval (default ten minutes) while the panel is hidden or fully covered by other windows. Opening the menu-bar view catches up immediately; uncovering the panel catches up within a minute. The app also schedules a check near known reset times, subject to provider throttling. No alerts can be detected while the app is quit, the Mac is asleep, or providers are inaccessible. A wake triggers a refresh. The app does not keep the Mac awake.

## Usage connections and privacy

- **Codex:** invokes the installed native CLI's documented `account/rateLimits/read` over stdio, after the initialization handshake, once per tracked ChatGPT account. The CLI owns sign-in and authentication. No model requests, conversation reads, reset-credit redemption, or listening network port are used. The short-lived process exits after the reading, with a bounded timeout. Homebrew/npm and common native CLI locations are supported. A CLI account can differ from the account selected in the Codex desktop app.
- **Claude:** reads the existing `Claude Code-credentials` item from the macOS Keychain through `/usr/bin/security`, the same way Claude Code reads it. Claude Code creates the item with that tool, so the read is silent and keeps working after Claude Code renews its sign-in. If that read fails, the app queries the Keychain directly without a prompt; **Connect Claude…** allows macOS to request access. The credentials-file fallback is also supported, including `CLAUDE_CONFIG_DIR`; custom-directory Keychain identities are not currently queried. The token is kept in memory only, until it nears expiry or Claude rejects it; it is never logged or written to the app's files.
- **Claude sign-in renewal:** Claude Code's access token lasts about eight hours, and Claude Code renews it only while it runs. With **Renew Claude Sign-In Automatically** (in **⋯**, on by default), the widget renews a lapsed sign-in the way Claude Code does: it holds Claude Code's own refresh lock (`~/.claude/.oauth_refresh.lock` and `~/.claude.lock`), re-reads the Keychain item, and renews only if the token is still expired, then saves the renewed tokens back into `Claude Code-credentials` through `/usr/bin/security`. Claude Code then picks up the renewed sign-in instead of renewing it a second time; two simultaneous renewals would spend the same single-use refresh token and sign you out. Renewal applies only to the default Keychain item, never to `CLAUDE_CONFIG_DIR` or the credentials file, and `--check` never renews. If Claude refuses the renewal, the panel asks you to sign in to Claude Code again. Turn the setting off to leave renewal to Claude Code; then open Claude Code to renew an expired sign-in.
- Claude's account-usage endpoint is not a public, stable API and can change. The app honors HTTP 429 and `Retry-After`, including date-form headers. It does not install or change Claude status-line configuration.

Usage snapshots and alert history are stored in `~/Library/Application Support/AIUsageWidget/usage.json`. The directory is private to the user. Preferences use the `com.mariomanzocco.aiusagewidget` defaults domain. No conversation content or authentication tokens are stored there.

Menu-bar marks use [Simple Icons](https://simpleicons.org) path data (CC0); the logos are trademarks of Anthropic and OpenAI.

Interface documentation: [Codex app server](https://learn.chatgpt.com/docs/app-server), [Claude credential storage](https://code.claude.com/docs/en/authentication).

## Performance

The app links only Apple frameworks and the system Swift runtime. There is no Electron, web view, Node service, polling shell, or persistent Codex helper. Network work runs off the main thread. Idle display updates happen once per minute and stop when both views are hidden. Refreshes slow to the hidden interval (default ten minutes) while nothing is watched (the panel counts as covered when one window above it contains it entirely; this check reads window bounds only), and the Claude sign-in is read from the Keychain only when needed, not on every refresh. The menu-bar image is redrawn only when a reading changes. Refresh timers use tolerance for macOS timer coalescing and pause on screen/system sleep. The interface uses solid fills, without continuous animations or blur effects. Launch at login is optional.

## Build and verify

```sh
swift test --package-path macOS --jobs 4
./macOS/scripts/build-app.sh
"$HOME/Applications/AI Usage Widget.app/Contents/MacOS/AIUsageWidget" --check
```

`--check` prints only connection status, window counts, and the email of each tracked ChatGPT account. It does not start the widget, request Keychain interaction, renew the Claude sign-in, or send notifications. A failed provider gives exit code 1.

With the installed app running, this requests a small runtime report:

```sh
"$HOME/Applications/AI Usage Widget.app/Contents/MacOS/AIUsageWidget" --diagnostics
cat "$HOME/Library/Application Support/AIUsageWidget/diagnostics.json"
```

`diagnostics.json` includes `claudeRenewals`, the number of sign-in renewals since launch.

Core tests cover Claude sign-in renewal requests, token merging, and Claude Code's refresh lock, ChatGPT account discovery, deduplication, and per-account alerts, as well as parsing, missing/zero values, dynamic durations, multiple quota buckets, strict alert thresholds, persistent deduplication, reset confirmation, stale/out-of-order observations, and permission-delay recovery. Set `AIW_KEYCHAIN_TESTS=1` to add a Keychain write/read round trip on a scratch item (never Claude Code's). Live UI, credentials, sign-in renewal against Claude, and macOS notification delivery are separate runtime checks.

## Remove

Disable **Launch at Login**, quit from the menu bar, and remove `~/Applications/AI Usage Widget.app`. If desired, separately remove `~/Library/Application Support/AIUsageWidget` and the app's preferences. Claude and Codex sign-ins are unaffected.
