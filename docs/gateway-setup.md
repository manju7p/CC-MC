# Gateway Setup Guide (Windows Service Install/Operate/Uninstall)

This is the operator-facing runbook for installing, configuring, starting,
verifying, stopping, upgrading, and uninstalling the CC-MC Local Device
Gateway as a Windows service on a chilling-centre machine.

For *why* this approach (WinSW v3 + vendored Node.exe) was chosen over the
alternatives, see [`docs/gateway-decision.md`](./gateway-decision.md) - this
document does not repeat that reasoning, only the concrete steps.

**Verification status of this document (Checkpoint 6B):** every command
below has been reviewed against the actual scripts/config in this
repository (`apps/gateway/service/install.ps1`, `uninstall.ps1`,
`CCMCGateway.xml`, `apps/gateway/packaging/build-deployment.mjs`) and
against documented WinSW/`sc.exe`/PowerShell behavior. **None of it has
been executed on a real Windows machine** - this development sandbox is
Linux-only and cannot run `.ps1` scripts or register a Windows service.
Real-Windows execution (install, reboot-autostart, crash-restart, stop,
uninstall, upgrade) is a Checkpoint 10 / real-hardware task. Anywhere this
document says a step "will" do something, read that as "is expected to, per
reviewed script logic and documented Windows/WinSW behavior" - not as
"was observed to."

---

## 1. Prerequisites

- A Windows machine, with an Administrator PowerShell session for install
  and uninstall (`Get-Service`/service registration require elevation).
- Network access from this machine to the CC-MC cloud API
  (`cloudApiBaseUrl` in the gateway config).
- A gateway service-account email/password already provisioned in the
  cloud API for this centre (`cloudAuthEmail`/`cloudAuthPassword` - see
  §3). This document does not cover provisioning that account server-side.
- The assembled deployment directory (§2).

There is no dependency on a globally-installed Node.js or any other
globally-installed runtime - the deployment directory is self-contained
(vendored `node\node.exe`), per `docs/gateway-decision.md` §1/§2.

## 2. Assembling the deployment directory

Run, from a machine that can build the gateway (this repository checked
out, with `pnpm install` already run):

```
node apps/gateway/packaging/build-deployment.mjs [outputDir]
```

`outputDir` defaults to `apps/gateway/dist-deployment`. This produces:

```
CCMCGateway.exe            <- WinSW executable (real Windows binary)
CCMCGateway.xml            <- WinSW service config (real, copied from apps/gateway/service/)
node\node.exe              <- vendored Node.js runtime (real Windows binary)
app\dist\main.js           <- compiled gateway entrypoint (real tsc build output)
app\node_modules\          <- gateway runtime dependencies (currently none - package.json declares zero)
config\gateway.config.example.json  <- template; copy and edit, see §3
data\                      <- empty; gateway.sqlite is created here on first start
logs\                      <- empty; WinSW writes rolling logs here
install.ps1 / uninstall.ps1
```

**Known gap, not silently glossed over:** in this sandbox,
`CCMCGateway.exe` and `node\node.exe` are written as clearly-labeled
PLACEHOLDER text files, not real Windows executables - this sandbox's
network egress allowlist returned 403 Forbidden when the script's author
checked `github.com/winsw/winsw/releases` and `nodejs.org/dist/` (see the
script's own header comment). Producing the real binaries requires running
this same script from a network-unrestricted machine (or at real Windows
packaging time), which downloads or vendors the genuine WinSW release exe
and official Windows Node.exe build in place of the placeholders. Every
other file the script produces (`CCMCGateway.xml`, `install.ps1`,
`uninstall.ps1`, `app\dist\main.js`, `app\node_modules\`) is the real
artifact, produced by this repository's actual build.

Copy the resulting directory to the target Windows machine, to whatever
install location you choose (e.g. `C:\CCMC\Gateway\`).

## 3. Configuring this install

Copy the template and edit it:

```
copy config\gateway.config.example.json config\gateway.config.json
notepad config\gateway.config.json
```

Fill in all seven required fields (`config.loader.ts` rejects the file if
any is missing - see `docs/gateway-architecture.md` for the full schema):

| Field | Meaning |
|---|---|
| `gatewayId` | This gateway's unique identifier. Once `gateway.sqlite` is created, this is pinned to the database file - a mismatch on a later start throws `GatewayIdentityMismatchError` rather than silently continuing (protects against a cloned/restored DB from a different install). |
| `centreId` | The chilling centre this gateway serves. Same pinning behavior as `gatewayId`. |
| `cloudApiBaseUrl` | Base URL of the CC-MC cloud API this gateway syncs to. |
| `logDirectory` | Currently validated at load time but not otherwise used by the gateway's own logger (which writes to stdout, captured by WinSW - see §6). Documented here as a known gap, not a working feature: do not rely on this field routing logs anywhere. |
| `dataDirectory` | Where `gateway.sqlite` is created/opened. Point this at the install's `data\` directory (e.g. `C:\CCMC\Gateway\data`) so it lives in the persistent-data branch of the tree, not inside `app\` (see §7). |
| `cloudAuthEmail` | Gateway service-account email, provisioned server-side ahead of time. |
| `cloudAuthPassword` | Gateway service-account password. Treat this file as containing a real secret: restrict its filesystem permissions to the service account and Administrators, same as `data\` (see `install.ps1`'s ACL step and `docs/gateway-decision.md` §9). |

`config\gateway.config.json` is never created, deleted, or modified by
`install.ps1`/`uninstall.ps1`/an upgrade - only read.

## 4. Installing the service

From an Administrator PowerShell prompt, in the deployment directory:

```
.\install.ps1
```

This script (reviewed in full, not executed here):

1. Verifies `CCMCGateway.exe`, `CCMCGateway.xml`, `node\node.exe`,
   `app\dist\main.js`, and `config\gateway.config.json` all exist, failing
   fast with a specific missing-file message if not.
2. Creates `data\` and `logs\` if absent.
3. Runs `CCMCGateway.exe install` to register the service with the Windows
   Service Control Manager (start type `Automatic` - starts on every boot,
   no user logon required).
4. Runs `CCMCGateway.exe start`.

On any step failing, the script exits non-zero with a message identifying
which step failed, rather than leaving a half-installed service silently
in place.

## 5. Verifying it started

```
Get-Service CCMCGateway
```

expected `Status: Running`.

For the gateway's own view of its state (gateway ID, centre ID, service
state, cloud/device connectivity, pending sync count, version, uptime),
run the same binary the service runs, in diagnostic mode:

```
node\node.exe app\dist\main.js --status
```

This prints one JSON object to stdout and exits. Note two current, honest
limitations in what this prints (not fixed in Checkpoint 6B - see
`docs/gateway-architecture.md` and this checkpoint's report for why):

- `cloudConnectivity` and `deviceConnectivity` currently always read
  `"UNKNOWN"` - neither is wired to a real signal yet (no devices are
  registered pending Checkpoint 6D; cloud connectivity has no
  structurally-unambiguous way to detect "disconnected" without guessing,
  per this checkpoint's investigation). This is reported, not invented.
- Running `--status` invokes the gateway's normal startup path
  internally, briefly, against the SAME `gateway.sqlite` the live service
  uses. As of Checkpoint 6B this is safe to run concurrently with a live
  service (it no longer disturbs in-flight sync state - see this
  checkpoint's report), but it is still a second, separate process
  briefly opening the same database file; avoid running it in a tight
  automated polling loop.

## 6. Logs

WinSW writes rolling log files under `logs\` (`CCMCGateway.wrapper.log`,
`CCMCGateway.out.log`, `CCMCGateway.err.log`, rolled by size per
`CCMCGateway.xml`'s `<log>` config). The gateway's own structured JSON
log lines (component, level, message, fields) are written to its stdout,
which WinSW redirects into these same files - there is no separate
gateway-specific log location to check. Gateway log lines never contain
passwords, JWTs, Bearer tokens, or the gateway's cloud service-account
credentials (reviewed in `logger.ts`, `http-cloud-client.ts`,
`cloud-auth.ts` during this checkpoint - the Authorization header value is
constructed and sent but never passed to the logger).

## 7. Stopping the service

```
.\CCMCGateway.exe stop
```

or `Stop-Service CCMCGateway`. The gateway's own shutdown handler (SIGINT/
SIGTERM) stops the sync engine, disconnects devices, and closes SQLite
cleanly (WAL checkpoint) before exiting - see `gateway.ts`'s `stop()`.

## 8. Upgrading

An upgrade replaces `CCMCGateway.exe`, `CCMCGateway.xml`, `node\node.exe`,
and `app\` with a newer build. It must **not** touch `config\`, `data\`,
or `logs\` - those live in a separate branch of the install tree
specifically so an upgrade cannot accidentally delete them (Checkpoint 2
constraint; see `packaging/build-deployment.mjs`'s header comment and
`docs/gateway-decision.md` §8). A safe upgrade sequence:

1. `.\CCMCGateway.exe stop`
2. Replace `CCMCGateway.exe`, `CCMCGateway.xml`, `node\`, and `app\` with
   the new build's copies (leave `config\`, `data\`, `logs\` untouched).
3. `.\CCMCGateway.exe start`

There is no separate upgrade script in this repository yet - this is a
manual sequence an operator follows, not an automated `upgrade.ps1`. If
Checkpoint 10+ real-Windows testing shows this manual sequence is
error-prone, an `upgrade.ps1` wrapping these same three steps would be a
natural, minimal addition - not built speculatively here without that
evidence.

On restart (whether from an upgrade, a crash-triggered WinSW restart, or a
clean stop/start), the gateway does not lose data: `gateway.sqlite` and
its outbox rows persist on disk; any outbox row that was `PROCESSING`
when the previous process instance ended is swept back to `PENDING` on
the next real startup and retried (see `sqlite-local-storage.ts`'s
startup recovery sweep, and `docs/gateway-architecture.md` for the full
outbox state machine).

## 9. Uninstalling

```
.\uninstall.ps1
```

Stops the service (non-fatal if already stopped) and deregisters it from
the SCM. **Does not delete** `data\`, `config\`, or `logs\` - an operator
who wants those gone removes them explicitly afterward. This is
deliberate: an uninstall must never silently destroy un-synced offline
transactions sitting in the outbox, the gateway's pinned identity
(`gatewayId`/`centreId` recorded inside `gateway.sqlite`), or its
configuration.

## 10. What is NOT covered by this document

- Provisioning the gateway's cloud service account (`cloudAuthEmail`/
  `cloudAuthPassword`) server-side - that is a cloud API/admin operation,
  out of scope for this gateway-side runbook.
  Real-hardware device wiring (RS232/Bluetooth) - explicitly out of scope
  for this checkpoint and not implemented anywhere in this repository yet.
- Least-privilege service account configuration (currently WinSW/SCM
  default `LocalSystem`) - deferred to Phase 12, see
  `docs/gateway-decision.md` §9.
- Any step's actual behavior when run on a real Windows machine - see the
  verification-status note at the top of this document.
