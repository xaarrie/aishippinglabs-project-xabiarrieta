# Role Catalog and Relationship Selectors

## Objective
Replace the user-role association with a selectable role catalog and expose editable entity relationships through accessible combo controls across the application.

## Decisions
- The role catalog initially contains `Admin` and `Consultant`.
- The existing administrator user is backfilled to `Admin`.
- `User` has one required `IDUserRole` selected from `UserRole`.
- `Project.OwnerId` and `TimelineEntry.AuthorId` can be reassigned only by an Administrator; for other users they remain session/route-derived.
- A project's current owner cannot be removed as a collaborator.

## Scope
Update entities, EF Core configuration and forward migration, seed/backfill behavior, contracts/services/API/JWT authorization, React forms/grids, hosted assets, and automated tests. Do not modify an already-applied initial migration. Preserve existing authorization boundaries and unrelated project files.

## Constraints
- Apply a forward database migration that upgrades populated databases safely.
- Preserve existing physical database constraints unless the new cardinality requires a replacement.
- Use combo/select controls for editable relationships shown by the UI; display related labels rather than raw IDs in grids.
- Use Ibernova DS tokenized web-admin patterns; no hardcoded visual values or inline SVGs.
- TDD mode: not configured; run migration, API, UI, build, and browser checks.
- Delivery strategy: ask-on-risk. The feature should be sliced into coherent work units if it exceeds the advisory 400 authored-line estimate.

## Tasks
- [x] ROLE-01 — Normalize the data model, EF Core mapping, migration/backfill, and fresh-database seed to a `UserRole` catalog with one required `Users.IDUserRole`; map current Administrator data to Admin and retain Consultant. Route: delegated; trigger: multiple non-trivial persistence files. Outcome: focused persistence tests, solution build, local Development migration, and `git diff --check` passed.
- [x] ROLE-02 — Update contracts, authorization, JWT claims, API role catalog endpoint, validation, and persistence/business tests for one selected role. Route: delegated; trigger: multiple non-trivial server/test files. Outcome: singular role contracts, Admin policy/claims, protected `/api/admin/roles`, selected-ID validation, and focused contract/persistence coverage implemented; React selector remains ROLE-03.
- [x] ROLE-03 — Replace user role text entry with a role combo; show selected role in grids, and add relationship labels/combo selectors for editable UI relationships. `Project.OwnerId` and `TimelineEntry.AuthorId` reassignment is Administrator-only and enforced server-side; other users retain session/route-derived relationships. Recovery-token and merge-review authorship remain non-editable. Route: delegated; trigger: React UI, styles, hosted tests, and generated assets. Outcome: focused tests, client build, static-assets-disabled solution build, and `git diff --check` passed; the standard solution checks remain blocked by the pre-existing modified WebApi csproj's stale generated asset reference.
- [ ] ROLE-04 — Upgrade-test an existing database, verify fresh seeding and authenticated workflows in Playwright, and run focused/full checks. Route: delegated verification; trigger: migration, browser, and test actions.
- [x] ROLE-05 — Correct database-current authorization; add primary-timeline selection and full collaborator maintenance while preventing removal of the current project owner. Route: delegated; trigger: persistence, API, UI, migration, and test files. Outcome: database-current authorization replaces stale JWT role claims; nullable primary timelines use an indexed SetNull FK and project-membership validation; project-scoped active-user selectors support collaborator add/update/remove with owner-removal protection; focused tests, local migration, client build, static-assets-disabled build, and diff checks passed.

## Acceptance criteria
- `UserRoles` is a role catalog with no `UserId`; `Users.IDUserRole` selects exactly one catalog role.
- The existing administrator is assigned to `Admin`; only `Admin` and `Consultant` are initially selectable.
- Role selection is validated by the API and represented consistently in JWT claims, authorization, and UI navigation.
- Editable relationships are rendered as labels in grids and selected through accessible combos in forms; owner/author reassignment is administrator-only.
- Existing populated databases upgrade without losing the administrator's access.
- User lists remain authorized after a role migration; projects can select one primary timeline and maintain collaborators without removing their current owner.

## Verification
- Targeted migration/backfill and authorization tests
- `dotnet test dotnet/ProjectTimeline.sln --no-restore`
- `npm run build --prefix dotnet/src/ProjectTimeline.UI.WebApi/ClientApp`
- Authenticated Playwright checks for role and relationship combos
- `git diff --check`

## Progress
ROLE-01, ROLE-02, ROLE-03, and ROLE-05 are complete. The normalized server contract exposes one selected role ID and displayable role code/name; only `Admin` and `Consultant` are selectable. The protected `/api/admin/roles` endpoint supplies the catalog, and admin user create/update reject unknown role IDs. React now uses the protected role catalog combo, displays role/owner/project labels, keeps nested relations contextual, and exposes owner reassignment only to Administrators while the API rejects non-administrator owner/author reassignment. ROLE-05 adds database-current authorization, a nullable primary timeline, and collaborator maintenance with current-owner protection. The local Development database applied `202610020001_NormalizeUserRoleCatalog` and `202610020002_ProjectPrimaryTimeline`.

## Next step
Run final ROLE-04 upgrade/browser validation; ROLE-04 remains pending.
