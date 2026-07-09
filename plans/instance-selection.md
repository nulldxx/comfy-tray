# Plan: Detect and launch a specific ComfyUI instance from the tray

## Context

`ComfyDiscovery` (added in v0.1.16) already finds *all* local ComfyUI installations —
portable, legacy Desktop, and 2026 Comfy Desktop managed "Standalone" instances under
`%USERPROFILE%\ComfyUI-Installs` — but the tray reduces that list to **one**
deterministically-chosen install (`DiscoverBest()` = first in priority order), pins it into
`%APPDATA%\ComfyTray\config.json`, and ignores the rest. With current Comfy Desktop supporting
multiple instances, the user has no way to see or choose which one the tray launches.

This change adds a **"ComfyUI instance" submenu** to the tray listing every discovered
installation with the active one checked, plus a **"Re-detect installations"** item. Selecting
an install switches the active instance (single-active model: one ComfyUI runs at a time),
matching decisions confirmed with the user.

**Prerequisite (urgent):** the v0.1.16 release build is currently RED. Three analyzer errors
(`TreatWarningsAsErrors` + `AnalysisLevel=latest-All`) must be fixed first — the feature builds
on this code.

## Step 0 — Fix the broken build (prerequisite)

Three mechanical fixes so `dotnet build -c Release` passes:

1. `ComfyDiscovery.cs` `DiscoverBest()` (~line 81) — replace `DiscoverAll(out _).FirstOrDefault()`
   with an index check: `var all = DiscoverAll(out _); return all.Count > 0 ? all[0] : null;` (CA1826).
2. `ComfyServerManager.cs` `ResolveLaunchable` (~line 160) — replace `installs.FirstOrDefault()`
   with `installs.Count > 0 ? installs[0] : null` (CA1826).
3. `ComfyDiscovery.cs` `EnumerateStandaloneInstanceRoots` (~line 327) — change return type
   `IReadOnlyList<string>` → `string[]`; the `[]` / `Directory.GetDirectories` returns already
   satisfy it (CA1859).

Verify with a full Release build before layering the feature on top.

## Design (single-active "switch" model)

**Active-instance identity without a schema change.** `config.json` already stores explicit
`PythonPath`/`MainScript`. An install is "active" when its `(PythonPath, MainScript)` matches the
current config — the same key `ComfyDiscovery.Add()` already uses for de-duplication. No new config
field needed.

**Selecting an instance** = `ComfyConfig.FromInstallation(chosen)` carrying over the user's runtime
preferences (`Host`, `Port`, `PurgeOutputsAndHistory`, `LogStdout`, `ExtraArguments`) → save →
if the server is running, `Stop()` then `Start()` on the new config; if stopped, just set active
(user starts it themselves). Least-surprising behaviour.

**Reuse / de-duplicate:** the "carry over runtime prefs onto a discovered install" logic already
exists inline in `ComfyServerManager.ResolveLaunchable`. Extract it once and reuse in both places:
add `ComfyConfig.FromInstallation(ComfyInstallation inst, ComfyConfig? carryOver)` (the existing
single-arg overload calls it with `null`). `ResolveLaunchable` and the new picker both call the
two-arg overload.

## Files to modify

- **`MainWindow.xaml`** — inside the `ContextMenu`, add:
  - `<MenuItem x:Name="InstancesItem" Header="ComfyUI instance" SubmenuOpened="Instances_SubmenuOpened"/>`
  - `<MenuItem Header="Re-detect installations" Click="Redetect_Click"/>`
  (placed near Start/Stop, above the Purge separator.)

- **`MainWindow.xaml.cs`** — new handlers:
  - `Instances_SubmenuOpened` — call `ComfyDiscovery.DiscoverAll(out _)`, clear and rebuild
    `InstancesItem.Items`: one checkable `MenuItem` per install, `Header` = friendly label
    (`{Kind} — {BaseDirectory}`), `IsChecked` when it matches `_config`, `Tag` = the install,
    `Click` = `Instance_Click`. If empty, a single disabled "No installations found" item.
  - `Instance_Click` — read the `ComfyInstallation` from `Tag`; if it's already active, no-op;
    else build the new config (carry-over overload), `_config = new; _config.TrySave(out _)`;
    if `_server.IsRunning` → `Stop()` then `Start(_config)` (reuse existing try/catch message box
    from `Start_Click`); refresh icon/tooltip via existing `UpdateForState`.
  - `Redetect_Click` — re-run discovery, rebuild the submenu, and show a brief `MessageBox`
    summary (`Found N installation(s)`; note if the active one is no longer detected).

- **`ComfyConfig.cs`** — add `FromInstallation(inst, carryOver)` overload (extract prefs copy);
  keep the one-arg overload delegating to it. Optional small helper
  `bool MatchesInstall(ComfyInstallation inst)` comparing `PythonPath`+`MainScript`
  (OrdinalIgnoreCase) for the checkmark logic.

- **`ComfyServerManager.cs`** — `ResolveLaunchable` uses the new two-arg `FromInstallation`
  (removes the inline pref-copy duplication); apply the Step 0 CA1826 fix here.

## Tests (`ComfyTray.Tests`, pure — no WPF)

- `FromInstallation(inst, carryOver)` preserves `Host`/`Port`/`Purge`/`LogStdout`/`ExtraArguments`
  while taking paths from the install.
- `MatchesInstall` returns true for the originating install and false for a different one.
- Selection round-trip: build config from install A, then from install B — assert paths switch and
  carried-over prefs persist. (Menu/XAML is not unit-testable; logic lives in testable helpers.)

## Verification

1. **Build green:** `dotnet build ComfyTray.csproj -c Release` (must pass analyzers) then
   `dotnet test ComfyTray.Tests/ComfyTray.Tests.csproj` — on the Windows dev box (no .NET SDK here).
2. **Manual multi-instance:** create two fake trees under `%USERPROFILE%\ComfyUI-Installs\A` and
   `\B` (each `.venv\Scripts\python.exe` + `ComfyUI\main.py`) → open the tray "ComfyUI instance"
   submenu → both listed, the active one checked → pick the other → confirm `config.json` now points
   at B and, if running, it restarts on B.
3. **Re-detect:** add a third tree, click "Re-detect installations" → it appears; delete the active
   one and re-detect → summary flags that the active install is missing.
4. **Empty:** with no installs and no config, submenu shows the disabled "No installations found".

## Out of scope

- Running multiple instances concurrently (would require multi-process `ComfyServerManager`,
  per-instance state/logs/ports).
- Remote/Cloud/Tracked Desktop instance types (no local `main.py` to launch).
