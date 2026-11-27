# AI Usage Widget for Windows

A small desktop widget and taskbar strip that shows how much of your Claude, ChatGPT/Codex and Antigravity (Google Gemini) subscription allowance is left, and when each limit resets.

## Install and run

Requirements:

- Windows 10 or 11 on x64. ARM64 builds (`win-arm64`) are expected to work but haven't been tested.
- For ChatGPT readings: the Codex CLI, installed and signed in with a ChatGPT subscription.
- For Claude readings: Claude Code, signed in with a Claude Pro or Max subscription.
- Optional: the Antigravity CLI, signed in with a Google AI plan (see [Antigravity](#antigravity)).
- Optional: Node.js and Claude Code 2.1.251 or newer for the Claude status-line feed.

**Release zip.** Release zips are self-contained, so no .NET install is needed. Download `AIUsageWidget-windows-x64.zip` (or `AIUsageWidget-windows-arm64.zip` for ARM PCs) from the [latest release](https://github.com/Marioarttv/AIUsageWidget/releases/latest) and extract it to a folder of your own, for example `%LOCALAPPDATA%\Programs`. The zip contains an `AI Usage Widget` folder; run `AIUsageWidget.exe` in it. The app isn't code-signed, so SmartScreen may ask first: choose **More info → Run anyway**. Keep the files together in that folder. The widget installs nothing anywhere else, apart from its data folder (see [Privacy](#usage-connections-and-privacy)).

**From source.** You need the .NET 10 SDK. Double-click `START-WIDGET.cmd` in the repository. If the app hasn't been built yet, it is published into the ignored `app/` folder first; this framework-dependent build needs the .NET 10 Windows Desktop runtime. To build a self-contained copy like the release zips:

```powershell
dotnet publish src/UsageWidget/UsageWidget.csproj -c Release -r win-x64 --self-contained true -o dist/win-x64
```

Only one widget runs at a time. Starting it again brings the running widget forward. If you can't find the widget, `SHOW-WIDGET.cmd` (in the app folder, or at the repository root) brings it forward and centres it on the primary monitor. New installations start in always-on-top mode, so the widget is easy to find.

## Display and controls

- **Drag the AI USAGE title** to move the widget. Its position is saved, and it is kept on its display when it grows or a monitor is disconnected.
- **The pin** switches between **Always on top** and **On desktop**. On desktop keeps the widget below every other window, even when clicked, while keeping its taskbar button. Show desktop (Win+D) hides it with the other windows. Your wallpaper isn't changed.
- **↻** refreshes everything. Readings also refresh every 3 minutes. Manual refreshes still respect each provider's minimum interval and rate-limit backoff.
- **Each subscription has a card.** The shortest window, normally the 5-hour session, is shown large. Longer windows such as weekly limits come below it, one line each, with a bar, the percentage left and a short reset countdown. Hover over a countdown or a row for the exact local reset time. When the cards are taller than the display, they scroll with the mouse wheel; a fade at the bottom shows that more follows.
- **Used-up weekly limits:** once a weekly limit runs out, the shorter limits it covers show 0% as well, in orange, with “Weekly used up · 4d 5h” (when it resets). Hover for the exact time and for what is left of the window itself. A limit only covers its own quota: a Claude model's weekly limit doesn't zero the session, but a spent Claude week zeroes everything. Each Codex limit bucket covers its own windows, and so does each Antigravity model group.
- **Renewal dates** sit at the foot of each card, beside “Updated …”. Click the date to open a small calendar, in a card that floats under the date, so the widget doesn't grow. Under Claude and Antigravity, whose sign-ins don't report billing, the date reads **Renewal date**. Pick the day, choose *Monthly*, *Yearly* or *Ends* (for a cancelled plan), and click **Save**. Escape, **Cancel** or a click elsewhere closes the calendar without saving. The card then shows “Renews 26 Nov” or “Ends 26 Nov”. A renewing date moves on to the next month or year by itself, and a dot in the calendar marks the next renewal. **Remove** hides the date. Dates are kept only in the widget's settings.
- **⋯** (also the tray icon's and the taskbar strip's right-click menu) holds Always on top / On desktop, **Start with Windows**, **Low 5-hour limit alerts**, **Reset notifications**, **Show taskbar usage strip**, **Theme**, **ChatGPT accounts**, **Show Antigravity**, Refresh now, Open Claude usage, Open usage data folder, Hide widget and Quit.
- **Theme:** *Dark* is the default. *Light* and *Match Windows* (which follows the Windows app colour mode) are in **⋯ → Theme**.
- Missing readings show a dash. Expired windows show “awaiting update” and wait for a new reading; the widget never assumes the allowance has been refilled.

### Taskbar usage strip

A compact strip just left of the primary taskbar's notification area shows the Claude and ChatGPT marks, plus the Gemini mark when Antigravity is shown, each with the **percentage remaining** in its shortest window (usually five hours). The ChatGPT mark stands for the Codex subscription allowance. With several ChatGPT accounts tracked, it shows one number: the average of what each account has left. That equals the share of the combined allowance left when the plans are the same size. An account whose weekly limit is used up counts as 0%. Hover over the ChatGPT mark for each account's own number.

- The strip is a small app-owned overlay, enabled by default. It follows the taskbar's position and display scale, and hides during fullscreen apps and with an auto-hidden taskbar. If the space beside the tray is taken, or its layout can't be checked, the strip moves just outside the taskbar. It doesn't reserve or change Explorer's taskbar space. With Antigravity shown it is a little wider.
- Hover over a mark for the window and reset countdown. Click it to bring the widget forward; right-click it for the menu. Readings keep updating while the widget is hidden. **Show taskbar usage strip** hides or restores it.
- One ordinary app icon stays in the tray for notifications and the menu.
- Its colours follow the taskbar's light or dark theme and Windows high contrast. The marks are the same Simple Icons vector marks as on the Mac menu bar.

## Multiple ChatGPT accounts

The widget shows one Codex card per tracked ChatGPT account, labelled with the account's email and plan. At the foot of each card, “Renews or ends 17 Nov” shows when the current paid period ends, as ChatGPT last recorded it in that sign-in; hover for the exact time. Once that date has passed without a recorded renewal, it reads “Paid until 28 Oct”, in orange. The sign-in doesn't say whether the plan was cancelled, so the date is when it either renews or ends. It updates when Codex renews the sign-in. Click it to say whether it renews or ends, for example after cancelling; **Use ChatGPT's Date** in the calendar goes back to the reported date. Each account has its own readings, low-allowance alerts and reset notifications. When more than one account is tracked, notification titles include the email.

- **Main sign-in:** the Codex CLI's own sign-in (`CODEX_HOME`, or `%USERPROFILE%\.codex`). It is always shown.
- **Codex Subscription Router:** every signed-in account in `%USERPROFILE%\.codex-mux\state.json` is found automatically, including accounts disabled for routing. Router slots without a sign-in are skipped, and an account that matches one already listed appears once. Paid accounts are shown by default; free-plan accounts are listed but stay off until you turn them on.
- **Codex sign-in folders:** **⋯ → ChatGPT accounts → Add Codex sign-in folder…** adds any Codex home that contains `auth.json`. To make one, sign the second account in with its own folder:

  ```powershell
  $env:CODEX_HOME = "$env:USERPROFILE\.codex-second"; codex login
  ```

  Then add `%USERPROFILE%\.codex-second`. Turning an added folder off removes it.

Use the checkmarks in **⋯ → ChatGPT accounts** to show or hide each account. New router sign-ins are picked up at the next refresh.

Each account is read by starting the Codex CLI with `CODEX_HOME` set to that account's folder, exactly as for the main sign-in. The CLIs run one at a time, so several accounts never start several Codex processes at once. To list accounts, the widget uses only the email, plan, account ID and paid-period end from each `auth.json`. It never stores or logs tokens. Account IDs, emails and added folder paths are kept in its settings and alert history. ChatGPT Team members share their workspace's account ID, so an account is identified by its ID and email together. The CLI may renew an account's sign-in while reading it, as usual.

## Antigravity

The widget shows an Antigravity card, and the strip a Gemini mark, once the Antigravity CLI is found. The large number is the Gemini 5-hour limit. Below it come the Gemini weekly limit, the *Claude and GPT* group (5-hour and weekly on one row, with a bar for the lower of the two), and any further limit, such as *Gemini 4 Argon · Weekly*. Within each group, models share a weekly and a 5-hour limit. Click **Renewal date** to enter when your Google AI plan renews; Antigravity doesn't report it. Turn the card off with **⋯ → Show Antigravity**.

- **Where the CLI is found:** `antigravity.exe` or `agy.exe` on PATH or in `%USERPROFILE%\.local\bin`. After those comes the copy T3 Code installs for its Antigravity sessions: `%USERPROFILE%\.t3\tools\antigravity-acp\win32-x64` (or `win32-arm64`), the release named in `active.json`, once its install has finished. Only a native Windows executable qualifies. The Antigravity editor's `Antigravity.exe` and launcher scripts don't.
- **How it is read:** at each refresh the widget runs `agy -p /usage --output-format json` once. The command runs no model turn. The CLI runs in an empty private folder (`%LOCALAPPDATA%\AIUsageWidget\antigravity-workspace`), with no update check and no log file, and is stopped after 35 seconds at most. The CLI owns the Google sign-in and renews it as needed. The widget never reads that sign-in, and doesn't check where it is stored.
- **No browser from a background refresh:** signed out, the CLI would open a browser to sign in. The widget is designed to prevent that in three ways. It starts the CLI with a minimal environment whose PATH is an empty folder, so the CLI can't find `rundll32` to open a browser. Right after start, the CLI goes into a Windows job object that allows it no other process and ends it when the widget is done with it. And the widget stops the CLI as soon as it asks for a login. This was tested with Antigravity CLI 1.5.18, signed out: the prompt was recognised in under a second, and no other process ran. The card then says *Sign-in expired · Open Antigravity in T3 Code* (or *Run agy in a terminal*). Signing in there fixes it. The widget doesn't start the CLI again for half an hour, unless you press ↻.
- Behind a proxy, set `HTTPS_PROXY` (and `NO_PROXY`) for your user, since the CLI doesn't read the Windows proxy settings. The widget passes these on.

## Notifications

Notifications appear as Windows notifications from the tray icon; click one to bring the widget forward. Both kinds are on by default:

1. **Low 5-hour limit alerts:** once per 5-hour window when less than 10% remains. Not while a used-up weekly limit already holds that window at 0%. Already-low windows warn on first connection; later refreshes and restarts don't repeat it for the same window.
2. **Reset notifications:** for every tracked window, including weekly and model-specific limits, once a fresh reading confirms the reset. A countdown reaching zero is never enough. Windows that weren't used before the reset are skipped, as are windows under a used-up weekly limit, which stay at 0% after their own reset. Reset history survives restarts, a first reading only sets a baseline, and simultaneous notifications wait their turn. Queued reset alerts expire after 30 minutes. Turning the setting off cancels pending reset alerts.

Resets are detected at the next successful refresh, normally within three minutes, even while the widget is hidden. Sleep, offline access, provider backoff and Windows notification settings (Focus, Do not disturb) can delay or suppress alerts.

## Claude status-line feed (optional)

Claude readings work as soon as Claude Code is signed in. Optionally, run `CONNECT-CLAUDE.cmd` from the app folder to also receive a reading after every Claude Code response. Setup saves the previous Claude status line and keeps all other settings. Run `DISCONNECT-CLAUDE.cmd` to restore the old status line. If the app folder moves, connect again from its new location. These scripts use the Node.js already installed on the PC.

The feed uses the documented status-line `rate_limits` fields, which Claude Code provides only in interactive sessions after a response. When a status-line reading is newer than the last account reading, its values replace the matching windows. The wrapper runs the previous status-line command in the same shell Claude Code uses (Git Bash when installed). If that command fails, it shows a simple model/context status line.

## Usage connections and privacy

- **Codex:** the local CLI's documented [`account/rateLimits/read`](https://learn.chatgpt.com/docs/app-server) app-server method, over stdio, once per tracked account. It makes no model requests and has no listening port. Window labels follow the account's actual durations, and multiple quota buckets are supported.
- **Claude:** the account usage endpoint behind Claude Code's `/usage` view (`api.anthropic.com/api/oauth/usage`). The endpoint isn't publicly documented, so it may change. The widget reads the existing Claude Code sign-in from `%USERPROFILE%\.claude\.credentials.json` (or `CLAUDE_CONFIG_DIR`) for each request. It never stores or refreshes the token. Readings cover the whole account, including claude.ai, Claude Code and SDK-based tools, and include model-specific weekly limits. If the sign-in has expired, open Claude Code once to renew it. The widget honours HTTP 429 and `Retry-After`.
- **Antigravity:** the CLI's own `/usage` command, as described under [Antigravity](#antigravity).
- **Packaged parents:** programs started by a packaged app, such as the Codex desktop app, have their `%LOCALAPPDATA%` and registry writes redirected into that app's private storage. When the widget detects this, it restarts itself through Explorer. That way its settings, the Claude status-line feed and **Start with Windows** use the normal locations.

Snapshots, settings (including renewal dates and account choices), alert history, a short log and the original Claude configuration backup are stored in `%LOCALAPPDATA%\AIUsageWidget`. Nothing is written beside the app. No conversation content or authentication tokens are stored.

The marks use [Simple Icons](https://simpleicons.org) path data (CC0); the logos are trademarks of Anthropic, OpenAI and Google. Simple Icons has no Antigravity mark, so Antigravity uses the Google Gemini mark. The app icon is generated artwork in `assets/widget.ico`. To recreate the ICO from its PNG master, run `python scripts/create-icon.py` with Pillow installed. Python is only needed for asset packaging.

## Resource use

The widget is native WPF with no browser runtime or background service. It uses [memory-conserving garbage collection](https://learn.microsoft.com/dotnet/core/runtime-config/garbage-collector#conserve-memory) and turns off [tiered recompilation](https://learn.microsoft.com/dotnet/core/runtime-config/compilation#tiered-compilation). It renders in software and trims its working set after each refresh. Hidden or minimised cards skip layout work; readings, the strip and notifications carry on. The Codex and Antigravity CLIs run only for a reading, one at a time. Taskbar button geometry is cached and refreshed when Explorer reports a layout change, with a 30-second fallback check (three seconds if the event hook or geometry is unavailable).

To measure a running build over five minutes, including an automatic refresh:

```powershell
./scripts/measure-memory.ps1 > app/memory.json
```

The report separates private working memory, total working memory (including shared runtime pages), and private committed memory. Compare the same display mode and activity over the same interval. An earlier comparison on one machine (against `c47051e`, 37 samples over about 197 seconds) measured average private working memory falling from 26.37 to 8.05 MiB. It was measured before multiple accounts and Antigravity were added; each extra card adds a few hundred UI elements.

## Build and verify

```powershell
dotnet publish src/UsageWidget/UsageWidget.csproj -c Release -o app
dotnet run --project tests/LogicTests/LogicTests.csproj -c Release
dotnet run --project tests/CoreTests/CoreTests.csproj -c Release
node --test tests/bridge.test.cjs tests/setup.test.cjs
```

- **LogicTests** builds the platform-neutral code for plain .NET, so it also runs on macOS and Linux. It covers quota pools and spent-weekly zeroing, Antigravity parsing, CLI discovery and its browser-proof environment, and ChatGPT account discovery, deduplication and choices. It also covers renewal-date rollover, the calendar grid, merged strip readings and reset alerts.
- **CoreTests** (Windows) runs those checks too, plus usage parsing, the cards and calendar, tray lifecycle, the strip's layout, placement and popup stacking, and Explorer layout events. The shell checks need an interactive desktop. Add `-- --tray-preview` to save tray and strip previews beside the test executable, or `-- --inspect-taskbar` to print the taskbar geometry and the strip's position.
- **Screenshots:** `AIUsageWidget.exe --render-demo <folder>` renders the widget, its calendar and the strip from built-in sample data, in dark and light, as PNGs. It doesn't touch your data, the network or any sign-in.
- **Live check:** `--check` refreshes every tracked service without opening the widget. It writes `check-result.json`, with Claude, each ChatGPT account (email, plan, window count, error) and Antigravity (found, path, managed by T3 Code, windows, error), and exits with code 1 if any service failed:

  ```powershell
  $p = Start-Process app/AIUsageWidget.exe -ArgumentList '--check' -Wait -PassThru; $p.ExitCode
  Get-Content "$env:LOCALAPPDATA/AIUsageWidget/check-result.json"
  ```

The `Windows` GitHub Actions workflow runs the tests on `windows-latest` for every pull request and push to `main`. It also builds the self-contained `win-x64` release layout, renders the screenshots from it (artifact `windows-screenshots`), and runs `--check` on a runner without sign-ins, which covers only the failure path. A separate, advisory job (it can't fail the build) downloads the current Antigravity CLI and runs it, signed out, through the widget's reader. It checks that the reader stops the CLI at the login prompt and that no other process starts.

## Remove

Turn off **Start with Windows**, choose **Quit**, and run `DISCONNECT-CLAUDE.cmd` if you connected the status-line feed. Then delete the app folder. If you also want the readings, settings and configuration backups gone, delete `%LOCALAPPDATA%\AIUsageWidget`. Claude, Codex and Antigravity sign-ins are unaffected.

## Data interface documentation

- [Codex app server](https://learn.chatgpt.com/docs/app-server)
- [Claude Code status line](https://code.claude.com/docs/en/statusline)
