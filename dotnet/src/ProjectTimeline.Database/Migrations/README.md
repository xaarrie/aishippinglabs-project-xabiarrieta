# PostgreSQL migration

The migration chain starts with `202610010001_InitialCreate.cs`. Timeline facts use `20261018000100_TimelineFacts.cs` and its frozen designer model. That migration executes `CREATE EXTENSION IF NOT EXISTS vector;` and creates a `vector(1536)` column; official pgvector files must already be installed on the PostgreSQL server, and the migration role needs extension-creation permission (or administrator pre-creation in that database).

Follow the [timeline facts deployment guide](../../../docs/timeline-facts.md) for deferred Windows administrator setup, backup, offline SQL review, secret-safe configuration, startup and disposable opt-in relational checks. Installing server files and enabling the extension per database are separate steps. Never put a connection password in command arguments or drop/reset the application database to make migrations pass. Actual extension installation and relational execution remain pending.

The application uses `Database.MigrateAsync()` at startup. It does not call `EnsureCreatedAsync()`; development seeding is separate and requires `PROJECT_TIMELINE_ADMIN_PASSWORD` only when the migrated database has no users.
