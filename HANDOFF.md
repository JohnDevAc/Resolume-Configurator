**Standing project coordination — 7 September 2026**

This project's existing `Current Dev` task (`01a07cf1-28a1-7b43-a9db-ba66594d63fe`, host `local`) owns Resolume Configurator implementation and independent development. Cross-project work is coordinated by `Coordinate Current Dev tasks` (`01a07cfa-c36b-7370-b78d-1b15d9d673f7`, host `local`) under the [master routing and working agreement](</C:/Users/jligh/OneDrive/Documents/Job Setup - Kiloview Suite/PROJECT-COORDINATION.md>).

Coordinate dependencies before integration changes and maintain one active writer per checkout. Preserve separate repositories, Git histories, branches, versions, contracts, releases, installers, updaters, and deployment authorization. Send significant cross-project decisions, blockers, contract/release implications, validation and completion to the master through `send_message_to_thread`; keep implementation context and local handoffs here. Avoid repeated acknowledgements. Read applicable project instructions and integration contracts before changes; PC NDI writes remain owned by PC Agent.

This setup authorizes only the coordination documentation and initial report. It does not resume completed work or authorize source changes, testing, commits, publication, or installation.

**Current implementation — 7 September 2026**

The user subsequently requested “fix items in audit report,” authorizing the local implementation and validation of those findings. All nine numbered findings are implemented; see the status table in [the audit report](APPLICATION-AUDIT-2026-09-07.md) for changes, regression coverage and remaining hardware acceptance. The PC Agent schema-1 production address is reused for sender identity, with no changes to the server/agent APIs or ownership of PC NDI settings. Initial validation used version 0.3.6 without committing, releasing or installing. The runtime now explicitly gates Arena compatibility to the validated 7.27.x API/XML family. Local install payloads must be prepared using `scripts/Publish-Local.ps1`, which creates the manifest required by `Install-Local.ps1`.

**Release authorization — 7 September 2026**

The user's subsequent “Push and deploy” request authorizes committing and pushing these changes, publishing version 0.3.7 using the existing stable/development release pattern, and updating this PC's per-user Resolume Configurator installation. Release targets are `main` / `v0.3.7` and `dev` / `v0.3.7-dev`, with a Windows x64 installer, portable package and SHA-256 checksums. Other projects retain separate deployment authorization. Physical Arena/NDI/Kiloview acceptance remains outstanding and must be distinguished from automated validation and successful package installation. See [the release notes](docs/releases/v0.3.7.md).
