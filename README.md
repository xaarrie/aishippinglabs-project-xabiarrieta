# Project Timeline MVP

This repository now includes a layered .NET 10 Project Timeline MVP. The Web API serves the static React UI, so one process is enough for local development.

## Quick path

1. Install the .NET 10 SDK and PostgreSQL locally.
2. Set the environment variables in `dotnet/.env.example` in your shell. **Do not commit the real password.**
3. Run `dotnet restore dotnet/ProjectTimeline.sln` and `dotnet run --project dotnet/src/ProjectTimeline.UI.WebApi`.
4. Open `http://localhost:5000` (or the URL printed by ASP.NET Core).

The development seed creates `x.arrieta@ibernova.com`; set `PROJECT_TIMELINE_ADMIN_PASSWORD` to choose its local password. If no password is supplied, the fallback is development-only and must be changed.

## Architecture

| Project | Responsibility |
|---|---|
| `ProjectTimeline.Database` | Development migration/seed orchestration and deployment notes |
| `ProjectTimeline.DataAccessLogic` | EF Core/Npgsql entities, DbContext, audit stamps, repository base |
| `ProjectTimeline.BusinessLogic` | Validation, transactions, password hashing, project and entry use cases |
| `ProjectTimeline.UI.WebApi` | ASP.NET Core API, JWT authentication, OData-style bounded read endpoint, static React UI |

## Database

The application calls `Database.MigrateAsync` at startup and then seeds roles `Administrator`, `Consultant`, `UserChief`, project roles `Creator`, `Reviewer`, `Contributor`, and the development administrator. The PostgreSQL password is read only from `ConnectionStrings__ProjectTimeline`; it is not in source control or logs.

## MVP boundaries

Artifact storage is designed around `BASE_PATH/ProjectCode/TimelineCode/EntryNumber/filename`; the API extension point should enforce filename normalization and traversal checks before enabling uploads. Review/merge requests are represented as a foundation only: no AI/RAG provider or guessed conflict-resolution algorithm is included.

## Verification

Run the commands listed in `odd/tasks/project-timeline-mvp.md`. The task document records exact observed results and the final commit identities.
