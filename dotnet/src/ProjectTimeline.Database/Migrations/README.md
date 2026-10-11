# PostgreSQL migration

`202610010001_InitialCreate.cs`, `202610020001_NormalizeUserRoleCatalog.cs`, and `202610020002_ProjectPrimaryTimeline.cs` are executable EF Core migration sources. Apply them with:

```powershell
dotnet ef database update --project dotnet/src/ProjectTimeline.Database --startup-project dotnet/src/ProjectTimeline.UI.WebApi --connection "$env:ConnectionStrings__ProjectTimeline"
```

The application uses `Database.MigrateAsync()` at startup. It does not call `EnsureCreatedAsync()`; development seeding is separate and requires `PROJECT_TIMELINE_ADMIN_PASSWORD` only when the migrated database has no users.
