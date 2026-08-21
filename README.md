# CC-MC — Chilling Centre Milk Collection & Device Integration System

A working MVP vertical slice for chilling-centre milk intake: an operator
logs in, records a milk reception (quantity/FAT/SNF/temperature) against a
source and vehicle, the system auto-validates quality and accepts or holds
it, a manager can override a HOLD, and everything is centre-scoped,
permission-checked, and audited. See `BRD/` for the original business
requirements and `Engineering Analysis/` for how this slice was scoped
from it.

**Implemented:** login (JWT), backend-authoritative RBAC (User -> Role ->
Permission, plus User -> Centre Assignment), Source/Vehicle CRUD, quality
validation (FAT/SNF/Temperature against configurable min/max rules),
manual-entry milk reception with automatic ACCEPT/HOLD, Manager override
of a HOLD to ACCEPTED/REJECTED, an audit trail for every mutation, and a
same-day dashboard - all centre-scoped end to end (see the cross-centre
denial tests in `apps/api/test/app.e2e-spec.ts`).

**Not implemented** (explicit non-goals for this slice, not oversights):
the Local Device Gateway, offline sync, formatted/printable receipts,
reporting beyond the dashboard, notifications, and an admin UI for
managing users/roles/permissions (seeded directly instead - see below).

**Blocked on hardware specs:** real scale/analyser device integration
(RS232, Bluetooth, or any other wire protocol) is not implemented and
cannot be, pending real manufacturer/protocol specifications from the
business - see `docs/architecture.md` and the prior engineering analysis
(question Q2). Nothing here should be read as an assumption about what
that protocol will look like.

## Prerequisites

- Node.js 20+ and pnpm (`corepack enable` or `npm install -g pnpm`)
- PostgreSQL 16 running locally (or reachable via `DATABASE_URL`)

## First-time setup

```bash
# 1. Install all workspace dependencies
pnpm install

# 2. Build the shared types package (both apps depend on its compiled output)
pnpm --filter @cc-mc/shared-types build

# 3. Create the two local databases. `.env.example` (below) assumes a
#    `postgres` role with password `postgres` on localhost:5432 - the
#    exact form below matches that. If your local Postgres uses peer/trust
#    auth for your current OS user instead, plain `createdb ccmc_dev` may
#    work without the flags; if you're not sure, use the explicit form -
#    it works either way and avoids a `createdb` that hangs prompting for
#    a password you didn't expect.
PGPASSWORD=postgres createdb -U postgres -h localhost ccmc_dev
PGPASSWORD=postgres createdb -U postgres -h localhost ccmc_test

# 4. Configure the API's environment
cp apps/api/.env.example apps/api/.env
# edit apps/api/.env if your local Postgres uses different credentials/port -
# every variable the API reads is in .env.example, nothing else is needed.
# apps/api/.env.test is committed as-is (test DB only, no real secrets).

# 5. Run database migrations (against ccmc_dev, via DATABASE_URL in apps/api/.env)
cd apps/api
pnpm migration:run

# 6. Seed reference/demo data (roles, permissions, 2 centres, 4 users, quality rules, sample sources/vehicles)
pnpm seed
```

Seeded users (all local-dev-only passwords):

| Email | Password | Role | Centre |
|---|---|---|---|
| admin@ccmc.local | Admin@12345 | Admin | All centres |
| manager1@ccmc.local | Manager@12345 | Manager | Bangalore |
| operator1@ccmc.local | Operator@12345 | Operator | Bangalore |
| operator2@ccmc.local | Operator@12345 | Operator | Mysore |

## Running locally

```bash
# Terminal 1 - API (http://localhost:3000)
cd apps/api
pnpm start:dev

# Terminal 2 - Web (http://localhost:5173, proxies /api to the API)
cd apps/web
pnpm dev
```

The two-terminal form above is the primary documented way to run both -
each process's own log output stays visible and Ctrl-C in that terminal
stops that process normally. If you'd rather use one terminal, `pnpm
dev:api & pnpm dev:web & wait` (from the repo root) starts both in the
background with interleaved output; stop them with `pkill -f "nest
start"; pkill -f vite` (Ctrl-C there only interrupts the `wait`, not the
two background jobs).

The frontend never hardcodes an API URL: in dev, Vite's own proxy (see
`apps/web/vite.config.ts`) forwards `/api/*` to `http://localhost:3000`;
in the production build (`apps/web/dist`), the app calls relative `/api/*`
paths with no base URL at all, so it works unmodified as long as whatever
serves `dist/` also proxies `/api` to the NestJS API - there is no
separate frontend `.env` or build-time API-URL variable to configure for
this slice.

Open http://localhost:5173, log in as `operator1@ccmc.local`, create a
reception (try FAT outside 3.0-6.0 to see it go to HOLD), then log in as
`manager1@ccmc.local` in another browser/incognito window to override the
HOLD. Seeded demo credentials are in the table above - deliberately not
also shown on the login page itself, so the compiled frontend bundle
doesn't ship working passwords.

## Tests

```bash
cd apps/api

# Unit tests (business logic - quality validation, etc.)
pnpm test

# Integration/e2e tests (real Postgres test DB, real RBAC guards, real HTTP requests)
pnpm test:e2e
```

`test:e2e` runs against `ccmc_test` (configured in `apps/api/.env.test`),
resetting and reseeding the schema before the suite via
`test/global-setup.ts`. It does not touch `ccmc_dev`.

## Repository layout

```
apps/
  api/      NestJS backend (see apps/api/src for module boundaries)
  web/      React + Vite frontend
packages/
  shared-types/   DTOs, enums, and permission codes shared by api + web
docs/
  architecture.md   As-built architecture summary
  assumptions.md    Every MVP default assumed where the BRD was ambiguous
  adr/              Architecture decision records
BRD/                 Original business requirements document
Engineering Analysis/  Prior analysis + implementation proposal
```

`apps/gateway` and `packages/device-adapters` do not exist yet - see
`docs/architecture.md` for why, and the implementation proposal for the
plan once real device specifications are available.

## Known limitations of this slice

See `docs/assumptions.md` for the full list. The short version: transaction
numbering is a simplified `{centreCode}-{id}` scheme (not the offline-safe
scheme a real gateway will need), quality rules are seeded globally only
(no per-centre or per-milk-type overrides yet, though the schema supports
them), rejection/hold reasons are free text, and there is no formatted
receipt, reporting beyond the dashboard, or admin UI for
users/roles/permissions.
