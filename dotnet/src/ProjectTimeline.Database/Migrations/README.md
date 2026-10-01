# Database deployment

The application runs `DatabaseSetup.MigrateAndSeedAsync` at development startup. Production deployments should review and apply the generated EF migration scripts through the deployment pipeline. The checked-in model is the source of truth for migrations; no credentials are stored here.
