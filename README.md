# AI Usage Widget

A Windows desktop widget for Claude and Codex subscription allowances and reset times. It shows remaining allowance, saves its position, and supports always-on-top and desktop views.

<img src="assets/widget.png" alt="AI Usage Widget icon" width="128" />

## Run

Double-click `START-WIDGET.cmd`. If the app has not been built yet, the launcher publishes it into the ignored `app/` folder before starting it. Double-click `SHOW-WIDGET.cmd` to bring an existing widget forward and recenter it on the primary monitor.

Requirements:

- Windows with the .NET 10 Windows Desktop runtime; the .NET 10 SDK is needed to build from source.
- An installed and signed-in Codex CLI for Codex subscription readings.
- Claude Code signed in with a Claude Pro or Max subscription for Claude readings.
- Optional: Node.js and Claude Code 2.1.251 or newer for the status-line feed.

Claude readings work as soon as Claude Code is signed in. Optionally, run `app/CONNECT-CLAUDE.cmd` after building to also receive a reading after every Claude Code response. Setup saves the previous Claude status line and preserves all other settings. Run `app/DISCONNECT-CLAUDE.cmd` to restore it. Reconnect if the app folder is moved.

## Controls

- Drag the title to reposition the widget.
- Use the pin to switch between **Always on top** and **On desktop**. On desktop keeps the widget below every other window, even when clicked, while keeping its taskbar button.
- Click **↻** to refresh both services. Codex refreshes every 90 seconds and Claude every 3 minutes.
- Hover over a countdown or limit row for the exact local reset date and time.
- Open **⋯** for **Start with Windows**, **Low 5-hour limit alerts**, **Hide**, and **Quit**.
- When a 5-hour limit drops below 10% remaining, a Windows notification appears once for that window. Click it to bring the widget forward. Alerts are on by default.
- Enable **Start with Windows** once to launch the app when signing in after a shutdown or restart.
- Double-click the tray icon to bring the widget forward.

New installations start in always-on-top mode so the widget is easy to find. Use the taskbar button, the tray icon, or `SHOW-WIDGET.cmd` to bring it forward from desktop mode.

## How readings work

Bars show **remaining** subscription allowance. The shortest window, normally the 5-hour session, is shown large for each service, with longer windows such as weekly limits listed below it. Missing readings show a dash. Expired quota windows wait for a new reading rather than assuming the allowance has replenished.

Codex uses the local CLI's documented `account/rateLimits/read` app-server method. Window labels follow the account's actual duration, and multiple quota buckets are supported.

Claude uses the account usage endpoint behind Claude Code's `/usage` view (`api.anthropic.com/api/oauth/usage`). The endpoint is not publicly documented, so it may change. The widget reads the existing Claude Code sign-in from `~/.claude/.credentials.json` (or `CLAUDE_CONFIG_DIR`) for each request. It never stores or refreshes the token. Readings cover the whole account, including claude.ai, Claude Code, and SDK-based tools, and include model-specific weekly limits when the account has them. If the sign-in has expired, open Claude Code once to renew it.

If the status-line feed is connected, it uses the documented status-line `rate_limits` fields. Claude Code only provides these in interactive sessions after a response. When a status-line reading is newer than the last account reading, its values replace the matching windows. The wrapper runs the previous status-line command in the same shell Claude Code uses (Git Bash when installed). If that command fails, the wrapper displays a simple model/context status line. The widget does not generate model requests or store conversations or authentication tokens.

Programs started by a packaged app, such as the Codex desktop app, have their `%LOCALAPPDATA%` and registry writes redirected into that app's private storage. When the widget detects this, it restarts itself through Explorer so that its settings, the Claude status-line feed, and **Start with Windows** use the normal locations.

Local snapshots, settings, a short log, and the original Claude configuration backup are stored under `%LOCALAPPDATA%\AIUsageWidget`, outside the repository.

The app, taskbar, and tray share the generated artwork in `assets/widget.ico`. The PNG master and generation prompt are kept in `assets/`. The Windows icon contains multiple sizes for different display scales.

To recreate the ICO from the generated PNG master, run `python scripts/create-icon.py` with Pillow installed. Python is only needed for asset packaging, not to build or run the widget.

## Build and check

```powershell
dotnet publish src/UsageWidget/UsageWidget.csproj -c Release -o app
node --test tests/bridge.test.cjs tests/setup.test.cjs
dotnet run --project tests/CoreTests/CoreTests.csproj -c Release
```

The publish command includes the Claude bridge, setup scripts, launchers, and user guide. Build output and local usage data are not committed.

To run a live Codex and Claude connection check without opening the widget:

```powershell
Start-Process app/AIUsageWidget.exe -ArgumentList '--check' -Wait
Get-Content "$env:LOCALAPPDATA/AIUsageWidget/check-result.json"
```

## Remove

Disable **Start with Windows**, choose **Quit**, and run `app/DISCONNECT-CLAUDE.cmd`. Local readings and configuration backups can be removed separately when no longer needed.

## Data interface documentation

- [Codex app server](https://learn.chatgpt.com/docs/app-server)
- [Claude Code status line](https://code.claude.com/docs/en/statusline)
