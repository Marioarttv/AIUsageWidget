# Contributing

Thanks for helping. Bug reports, fixes and small features are all welcome.

## Before you start

- For anything larger than a fix, open an issue first so we can agree on the approach.
- The two apps are separate native code bases: **macOS** in `macOS/` (Swift, SwiftUI and AppKit) and **Windows** in `src/UsageWidget/` (C#, WPF on .NET 10). A feature usually belongs in both; it's fine to send one platform and say so in the pull request.
- Keep the apps light: no Electron, web views, background services or third-party runtime dependencies.

## Build and test

macOS (Xcode 15 or newer):

```sh
swift test --package-path macOS
./macOS/scripts/install.sh            # builds, signs locally and opens the app
```

Windows (.NET 10 SDK, Node.js for the bridge tests):

```powershell
dotnet publish src/UsageWidget/UsageWidget.csproj -c Release -o app
dotnet run --project tests/CoreTests/CoreTests.csproj -c Release
node --test tests/bridge.test.cjs tests/setup.test.cjs
```

On a Mac or Linux, `dotnet run --project tests/LogicTests/LogicTests.csproj -c Release` runs the Windows app's platform-neutral logic tests, and `dotnet build tests/CoreTests/CoreTests.csproj -p:EnableWindowsTargeting=true` compiles (but can't run) the WPF app. Continuous integration runs the tests on real macOS and Windows machines for every pull request.

Screenshots for the website come from the apps' demo renderers, which use made-up sample data: `AIUsageWidget --render-demo <folder>` (run the binary inside the built `.app` on macOS, or `AIUsageWidget.exe` on Windows).

## Privacy rules

These apps read sign-ins that grant access to paid accounts. Pull requests must keep to the existing rules:

- Never log, store, upload or display access tokens. Read only what a reading needs.
- No telemetry and no network requests other than the providers' own usage endpoints and CLIs.
- Never commit real emails, tokens or personal paths, including in tests and screenshots. Use `example.com` addresses.

## Pull requests

- One topic per pull request, with tests for new logic.
- Describe what you checked and on which OS.
- By contributing you agree that your work is released under the [MIT License](LICENSE).
