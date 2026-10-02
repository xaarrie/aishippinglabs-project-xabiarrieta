# User and Project Data Grids

## Objective
Make the Users and Projects maintenance pages professional web-admin grids: aligned, searchable, sortable, filterable, and responsive, following Ibernova DS conventions.

## Problem and scope
The current lists are static HTML tables with implicit API ordering, no filtering or sorting, crowded action cells, and unstructured forms. This work covers the authenticated Users and Projects list/create/edit experience only. It does not change database schema, authorization rules, or generated hosted assets directly.

## Constraints
- Use Ibernova DS semantic CSS tokens; do not hardcode colours or add inline SVGs.
- Preserve existing routes, API contracts, and soft-deactivation behaviour.
- Do not edit generated `wwwroot/assets` files directly.
- TDD mode: not configured; run the ordinary focused and solution-level checks below.
- Delivery strategy: ask-on-risk. Forecast: under 400 authored changed lines if feasible; use coherent behavior over artificial splitting.

## Tasks
- [x] GRID-01 — Replaced static list behavior with accessible client-side search, filters, tri-state sorting, deterministic ordering, and compact pagination for Users and Projects. Route: delegated; trigger: App.jsx and styles.css are non-trivial files.
- [x] GRID-02 — Improved list and form alignment: semantic status/role treatments, constrained descriptions, contextual row actions, and grouped field layouts. Route: delegated; trigger: App.jsx, styles.css, and hosted UI tests are non-trivial files.
- [x] GRID-03 — Validated the rebuilt hosted UI in Playwright and ran focused/full .NET checks. Route: delegated verification; trigger: browser and build/test actions.

## Acceptance criteria
- Both registers support accessible text search, relevant filters, sortable columns, and page-size/pagination controls.
- Table headers expose sort state and tables remain usable at desktop and narrower viewport widths.
- User status and roles are visually scannable without colour-only meaning.
- Project descriptions do not force the action column to overflow.
- User and project create/edit fields use a consistent professional grid and communicate immutable identifiers.
- Existing authenticated navigation and maintenance routes remain functional.

## Verification
- `npm run build --prefix dotnet/src/ProjectTimeline.UI.WebApi/ClientApp`
- `dotnet test dotnet/tests/ProjectTimeline.Tests/ProjectTimeline.Tests.csproj --filter FullyQualifiedName~HostedUiTests`
- `dotnet test dotnet/ProjectTimeline.sln --no-restore`
- Playwright authenticated checks for Users and Projects lists
- `git diff --check`

## Progress
GRID-01 and GRID-02 implemented. The bounded acceptance correction now treats zero filtered records like zero source records, rendering the existing accessible empty state instead of table/pagination chrome. Project descriptions are line-clamped with a full `title`, and row actions remain single-line so normal rows stay compact. Changed authored files: `dotnet/src/ProjectTimeline.UI.WebApi/ClientApp/src/App.jsx`, `dotnet/src/ProjectTimeline.UI.WebApi/ClientApp/src/styles.css`, `dotnet/tests/ProjectTimeline.Tests/HostedUiTests.cs`, and this task file. The client provides reusable accessible Users/Projects grids with deterministic sorting, tri-state `aria-sort`, search, relevant filters, pagination, semantic status/role treatments, constrained descriptions, readable actions, and grouped create/edit fields with immutable identifier context.

Checks run:
- `npm run build --prefix dotnet/src/ProjectTimeline.UI.WebApi/ClientApp` — passed (`vite build`, 30 modules transformed).
- `dotnet test dotnet/tests/ProjectTimeline.Tests/ProjectTimeline.Tests.csproj --filter FullyQualifiedName~HostedUiTests` — passed (1/1).
- `git diff --check` — passed (only existing LF-to-CRLF warnings reported).
- `dotnet test dotnet/ProjectTimeline.sln --no-restore` — passed (11/11).
- Playwright — authenticated Users and Projects grids rendered without console errors; project sort exposed `aria-sort="descending"`, user status filtering worked, and a no-match project search rendered the accessible empty state. The compact project row measured 61px.

## Next step
Completed in work-unit commit `c66217c` (`feat(ui): improve users and projects grids`). Receipt-driven development was globally disabled at closure, so no native review was started. Delivery remains under ordinary repository policy.
