# Resolume Arena Configurator

See [Job Configurator interoperability](INTEROPERABILITY.md) for required job identity, local NDI readiness and remote credential ownership.

A native Windows companion app that turns the current NDI Job Configurator fleet into a Resolume Arena workspace.

Download the Windows installer or portable package from the [latest release](https://github.com/JohnDevAc/Resolume-Configurator/releases/latest).

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
- Resolume Arena **7.27.x** with **Preferences → Webserver** enabled on port `8080`. Other API/XML versions fail preflight until their compatibility has been validated.
- A local **NDI Configurator PC Agent**, onboarded into the selected job and reachable on TCP `8094`. Its schema-1 `agent-state.json` and live status must agree on the endpoint, production adapter, IPv4 address, job groups and Discovery Server. This is required even when Job Configurator is remote.
- A running NDI Job Configurator available on TCP `8091`. On its hosting PC, the app connects locally. On other PCs, it scans the active local IPv4 subnets and lists the instances found, with job names, addresses, and device counts. Select an instance and click **Continue** before the main application opens, even if only one instance is found.
- The network scan prioritizes nearby addresses and has a 15-second budget. `NDI_JOB_CONFIGURATOR_URL` adds a known remote server to the scan, including servers outside the local subnets. If nothing is found, the app displays a warning and exits when **Close application** is clicked. Saved state cannot substitute for a running configurator.
- Encoder NDI sources visible in Arena before configuration. Automatic matching requires complete, unambiguous identities; duplicate source assignments must be corrected.
- Kiloview N6 or N60 decoders. Other decoder families are rejected before authentication. Arena output senders must advertise the production IPv4 address verified by PC Agent.
- Decoder credentials saved by NDI Job Configurator, or its standard onboarding credentials for the current job (used locally and never displayed or logged). Local credentials from a different job are ignored. Custom credentials must be available in current local state.

## Run locally

Source builds require the .NET 8 SDK with Windows desktop support. Published self-contained packages include the app runtime.

```powershell
dotnet run --project .\src\ResolumeConfigurator\ResolumeConfigurator.csproj
```

## Build the Windows app

```powershell
.\scripts\Publish-Local.ps1
```

Run the automated regression suite (a console executable, not a `dotnet test` project):

```powershell
dotnet run --project .\tests\ResolumeConfigurator.Tests\ResolumeConfigurator.Tests.csproj -c Release
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\tests\PackagingRegressionTests.ps1
```

The suite uses temporary files, in-memory HTTP handlers, local test HTTP servers, an unshown main window, and briefly displayed startup dialogs with simulated discovery results. Production startup is disabled in the test application. Packaging tests mock installation removal and process checks. These tests do not modify the running Arena composition, physical decoders or installed application. See [the audit and fix status](APPLICATION-AUDIT-2026-09-07.md) and [earlier live test results](REVIEW.md).

Install the published test build for the current Windows user:

```powershell
.\scripts\Install-Local.ps1
```

The app is installed under `%LOCALAPPDATA%\Programs\Resolume Arena Configurator` and shortcuts are added to the Start menu and desktop.
It is also registered under Windows **Installed apps**, where it can be uninstalled normally.
`Publish-Local.ps1` includes the installation scripts, licence and a SHA-256 payload manifest. Installation verifies the complete manifest before closing the app or copying files. A bare `dotnet publish` directory must be prepared with `Publish-Local.ps1` before using `Install-Local.ps1`.

## Build the Windows installer

```powershell
.\scripts\Build-Installer.ps1
```

This publishes the self-contained `win-x64` app and creates a versioned Setup executable plus its SHA-256 checksum under `artifacts\release`. The installer is per-user, does not require administrator access, adds Start menu and desktop shortcuts, and registers the app under Windows **Installed apps**.

IExpress requires ASCII staging paths. The builder uses a writable ASCII temporary directory, falling back to Common Documents when the user temporary path contains Unicode. `-StagingDirectory` supplies an explicit alternative. The final `-OutputDirectory` can contain Unicode. Nested publish content is rejected explicitly rather than omitted from the installer.

The generated installer is not code-signed. Windows SmartScreen may therefore show an unrecognized-app warning until the executable is signed with a trusted code-signing certificate.

## Safety and Arena behavior

The selected configurator remains fixed for refreshes, configuration, and the post-restart helper. A lost connection is reported instead of switching to another job or using cached job data. Local state is used only to supplement credentials for the matching current job.

The default Arena data root is Windows' actual Documents folder plus `Resolume Arena`, including normal Documents redirection. There is no guessed OneDrive fallback. The chosen root is displayed in the activity log. Optional environment overrides must be absolute paths (environment-variable expansion is supported):

| Override | Meaning |
| --- | --- |
| `RESOLUME_ARENA_DATA_DIR` | Existing Arena user-data folder containing `Preferences`. |
| `RESOLUME_ARENA_EXE` | The existing `Arena.exe` to launch; required to disambiguate multiple installations. |
| `RESOLUME_PC_AGENT_STATE_PATH` | Full path to the local PC Agent's current `agent-state.json`. |
| `NDI_JOB_CONFIGURATOR_DATA_DIR` | Directory containing optional local `state.json` credentials; `KILOVIEW_DATA_DIR` is the legacy fallback. |

Generated filenames escape reserved names and use a stable suffix when sanitization or shortening is necessary. Arena file paths are kept within a conservative 240-character budget; an excessively long root fails preflight with instructions to choose a shorter directory. The runtime generates its own presets and does not require an external XML template or source checkout.

Before composition edits, all output destinations are probed for writing/replacement, existing XML is checked, and the generated output files are staged. Recovery records under `Compositions\Configurator Backups\<operation ID>\operation.json` identify the composition backup, archived job composition, staged files and each output replacement. File-write failures roll back earlier output replacements where possible. A changed job/readiness check stops further writes and leaves the record for recovery. These checks cannot guarantee that a disk or network share remains available later.

The parent app monitors helper exit and reads a result with the matching operation ID. Unexpected exit, missing/malformed results, and a helper deadline restore an actionable error instead of relying on a window notification. The helper has a 15-minute overall deadline, sender discovery a 35-second deadline per decoder, and activation verification 15 seconds. Job Configurator discovery probes retain their 850 ms budget; reads of the selected server use five seconds. Current Agent-state reads retry brief incomplete writes without falling back to an old adapter identity.

Save/open completion is verified for both fast and stalled HTTP responses. An unchanged old thumbnail alone cannot confirm a stalled update; the app requires a live-image transition, a changed update token, or a successful HTTP acknowledgement with a live thumbnail. If Arena provides none of these, configuration reports a timeout for review instead of claiming that the refresh succeeded.

At startup, Arena is launched only after a configurator has been found and selected. Its webserver is checked until ready, up to 15 seconds, instead of always waiting the full interval. A fresh discovery snapshot is reused for the first refresh; independent job and Arena reads run concurrently, and source matching prepares the discovered source names once per refresh.

When Configure is pressed, the app verifies decoder login and preset capacity, then saves the open composition under `Compositions\Configurator Backups`. It compares the saved layer, group, and clip identities with Arena's API. Arena 7.27 can retain stale API objects after New/Open; when this happens, the app restarts Arena with that backup and verifies synchronization before editing.

The configurator then overwrites the open composition using a deletion-free build: it reuses existing groups and layers, moves ungrouped or surplus layers into the required groups, adds only missing structure, clears clips, and renames the final graph. Routing is deferred until the complete layer/group structure has been re-read from Arena. Advanced Output is not exposed by Resolume's REST API, so the app writes both a named XML preset under `Resolume Arena\Presets\Advanced Output` and Arena's active `Preferences\AdvancedOutput.xml` state directly. The active file is backed up and replaced atomically; no menu selection or window automation is used.

Arena does not hot-reload its active Advanced Output XML. Before restarting, the configurator reloads the patched composition, applies `Fit`, enables every decoder Video Router, refreshes decoder and Show thumbnails, and saves again. After all files are safely saved, it stops Arena and relaunches it with the saved job composition so Arena can load the new Advanced Output state. A fresh helper waits 15 seconds, rereads the job, verifies the restarted composition, restores Fit and connected routers, refreshes source thumbnails, saves, and activates the decoder presets. Configuration controls remain disabled until the helper reports completion to the originating app window.

The app then uses the Kiloview N6/N60 HTTP APIs to discover each named Arena output, preserve unrelated decoder presets, replace the prior matching app-managed slot when present (or choose the first empty slot), and select it for decoding.

Arena 7.27 can leave `/composition/save`, `/composition/open`, or a thumbnail update pending after the requested work has already completed. The configurator verifies completion against a newly written settled AVC file, a stable reloaded composition state, and each thumbnail's `last_update` token. If a job-named AVC already exists, it is preserved as a timestamped `Previous` file before replacement.

Arena also does not serialize the Video Router's live `Resize = Fit` choice. The configurator therefore restores Fit after reload and after its controlled restart. A later manual Arena restart may require running Configure again to restore that live choice.

Decoder resolutions not reported by NDI Job Configurator appear as editable `1920 × 1080` fallbacks and are marked **Review**.

## Licence

Copyright © 2026 John Lightfoot. All rights reserved.

This is proprietary software made available free of charge for non-commercial use. Commercial use requires a separate written licence from John Lightfoot. See [LICENSE](LICENSE) for the complete terms.
