# ADR 0001: TypeORM instead of Prisma

**Status:** Accepted (implementation blocker, decided during the MVP build)

## Problem found

The implementation proposal did not mandate a specific ORM. Prisma was the initial
choice while scaffolding the API (Section 4, "Database Design", of the proposal
describes the schema in ORM-agnostic terms). During Checkpoint 3, `prisma generate`
and `prisma migrate` failed: Prisma's CLI needs to download native query/schema
engine binaries from `binaries.prisma.sh` on first use, and every request to that
host (and its S3 mirror) returned `403 Forbidden` in this build environment. `pnpm
install` itself succeeded (npm registry access is fine) - only the Prisma-specific
binary download step was blocked.

## Why the current approach was insufficient

A network-locked-down build environment is not a one-off inconvenience specific to
this sandbox - the same failure mode hits any CI runner, corporate network, or
air-gapped environment that allowlists npm but not `binaries.prisma.sh`. An ORM
whose migration tooling silently cannot run in that class of environment is a
reliability risk for a project whose deployment environment (per the BRD) is not
yet fully specified.

## What was done instead

Replaced Prisma with **TypeORM** + the `pg` driver. TypeORM is pure JavaScript/TypeScript
with no native binary download step; `typeorm-ts-node-commonjs migration:generate`
and `migration:run` work against a local Postgres using only what `pnpm install`
already fetched from the npm registry.

## Tradeoffs introduced

- TypeORM's query API is repository-based rather than Prisma's fluent, fully
  type-inferred client - slightly more verbose in places (e.g. `createQueryBuilder`
  for the dashboard's `GROUP BY` query instead of Prisma's `groupBy`).
- Decimal columns come back as strings from `pg` (to avoid floating-point precision
  loss), so numeric conversion (`Number(...)`) is handled explicitly in
  `QualityRulesService.resolveRulesForCentre` and `ReceptionService` rather than
  being transparent.
- Circular entity references (e.g. `Role` <-> `RolePermission`) need the
  string-target `@OneToMany("RolePermission", ...)` pattern with `import type`
  rather than Prisma's schema-file approach, which has no such circularity concern.

## What did not change

The approved architecture (Nest/React/Postgres, monorepo, shared-types package,
the logical schema described in the proposal) is unchanged. This is a library
swap beneath the "technology choices" line, not a redesign of any system
boundary.
