# Project Timeline MVP

The Web API hosts the production React bundle and the layered .NET 10 application. Run one process for local development.

## Quick path

1. Install the .NET 10 SDK and PostgreSQL.
2. Set `ConnectionStrings__ProjectTimeline` and a 32-character `Authentication__SigningKey` in the shell. Never commit either value.
3. On the first empty database only, set `PROJECT_TIMELINE_ADMIN_PASSWORD` to create `x.arrieta@ibernova.com`. There is no password fallback. A missing value logs an explicit initialization error and creates no administrator.
4. Run `dotnet restore dotnet/ProjectTimeline.sln`, then `dotnet run --project dotnet/src/ProjectTimeline.UI.WebApi`.
5. Open the URL printed by ASP.NET Core.

## API and authorization

JWTs contain role claims. `/api/admin/*` requires the `Administrator` policy. Project, timeline, entry, and artifact endpoints check owner or participant access; project writes require owner, Creator, or Contributor behavior. Password hashes are never returned by DTOs. Artifact paths are normalized and confined below the configured base path. Administrators are authorized by the same centralized project predicates for every descendant resource, including projects they do not own or participate in.

## Password recovery

`POST /api/auth/recovery` always returns a generic acknowledgement. For an active account it creates a one-time token that expires after `Recovery:LifetimeMinutes` (30 by default), stores only its SHA-256 representation, and sends a recovery link to the configurable local outbox. `POST /api/auth/recovery/reset` accepts the token and a new password; successful reset invalidates the token and stores a PBKDF2 password hash. Tokens, passwords, and hashes are never returned or logged.

Set `Recovery__BaseUrl` and `Recovery__OutboxPath` for local development. The default provider writes JSON messages to `recovery-outbox` beside the application. Inspect that directory during development to copy the link into the hosted UI's **Reset password** form; do not commit outbox files or paste their secret values into logs, source, or documentation. The UI supports both request and reset journeys.

## OData

`/odata/Projects` is served by ASP.NET Core OData with a convention EDM. Supported options are `$select`, `$filter`, `$orderby`, `$top`, `$skip`, and `$count`; server page size and `$top` are capped at 100. The project query is authorization-filtered before OData applies client query options.

## Database

PostgreSQL schema creation is an executable EF Core migration in `dotnet/src/ProjectTimeline.Database/Migrations/202610010001_InitialCreate.cs`. Runtime initialization uses `MigrateAsync()` only. To apply it explicitly:

```powershell
dotnet ef database update --project dotnet/src/ProjectTimeline.Database --startup-project dotnet/src/ProjectTimeline.UI.WebApi --connection "$env:ConnectionStrings__ProjectTimeline"
```

Development seeding is separate and secret-gated. No credentials are stored in source control.

## Hosted UI workflows

`dotnet/src/ProjectTimeline.UI.WebApi/ClientApp` is the React source project. Run `npm ci --prefix dotnet/src/ProjectTimeline.UI.WebApi/ClientApp` followed by `npm run build --prefix dotnet/src/ProjectTimeline.UI.WebApi/ClientApp` to reproduce the committed `wwwroot` assets. The Web API project also runs this build before a normal .NET build; `dotnet run --project dotnet/src/ProjectTimeline.UI.WebApi` serves the committed bundle without a separate frontend server. The hosted UI contains sign-in, recovery request/reset, profile name/password maintenance, administrator user CRUD/deactivation, project creation/listing, participant add/update/remove, timeline and entry CRUD, and artifact upload/list/download controls. All calls use the Web API and the bearer token.

## AI/RAG and merge review foundation

The business layer publishes provider-neutral `IRagIndexService`, `IRagQueryService`, and `ILifecycleAnalysisService` contracts plus request/read models. Their protected API routes deliberately return `501 Not Implemented` until a provider is selected; no live AI behavior is claimed. `IMergeReviewService` and its protected timeline routes persist review requests and support `Open`, `Approved`, `Rejected`, and `Closed` status transitions after the same project authorization checks as timeline writes.

## Verification

Run the commands in `odd/tasks/project-timeline-mvp.md`. The canonical tracker records original evidence without alteration, reopened corrective tasks, and final correction evidence. The complete mirror is `odd/project-timeline-mvp/tasks`.
