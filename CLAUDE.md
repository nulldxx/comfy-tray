# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

A Windows system tray application built with .NET 10 / WPF using the [H.NotifyIcon.Wpf](https://github.com/HardcodetNet/H.NotifyIcon) library. It runs a **ComfyUI server** headless in the background. The tray icon is **red when stopped (the default) and green when running**. The context menu can Start/Stop ComfyUI, open the **Configuration** dialog, open a live **Logs** window, show About, and Exit. There is no visible main window, and the ComfyUI server is launched with no console window. It includes a WiX v3 MSI installer (per-user: installs to `%LocalAppData%\Programs\ComfyTray`, no elevation, registers auto-start via `HKCU\...\Run`) and a GitHub Actions release pipeline.

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
- **ComfyTray.Installer** (`ComfyTray.Installer/`) - WiX v3 MSI installer. Per-user (no elevation): installs to `%LocalAppData%\Programs\ComfyTray`, creates Start Menu/Desktop shortcuts, and registers auto-start via `HKCU\...\Run`.

## Release Process

Releases are triggered by pushing a `v*.*.*` tag (or manual workflow_dispatch). After completion of a feature or bug fix, please commit and push changes before running `scripts/make-release`
