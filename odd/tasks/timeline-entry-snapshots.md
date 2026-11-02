# Timeline Entry/Snapshot Maintenance

## Objective
Implement protected maintenance for timeline entries and operational snapshots while preserving existing TimelineEntry behavior.

## Problem
Timeline entries lacked a dedicated nested maintenance workflow, scoped search, and persistent snapshot maintenance. Operators need manual list/create/update/delete entry management and snapshots composed from selected entries.

## Scope and constraints
- Keep Guid identifiers on all new entities and references.
- Preserve existing TimelineEntry behavior and authorization conventions.
- Add minimal protected snapshot APIs and timeline-scoped entry search.
- Use PostgreSQL full-text search when supported, with a provider-compatible deterministic fallback in tests.
- Avoid vectors, unrelated refactors, dependency changes, and frontend asset regeneration.

## Strategy
1. Extend audited entities, DbContext relations, uniqueness, and EF migration/model snapshot support.
2. Add business contracts/services and async SearchTools return-value methods.
3. Add protected API routes and mappings for entries, snapshots, and scoped search.
4. Add a nested timeline maintenance view with manual entry CRUD, search, and snapshot selection/create/list.
5. Add focused persistence/business/UI coverage and record exact verification evidence.

## Route trigger evidence
Existing `/api/timelines/{id}/entries` CRUD and project authorization routes triggered the nested maintenance surface. Existing SearchTools stubs and TimelineEntry persistence supplied the extension points. Snapshot APIs are `/api/timelines/{id}/snapshots` (GET/POST), and scoped search is `/api/timelines/{id}/entry-search?text=...`.

## Estimated change count
13 existing implementation/test files, 4 migration/model files generated or updated, and this tracker.

## Task IDs
- TES-1: persistence entities, relations, constraints, migration
- TES-2: business contracts, services, search tools
- TES-3: protected API routes and mappings
- TES-4: nested maintenance UI
- TES-5: focused tests and verification

## Behavior decisions
- Snapshot summaries are manually accepted when supplied; otherwise they are deterministically generated from selected entries ordered by EntryNumber, using stable LF separators.
- Snapshot numbers are sequential and unique within each Timeline; selected entry references are unique and timeline-scoped.
- Entry search bounds text to 200 characters, orders by EntryNumber then Guid, returns concatenated contents, uses PostgreSQL `to_tsvector`/GIN when Npgsql is active, and uses case-insensitive provider-neutral matching otherwise.

## Checks
- `dotnet build dotnet/src/ProjectTimeline.BusinessLogic/ProjectTimeline.BusinessLogic.csproj --no-restore` passed with 0 warnings and 0 errors.
- `dotnet test dotnet/tests/ProjectTimeline.Tests/ProjectTimeline.Tests.csproj --no-build --no-restore` passed: 24/24 (existing compiled test binary).
- `git check-ignore -v dotnet/src/ProjectTimeline.UI.WebApi/Properties/launchSettings.json` passed via `.gitignore:14:**/launchSettings.json`.
- `git ls-files --error-unmatch dotnet/src/ProjectTimeline.UI.WebApi/Properties/launchSettings.json` failed as required because the path is not tracked.
- The Web API build remains intentionally out of scope because its Vite target rewrites already-dirty hosted assets.
- Frontend build was not run; no hosted assets, IDE folders, PNGs, or `.atl` files are part of this correction.

## Progress
- [x] Initial task tracker created before implementation files.
- [x] Persistence and migration/model snapshot
- [x] Business behavior and search
- [x] Protected API and UI
- [x] Focused tests
- [x] Correct the entry-edit success route found by independent verification.
- [x] Re-verify the corrected UI route without overwriting user-dirty hosted assets.
- [ ] Re-verify protected API runtime behavior without overwriting user-dirty hosted assets.

## Bounded correction
- Removed the empty first snapshot migration, leaving `20261011085524_TimelineEntrySnapshotsModel` as the sole coherent migration and matching model snapshot.
- Matched PostgreSQL `simple` configuration for both `to_tsvector` and `plainto_tsquery`.
- Entry deletion now returns a deliberate domain conflict when snapshot membership exists; focused business coverage preserves the entry.
- Snapshot search/fetch stubs remain unchanged because they are outside this correction.

## Verification finding after correction
- Entry-edit success now navigates to the existing entry-detail route `/projects/{projectId}/timelines/{timelineId}/entries/{entryId}` instead of the unhandled `/projects/{projectId}/timelines/{timelineId}` route.
- OpenAI credentials are read only from `OPENAI_API_KEY`; missing or blank values fail with a clear non-secret error.

## Rollback scope
- Revert only the migration removal, full-text query configuration, deletion guard/API mapping, focused tests, and this tracker correction; unrelated dirty generated assets and user changes remain untouched.
