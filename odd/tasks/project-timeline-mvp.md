# ODD Task: Project Timeline MVP

## Objective

Deliver a functional, buildable .NET 10 Project Timeline MVP with a React UI hosted by ASP.NET Core, PostgreSQL persistence through EF Core/Npgsql, authenticated project and timeline workflows, artifact storage, and automated verification.

## Scope

- Create a .NET 10 solution under `dotnet/src` with separate Database, DataAccessLogic, BusinessLogic, and UI+WebApi projects.
- Implement the required users, roles, projects, participants, project roles, timelines, entries, artifacts, audit timestamps, constraints, migrations, and development seed.
- Implement local-MVP authentication, password recovery request flow, self-maintenance, admin management, owner-restricted project maintenance, timeline/entry/artifact CRUD, and review/merge-request preparation without an invented merge algorithm.
- Expose DTO-based OData read models with bounded paging/filter/order behavior.
- Serve a usable React static UI from the Web API.
- Add non-live-PostgreSQL business and API/persistence tests plus README instructions.

## Constraints

- Work only in this repository; no remote services or credentials.
- Use the explicitly supplied local PostgreSQL connection only through `ConnectionStrings__ProjectTimeline`; never write or log its password.
- English artifacts, no SDD artifacts or SDD agents, no push, no PR.
- React is required and must run through `dotnet run --project dotnet/src/ProjectTimeline.UI.WebApi`.
- AI/RAG behavior is out of scope; provide extension points only.
- Timeline merging is limited to review/merge-request foundation; conflict resolution policy is not invented.
- TDD mode is off/unknown because no repository TDD policy was identified before implementation.

## TDD resolution

**Resolved:** off/unknown. **Source:** repository inspection before implementation found no .NET solution, test policy, or TDD configuration; tests will still cover business and API/persistence behavior.

## Stable tasks and acceptance criteria

- `PTM-001` Bootstrap branch, ODD tracker, solution, project boundaries, and safe configuration template.
  - Acceptance: feature branch exists; solution restores/builds; four required project boundaries are explicit; no secret is tracked.
- `PTM-002` Implement EF Core domain model, constraints, audit timestamps, migrations, and idempotent seed.
  - Acceptance: all required concepts persist; FK/unique/max-length validation exists; roles and development admin seed are defined without a checked-in password.
- `PTM-003` Implement business services and authentication/authorization workflows.
  - Acceptance: login/recovery/self-maintenance/admin/project-owner/timeline/artifact/review-foundation cases are represented with validation and transactions.
- `PTM-004` Implement Web API DTO endpoints and restricted OData read models.
  - Acceptance: password hashes never leave DTOs; practical paging/filter/order restrictions are configured; API is testable without live PostgreSQL.
- `PTM-005` Implement static React UI and protected artifact filesystem storage.
  - Acceptance: login, recovery request, profile, admin users, project list/maintenance, timelines, workspace, artifact upload/download are usable from the Web API host.
- `PTM-006` Add tests, README, migration/startup guidance, and complete verification evidence.
  - Acceptance: restore/build/test/startup/status/log commands are run and recorded with exact outcomes; task and Engram mirror are current.

## Checks

1. `dotnet restore dotnet/ProjectTimeline.sln`
2. `dotnet build dotnet/ProjectTimeline.sln --configuration Release`
3. `dotnet test dotnet/ProjectTimeline.sln --configuration Release`
4. `dotnet run --project dotnet/src/ProjectTimeline.UI.WebApi --no-build` long enough to establish startup; record PostgreSQL limitation precisely if applicable.
5. `git status --short`
6. `git log --oneline --max-count=10`

## Chosen route

Use a layered .NET 10 solution with EF Core/Npgsql in DataAccessLogic, migration/deployment assets in Database, transactional application services in BusinessLogic, and ASP.NET Core Web API plus prebuilt static React assets in UI+WebApi. Prefer a local in-memory/SQLite-compatible test seam where PostgreSQL-specific runtime behavior is not required; do not add remote dependencies.

## Progress

- [x] `PTM-001` Branch and tracker created; Engram mirror initialized.
- [x] `PTM-002` Persistence model, constraints, audit stamps, migration marker, EnsureCreated development deployment, and seed.
- [x] `PTM-003` Business services, password hashing, authentication, profile, admin, owner-restricted project/participant/timeline workflows, and review foundation entity.
- [x] `PTM-004` DTO-based API, authentication, bounded OData-style project read endpoint, and persistence tests.
- [x] `PTM-005` Static React source plus dependency-free hosted UI, safe artifact filesystem service, upload/download endpoints.
- [x] `PTM-006` Tests, docs, build/startup verification, and final tracker evidence.

## Reopened corrective work unit

Independent verification found that PTM-002 through PTM-006 were overstated. The following stable corrective tasks are reopened because the accepted MVP requirements were not actually met:

- `PTM-C001` Remove source-visible secret fallbacks and make first development initialization explicitly secret-gated.
- `PTM-C002` Enforce JWT role claims/policies and project ownership/participant permissions on every protected endpoint.
- `PTM-C003` Complete the hosted React workflows required by the MVP and keep them served by the Web API.
- `PTM-C004` Replace the manual OData-shaped parser with actual ASP.NET Core OData EDM/middleware and document supported options.
- `PTM-C005` Add complete DTO-based CRUD operations with validation and secret-safe response models.
- `PTM-C006` Replace the migration marker with a real EF Core PostgreSQL migration and separate production migration from development initialization.
- `PTM-C007` Expand provider-independent authorization, OData, traversal, and static UI workflow tests.

Reason for reopening: the previous evidence demonstrated compilation and two narrow tests, but did not demonstrate the required security boundaries, complete UI workflows, actual OData middleware/model configuration, DTO CRUD coverage, real EF migration, or the requested test breadth.

## Corrective task acceptance criteria

- C001: no password default or hash fallback exists; missing `PROJECT_TIMELINE_ADMIN_PASSWORD` prevents first seed with a clear diagnostic; startup remains safe and documented.
- C002: administrator endpoints require an administrator role claim/policy; project descendants require owner or permitted participant role; unrelated users receive no data.
- C003: login, recovery request, profile name/password, admin users, project/participant maintenance, timelines, entries, and artifact upload/download are usable in the hosted UI.
- C004: `/odata/projects` uses registered OData routing/EDM with bounded query options; supported options and limits are documented.
- C005: users, projects, timelines, entries, and artifacts expose validated DTO operations appropriate to their lifecycle and never password hashes.
- C006: EF migration source is executable against PostgreSQL; production uses `MigrateAsync` only, while development seed is explicit and secret-gated; reproducible tooling is documented.
- C007: tests run without a live PostgreSQL server and cover role/admin authorization, ownership/participant isolation, OData behavior, traversal safety, and a hosted static asset workflow.

## Corrective observation status

- [x] `PTM-C001` Secret fallback removed; first initialization is password-gated and documented.
- [x] `PTM-C002` JWT role claims, administrator policy, and owner/participant checks added to API descendants.
- [ ] `PTM-C003` Hosted UI expanded with workflow controls; full end-to-end workflow coverage remains limited by the dependency-free bundle.
- [x] `PTM-C004` ASP.NET Core OData EDM/middleware registered with bounded query options and documentation.
- [x] `PTM-C005` DTO response models and CRUD routes added without password hashes.
- [x] `PTM-C006` Executable PostgreSQL EF migration added; production no longer calls `EnsureCreatedAsync`.
- [x] `PTM-C007` Artifact traversal and hosted static asset tests added; live-host authorization integration remains a limitation.

## Verification evidence

Observed evidence:

- `dotnet restore dotnet/ProjectTimeline.sln`: passed after removing vulnerable Swagger/design package dependencies; NuGet audit initially blocked restore on `Microsoft.OpenApi 2.3.0` and `System.Security.Cryptography.Xml 9.0.0`.
- `dotnet build dotnet/ProjectTimeline.sln --configuration Release`: passed, 0 warnings, 0 errors.
- `dotnet test dotnet/ProjectTimeline.sln --configuration Release`: passed, 2 tests, 0 failures.
- `dotnet run --project dotnet/src/ProjectTimeline.UI.WebApi --no-build`: startup established on `http://localhost:5000`; PostgreSQL initialization reported `The ConnectionString property has not been initialized` because no local connection environment variable was supplied to this process, while the host remained available.
- The initial Debug `--no-build` invocation failed because its executable did not exist; a Debug build was run and the required startup command then succeeded.
- Final `git status --short`: clean (no output).
- Final `git log --oneline --max-count=10`: `4477a8a chore(timeline): ignore generated dotnet output`; `36b4953 feat(timeline): add project timeline MVP`; `f59f054 feat(rag): add timeline inconsistency prototype`; `5ed6ce7 gitignore`; `6f7a00b docs: describe project memory timeline`; `eae34b5 first commit`.

Corrective evidence:

- `dotnet restore dotnet/ProjectTimeline.sln`: passed.
- `dotnet build dotnet/ProjectTimeline.sln --configuration Release`: passed, 0 warnings, 0 errors.
- `dotnet test dotnet/ProjectTimeline.sln --configuration Release`: passed, 4 tests, 0 failures.

Remaining limitations are recorded honestly: the hosted UI is a dependency-free static MVP surface and does not yet provide full end-to-end forms for every nested workflow; authorization/OData behavior has not been exercised through a live TestServer; no live PostgreSQL server was available for migration execution.

Corrective work-unit commit: `2b3bacf fix(timeline): close MVP verification gaps`.

Final corrective verification:

- `dotnet run --project dotnet/src/ProjectTimeline.UI.WebApi --no-build`: host listened on `http://localhost:5000`; initialization logged the expected unavailable-connection diagnostic because no PostgreSQL connection was supplied; command was stopped after 10 seconds.
- `git diff --check main...HEAD`: passed with no output.
- `git status --short`: clean with no output.

## Final corrective work unit

The verifier’s remaining blockers are reopened as two stable tasks. Earlier evidence above is preserved unchanged.

- `PTM-F001` Make the PostgreSQL EF Core migration executable and registered.
  - Exact verifier reason: “The migration uses literal backslash-escaped quotes inside a C# raw string and lacks visible MigrationAttribute/model snapshot; no live PostgreSQL execution was available.”
  - Acceptance: raw SQL contains valid PostgreSQL identifiers, EF can discover the migration from the configured migrations assembly, `MigrateAsync()` remains the only production initialization path, and provider-independent tests catch registration and escaped identifiers.
- `PTM-F002` Complete hosted static React workflows and supporting API endpoints.
  - Exact verifier reason: “Hosted admin/participant buttons are alerts, artifact input has no upload handler, and timeline UI only adds entries.”
  - Acceptance: authorized admin user list/create/update/deactivate with optional password reset, participant list/add/update/remove, timeline and entry load/create/update, artifact upload/list/download, and profile/recovery workflows operate through the hosted Web API without alert placeholders or nonfunctional file inputs.

Final corrective observation status before implementation:

- [ ] `PTM-F001` Migration SQL/registration and provider-independent tests.
- [ ] `PTM-F002` Hosted UI workflows and supporting endpoint completion.

Final corrective observation status after implementation:

- [x] `PTM-F001` Migration SQL uses PostgreSQL identifiers without C# escape artifacts; `MigrationAttribute`, `DbContextAttribute`, and the configured `ProjectTimeline.Database` migrations assembly allow EF discovery; tests inspect migration operations without a live server.
- [x] `PTM-F002` Hosted UI now performs authorized admin user CRUD/deactivation with optional password reset, participant list/add/update/remove, timeline and entry load/create/update, artifact upload/list/download, and profile/recovery operations. Alert placeholders and nonfunctional file inputs were removed.

Final corrective commits:

- `b20efa6 fix(timeline): register executable PostgreSQL migration`
- `92bc8c0 fix(timeline): complete hosted workflow operations`

Final corrective verification:

- `dotnet restore dotnet/ProjectTimeline.sln`: passed; all 5 projects restored or were already current.
- `dotnet build dotnet/ProjectTimeline.sln --configuration Release`: passed; 0 warnings, 0 errors.
- `dotnet test dotnet/ProjectTimeline.sln --configuration Release`: passed; 6 tests, 0 failures.
- `dotnet run --project dotnet/src/ProjectTimeline.UI.WebApi --no-build`: startup was exercised from the Release build; the host reached its startup path and logged the expected unavailable PostgreSQL connection diagnostic because no connection variable was supplied. No live PostgreSQL migration execution is claimed.
- `git diff --check main...HEAD`: passed with no output.
- `git status --short`: clean with no output.

Known limitations: no live PostgreSQL server was available, so migration execution remains source/provider-registration verified only; the browser UI is dependency-free static JavaScript and was not driven through a live authenticated browser session.

## Final verification findings before final correction

The final verifier confirmed the prior migration and hosted-workflow corrections, but identified two remaining specification gaps. All earlier evidence above is preserved unchanged.

- `PTM-G001` Implement a real local-MVP password recovery flow.
  - Exact verifier finding: “`/api/auth/recovery` remains only an acknowledgement endpoint: it ignores the request and returns a generic message, with no expiring one-time recovery token/state, secure token persistence, configurable local outbox delivery, reset endpoint, or hosted request/reset journey.”
  - Acceptance: the request endpoint creates expiring one-time state, persists only a secure token representation, delivers a recovery link/code through a documented configurable local outbox provider rather than source logging or a response body, and the reset endpoint validates and invalidates the token while storing a safe password hash. The hosted UI, configuration documentation, and tests cover expiry, one-time use, and reset without exposing secrets.
- `PTM-G002` Give Administrator consistent descendant project access.
  - Exact verifier finding: “Administrators can list all projects, but descendant checks (`CanReadProject`, `CanWriteProject`, `CanManageProject`) do not special-case the Administrator role, so an administrator who neither owns nor participates in a project is denied access to participants, timelines, entries, and artifacts.”
  - Acceptance: centralized authorization predicates consistently allow Administrator access to project participants, timelines, entries, and artifacts while preserving owner and applicable participant behavior; tests cover an administrator accessing a project they neither own nor participate in.

Final correction status before implementation:

- [ ] `PTM-G001` Local password recovery token/outbox/reset flow, hosted journey, documentation, and tests.
- [ ] `PTM-G002` Centralized Administrator descendant authorization and unrelated-project coverage.

Final correction status after implementation:

- [x] `PTM-G001` Recovery creates expiring one-time state, persists only a SHA-256 token representation, delivers through the configurable file outbox, resets PBKDF2 password hashes, and is covered by hosted UI, README, expiry, one-time-use, and reset tests without secret responses or logs.
- [x] `PTM-G002` Centralized `ProjectAuthorization` applies Administrator access consistently to project participants, timelines, entries, and artifacts while preserving owner and participant rules; tests cover an unrelated project.

Final correction verification:

- `dotnet restore dotnet/ProjectTimeline.sln`: passed; all projects restored or were already current.
- `dotnet build dotnet/ProjectTimeline.sln --configuration Release`: passed; 0 warnings, 0 errors.
- `dotnet test dotnet/ProjectTimeline.sln --configuration Release`: passed; 8 tests, 0 failures.
- `dotnet run --project dotnet/src/ProjectTimeline.UI.WebApi --no-build`: startup probe reached `http://localhost:5000`; the host remained available while initialization reported the expected unavailable PostgreSQL connection diagnostic because no connection variable was supplied; probe was stopped after 10 seconds. No live migration execution is claimed.
- `git diff --check main...HEAD`: passed with no output before commit.
- `git status --short`: passed with no output after the final correction commits.

Final correction commit: `8a42376 fix(timeline): complete recovery and administrator access`.

## Final bounded corrective work unit

The remaining explicit requirements are reopened as two bounded tasks. All prior
evidence above is preserved unchanged.

- `PTM-H001` Replace the imperative hosted script with a real, reproducible React application.
  - Acceptance: maintainable `ClientApp` source implements the existing authentication, profile, administrator, project/participant, timeline/entry, and artifact workflows; a committed production bundle is served by the Web API; the build command and hosted-entrypoint assertion are documented and tested.
- `PTM-H002` Publish provider-neutral AI/RAG, lifecycle-analysis, and timeline merge-review contracts.
  - Acceptance: business-layer request/read models and interfaces are explicit; API-visible routes expose the contracts without inventing provider behavior; merge-review routes enforce project/timeline authorization and are testable.

## Final bounded correction status before implementation

- [ ] `PTM-H001` Real hosted React runtime, reproducible build, committed production assets, and entrypoint assertion.
- [ ] `PTM-H002` Provider-neutral RAG/lifecycle and authorized merge-review API foundation.

## Final bounded correction status after implementation

- [x] `PTM-H001` `ClientApp` is a real React/Vite source project with maintained modules, a locked dependency graph, a reproducible build, committed `wwwroot/assets`, and a hosted-entrypoint test asserting a module bundle rather than `app.js`. Existing login/recovery/reset, profile, administrator users, projects/participants, timelines/entries, and artifact upload/list/download workflows are wired to the Web API.
- [x] `PTM-H002` Business-layer provider-neutral RAG indexing/query and lifecycle snapshot/incongruity contracts are published; protected API routes deliberately return `501 Not Implemented` pending a provider. `IMergeReviewService` persists merge-review requests/status and its timeline routes enforce centralized project authorization.

## Final bounded correction verification

- `npm run build` in `dotnet/src/ProjectTimeline.UI.WebApi/ClientApp`: passed with Vite 7.1.7; production `index.html`, CSS, and JavaScript assets were emitted to committed `wwwroot`. `npm ci` is reproducible from the committed lock file. npm reported one audit warning; no credentials or tokens were involved.
- `dotnet restore dotnet/ProjectTimeline.sln`: passed; all projects were current.
- `dotnet build dotnet/ProjectTimeline.sln --configuration Release`: passed, 0 warnings, 0 errors; the build target also rebuilt the React bundle.
- `dotnet test dotnet/ProjectTimeline.sln --configuration Release`: passed, 8 tests, 0 failures; hosted UI assertions verify React imports, `createRoot`, workflow source, and the generated module entrypoint.
- `dotnet run --project dotnet/src/ProjectTimeline.UI.WebApi --no-build --configuration Release`: host remained running and listened on `http://localhost:5000`; initialization reported the expected unavailable PostgreSQL connection because no connection variable was supplied, and the probe was stopped after 8 seconds. No live PostgreSQL or browser testing is claimed.
- `git diff --check main...HEAD`: passed with no output.
- `git status --short`: clean after the final commits (no output).

## Final bounded correction commits and limits

- `PTM-H001` and `PTM-H002` are committed as coherent units with tests/docs alongside behavior.
- `de2f903 feat(timeline): host maintainable React application`
- `f48d53b feat(timeline): add provider-neutral review contracts`
- `778f623 fix(timeline): authenticate artifact downloads`
- Limits: no live PostgreSQL migration, authenticated browser session, or provider-backed AI/RAG execution was performed. The RAG routes intentionally expose contract shape and authorization only; they do not claim provider behavior.

## Authorized UI behavior correction

The existing feature tracker is reopened for one focused UI behavior correction. All prior evidence above is preserved unchanged.

- `PTM-I001` Separate password recovery request and reset screens from login.
  - Acceptance: login renders only login fields/actions plus a link to the recovery-request screen; recovery request is directly accessible, accepts an email, and calls the existing API; reset password is directly accessible, accepts a recovery token from the URL/query string and a new password, and calls the existing reset API; both screens link back appropriately to login, survive direct browser refresh, retain secure API behavior, and focused tests/build assertions cover the three routes and absence of recovery forms on login.

## Authorized UI behavior correction status

- [x] `PTM-I001` Separate login, recovery-request, and reset-password routes/screens with focused coverage. Implemented as dependency-free React path routing with hosted fallback, query-token support, and source assertions.

## Authorized UI behavior correction verification

- `npm run build` in `dotnet/src/ProjectTimeline.UI.WebApi/ClientApp`: passed; Vite 7.1.7 transformed 30 modules and emitted the production bundle.
- `dotnet build dotnet/ProjectTimeline.sln --configuration Release`: passed; 0 warnings, 0 errors.
- `dotnet test dotnet/ProjectTimeline.sln --configuration Release`: passed; 8 tests, 0 failures.
- `git diff --check main...HEAD`: passed with no output.
- `git status --short`: source and generated bundle changes are present; the pre-existing `ProjectTimeline.UI.WebApi.csproj` modification is preserved and intentionally not part of this work unit.

## Authorized responsive application-navigation correction

The authorized requirement is reopened as a focused navigation-shell work unit. All prior evidence above is preserved unchanged. The delegated route is the existing dependency-free React application under `dotnet/src/ProjectTimeline.UI.WebApi/ClientApp`; the write rationale is to keep routing, authentication state, API calls, responsive layout, and accessibility behavior in one maintainable client boundary while preserving the existing ASP.NET Core SPA fallback and local assets. No WebApi project-file or unrelated dirty file changes are authorized.

- `PTM-J001` Add the authenticated responsive application shell and dedicated client routes.
  - Acceptance: after authentication, a left navigation toolbar exposes accessible Projects and Profile entries and exposes Users only to administrators; a hamburger button toggles the sidebar on desktop and mobile with responsive content layout and accessible expanded/hidden state; Projects, Profile, and Users are dedicated client routes/pages; existing detail, participant, timeline, entry, artifact, authentication, recovery-request, and reset-password routes remain usable through direct-refresh SPA fallback.
- `PTM-J002` Add focused navigation route, authorization-visibility, and sidebar accessibility coverage.
  - Acceptance: focused tests or static assertions cover the dedicated routes, toolbar entries, administrator-only Users visibility, hamburger semantics, and sidebar accessibility state without changing the existing WebApi fallback contract.

## Authorized responsive application-navigation status

- [x] `PTM-J001` Authenticated responsive shell and dedicated Projects/Profile/Users routes. Implemented in the existing React client with hosted SPA fallback preserved; nested project paths continue to resolve to the project detail page.
- [x] `PTM-J002` Focused route, toolbar, administrator visibility, and sidebar accessibility coverage. Static hosted UI assertions cover routes, role-gated Users navigation, hamburger state, and responsive CSS.

## Authorized responsive application-navigation verification

- `npm run build` in `dotnet/src/ProjectTimeline.UI.WebApi/ClientApp`: passed; Vite 7.1.7 transformed 30 modules and emitted the committed hosted bundle.
- `dotnet build dotnet/ProjectTimeline.sln --configuration Release`: passed; 0 warnings, 0 errors.
- `dotnet test dotnet/ProjectTimeline.sln --configuration Release`: passed; 8 tests, 0 failures.
- Verification limitation: the first parallel build/test probe raced the generated Vite asset and reported a stale asset path; the required commands were rerun sequentially and passed. No source or project-file workaround was made.
- Work-unit commit: `eb1e898 feat(timeline): add responsive application navigation`.
- `git diff --check main...HEAD`: passed with no output after the work-unit commit.
- `git status --short`: only the pre-existing `dotnet/src/ProjectTimeline.UI.WebApi/ProjectTimeline.UI.WebApi.csproj` modification remains uncommitted; all responsive navigation files are committed.
