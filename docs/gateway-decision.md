# Gateway Decision: Windows Service Hosting Approach

**Status:** Accepted (Checkpoint 1 / Phase 0 of the Local Device Gateway milestone)
**Scope of this document:** how the gateway process gets installed and run as an
unattended Windows OS-level service. It does **not** cover the gateway's internal
architecture (device abstraction, sync engine, SQLite schema) - that's
`docs/gateway-architecture.md`, written once the skeleton exists (Checkpoint 2+).

This decision was made after researching the current (August 2026) state of the
two realistic candidates rather than from memory alone, since "which wrapper is
actively maintained" is exactly the kind of fact that goes stale. Sources are
listed at the end of each relevant section.

## 1. Chosen approach

**WinSW (Windows Service Wrapper), v3, self-contained build** wrapping a
**vendored Node.js runtime** (the official Windows x64 Node binary, not a
system-installed one) running the gateway's compiled JavaScript.

Concretely, the deployed service is:

```
CCMCGateway.exe            <- WinSW's own executable, renamed
CCMCGateway.xml            <- WinSW service config (name, executable, args, restart policy, logging)
node\node.exe              <- vendored Node.js runtime (official Windows x64 build)
app\dist\main.js           <- compiled gateway entrypoint
app\node_modules\          <- gateway's runtime dependencies
config\gateway.config.json <- local, non-secret configuration (see docs/gateway-decision.md §9)
data\gateway.sqlite         <- local persistence (Phase 4+)
logs\                        <- WinSW-managed rolling log files
```

`CCMCGateway.xml`'s `<executable>` points at `node\node.exe` (relative to the
service's install directory) with `<arguments>app\dist\main.js</arguments>` -
WinSW itself doesn't know or care that the wrapped process is Node; it treats
it as "any executable," which is exactly the point.

## 2. Why it was selected

- **WinSW is purpose-built for exactly this problem** and is actively
  maintained: the v3 branch has ongoing commits and its own CI pipeline, it's
  MIT licensed, and it ships self-contained native executables (built on
  .NET 7) that don't require installing .NET Framework on the target machine
  - important since chilling-centre machines are not developer machines and
    we can't assume any particular .NET version is present.
  ([winsw/winsw on GitHub](https://github.com/winsw/winsw))
- **It's the same mechanism the more "convenient" alternative already uses
  under the hood.** `node-windows` (the npm package that promises to wrap a
  Node script as a service via a JS API) turns out to itself "use the winsw
  utility to create a unique .exe for each Node.js script deployed as a
  service" per its own README. Going through `node-windows` would mean taking
  on an extra npm dependency, whose own maintenance signals are weak (its
  README's most recent concrete evidence of activity is a "Sponsors as of
  2020" note, with no visible deprecation notice but no clear recent-activity
  signal either), purely to auto-generate the same WinSW wrapper we can
  configure directly and transparently ourselves. Using WinSW directly is
  simpler, not more complex, despite being "one more layer down."
  ([node-windows on npm](https://www.npmjs.com/package/node-windows))
- **A wrapper is architecturally required, not optional.** A Windows service
  must implement the Service Control Protocol (respond to SCM start/stop/
  status control codes); a plain `node.exe app.js` process does not do this,
  so pointing `sc.exe create` directly at Node would not behave as a real,
  manageable service - it wouldn't report `SERVICE_RUNNING` correctly, and
  the SCM couldn't cleanly signal it to stop. Something has to sit between
  the SCM and the Node process either way.
- **Vendoring the Node runtime rather than assuming Node is pre-installed**
  removes an entire category of unattended-deployment failure: a chilling
  centre PC with no Node.js, the wrong Node.js version, or a Node.js that gets
  silently upgraded by someone else later and breaks compatibility.
  Bundling the exact runtime version we tested against is more boring and
  more reliable than depending on host state we don't control - directly in
  line with Rule 11 ("prefer boring, reliable technology").

## 3. Rejected alternatives

- **NSSM (Non-Sucking Service Manager).** Functionally similar to WinSW - and
  historically the more famous option - but its **official stable release is
  NSSM 2.24, from August 31, 2014**; the most recent commonly-referenced
  build is a 2017 pre-release (`2.24-101`) needed to work around a Windows 10
  Creators Update bug. That's over a decade without an official stable
  release. NSSM remains widely used and is not "broken," but choosing an
  actively-developed tool with a clear MIT license and recent commits (WinSW)
  over one with no official release since 2014 is the more defensible,
  boring choice for something we're standardizing on today.
  ([nssm.cc/download](https://nssm.cc/download))
- **`node-windows` (npm).** See §2 - it's WinSW underneath, plus an npm
  dependency and a JS install API we don't need, since our install step is a
  short PowerShell script, not application code that needs to programmatically
  register itself.
- **Compiling to a single executable (`pkg`, Node's Single Executable
  Application feature) instead of vendoring the runtime.** `vercel/pkg` is
  deprecated/archived by its maintainer (confirmed via an open issue on a
  dependent project explicitly titled around pkg's deprecation blocking
  Node 20+ support). Node's own built-in SEA support is still explicitly
  marked experimental in current Node docs. Neither is boring enough to bet
  an unattended production deployment on when "copy the official node.exe
  into a folder" is simpler, uses a fully-supported artifact, and side-steps
  SEA's current experimental-API caveats entirely.
  ([vercel/pkg deprecation referenced in pulumi/pulumi#15510](https://github.com/pulumi/pulumi/issues/15510))
- **PM2 + `pm2-windows-service`/`pm2-installer`.** Adds a whole separate
  process-manager daemon on top of the service wrapper, plus its own
  Windows-service registration package - two moving parts and two things to
  patch instead of one, for a single-purpose, single-service deployment.
  Rejected under Rule 11 / "do not over-engineer."
- **Windows Task Scheduler "run at logon/startup."** Doesn't register as a
  real Windows service (no SCM integration, no `services.msc` entry, no
  `sc query` visibility), commonly runs tied to a user session rather than
  as a true background service, and doesn't restart on crash without bolting
  on more scripting. Rejected because the business explicitly asked for "a
  proper Windows OS-level service."
- **Raw `sc.exe create` pointing at `node.exe` directly.** See §2 - would not
  correctly implement the Service Control Protocol; explicitly not a working
  option, not just a worse one.

## 4. Installation model

A flat deployment folder (e.g. `C:\Program Files\CCMC\Gateway\`) containing
the tree shown in §1, installed by an Administrator-run PowerShell script
(`install.ps1`, to be written in Checkpoint 2) that:

1. Copies the deployment folder into place.
2. Creates `data\` and `logs\` if absent, with ACLs restricting them to the
   service account and Administrators (no "Everyone" access - see §9).
3. Runs `CCMCGateway.exe install` (WinSW registers the service with the SCM
   using the XML config).
4. Runs `CCMCGateway.exe start`.
5. Exits non-zero with a clear message on any failure at any step, rather
   than silently leaving a half-installed service.

A double-click `.exe` installer wrapping this script is Phase 11's job (see
Known Limitations, §10) - not attempted in this checkpoint.

## 5. Startup model

WinSW registers the service with **Start type = Automatic**, so the Windows
Service Control Manager starts it on every boot with no user logon required -
satisfying "gateway starts automatically when Windows boots" directly. Delayed
Auto-start (a standard SCM option, configurable in the same WinSW XML) is
worth considering once real device/network timing is understood, so the
gateway doesn't race the machine's network stack coming up during a very cold
boot - noted as a tuning decision for later, not a blocker now.

## 6. Crash recovery model

**Revised in Checkpoint 2** (Principal Engineer constraint 11: "reconsider
the proposed dual restart mechanism... choose a clear primary recovery
mechanism and document why"). The original Checkpoint 1 draft of this
section described two independent, "deliberately redundant" restart
layers. On reconsideration, that is unnecessary complexity for what is
architecturally a single failure mode, so this design now has **one
primary recovery mechanism**, not two:

**WinSW's own child-process supervision is the sole/primary mechanism.**
`CCMCGateway.xml` configures `<onfailure action="restart" delay="5 sec"/>`
with `<resetfailure>1 day</resetfailure>` (see `service/CCMCGateway.xml`,
schema verified against WinSW's own docs - not invented). If the wrapped
`node.exe` process exits unexpectedly, WinSW - which directly spawned that
process and can observe its actual exit code - relaunches it after the
configured delay. The reset-failure window means a crash-loop doesn't
retry forever without backing off.

**SCM-level "Recovery" tab actions (`sc.exe failure`) are deliberately NOT
configured.** Reasoning:

- WinSW is the process directly supervising `node.exe`; it observes the
  real child exit event with no additional indirection. SCM-level
  recovery actions only matter for a *different* failure mode - the
  WinSW-wrapped process itself (`CCMCGateway.exe`) dying or hanging
  without WinSW noticing its own child died - which is a much rarer event
  than the Node process crashing, since WinSW is a small, mature,
  battle-tested supervisor with a narrow job.
- Configuring both layers with independent restart/backoff timers creates
  two systems that can both decide to act on the same underlying failure,
  with no coordination between them - exactly the kind of accidental
  complexity Rule 11 ("prefer boring, reliable technology... do not
  over-engineer") warns against, not a genuine reliability improvement.
- WinSW has no first-class XML element for SCM recovery actions; adding
  them would mean a second, separate `sc.exe failure ...` call in
  `install.ps1`, maintained independently of the XML config it's meant to
  complement - another place for the two mechanisms to drift out of sync.
- If real-Windows testing (Checkpoint 10) surfaces a concrete case where
  `CCMCGateway.exe` itself fails to recover (not just the wrapped Node
  process), that is new evidence this decision can be revisited against -
  not a reason to pre-emptively add a second mechanism today, with nothing
  yet observed that it's needed for.

This satisfies "gateway automatically restarts after a crash" at the
application-process level, via a single, clearly-owned mechanism, rather
than two overlapping ones.

## 7. Logging model

Two layers that serve different purposes:

- **WinSW-managed rolling log files** (`logs\CCMCGateway.out.log`,
  `.err.log`, size- or time-based rotation configured in the XML) capture
  the wrapped process's raw stdout/stderr - including failures the
  application itself can't log, like it failing to start at all.
- **The gateway's own structured application logs** (Phase 3 - JSON lines
  with gateway ID, centre ID, version, connectivity state, etc.) are a
  separate, additive concern layered on top in the application code, not a
  replacement for the wrapper-level logs.
- Windows Event Log also receives standard service start/stop/failure
  entries automatically via the SCM, giving anyone using standard Windows
  tooling (`services.msc`, Event Viewer) a place to look without knowing
  anything about this specific app.

## 8. Upgrade/uninstall model

- **Upgrade:** stop the service, replace the contents of `app\` (and `node\`
  only when the vendored runtime version actually changes), start the
  service. `data\` (the SQLite DB, once Phase 4 exists) lives outside `app\`
  specifically so an upgrade never touches it.
- **Uninstall:** `CCMCGateway.exe uninstall` cleanly deregisters the service
  from the SCM. Local data (SQLite DB, logs, config) is **explicitly not
  deleted** by uninstall - it's preserved unless a human deliberately removes
  the folder, so uninstalling never silently destroys un-synced offline
  transactions sitting in the outbox.

## 9. Security considerations (approach-level; full treatment is Phase 12)

- The service account WinSW runs under should be the least-privilege account
  that can still read/write its own install folder and talk to local COM
  ports / USB devices and make outbound HTTPS calls - not necessarily
  `LocalSystem`. The exact account is a Phase 12 decision, not made here.
- Nothing about the wrapper approach itself should carry secrets: the WinSW
  XML config and its log files must never contain the cloud API URL's
  credentials, the gateway's auth token, or any device-specific secrets -
  those belong in `config\gateway.config.json` (or a Windows-native secret
  store), addressed fully in Phase 12, with folder ACLs restricting who can
  read it.
- The gateway sits between the physical devices and the cloud - it makes
  outbound HTTPS calls to the existing cloud API; nothing about this hosting
  approach opens an inbound port or otherwise exposes the wrapped process to
  the network. (The devices themselves are never internet-reachable - see
  Phase 12's required architecture in the original task.)

## 10. Known limitations

- **Not yet verified on a real Windows machine.** This decision, and
  everything in this document, is grounded in WinSW's public documentation,
  its own use in large real-world deployments (e.g., Jenkins' own Windows
  agent packaging uses WinSW), and NSSM's/pkg's independently verifiable
  release history - not something executed here, since this sandbox is
  Linux-only. Phase 10 explicitly requires real-Windows-machine verification
  for install/boot/crash-restart/stop/uninstall behavior; until that runs,
  this remains a documented, well-grounded design decision, not a tested
  fact. This will be reported as such at every relevant checkpoint rather
  than implied to be verified.
- **Vendoring a specific Node.js version is a real ongoing cost, not a free
  win.** It adds ~40-70MB to the deployment package and means someone has to
  track Node.js security releases and refresh the vendored runtime - this
  tradeoff is accepted deliberately (see §2) but should not be forgotten.
- **No installer UI exists yet.** Today's installation model (§4) is a
  script an administrator runs, not the `CCMC-Gateway-Setup.exe` double-click
  experience the business ultimately wants - that's Phase 11, not this
  checkpoint.
- **Delayed-start tuning, the exact service account, and the WinSW restart
  backoff parameters are all left as explicit TBDs** for the checkpoints
  that actually build and test the service (2 and 10), rather than guessed
  at here without anything real to tune them against yet.
