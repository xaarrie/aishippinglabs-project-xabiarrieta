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

## Verification evidence

Observed evidence:

- `dotnet restore dotnet/ProjectTimeline.sln`: passed after removing vulnerable Swagger/design package dependencies; NuGet audit initially blocked restore on `Microsoft.OpenApi 2.3.0` and `System.Security.Cryptography.Xml 9.0.0`.
- `dotnet build dotnet/ProjectTimeline.sln --configuration Release`: passed, 0 warnings, 0 errors.
- `dotnet test dotnet/ProjectTimeline.sln --configuration Release`: passed, 2 tests, 0 failures.
- `dotnet run --project dotnet/src/ProjectTimeline.UI.WebApi --no-build`: startup established on `http://localhost:5000`; PostgreSQL initialization reported `The ConnectionString property has not been initialized` because no local connection environment variable was supplied to this process, while the host remained available.
- The initial Debug `--no-build` invocation failed because its executable did not exist; a Debug build was run and the required startup command then succeeded.
- Final `git status --short` and `git log --oneline --max-count=10` are recorded in the handoff and must be rerun after the final work-unit commit.

Known implementation limitation: the repository includes a migration/deployment marker and uses `MigrateAsync` plus `EnsureCreatedAsync` for a fresh development database because the EF CLI was not installed locally; provider-specific migration generation remains a deployment follow-up.
