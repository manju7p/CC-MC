# Assumptions Made During Implementation

The BRD is ambiguous or silent in several places. Per the engineering rules
for this build, nothing ambiguous was silently decided without recording it
here. Each entry states the question, the assumption made for the MVP, and
why. None of these are final business decisions - they are the minimum
choices needed to make the application runnable tonight, and should be
confirmed with the business before they're relied upon beyond the MVP.

### #auto-reject-vs-hold - Does the system ever auto-reject, or only auto-hold?

The BRD (Section 38) lists ACCEPTED/REJECTED/HOLD as the three statuses but
never states which are automatic. Section 13's own diagram shows only one
automatic path: quality outside limits -> HOLD -> Manager Review -> Accept
or Reject. **Assumption:** the system never auto-rejects. `QualityValidationService`
can only return ACCEPTED or HOLD; REJECTED is reachable only through a
Manager's override of a HOLD (`ReceptionService.override`). This is grounded
directly in the BRD's diagram, not invented from nothing, but it is still an
interpretation - confirm with the business whether any condition should
cause an immediate system-level REJECTED with no human in the loop.

### #centre-assignment-model - How is "assigned to all centres" represented?

BRD Section 9 says a user may be assigned to one centre, multiple centres, or
all centres, without specifying a data shape. **Assumption:** one
`user_centre_assignments` row per centre a user is scoped to, plus a
separate `allCentres = true, centreId = null` row for organization-wide
access. Postgres allows multiple NULL `centreId` rows under the unique
constraint, so "only one ALL row per user" is an application-level
convention (enforced by `seed.ts` and by never writing a second ALL row),
not a database constraint. If a future admin UI writes these rows directly,
it must preserve this convention.

### #transaction-numbering - What is the real transaction number format?

The BRD's `MCC-2026-000123` example (Section 35) is explicitly illustrative.
**Assumption:** the MVP uses `{centreCode}-{databaseId}` (e.g. `BLR-CC-01-17`),
assigned by the cloud database after the row is inserted, so two concurrent
creations can never collide - no sequence-per-year, no offline generation
scheme. This intentionally sidesteps the harder offline-numbering problem
(flagged as Q7 in the prior engineering analysis) since the gateway does not
exist yet in this slice. Revisit when the Local Device Gateway and its sync
design are actually built (see the implementation proposal, Section 8).

### #dashboard-day-boundary - What does "today" mean for the dashboard?

Not specified in the BRD; the deployment context is Indian dairy chilling
centres. **Assumption:** "today" is the IST (UTC+5:30) calendar day,
computed in `DashboardService` without a timezone library (a small,
hardcoded offset calculation). If centres ever operate outside India, this
needs to become per-centre configurable rather than a single hardcoded
offset.

### #role-set - Which roles are seeded for the MVP?

BRD Section 5 lists seven illustrative roles and says the set is
"recommended," with roles meant to be configurable. Tonight's scope
explicitly asked for three: **Operator, Manager, Admin**. The permission
grants for each (see `src/seed.ts`) are this implementation's best-effort
mapping from the BRD's Section 8 example RBAC matrix, narrowed to the
permission codes that exist in this slice (no Sources/Vehicles/Quality/Device/
User/Role/Audit module split beyond what's listed in `shared-types`
`PERMISSIONS`). Confirm the exact default grants with the business before
treating them as final - Section 8 of the BRD itself is labeled "Example."

### #rejection-reason-free-text - Is the reject/hold reason free text or a master list?

BRD Section 49 names a `RejectionReason` entity but nowhere describes it as
a configurable list with its own management screen. **Assumption:** for the
MVP, `reason` on `MilkReceptionTransaction` and `TransactionOverride` is
free text, not a foreign key to a master table. No `RejectionReason` table
was built. This was a deliberate scope cut (Rule 4: don't overbuild) rather
than an oversight - add the master table and a picklist UI if/when the
business confirms standardized reasons are required for reporting.

### #quality-rule-scope - Are quality limits global, per-centre, or per-milk-type?

BRD Section 37's example table (FAT/SNF/Temperature) has no scoping
dimension, and BRD Section 23 lists more parameters (CLR, Density, Added
Water, Protein, Lactose) than the example limits table covers. **Assumption:**
the MVP seeds only global (centre-independent) rules for FAT, SNF, and
Temperature - the three the "tonight's scope" instructions explicitly
named. The schema (`QualityRule.centreId`, nullable) already supports a
centre-specific override, and `QualityRulesService.resolveRulesForCentre`
already resolves centre-specific-over-global, but no centre-specific rows
are seeded, and no rules exist yet for CLR/Density/etc. Confirm whether milk
type (cow vs. buffalo) needs its own dimension before this goes further.

### #receipt-format - Is the receipt a formatted document or a data response?

BRD Section 41 lists the fields a receipt must contain but not its format
(printed, PDF, on-screen). **Assumption:** for tonight, the created
transaction response (returned by `POST /reception`) already contains every
field BRD Section 41 requires, and the frontend displays it as a result
banner. No separate formatted/printable receipt artifact was built - this
was explicitly listed as a non-goal for tonight ("formatted PDF receipts").

### #source-vehicle-centre-scoped - Are sources and vehicles global or per-centre?

Not explicitly stated by the BRD. Given Section 51's example ("Operator001
... Cannot: Access Chilling Centre B") and the RBAC matrix treating
Sources/Vehicles as centre-scoped modules, **assumption:** both `Source` and
`Vehicle` belong to exactly one `ChillingCentre` (a required `centreId`
foreign key), not shared across centres. If a real tanker regularly services
multiple centres, this will need to change to a many-to-many relationship.
