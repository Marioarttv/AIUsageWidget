# AI Usage Widget

A desktop widget for Claude and Codex subscription allowances and reset times, with separate native Windows and macOS apps. It shows remaining allowance, saves its position, and supports always-on-top and desktop views.

<img src="assets/widget.png" alt="AI Usage Widget icon" width="128" />

## macOS

The Mac app uses SwiftUI and AppKit, with a desktop panel, an always-on-top toggle, and a menu-bar indicator. It needs macOS 13 or newer and an existing Codex CLI / Claude Code sign-in. Xcode is needed to build; the installed app has no third-party framework or browser runtime dependencies.

```sh
./macOS/scripts/install.sh
```

This builds, signs using an existing Apple Development identity when available, installs to `~/Applications/AI Usage Widget.app`, and opens the app. It starts in desktop mode. Drag its title to move it; click the pin to keep it above other windows. The menu bar shows the Claude and ChatGPT/Codex marks with each remaining allowance; click it for the complete widget and settings.

To track more than one ChatGPT subscription, sign the extra account in to [Codex Subscription Router](https://github.com/b-nnett/codex-subscription-router); the widget finds it automatically and shows each account with its email. Choose which accounts to show in **⋯ → ChatGPT Accounts**, or add another Codex sign-in folder there. See [the Mac guide](docs/MACOS.md#multiple-chatgpt-accounts).

Allow notifications when macOS asks. Claude connects automatically when Claude Code is signed in; if it still needs Keychain permission, choose **⋯ → Connect Claude…**. Enable **Launch at Login** in that menu if desired.

Both notification types are on by default:

- A warning once per five-hour window when **less than 10% remains**.
- A notification after a fresh provider reading **confirms a usage reset**, including weekly windows. Reaching a countdown alone does not imply a reset.

The app refreshes every three minutes by default (choose **⋯ → Refresh While Visible / Refresh While Hidden** to change it), respects provider backoff, and pauses timers during sleep. Countdown text updates once a minute while a panel is visible. Notifications are detected on refresh and can be delayed by sleep, offline access, provider limits, or macOS notification settings.

See [the Mac guide](docs/MACOS.md) for build checks, credentials, performance, and removal.

## Windows: run

Double-click `START-WIDGET.cmd`. If the app has not been built yet, the launcher publishes it into the ignored `app/` folder before starting it. Double-click `SHOW-WIDGET.cmd` to bring an existing widget forward and recenter it on the primary monitor.

Requirements:

- Windows with the .NET 10 Windows Desktop runtime; the .NET 10 SDK is needed to build from source.
- An installed and signed-in Codex CLI for Codex subscription readings.
- Claude Code signed in with a Claude Pro or Max subscription for Claude readings.
- Optional: Node.js and Claude Code 2.1.251 or newer for the status-line feed.

Claude readings work as soon as Claude Code is signed in. Optionally, run `app/CONNECT-CLAUDE.cmd` after building to also receive a reading after every Claude Code response. Setup saves the previous Claude status line and preserves all other settings. Run `app/DISCONNECT-CLAUDE.cmd` to restore it. Reconnect if the app folder is moved.

## Windows: controls

- Drag the title to reposition the widget.
- Use the pin to switch between **Always on top** and **On desktop**. On desktop keeps the widget below every other window, even when clicked, while keeping its taskbar button.
- Click **↻** to refresh both services. Both refresh automatically every 3 minutes.
- Hover over a countdown or limit row for the exact local reset date and time.
- Open **⋯** for **Start with Windows**, **Low 5-hour limit alerts**, **Reset notifications**, **Hide**, and **Quit**.
- When a 5-hour limit drops below 10% remaining, a Windows notification appears once for that window. Click it to bring the widget forward. Alerts are on by default.
- **Reset notifications** are also on by default for Claude and ChatGPT/Codex, including weekly and model-specific windows. A fresh reading must confirm the reset; countdown expiry alone never triggers one. Click a notification to open the widget. Reset history survives restarts, and simultaneous notifications wait their turn.
- Resets are detected on the next successful refresh (normally every three minutes), including while the widget is hidden. Sleep, offline access, provider backoff, and Windows notification settings can delay or suppress alerts. A first reading establishes the baseline. Queued reset alerts expire after 30 minutes, and turning the setting off cancels pending reset alerts.
- Enable **Start with Windows** once to launch the app when signing in after a shutdown or restart.
- A compact **taskbar usage strip** keeps the Claude and ChatGPT logos with both **remaining percentages** together, immediately left of the primary taskbar's notification area. Each reading uses its shortest quota window (usually five hours); the ChatGPT mark represents the Codex subscription allowance.
- The strip is a small app-owned overlay, enabled by default. It follows the taskbar's position and display scale, hides for fullscreen apps and an auto-hidden taskbar, and moves just outside the taskbar if space is occupied or its layout cannot be checked. It does not reserve or modify Explorer's taskbar space.
- Hover over either provider for the window and reset countdown. Click to bring the widget forward; right-click for settings. Readings keep updating while the desktop widget is hidden. Toggle **Show taskbar usage strip** in the menu to hide or restore it.
- One ordinary app icon remains in the tray for notifications and access to settings. The four separate logo/number entries are no longer used.

New installations start in always-on-top mode so the widget is easy to find. Use the taskbar button, the tray icon, or `SHOW-WIDGET.cmd` to bring it forward from desktop mode.

## Windows: how readings work

Bars show **remaining** subscription allowance. The shortest window, normally the 5-hour session, is shown large for each service, with longer windows such as weekly limits listed below it. Missing readings show a dash. Expired quota windows wait for a new reading rather than assuming the allowance has replenished.

Codex uses the local CLI's documented `account/rateLimits/read` app-server method. Window labels follow the account's actual duration, and multiple quota buckets are supported.

Claude uses the account usage endpoint behind Claude Code's `/usage` view (`api.anthropic.com/api/oauth/usage`). The endpoint is not publicly documented, so it may change. The widget reads the existing Claude Code sign-in from `~/.claude/.credentials.json` (or `CLAUDE_CONFIG_DIR`) for each request. It never stores or refreshes the token. Readings cover the whole account, including claude.ai, Claude Code, and SDK-based tools, and include model-specific weekly limits when the account has them. If the sign-in has expired, open Claude Code once to renew it.

If the status-line feed is connected, it uses the documented status-line `rate_limits` fields. Claude Code only provides these in interactive sessions after a response. When a status-line reading is newer than the last account reading, its values replace the matching windows. The wrapper runs the previous status-line command in the same shell Claude Code uses (Git Bash when installed). If that command fails, the wrapper displays a simple model/context status line. The widget does not generate model requests or store conversations or authentication tokens.

Programs started by a packaged app, such as the Codex desktop app, have their `%LOCALAPPDATA%` and registry writes redirected into that app's private storage. When the widget detects this, it restarts itself through Explorer so that its settings, the Claude status-line feed, and **Start with Windows** use the normal locations.

Local snapshots, settings, a short log, and the original Claude configuration backup are stored under `%LOCALAPPDATA%\AIUsageWidget`, outside the repository.

The app, taskbar button, and fallback tray icon use the generated artwork in `assets/widget.ico`. The PNG master and generation prompt are kept in `assets/`. The taskbar strip uses the same Claude and ChatGPT vector marks as the Mac menu bar. Its colors adapt to light/dark themes and Windows high contrast.

To recreate the ICO from the generated PNG master, run `python scripts/create-icon.py` with Pillow installed. Python is only needed for asset packaging, not to build or run the widget.

## Windows: build and check

```powershell
dotnet publish src/UsageWidget/UsageWidget.csproj -c Release -o app
node --test tests/bridge.test.cjs tests/setup.test.cjs
dotnet run --project tests/CoreTests/CoreTests.csproj -c Release
```

The publish command includes the Claude bridge, setup scripts, launchers, and user guide. Build output and local usage data are not committed.

Core checks include confirmed reset detection, persistence and deduplication, fresh Claude source timestamps, notification sequencing, native tray lifecycle, and the strip's readings, popup stacking, placement, and taskbar layout events. Shell checks require an interactive Windows desktop. Add `-- --tray-preview` to save light/dark previews beside the test executable, or `-- --inspect-taskbar` to inspect taskbar geometry and the calculated strip position.

To run a live Codex and Claude connection check without opening the widget:

```powershell
Start-Process app/AIUsageWidget.exe -ArgumentList '--check' -Wait
Get-Content "$env:LOCALAPPDATA/AIUsageWidget/check-result.json"
```

## Windows: resource use

Taskbar button geometry is cached and refreshed when Explorer reports a layout change, with a 30-second fallback check (three seconds if the event hook or geometry is unavailable). Accessibility queries retrieve cached rectangles without retaining full element references. Hidden or minimized desktop cards skip layout updates; readings, the taskbar strip, and notifications continue normally. The runtime uses [memory-conserving garbage collection](https://learn.microsoft.com/dotnet/core/runtime-config/garbage-collector#conserve-memory) and disables [tiered recompilation](https://learn.microsoft.com/dotnet/core/runtime-config/compilation#tiered-compilation) to limit background compilation and its memory overhead.

To measure a running build over five minutes, including an automatic provider refresh:

```powershell
./scripts/measure-memory.ps1 > app/memory.json
```

The report distinguishes private working memory (resident pages owned by the process), total working memory (including shared runtime pages), and private committed memory (including nonresident pages). Compare the same display mode and activity over the same interval. Startup, refreshes, and Windows paging change these numbers; an immediate drop after a restart is not evidence of a lasting saving. The sampler only reads counters and does not trim the process.

A local comparison against `c47051e` on Windows, with the desktop widget and strip enabled, sampled each build 37 times over about 197 seconds after startup, including the next scheduled refresh. Average private working memory decreased from 26.37 to 8.05 MiB (peak 33.77 to 11.46 MiB), and average private committed memory from 63.41 to 46.19 MiB. Widget process CPU time during those intervals decreased from 875 to 141 ms. These are measurements from one machine and short run, not a memory cap or a long-duration guarantee; both builds used the same existing post-refresh memory trim.

## Windows: remove

Disable **Start with Windows**, choose **Quit**, and run `app/DISCONNECT-CLAUDE.cmd`. Local readings and configuration backups can be removed separately when no longer needed.

## Data interface documentation

- [Codex app server](https://learn.chatgpt.com/docs/app-server)
- [Claude Code status line](https://code.claude.com/docs/en/statusline)
