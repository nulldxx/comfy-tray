# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

A Windows system tray application built with .NET 10 / WPF using the [H.NotifyIcon.Wpf](https://github.com/HardcodetNet/H.NotifyIcon) library. It runs a **ComfyUI server** headless in the background. The tray icon is **red when stopped (the default) and green when running**. The context menu can Start/Stop ComfyUI, open a live **Logs** window, show About, and Exit. There is no visible main window, and the ComfyUI server is launched with no console window. It includes a WiX v3 MSI installer and a GitHub Actions release pipeline.

### ComfyUI launch

The server is started as `python main.py ... --listen 0.0.0.0 ...` with `CreateNoWindow=true` and stdout/stderr redirected into a ring buffer for the Logs window. The full command (interpreter path, directories, host/port, flags) is defined by `ComfyConfig`, whose defaults mirror the ComfyUI Desktop install. On first run defaults are written to `%APPDATA%\ComfyTray\config.json`, which the user can edit (paths support `%VAR%` environment tokens).

Key source files:
- `ComfyConfig.cs` — typed launch config + JSON load/save + argument building.
- `ComfyServerManager.cs` — process lifecycle (start/stop entire tree), state, log ring buffer.
- `MainWindow.xaml(.cs)` — tray icon, context menu, red/green state.
- `LogWindow.xaml(.cs)` — live log viewer.
- `icons/red.ico`, `icons/green.ico` — embedded as WPF resources (not shipped as separate files).

## Build & Test Commands

```bash
# Build
dotnet build ComfyTray.csproj -c Release

# Run tests (xUnit)
dotnet test ComfyTray.Tests/ComfyTray.Tests.csproj

# Publish
dotnet publish ComfyTray.csproj -c Release -o bin/Release/net10.0-windows/publish

# Create a release (increments patch version, tags, and pushes)
./scripts/make-release
```

## Architecture

- **ComfyTray** (`ComfyTray.csproj`) - .NET 10 WPF WinExe. `App.xaml` sets `ShutdownMode="OnExplicitShutdown"` so the app stays alive in the tray. `MainWindow.xaml` is a zero-size invisible window that hosts a `TaskbarIcon` from H.NotifyIcon.
- **ComfyTray.Tests** (`ComfyTray.Tests/`) - xUnit test project (currently placeholder).
- **ComfyTray.Installer** (`ComfyTray.Installer/`) - WiX v3 MSI installer. Installs to Program Files, creates Start Menu/Desktop shortcuts, and registers auto-start via `HKLM\...\Run`.

## Release Process

Releases are triggered by pushing a `v*.*.*` tag (or manual workflow_dispatch). The GitHub Actions workflow (`.github/workflows/release.yml`) builds the app, runs tests, builds the WiX MSI, and creates a GitHub Release with the MSI attached.
