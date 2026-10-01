# Project Timeline MVP

The Web API hosts the dependency-free React UI and the layered .NET 10 application. Run one process for local development.

## Quick path

1. Install the .NET 10 SDK and PostgreSQL.
2. Set `ConnectionStrings__ProjectTimeline` and a 32-character `Authentication__SigningKey` in the shell. Never commit either value.
3. On the first empty database only, set `PROJECT_TIMELINE_ADMIN_PASSWORD` to create `x.arrieta@ibernova.com`. There is no password fallback. A missing value logs an explicit initialization error and creates no administrator.
4. Run `dotnet restore dotnet/ProjectTimeline.sln`, then `dotnet run --project dotnet/src/ProjectTimeline.UI.WebApi`.
5. Open the URL printed by ASP.NET Core.

## API and authorization

JWTs contain role claims. `/api/admin/*` requires the `Administrator` policy. Project, timeline, entry, and artifact endpoints check owner or participant access; project writes require owner, Creator, or Contributor behavior. Password hashes are never returned by DTOs. Artifact paths are normalized and confined below the configured base path.

## OData

`/odata/Projects` is served by ASP.NET Core OData with a convention EDM. Supported options are `$select`, `$filter`, `$orderby`, `$top`, `$skip`, and `$count`; server page size and `$top` are capped at 100. The project query is authorization-filtered before OData applies client query options.

## Database

PostgreSQL schema creation is an executable EF Core migration in `dotnet/src/ProjectTimeline.Database/Migrations/202610010001_InitialCreate.cs`. Runtime initialization uses `MigrateAsync()` only. To apply it explicitly:

```powershell
dotnet ef database update --project dotnet/src/ProjectTimeline.Database --startup-project dotnet/src/ProjectTimeline.UI.WebApi --connection "$env:ConnectionStrings__ProjectTimeline"
```

Development seeding is separate and secret-gated. No credentials are stored in source control.

## Hosted UI workflows

The hosted UI contains sign-in, recovery request, profile name/password maintenance, project creation/listing, participant-maintenance entry point, timeline workspace entry point, admin-user maintenance entry point, and artifact upload/download controls. All calls use the Web API and the bearer token.

## Verification

Run the commands in `odd/tasks/project-timeline-mvp.md`. The canonical tracker records original evidence without alteration, reopened corrective tasks, and final correction evidence. The complete mirror is `odd/project-timeline-mvp/tasks`.
