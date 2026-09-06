# Application review — 6 September 2026

Reviewed and fixed the WPF interface, job discovery, source matching, Arena configuration and restart, XML generation, decoder preset selection, and packaging scripts. Expanded the regression suite from 12 to 27 tests and completed two successful full network runs, including automatic recovery from Arena's stale API state. The updated installer was built and installed on this PC.

## Fixed issues

| Priority | Issue | Result |
| --- | --- | --- |
| High | Decoder preset selection used substring matching, so `KV-001` could reuse and overwrite the preset for `KV-0010`. | Matching requires the full output name, allowing the supported NDI `HOST (channel)` and `HOST (Arena - channel)` forms. |
| High | Configure and Refresh became available before the restart helper finished; editable rows could also change a running plan. | Configuration inputs remain disabled through helper completion, and repeated Configure clicks are guarded. |
| High | Restart ignored the saved composition path and passed a sanitized filename as the expected job name. | Arena receives the composition path as one argument; the helper receives the original job name and originating process ID. |
| Medium | Editing a matched encoder name retained its old hidden source token. | Manual edits clear the detected token and update matching status. Clearing the name makes the source unmatched. |
| Medium | Product/name HTTP reads could hang indefinitely after receiving headers and an incomplete body. | The configured timeout now covers the response body too. Local HTTP tests reproduce the stalled-response case. |
| Medium | A persistently corrupt primary state file threw on its last retry, preventing backup recovery. | Invalid or unreadable primary state falls back to `.bak`; malformed API responses are skipped, and caller cancellation is preserved. |
| Medium | Invalid numeric edits left the previous valid model value eligible for configuration. Oversized resolution strings could be truncated by the parser. | WPF binding errors block Configure; edits are committed before execution; resolution parsing requires complete numeric dimensions. |
| Medium | Some invalid plans were rejected only by the UI, and oversized existing compositions were rejected after renaming/resizing them. | UI and orchestration share plan validation; existing group/layer capacity is checked before Arena mutations. |
| Medium | Punctuation-only source identities normalized to an empty string and matched arbitrary feeds. | Empty normalized identities are excluded. |
| High | Arena's API retained 15 old layers after its UI switched to a blank 3-layer composition. Clip loading failed with 404. | The app saves a backup and compares actual saved layer/group/clip identities before editing. A mismatch triggers a verified Arena restart. The reproduced failure now recovers automatically. |
| High | Local credentials belonged to an older job, and decoder login failures occurred too late. | Ignore credentials from another job, resolve the current job's documented onboarding credentials when appropriate, and validate all decoder logins and preset capacity before modifying Arena. |
| Medium | Router Fit was lost after restart. | The fresh helper verifies the reopened structure, restores all source/router Fit settings, reconnects routers, refreshes thumbnails, and saves before decoder activation. |
| Medium | IExpress rejected a quoted SED filename; packaging completion was not robustly checked. | Invoke the relative SED filename from the build directory, wait for a stable unlocked package, and validate temporary paths before cleanup. Build and installation both passed. |

The streaming timeout follows Microsoft's documented requirement to [time response content separately when using `ResponseHeadersRead`](https://learn.microsoft.com/en-us/dotnet/api/system.net.http.httpcompletionoption?view=net-9.0).

## Optimizations

- Discovery reuses one HTTP client and runs a bounded set of workers instead of allocating a task and client for every scanned host. The existing limit of 48 concurrent probes is retained; service continuations run independently of the WPF UI context.
- Source identities are normalized once per match operation instead of repeatedly inside the candidate comparison loops.
- Post-restart decoder resolution is parsed once per device instead of three times.
- Refresh detaches row event handlers and clears stale counts and connection state.
- LAN discovery uses actual adapter subnet prefixes, scans nearby addresses first, enumerates lazily, and imposes a 15-second overall budget before local fallback.

These reduce redundant work; fleet-scale performance was not benchmarked.

## Verification

- Original baseline: **12/12 passed**. Five new regression cases were observed failing before their fixes: empty source identities, manual source corrections, preset name collisions, resolution boundaries, and streaming response timeouts.
- Expanded suite: **27/27 passed**, including temporary-file recovery, local HTTP response tests, restart arguments, subnet enumeration, credential selection, post-restart restoration, stale graph detection, plan validation, and WPF validation/busy-state checks. The WPF window is constructed without being shown or triggering discovery.
- Release solution build: **zero warnings and zero errors**, with warnings treated as errors.
- Self-contained single-file `win-x64` publish succeeded to `artifacts/review/win-x64`.
- PowerShell syntax checks passed. IExpress built a 51.2 MiB installer. Quiet per-user installation returned exit code 0; the installed executable's SHA-256 matches the tested build. Installed-app registration and application startup/discovery were verified. Uninstallation was not exercised.
- The initial read-only API probe returned promptly but was later found to describe stale Arena objects. A successful GET alone is insufficient evidence of a valid live graph; the new saved-state comparison addresses this.

Reproduce the primary checks from the repository root:

```powershell
dotnet build ResolumeConfigurator.sln -c Release -warnaserror
dotnet run --project tests/ResolumeConfigurator.Tests/ResolumeConfigurator.Tests.csproj -c Release
dotnet run --project tests/ResolumeConfigurator.Tests/ResolumeConfigurator.Tests.csproj -c Release --no-build -- --probe-arena
```

## Live network acceptance

Tested on Arena **7.27.1** with the current **LivewireTest** job:

| Device | Address | Result |
| --- | --- | --- |
| TeleTool encoder LivewireTest-TT-001 | 192.168.0.31 | Live 1080p60 test signal discovered and loaded into 12 source clips. |
| N6 LivewireTest-KV-001 | 192.168.0.90 | Matching Arena output active; existing managed slot 3 reused. |
| N60 LivewireTest-KV-002 | 192.168.0.91 | Matching Arena output active; existing managed slot 2 reused. |
| N60 LivewireTest-KV-003 | 192.168.0.92 | Matching Arena output active; existing managed slot 2 reused. |

- First successful full run: **10:18:38–10:19:08 BST**, including restart and helper completion.
- Recovery run: **10:24:17–10:25:06 BST**. Created a blank composition in Arena, confirmed its API still reported the old 15-layer graph, and ran the updated application. It detected the mismatch, saved the real blank composition, restarted, verified the 3-layer graph, built the job, restarted again, and completed all decoder activations.
- Final saved/live structure: **3840 × 2160 at 50 fps**, 20 columns, 15 layers, 5 correctly ordered groups. Ten Holding/Secondary layers are collapsed.
- All **12 NDI clips** report **Fit** and non-default refreshed thumbnails. All **3 routers** report **Fit** and Connected. Saved router inputs target Show (`1:5`).
- Advanced Output contains **4 enabled NDI screens** and **1 LED Wall virtual screen**, mapped to the correct final group indices. Composition-level NDI sharing remains off.
- Before/after decoder API snapshots match for every preset, including unrelated encoder and black presets; all three active source names/URLs match their intended Arena outputs.
- Triggered the Show Primary encoder clip for a video test. NDI Studio Monitor displayed the live TeleTool test pattern through **each of the three Arena decoder outputs at 1080p50**. The Show clip and decoder routers remain active; the job was saved.
- Detailed local evidence is in ignored `artifacts/live-test`: `recovery-run.log`, `fleet-before.json`, `fleet-after.json`, `arena-final.json`, `verify-final.ps1`, composition backups, and output preference backups.

## Limits

- Video was observed through NDI Studio Monitor and decoder activation through device APIs. Physical HDMI displays and audio synchronization were not independently observed.
- Decoder dimensions were not supplied by the companion job, so all three used the visible editable 1920 × 1080 fallback; actual Arena sender dimensions were confirmed in Studio Monitor.
- Wider-subnet enumeration is covered by regression tests; this live run discovered the companion on localhost. Fleet-scale scanning performance and unreachable remote-VPN deployments were not benchmarked.
- Arena's live router Fit choice is restored by the configurator's controlled restart. A later manual Arena restart is outside that helper lifecycle.
