@echo off
if not exist "%~dp0app\AIUsageWidget.exe" (
    call "%~dp0START-WIDGET.cmd"
    exit /b
)
start "" "%~dp0app\AIUsageWidget.exe" --show
