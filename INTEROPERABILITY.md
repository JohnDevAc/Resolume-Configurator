# Job Configurator interoperability

## Audit fixes — 7 September 2026

Decoder sender selection is pinned to the production IPv4 address already verified through the existing schema-1 PC Agent state/status contract. Endpoint, adapter, address and prefix are carried to the restart helper and must remain unchanged. Both N6 and N60 reject senders on other hosts and ambiguous same-host outputs. N60 activation requires the selected URL as well as the output name. No server/agent API or credential-export contract was added, and PC NDI writes remain owned by PC Agent.

Mutation guards still reread the selected job and local readiness immediately before writes, including after internal waits. They skip optional local credential merging during repeated identity checks, reuse the Agent HTTP transport, and no longer run duplicate outer validations during restoration. Credentials are read at the start of the helper only for the verified job revision. Selected-server operations have a five-second health/state deadline; network discovery retains 850 ms probes.

Output paths and XML are preflighted and staged before composition changes. Operation records retain backup paths and file commit status. A job/readiness failure stops further writes; recovery can still be necessary for earlier completed work. Helper completion is correlated by operation ID and monitored process exit, with a bounded operation lifetime.

## Additional QA corrections — 6 September 2026

Local readiness validates the saved schema and endpoint/adapter identities, then requires the exact saved adapter to be up with the preferred IPv4 address and matching prefix. An identical IP on another adapter is insufficient. The live Agent response must match schema, endpoint, adapter, address and prefix before job/NDI readiness can permit writes.

## QA follow-up — 6 September 2026

The restart worker checks local PC Agent/NDI readiness as well as the expected job identity. After waiting for Arena's saved composition to reopen, it revalidates before restoration writes. The API client also validates immediately before each mutating request, covering internal clip-loading waits during both initial configuration and restoration. Decoder configuration revalidates after source discovery and before each preset replacement/addition and activation request.

A changed job, NDI drift or unavailable local Agent stops further writes. Earlier completed writes can remain; use the existing composition backup and operation log to recover partial work. Tests simulate restart delay, internal clip delay, changed job, changed Discovery setting, unavailable Agent, and N6/N60 source lookup boundaries without contacting Arena or hardware.

Resolume Configurator remains an independent Arena client. Job Configurator, KiloLink and NDI Discovery may run elsewhere. The Arena PC must participate in the intended job through its local PC Agent; it does not need local server components.

Before changing Arena, the plan requires the selected Job Configurator's schema-1 server ID, job ID and revision. Old servers without that identity must be updated. Configuration is revalidated before mutation and around later phases; the restart helper carries the expected identity and checks it before restoration and each decoder. Same-name replacement, a different server or a changed device configuration requires reloading and reviewing the plan.

Readiness verifies the local Agent endpoint, its selected NDI interface, send/receive groups and Discovery address. Repair drift with PC Agent rather than writing PC NDI settings from Resolume. No general internet gate is imposed on configured LAN operation or the self-contained installer.

Local decoder credentials are used only for the exact selected server/job revision and device ID. Matching IP addresses or job names alone are insufficient. Without a verified local credential source, normal onboarded-device credentials follow the existing admin/job-name contract. Custom credentials on a remote server require a verified exact-identity source; decoder preflight fails clearly before Arena mutation when those credentials are unavailable. No general server credential export API is added.

Arena composition backups remain under `Configurator Backups`. A failure after mutation can leave a partial configuration; inspect the operation log and restore the saved composition when required. Distributed revision checks do not make Arena and firmware writes a single atomic transaction.

Ports: Job Configurator TCP 8091, local Agent TCP 8094, Discovery TCP 5959 and Arena TCP 8080. Keep these distinct on combined hosts.

Run `dotnet run --project tests/ResolumeConfigurator.Tests/ResolumeConfigurator.Tests.csproj` for isolated validation, including identity, credentials, NDI readiness and restart arguments. The suite workspace's HTTP fixture can additionally exercise this reader against a real isolated Job Configurator process. Real Arena/NDI/device operation still needs controlled hardware acceptance.
