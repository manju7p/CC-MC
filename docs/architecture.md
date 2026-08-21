# Architecture (as built - MVP vertical slice)

This is a short, as-built summary. For the full rationale behind these
choices, see `Engineering Analysis/CC-MC_Implementation_Proposal.md` (the
Principal-Engineer-approved proposal this build followed) and
`docs/adr/0001-typeorm-over-prisma.md` for the one deviation from it.

## Components built tonight

- **`apps/api`** - NestJS + TypeScript backend. Modules: `auth`, `rbac`
  (guard + centre-access service, no controllers of its own), `centres`,
  `sources`, `vehicles`, `quality-rules`, `reception` (includes the isolated
  `QualityValidationService`), `audit`, `dashboard`.
- **`apps/web`** - React + TypeScript + Vite frontend. Pages: Login,
  Dashboard, Sources, Vehicles, Reception (the core screen - create a
  transaction, see live ACCEPT/HOLD/REJECT status, Managers can override a
  HOLD inline).
- **`packages/shared-types`** - permission codes, enums, and DTOs shared by
  `apps/api` and `apps/web`, built as a dual CJS/ESM package (see below) so
  both a CommonJS Nest/Jest backend and a Vite/ESM frontend can consume it
  without a bundler-specific hack.
- **PostgreSQL** - single source of truth, managed via TypeORM migrations
  (`apps/api/src/migrations`), not `synchronize`.

## Components explicitly NOT built tonight (by design, per scope)

- **`apps/gateway`** (Local Device Gateway) - not created. No RS232/Bluetooth
  code, no device adapters, no offline sync subsystem exist yet. This was
  an explicit non-goal (Rule 1: no invented hardware specs; real device
  integration is blocked on the business supplying actual scale/analyser
  specs - see the prior engineering analysis, question Q2).
- **`packages/device-adapters`** - not created as a real package. Nothing to
  put in it yet without real device specs or at least a simulator design,
  and an empty placeholder package would violate the "don't create hundreds
  of empty folders" instruction. Reserve the path when gateway work starts.
- Formatted/printable receipts, advanced reporting, notifications, the full
  admin UI (user/role/permission management screens) - all explicit
  tonight non-goals.

## Why shared-types needs a dual build

`apps/api` runs under CommonJS (NestJS + ts-node + Jest). `apps/web` runs
under Vite's native ESM dev server. A single CommonJS build of
`shared-types` broke Vite's static analysis of named exports (`PERMISSIONS`,
the enums) - this was caught during Checkpoint 6 browser testing, not
assumed away. The fix: `packages/shared-types` builds twice
(`tsconfig.cjs.json` -> `dist/cjs`, `tsconfig.esm.json` -> `dist/esm`), and
`package.json`'s `exports` map picks the right one per consumer.

## RBAC enforcement (as built)

Exactly the two-part model the engineering rules specified - no policy
engine:

1. **Permission check** - `PermissionGuard` (backend-authoritative) reads a
   `@RequirePermission(code)` decorator and checks the caller's permission
   set, computed fresh from the database on every request via
   `AuthService.loadUserContext` (not cached in the JWT).
2. **Centre-scope check** - `CentreAccessService.assertCanAccess`, called
   explicitly inside each service method (not a generic interceptor),
   because the relevant centre differs by endpoint shape (request body on
   create, the fetched entity's `centreId` on read/update). List endpoints
   filter by the caller's accessible centre IDs directly in their own
   query rather than through a shared helper - each one's `where` shape is
   different enough (a plain array of `{centreId}` OR-conditions here, a
   query-builder `IN (...)` there) that a generic wrapper added a layer of
   indirection without saving real duplication; a `scopedWhere()` helper
   was tried and removed during the pre-release hardening review once it
   turned out nothing actually called it.

The frontend's `usePermission()`-equivalent (`AuthContext.hasPermission`) is
UX-only, exactly as instructed - it hides buttons; it never gates a request,
and every one of its checks is independently re-verified server-side (see
the automated cross-centre-denial and unauthorized-override tests in
`apps/api/test/app.e2e-spec.ts`).

## Authentication hardening (added during the pre-release review)

Two things the hardening review found and fixed, both covered by e2e tests:

- `JWT_SECRET` has no hardcoded fallback. Both `auth.module.ts` (signing)
  and `jwt.strategy.ts` (verifying) read it through `requireEnv()`
  (`src/env.ts`), which throws at boot if it's unset - a deployment that
  forgot to configure it fails immediately instead of silently signing
  tokens with the default committed in `.env.example`.
- `AuthService.login` always runs a bcrypt comparison, even for an email
  that doesn't match any user, against a fixed dummy hash - otherwise
  "no such user" would respond measurably faster than "wrong password"
  (bcrypt is deliberately slow), leaking which emails are registered via
  response timing.

## Database

See `docs/assumptions.md` for every place the schema had to pick an MVP
default the BRD didn't specify. The schema itself lives in
`apps/api/src/**/entities/*.entity.ts`, with the generated migration in
`apps/api/src/migrations/`. Relational integrity throughout: every foreign
key is explicit, uniqueness is enforced at the database level (user email,
source code, vehicle number, permission code, role name,
role+permission pair, user+role pair, user+centre pair, quality-rule
parameter+centre pair), and JSON is used in exactly one place
(`AuditLog.oldValue`/`newValue`) where the data is genuinely heterogeneous
across resource types - per the "avoid unnecessary JSON fields" guideline.
