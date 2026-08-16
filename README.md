# Resolume Arena Configurator

A native Windows companion app that turns the current NDI Job Configurator fleet into a Resolume Arena workspace.

## What it builds

- A new selectable `1080p`, `4K`, `5K`, or `8K` composition at `50` or `60 fps` (defaults to `4K` at `50 fps`).
- `Show` at the top of the composition, `LED Wall` second, then one named group per onboarded decoder.
- Three layers in every group, ordered rear-to-front as `Holding`, `Secondary`, `Primary`, with Holding and Secondary collapsed after reload.
- Optional automatic placement of every onboarded encoder on all three layers, preserving NDI Job Configurator device order and starting at a selectable column (enabled at column `5` by default).
- The same ordered encoder feeds inserted into all three `Show` layers.
- Between 5 and 50 composition columns (default `20`). With automatic placement enabled, the minimum expands to fit the starting offset, every detected NDI source, and the following Video Router column. The enabled router is routed to `Show` with `Resize = Fit`.
- Every NDI clip is set to `Resize = Fit`, then its thumbnail is refreshed from the live feed.
- Advanced Output NDI screens for `Show` and every decoder, plus a non-NDI `LED Wall` virtual output.
- Every NDI screen is enabled. Each Kiloview decoder gets its matching Arena NDI output in the next unpopulated preset slot and immediately activates it. A later run reuses that same matching slot instead of consuming another.
- Resolume's persisted NDI composition-sharing output can be enabled or disabled for the restart (disabled by default).
- Slice inputs are resolved only after all groups and layers exist, using Arena's final 1-based group indices.
- The composition name is the NDI Job Configurator job name.

## Requirements

- Windows 10 or 11.
- Resolume Arena 7 with **Preferences → Webserver** enabled on port `8080`.
- NDI Job Configurator available on TCP `8091`. The app checks localhost, then discovers it across local IPv4 subnets using `/api/health`. Local `state.json` is a fallback.
- Encoder NDI sources visible in Arena before configuration.
- Decoder credentials saved by NDI Job Configurator (used locally and never displayed or logged).

## Run locally

```powershell
dotnet run --project .\src\ResolumeConfigurator\ResolumeConfigurator.csproj
```

## Build the Windows app

```powershell
dotnet publish .\src\ResolumeConfigurator\ResolumeConfigurator.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o .\artifacts\publish\win-x64
```

Install the published test build for the current Windows user:

```powershell
.\scripts\Install-Local.ps1
```

The app is installed under `%LOCALAPPDATA%\Programs\Resolume Arena Configurator` and shortcuts are added to the Start menu and desktop.
It is also registered under Windows **Installed apps**, where it can be uninstalled normally.

For a GitHub release, extract the Windows ZIP and run the included `Install-Local.ps1`. The release installer automatically uses the application files beside it.

## Safety and Arena behavior

The app first saves the current composition to a timestamped backup in the Resolume compositions folder. It then creates and loads the new composition. Routing is deferred until the complete layer/group structure has been re-read from Arena. Advanced Output is not exposed by Resolume's REST API, so the app writes both a named XML preset under `Resolume Arena\Presets\Advanced Output` and Arena's active `Preferences\AdvancedOutput.xml` state directly. The active file is backed up and replaced atomically; no menu selection or window automation is used.

Arena does not hot-reload its active Advanced Output XML. After all files are safely saved, the configurator stops Arena and relaunches it with the saved job composition. Arena loads the new Advanced Output state during startup. Because startup resets live clip Resize and connection states, the configurator reconnects, reapplies `Fit`, enables every decoder Video Router, refreshes decoder and Show thumbnails, and saves once more before reporting success.

The app then uses the Kiloview N6/N60 HTTP APIs to discover each named Arena output, preserve unrelated decoder presets, replace the prior matching app-managed slot when present (or choose the first empty slot), and select it for decoding.

Arena 7.27 can leave `POST /composition/new`, `/composition/save`, `/composition/open`, or a thumbnail update pending after the requested work has already completed. The configurator avoids the broken new-composition endpoint and verifies completion against a newly written, settled AVC file, a stable live composition state, and each thumbnail's `last_update` token. If a job-named AVC already exists, it is preserved as a timestamped `Previous` file before replacement.

Arena also does not serialize the Video Router's live `Resize = Fit` choice. The configurator therefore restores Fit after the composition is patched to the selected frame rate and reloaded.

Decoder resolutions not reported by NDI Job Configurator appear as editable `1920 × 1080` fallbacks and are marked **Review**.
