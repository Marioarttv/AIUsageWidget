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
- **The Claude and ChatGPT marks in the menu bar** show Claude / Codex remaining allowance for the shortest reported window. Click for the complete widget; right-click for quick show/hide, pin, and quit commands. Unknown, expired, or failed readings show a dash in the menu bar.
- **⋯** contains show/hide, reposition, launch at login, both alert toggles, a test notification, notification settings, and Claude connection access.
- **↻** refreshes manually. Requests still respect the provider's minimum interval and rate-limit backoff.
- Hover over a reset time for the exact local date and time. Expired windows show “awaiting update” instead of inventing a full allowance. Failed readings retain visibly dimmed last-known data with an explanation.

## Notifications

Allow notifications on first launch. Use **Send Test Notification** to verify the delivery path. If permission was denied, enable the app in **System Settings → Notifications**. Focus, notification style, and screen-sharing settings can affect banners and sound.

The two toggles are independent and enabled by default:

1. **Below 10%:** only windows whose reported duration is five hours qualify. Exactly 10% remaining does not trigger a warning. Already-low windows warn on first connection; subsequent polls and app restarts do not repeat that warning for the same window.
2. **Usage reset:** all tracked windows qualify, including weekly and model-specific weekly limits. The app compares fresh provider observations. An advanced reset boundary after the old boundary has passed confirms rollover, even if the new window is already partly used. A return to an idle zero-use window without a reset date can also confirm rollover. An early reset requires zero use and an advanced boundary. The app never spends a reset credit.

Reset messages for multiple windows on the same provider are grouped when detected together. Clicking a notification opens the menu-bar view. A first reading establishes a baseline without a historical reset alert. Notification history and baselines survive restart; delayed, unsent events expire to avoid stale alerts.

Small changes in a provider's reported reset timestamp are treated as jitter within the same window, so they do not duplicate low-allowance alerts or imply a new reset.

Detection happens at the next successful refresh, normally within roughly three minutes while the Mac is awake. The app also schedules a check near known reset times, subject to provider throttling. No alerts can be detected while the app is quit, the Mac is asleep, or providers are inaccessible. A wake triggers a refresh. The app does not keep the Mac awake.

## Usage connections and privacy

- **Codex:** invokes the installed native CLI's documented `account/rateLimits/read` over stdio, after the initialization handshake. The CLI owns sign-in and authentication. No model requests, conversation reads, reset-credit redemption, or listening network port are used. The short-lived process exits after the reading, with a bounded timeout. Homebrew/npm and common native CLI locations are supported. A CLI account can differ from the account selected in the Codex desktop app.
- **Claude:** reads the existing `Claude Code-credentials` item from the macOS Keychain through `/usr/bin/security`, the same way Claude Code reads it. Claude Code creates the item with that tool, so the read is silent and keeps working after Claude Code renews its sign-in. If that read fails, the app queries the Keychain directly without a prompt; **Connect Claude…** allows macOS to request access. The credentials-file fallback is also supported, including `CLAUDE_CONFIG_DIR`; custom-directory Keychain identities are not currently queried. Tokens stay in memory and are neither logged, cached, nor refreshed by the widget. Open Claude Code to renew an expired sign-in.
- Claude's account-usage endpoint is not a public, stable API and can change. The app honors HTTP 429 and `Retry-After`, including date-form headers. It does not install or change Claude status-line configuration.

Usage snapshots and alert history are stored in `~/Library/Application Support/AIUsageWidget/usage.json`. The directory is private to the user. Preferences use the `com.mariomanzocco.aiusagewidget` defaults domain. No conversation content or authentication tokens are stored there.

Menu-bar marks use [Simple Icons](https://simpleicons.org) path data (CC0); the logos are trademarks of Anthropic and OpenAI.

Interface documentation: [Codex app server](https://learn.chatgpt.com/docs/app-server), [Claude credential storage](https://code.claude.com/docs/en/authentication).

## Performance

The app links only Apple frameworks and the system Swift runtime. There is no Electron, web view, Node service, polling shell, or persistent Codex helper. Network work runs off the main thread. Idle display updates happen once per minute and stop when both views are hidden. Refresh timers use tolerance for macOS timer coalescing and pause on screen/system sleep. The interface uses solid fills, without continuous animations or blur effects. Launch at login is optional.

## Build and verify

```sh
swift test --package-path macOS --jobs 4
./macOS/scripts/build-app.sh
"$HOME/Applications/AI Usage Widget.app/Contents/MacOS/AIUsageWidget" --check
```

`--check` prints only connection status and window counts. It does not start the widget, request Keychain interaction, or send notifications. A failed provider gives exit code 1.

With the installed app running, this requests a small runtime report:

```sh
"$HOME/Applications/AI Usage Widget.app/Contents/MacOS/AIUsageWidget" --diagnostics
cat "$HOME/Library/Application Support/AIUsageWidget/diagnostics.json"
```

Core tests cover parsing, missing/zero values, dynamic durations, multiple quota buckets, strict alert thresholds, persistent deduplication, reset confirmation, stale/out-of-order observations, and permission-delay recovery. Live UI, credentials, and macOS notification delivery are separate runtime checks.

## Remove

Disable **Launch at Login**, quit from the menu bar, and remove `~/Applications/AI Usage Widget.app`. If desired, separately remove `~/Library/Application Support/AIUsageWidget` and the app's preferences. Claude and Codex sign-ins are unaffected.
