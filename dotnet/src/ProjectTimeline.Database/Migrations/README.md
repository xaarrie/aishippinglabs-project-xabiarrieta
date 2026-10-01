# PostgreSQL migration

`202610010001_InitialCreate.cs` is an executable EF Core migration source. Apply it with:

```powershell
dotnet ef database update --project dotnet/src/ProjectTimeline.Database --startup-project dotnet/src/ProjectTimeline.UI.WebApi --connection "$env:ConnectionStrings__ProjectTimeline"
```

The application uses `Database.MigrateAsync()` at startup. It does not call `EnsureCreatedAsync()`; development seeding is separate and requires `PROJECT_TIMELINE_ADMIN_PASSWORD` only when the migrated database has no users.
