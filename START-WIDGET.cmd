@echo off
if not exist "%~dp0app\AIUsageWidget.exe" (
    dotnet publish "%~dp0src\UsageWidget\UsageWidget.csproj" -c Release -o "%~dp0app"
    if errorlevel 1 (
        pause
        exit /b 1
    )
)
start "" "%~dp0app\AIUsageWidget.exe"
