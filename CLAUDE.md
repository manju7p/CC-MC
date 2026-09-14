# Working guidelines for CCMC

**What is CC-MC?** A native Windows desktop application (WPF, C#/.NET 8,
code-behind — no MVVM) for the operational workflow at a dairy Chilling
Centre: weigh and quality-test incoming milk from a vehicle/source, apply
a manual Accept/Hold/Reject decision (with an advisory automatic
suggestion), compute Rate/Amount from a configured formula, save locally
first (SQLite, fully offline-capable), and sync idempotently to a cloud
API (ASP.NET Core 8 / EF Core / PostgreSQL) for cross-centre reporting,
RBAC, and audit. See `context.md` "Product" for the full scope statement
and `Doc/CCMC_BRD_and_Technical_Design_v2.docx` for the authoritative
requirements.

Engineering guidelines for building this software — how to test it, how
to keep its docs honest, the code-style and git defaults, and the
security/infra baseline. This file should stay true regardless of which
part of the repo a session is touching (Windows client or cloud API) and
regardless of which phase the project is in.

**Where to look for everything else** — this repo splits "how to build"
from "what's true right now" from "what happened and why," on purpose:

- **`Doc/CCMC_BRD_and_Technical_Design_v2.docx`** — the source of truth
  for product scope. If any other document disagrees with it, the other
  document is wrong, not the BRD — unless the BRD itself is stale, in
  which case fix the BRD first (see "Documentation consistency" below).
- **`context.md`** — the current-state summary: architecture, module
  responsibilities, hardware facts, constraints, "things we must not do,"
  open questions. Read this first when picking up work.
- **`STATUS.md`** — the chronological engineering log: phases completed,
  bugs found and fixed, product decisions made and why. Read this when
  `context.md` cites something ("see STATUS.md ...") and you need the
  full story behind it.
- **`architecture.mmd`** — a full-layer Mermaid diagram (devices → WPF →
  domain/application/infrastructure → cloud API → PostgreSQL → Docker/Neon
  → future Render). Read this for the shape of the whole system before
  diving into any one layer's code.
- **`HOW_TO_RUN.md`** — the practical runbook: build, test, Docker mode,
  Neon mode, switching between them, logs, migrations, dev credentials.

## Current operational state (checkpoint 2026-09-15)

Read this section first in any new session — it's the fastest way to avoid
re-deriving context that already exists. Originally written 2026-09-13,
kept current via dated `Update (...)` notes below rather than rewritten
each time — see `STATUS.md`'s own "Checkpoint" section and
`progress.md`'s dated checkpoints for the full chronological detail this
section only summarizes.

- **Branch:** `windows-application`. **`pranav-dev`** is a separate,
  historically unrelated legacy NestJS implementation on its own branch —
  never checkout, merge, rebase, or modify it. Read-only reference only
  (`git show pranav-dev:<path>`), and only when explicitly relevant.
- **Solution:** 11 projects — client (`CCMC.Domain`, `CCMC.Contracts`,
  `CCMC.Application`, `CCMC.Infrastructure`, `CCMC.Desktop`, `CCMC.Tests`)
  and cloud (`CCMC.Cloud.Domain`, `CCMC.Cloud.Application`,
  `CCMC.Cloud.Infrastructure`, `CCMC.Cloud.Api`, `CCMC.Cloud.Api.Tests`).
  `CCMC.Contracts` is the only project shared across both tiers — the two
  tiers otherwise have zero code coupling.
- **Docker:** `docker compose -p cc-mc up -d --build` runs containers
  `cc-mc` (API, host port `8081`) and `cc-mc-postgres` (Postgres 16, host
  port `55433`, only created when the `docker` Compose profile is active).
  The WPF client's `CloudApi:BaseUrl` is always `http://localhost:8081/` —
  identical in both modes below.
- **Environment switching:**
  `Copy-Item .env.docker .env -Force` (local Postgres) or
  `Copy-Item .env.neon .env -Force` (Neon) before `docker compose up`.
  `.env`, `.env.docker`, `.env.neon` are real and gitignored; only
  `.env.example` (placeholders) is tracked. Never put a secret in a tracked
  file, `compose.yaml`, or `appsettings.json`.
- **Neon:** project `fancy-cherry-25725711`, branch `production`. Connectivity
  and automatic migrations are verified working. **Update (2026-09-15):**
  no longer zero users — a production bootstrap mechanism now exists
  (`ProductionBootstrapSeeder`, env-var-gated via `Bootstrap:AdminEmail`,
  a no-op unless configured) and was used to create one Admin, one
  Manager, and one Operator account plus one Chilling Centre (`BLR-CC-01`,
  a placeholder identity by explicit human choice — rename once the real
  centre is known). `DevelopmentSeeder` still correctly never runs outside
  `ASPNETCORE_ENVIRONMENT=Development`; the two mechanisms are independent
  and share their RBAC grants via `SeedHelpers`. See `STATUS.md` "Neon
  Production Bootstrap (2026-09-15)" / `HOW_TO_RUN.md` §9 for the full
  writeup, including a real secret-exposure incident from that session
  (Neon DB password, JWT secret, and all three bootstrap passwords printed
  into a session transcript by a careless diagnostic command) whose
  rotation the human deferred to themselves — treat those credentials as
  compromised until confirmed rotated.
- **Rate calculation:** `RateCalculationService` (BRD §25) — Fat-vs-SNF and
  TS-based formulas, both implemented exactly as specified, full-precision
  `Rate` used to compute `Amount` (not a rounded intermediate). Config is
  cached locally per centre and reaches the client via
  `MasterDataSyncService`. No fabricated defaults — an unconfigured formula
  computes Rate=0/Amount=0, never a guessed value.
- **Offline-first:** `Session.IsOffline` gates both `SyncEngineService` and
  master-data sync. Offline login uses Argon2id + DPAPI
  (`OfflineCredentialStore`, `CurrentUser` scope) — the raw password is
  never stored, only the salted hash inside a DPAPI-protected blob. Online
  login always takes priority; offline is only a fallback on genuine
  network failure, never on a real (even negative) server response.
- **Authentication/authorization:** JWT (HMAC-SHA256, 8h expiry, no refresh
  tokens) carries no roles/permissions/centre claims — those are resolved
  fresh from the database on every request via
  `ICurrentUserAccessor`/`RequestUser`. Every protected cloud endpoint
  carries an explicit `[RequirePermission(code)]` (14 codes total) plus
  `CentreAccessGuard` for centre scoping — this is the sole real
  authorization boundary. The Windows client's own permission checks are
  UX-only convenience, never a security control (see "Security baseline"
  below).
- **Recently fixed (2026-09-12):** a stale `CloudApi:BaseUrl`
  (`localhost:5000` instead of the Docker `localhost:8081`) was the shared
  root cause of "login shows offline," a blank rate calculator, and "app
  appears offline." Also fixed: History screen missing Rate/Amount columns,
  no Source/Vehicle management UI for Manager/Admin (server-side
  permissions already existed), and Enter key not submitting login
  (`IsDefault="True"`). Full writeup: `STATUS.md` "Application Bug Fixes
  (2026-09-12)".
- **Render deployment: BLOCKED, not merely "not started" (as of
  2026-09-15).** The GitHub repository is owned/controlled by the CEO, and
  the current session's operator does not have the access needed to
  connect the private repo to Render. This is an access/permissions
  blocker, not a technical one — the Docker image and Neon backend are
  both already deployment-ready (see the Neon bullet above and
  `HOW_TO_RUN.md` §10). Do not attempt to work around this (e.g. by
  requesting elevated access, forking, or making the repo public) without
  being explicitly asked; do not perform a Render deployment without being
  explicitly asked, either.
- **Other remaining gaps:** no in-app password-change/reset endpoint
  exists anywhere (rotating any account's password today requires direct
  database access with the same `IPasswordHasher` the app uses at
  runtime); a real `POST /auth/login` round-trip against Neon's now-real
  accounts has not actually been exercised (verified at the data layer
  only, by the human's own choice — see `STATUS.md`); literal WPF GUI
  mouse/keyboard interaction not verifiable in any environment used so far
  (verification instead uses real production service classes against a
  real running API, plus a real `.exe` launch confirming clean startup —
  see `STATUS.md` "UI/UX Redesign (2026-09-15)"); physical Ekomilk
  KAM98-2A serial hardware link not yet verified (payload *decode* is
  verified against real sample frames).
- **Next intended step:** obtain Render-connect access from the repo
  owner, then deploy — see `HOW_TO_RUN.md` §10 and `STATUS.md`
  "Checkpoint" → NEXT for the ordered list once unblocked. Do not perform
  Render deployment without being explicitly asked. Production user
  bootstrap itself is done (see the Neon bullet above); if a new
  centre/account needs bootstrapping later, reuse
  `ProductionBootstrapSeeder` via `Bootstrap__*` env vars (see
  `HOW_TO_RUN.md` §9) rather than inventing a new mechanism. A UI/UX
  redesign pass (2026-09-15) also landed — see `STATUS.md` "UI/UX Redesign
  (2026-09-15)" / "UI/UX Refinement Pass (2026-09-15)" — purely
  presentational, no domain/application/infrastructure/rate/quality/sync
  logic touched, 190/190 tests unaffected.

## Testing philosophy

- **Real branches, not just the happy path.** Every layer should cover
  success, validation failure, a real error condition (COM port
  unavailable, cloud unreachable, malformed serial data), and
  boundary/empty states — not just "it builds" or "the call succeeds."
- **Two dependency tiers, both real, kept explicit.** This repo already
  does this correctly — keep doing it: the Windows-client test project
  (`tests/CCMC.Tests`) exercises real `Microsoft.Data.Sqlite` against a
  real (temp-file) database rather than mocking the ORM layer, because
  the thing worth proving is atomicity/idempotency under a real engine.
  The cloud test project (`tests/CCMC.Cloud.Api.Tests`) runs the actual
  ASP.NET Core startup pipeline (`WebApplicationFactory<Program>`,
  real migrations, real seed) against a dedicated `ccmc_cloud_test`
  database — **never** `ccmc_cloud_dev`. Don't introduce a mocked-DB
  layer to make tests "faster" — the real-engine tests are what caught
  both runtime bugs documented in STATUS.md ("CC-MC Cloud Backend" →
  "Fixed Bugs"); a mock would have passed both times regardless.
- **Document known gaps instead of hiding them.** If something isn't
  covered — a device parser that doesn't exist yet, an endpoint with no
  idempotency mechanism — write it down (STATUS.md "Known Limitations" /
  context.md "Open Questions" are the existing pattern) rather than
  letting the absence of a red test imply the absence of a problem.
- **One command runs everything.** `dotnet test CCMC.sln` already is
  that command for this repo — it runs both the Windows-client and
  cloud-backend suites and prints one pass/fail count. Keep it that way;
  don't let a new test project need its own separate invocation to be
  included in "the tests pass."
- **Hardware-in-the-loop tests are their own tier, not a substitute for
  the above.** No physical Videocon scale or milk analyser exists in any
  dev environment today — tests that would need one are explicitly
  listed as not-yet-covered (STATUS.md "Tests Completed"), not silently
  skipped or faked with synthetic device data.

## CI

- No CI workflow exists in this repo yet (`.github/workflows` is empty).
  When one is added, the required job should run `dotnet build CCMC.sln`
  and `dotnet test CCMC.sln` on every push and PR — both are already
  fast and self-contained enough for that (no live Postgres needed for
  the Windows-client suite; the cloud suite needs a Postgres service
  container, which GitHub Actions supports natively).
- Anything that needs real hardware (a physical scale/analyser) or a
  manual step belongs in a separate, non-blocking job once it exists —
  don't gate every PR on a dependency most dev machines and CI runners
  don't have.
- Prefer one required "summary" check over requiring each job
  individually — simpler to wire into branch protection later.

## Documentation consistency

- The BRD is the source of truth for scope (see "Where to look" above).
  `context.md`, `STATUS.md` and `README.md` are technical companions —
  fix them to match the BRD when they diverge, not the other way round,
  unless the divergence reveals the BRD itself is stale (the BMC-vs-
  Chilling-Centre correction in BRD v2.1 §20 is the example of this: the
  BRD's own scope framing was wrong, so it was fixed first, with a
  version bump and changelog line, before anything else was touched).
- When any of these documents changes materially, bump its version
  header and add a one-line changelog entry in the header itself saying
  what changed and why (see the BRD's own "Changelog (v2.1): ..." line
  for the pattern). A future session — human or Claude — should be able
  to tell what's new without diffing.
- `README.md`'s job is to get a new contributor from clone to a running,
  tested system (`dotnet build`, `dotnet test`, `dotnet run` for the
  cloud API). If it describes commands or state that no longer work,
  that's a bug — fix it the same way you'd fix a broken build script.

## Code style defaults

- **No premature abstraction.** Three similar lines beats a shared
  helper built for a hypothetical second caller. This repo already made
  this call explicitly at the architecture level — hand-rolled SQLite
  over EF Core, a small hand-rolled `ILoggerProvider` over a logging
  package — both because the alternative's machinery wasn't justified by
  this project's actual size. Apply the same judgment at the function
  level.
- **No speculative error handling.** Validate at real boundaries (serial
  port I/O, HTTP calls to the cloud, user input) and trust internal
  domain/framework guarantees elsewhere. Every error condition in
  `context.md`'s device/sync model is a boundary that was actually
  identified — COM port unavailable, device disconnected, malformed
  serial data, cloud unreachable — not a generic try/catch wrapped
  around code that cannot fail.
- **Comment the why, not the what.** A well-named method or class
  already says what it does. Only comment a genuinely non-obvious
  constraint — `ReadingProvenanceTracker`'s doc comment (explaining why
  "MANUAL the moment either field is edited" is the least-ambiguous
  behavior a single `ReadingSource` can represent) is the model to
  follow, not the exception.
- **No half-finished implementations or speculative feature flags.** If
  a requirement doesn't exist yet, don't build for it. This is also
  already a named project rule (STATUS.md "Engineering Rules": "no fake
  device data," "no guessed device protocol") — extend the same
  discipline to any other feature, not just device parsing.
- **Match the existing patterns in the file/module you're editing.**
  Consistency within a codebase beats a "better" pattern used in exactly
  one place.

## Device / serial integration discipline

CCMC-specific, because this repo talks to physical hardware and the
failure modes are different from a normal web/mobile stack:

- **Never fabricate a device reading.** If a protocol isn't verified,
  the adapter throws `DeviceProtocolNotEstablishedException` — it does
  not return a plausible-looking number. This is the single
  most-enforced rule in the codebase (see STATUS.md "Architecture
  Decisions" and "Engineering Rules") and it does not get relaxed for
  convenience, a demo, or a deadline.
- **Never guess a device protocol.** A real parser is built only from
  manufacturer documentation or controlled raw captures from the
  physical device (`RawCaptureLogger`), and ships with captured-frame
  regression tests. See "Parser Development Workflow" in STATUS.md for
  the exact sequence.
- **Exactly one component owns a given COM port**, enforced at runtime
  (`SerialPortOwnershipException` on a second `Acquire()`), not just by
  convention.
- **Treat raw serial bytes as untrusted input**, the same as an HTTP
  request body — validate/frame-detect before trusting them, and never
  let malformed serial data reach the reception workflow or crash the
  application.
- **Business logic stays out of both the UI and the parser layer.** The
  reception workflow (`CCMC.Application`) is the only place quality
  rules and acceptance logic live.

## Git and review hygiene

- Only commit when explicitly asked — draft the diff, let the human
  decide when it lands. (This repo currently has nothing committed
  beyond the initial scaffold — see `git status` before assuming
  otherwise.)
- New commits over amends, unless explicitly asked to amend — an amend
  after a failed pre-commit hook rewrites the wrong thing.
- Stage files by name, not `git add -A`/`.` — a broad add can pull in a
  `.env`, `appsettings.*.json` with a real connection string, or a
  generated `bin`/`obj` artifact that doesn't belong in the diff.
- Before pushing, skim what's actually staged (`git status`, `git diff
  --staged`) for anything that looks like a secret, even behind an
  innocuous filename (e.g. `appsettings.Development.json`).
- Never force-push, `reset --hard`, or skip hooks without the human
  explicitly asking for that specific action in that specific instance.

## Security baseline

- Parameterized queries only — `Microsoft.Data.Sqlite` parameters on the
  client, EF Core/Npgsql on the cloud side — never string-concatenated
  SQL, on either side of the stack.
- Authorization is enforced server-side, per endpoint/action
  (`[RequirePermission(code)]` + `CentreAccessGuard`, fails closed if an
  action forgets the attribute). The Windows client's own permission
  checks are UX-only, exactly as documented in `context.md` — never
  treat a client-side check as an actual security control.
- Secrets (`Jwt:Secret`, `ConnectionStrings:CcmcDb`, any future API key)
  come from environment variables or configuration outside source
  control — never hardcoded, and never committed in a non-dev
  `appsettings.*.json`.
- TLS in transit for all cloud calls; DPAPI-protected storage at rest
  for anything sensitive kept locally (the offline-credential
  Argon2id verifier is the existing pattern — see `context.md`
  "Authentication / Authorization"). Never persist a plaintext password
  or treat a cloud access token as an offline-login substitute.
- Treat raw device bytes as untrusted input in the same sense as a
  webhook payload — see "Device / serial integration discipline" above.

## When picking new infrastructure or a cloud target

- Size the cloud API's hosting/database tier to the actual number of
  chilling centres being served, not an imagined national rollout —
  start on the smallest managed tier that fits, document the upgrade
  path.
- Prefer managed Postgres and managed container/app hosting over a
  self-managed VM for the cloud API — undifferentiated ops work is a
  cost, not a feature, for a project this size.
- Keep the cloud API portable (standard EF Core/Npgsql, no
  cloud-proprietary services in the application layer) even once a
  specific host is chosen, so a future migration is a redeploy, not a
  rewrite.
- Any deployment doc must clearly mark what's actually provisioned/
  running today versus a recommended target that hasn't been built yet —
  `context.md`/STATUS.md already do this correctly for the installer
  (WiX MSI chosen, not built) and the cloud API's own deployment
  packaging (none yet); keep that distinction explicit as new
  infrastructure decisions get made.
