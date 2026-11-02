# Deploy and operate timeline facts

**Local pgvector files installed; database activation and relational execution remain pending.** Official pgvector v0.8.7 was compiled with Visual Studio C++ x64 tools and installed for PostgreSQL 18.6 through user-confirmed UAC. Independent verification matched all 47 installed files by SHA-256 and size; the PostgreSQL service remains running. No database connection, `CREATE EXTENSION`, application migration, runtime vector check or paid call was performed. Do not start the application against the incomplete database schema. Offline verification needs no database reset or paid call.

## What the feature does

- Initial extraction saves source-grounded facts **directly**, effective immediately, anchored to a durable extraction job/fingerprint/candidate key. It creates no review proposal and invokes no acceptance. Atomic completion saves facts, completion evidence, timeline revision and queued embedding jobs; it does not execute embeddings.
- Original entry text is not rewritten by extraction or acceptance. Persisted extraction/review evidence protects source edits/deletions; later review corrections add facts and invalidate prior facts, retaining quotes, fingerprints and origin/history.
- Review proposals remain separate and pending until explicitly accepted. Pending additions are not effective search results. Acceptance rechecks access, scope, evidence, unanswered questions and timeline revision; it applies additions/invalidations atomically and is idempotent by stored proposal ID. A stale proposal requires refresh/regeneration, not a forced retry with client content.
- General timeline information search uses the **effective, still-valid facts across the timeline** (direct extraction or accepted review origin), not original entry content. Review-specific retrieval remains anchored to the reviewed entry number and bounds source/review chronology by that anchor. Both use authorized bounded lexical/entity matching, **not vector similarity search**: at most 12 tokens, 300 search characters, default 20/max 50 results. No GraphRAG, graph database, exhaustive conflict guarantee, or raw-entry fallback is implemented.
- Both direct extraction and accepted-review facts are eligible for `text-embedding-3-small` embeddings with 1536 dimensions (`vector(1536)`) while still valid. Missing/failed embeddings do not disable lexical retrieval. Extraction completion does not mean a review was accepted or that history is exhaustive. The initializer queues embeddings only; it never drains them. See [initializer safety and extraction limits](facts-initializer.md).

## Self-contained facts and correction history

Descriptions, definitions and attributes are facts too: an explicitly red car or a database table's explicit definition/columns need not describe an event. Extraction requests entity names and required project/system/table/scope in each claim, preserving original language, conditions and all stated schema details without inference. Meaningful components can share an entity and repeated context; they are not collapsed into one fact per entity. The verifier independently checks these claims under the same all-batch support, evidence and score rules as event claims. Curated fake car/schema outputs prove the request/transport contract, not live-model completeness.

New invalidations require an explicitly selected replacement addition from the **same proposal**. Draft indices are resolved to stable addition IDs; acceptance creates replacement facts with those IDs and records the old fact's nullable `InvalidatedByFactId` atomically with the accepted review/date. One replacement can invalidate multiple older facts. No arbitrary first-addition selection, self-cause, future-source cause or invented replacement is permitted. Ask for clarification if no supported replacement is available. Historical null causes remain null, with no fabricated backfill; an old link remains unchanged if its cause is later invalidated.

The entry facts API exposes `invalidatedByFactId` on stored facts. Proposal invalidations expose `invalidatingAdditionId`, zero-based `invalidatingAdditionIndex` in the **returned** additions order, and `invalidatingClaim`. The index is remapped from the stored ID, never reused from the original draft order. Legacy missing associations remain nullable. These proposal fields express intended replacement, not persisted invalidation; only acceptance establishes the stored fact link.

Fact Review displays the actual stored cause ID and its claim only when that fact is present in the same authorized bounded state, even if the cause is now invalid. A cause in another entry or beyond the response limit shows its ID with a not-loaded message; legacy nulls show no recorded replacement. No extra fetch or latest-valid-fact substitution is used. Proposal previews label the intended addition (UI number is one-based), not an already-persisted cause. Read-only access, stale handling and explicit ID-only acceptance remain unchanged. Mounted-browser verification of this projection remains a separate check; Node tests cover helper mapping and component wiring only.

## Search answers and evidence

Submit a question through **Search accepted facts** in the timeline workspace (legacy UI wording; effective direct facts also qualify). Chronological entries, original text and CRUD remain unchanged. Queries are limited to 300 characters. The UI requires structured evidence; a legacy content-only response is not treated as a grounded answer.

`GET /api/timelines/{timelineId}/entry-search?text=...` returns:

| Field | Meaning |
| --- | --- |
| `content` | Model narrative, separate from retrieved evidence; shown as plain text, not executable HTML/Markdown |
| `facts` | Retrieved fact IDs, claims, entity keys/types, source quotes/kinds/fingerprints, clarification evidence, source entry IDs/numbers, nullable creation-review IDs and extraction provenance |
| `citations` | Fact/source/review references; the UI shows only references matching actually returned facts, separating cited facts from additional retrieved context |
| `coverage` | `entries`, `entriesWithoutCompletedExtraction`, `pendingReviews`, `acceptedValidFacts`, `truncated`, and server `warnings` |

Direct facts have extraction provenance and no creation-review ID; do not fabricate a review citation for them. Source links open the existing entry detail route. **Clarification** quotes come from review answers, not original entry text; stored question/answer evidence is displayed separately. Generated narrative is not proof that every sentence is supported. Coverage counters and warnings remain visible, including incomplete extraction, pending acceptance, non-exhaustive bounded matching and truncated output. No facts/no accepted matches is an explicit empty state, never a fallback to chronology or raw content.

**Empty retrieval does not construct the model/provider and incurs no synthesis billing.** Searches with matching facts may invoke the existing model and incur billing even when background processing is disabled. No paid provider, database deployment or relational execution is implied by offline UI/API tests. Mounted-browser regression and deployment checks remain separate verification steps.

## Administrator handoff: official Windows pgvector

These are **future operator instructions, not commands executed during preparation**. Obtain explicit approval for the C++ download, elevated shell and installation first. Use only the [official pinned v0.8.7 instructions](https://raw.githubusercontent.com/pgvector/pgvector/v0.8.7/README.md) / [official repository](https://github.com/pgvector/pgvector/tree/v0.8.7). Never download third-party DLLs.

1. Install the Visual Studio C++ build tools required by the official instructions. Open **x64 Native Tools Command Prompt for VS as administrator**. Confirm the PostgreSQL installation and architecture.
2. In that **cmd.exe** shell, use a fresh, unique temporary working directory (do not reuse/overwrite a checkout):

   ```bat
   set "PGROOT=C:\Program Files\PostgreSQL\18"
   mkdir "%TEMP%\pgvector-build-%RANDOM%-%RANDOM%"
   rem cd into the exact newly created directory before continuing
   git clone --branch v0.8.7 https://github.com/pgvector/pgvector.git
   cd pgvector
   nmake /F Makefile.win
   nmake /F Makefile.win install
   ```

3. Installation copies the server extension files; it does **not** enable the extension in every database. Check the installed files/server version with the administrator. Database availability can later be checked with `SELECT name, default_version, installed_version FROM pg_available_extensions WHERE name = 'vector';` in an explicitly selected database.
4. `20261018000100_TimelineFacts` starts with `CREATE EXTENSION IF NOT EXISTS vector;`. A database administrator must enable it in the target database or grant the migration role the appropriate privilege. Extension files must already exist on that server. An extension enabled in `postgres` is not automatically enabled in the application or disposable test databases.

## Safe migration and startup sequence

Effective facts are stored in the physical SQL table `TimelineEntryFacts`; CLR entity `TimelineFact` and DbSet `TimelineFacts` keep their names. Proposal and job table names are unchanged. The existing `20261018000100_TimelineFacts` migration defines this table. Preserve it unchanged: the new **forward migration `20261018000200_DirectTimelineFactExtraction` is required** for nullable review provenance, extraction-job provenance and database constraints/indexes. The additional forward migration **`20261018000300_FactInvalidationCause` is also required**: it adds nullable cause/association foreign keys with restricted deletion and indexes, preserving historical rows without backfill. Existing migrations must not be rewritten. Actual applied migration state is unknown; inspect it only in a separately authorized deployment workflow. No migration was applied during this source-only implementation.

1. Back up the intended application database using your approved PostgreSQL backup procedure; verify restore capability separately. Confirm the target server/database privately. Never drop/reset the default application database to fix a failed migration. See [PostgreSQL backup guidance](https://www.postgresql.org/docs/18/backup.html).
2. Review forward migration SQL **offline before applying it**. The existing `TimelineFactModelTests` generates the facts delta with `IMigrator.GenerateScript("20261011085524_TimelineEntrySnapshotsModel", "20261018000100_TimelineFacts")`, without opening a connection. For full deployment review, generate from the actual last applied migration using an offline Npgsql context and `IMigrator.GenerateScript`; do not boot the Web API just to generate SQL. Also review the forward direct-extraction and cause migration SQL and frozen metadata: nullable review IDs, extraction-job foreign key, origin constraint, candidate uniqueness, restricted fact-to-fact cause and proposal-addition association. Cause migration rollback refuses to discard populated cause links; it is not a reset workaround. Review the frozen target model, extension creation, foreign keys, revision column and `vector(1536)` column. Offline script checks are not execution evidence.
3. After administrator setup, rehearse migrations and the opt-in tests below on disposable databases. Apply reviewed migrations through your approved deployment process to the explicitly selected, backed-up application database. No migration has been executed as part of this preparation.
4. Provide `ConnectionStrings__ProjectTimeline`, `Authentication__SigningKey` (at least 32 characters), and any required first-empty-database `PROJECT_TIMELINE_ADMIN_PASSWORD` through a secret manager or privately injected environment. No password in command arguments, shell history, logs or artifacts. Do not rely on the application's localhost/default-database fallback. See [ASP.NET Core secret handling](https://learn.microsoft.com/aspnet/core/security/app-secrets).
5. Startup calls `MigrateAsync()` and seeds separately. Schema initialization failure is **fatal**, even with background processing disabled. Resolve extension/schema deployment before serving requests; do not bypass it with `EnsureCreated`.

To build without touching the normal locked output or rebuilding dirty `wwwroot`, from the repository root (dependencies must already be restored):

```powershell
dotnet build dotnet/tests/ProjectTimeline.Tests/ProjectTimeline.Tests.csproj --no-restore -p:DesignTimeBuild=true -p:BuildProjectReferences=true -p:OutputPath=bin/T1/
```

Only **after deployment approval and schema readiness**, start the isolated built application, avoiding normal `dotnet run`/frontend build hooks and launch settings:

```powershell
dotnet dotnet/src/ProjectTimeline.UI.WebApi/bin/T1/ProjectTimeline.UI.WebApi.dll --contentRoot dotnet/src/ProjectTimeline.UI.WebApi
```

This serves the existing `wwwroot`, not a freshly rebuilt UI. Do not terminate Visual Studio/Web API processes to unlock normal outputs. Build success does not prove application startup or relational compatibility.

### Before any separately approved regeneration

A backup and derived-data reset are **future operator work**, not performed by this source change. Recheck actual row scope, dependencies, migration state and active work; obtain a fresh approved backup with archive listing/hash, disclosing whether restoration has been tested. Original timeline entries must remain unchanged. Preserve proposals, accepted reviews, clarification answers, invalidations and audit history. Fact-to-fact cause links and retained proposal references can block a facts/jobs-only reset: **stop rather than cascading or deleting retained evidence**. No FK/guard bypass or schema hack is acceptable. See the [initializer dependency gate](facts-initializer.md#future-derived-data-regeneration-dependency-gate) for the scope/order and concurrent-revision caveats. Offline tests/builds alone do not authorize operational mutation, regeneration or billing.

## Processing and billing controls

Remote extraction uses typed `TimelineFactExtractionAgent` followed by independent `TimelineFactSemanticVerifierAgent` over the existing framework chat client. `TimelineFactOpenAIProvider` orchestrates **one extra verifier call per nonempty extraction** (two calls total); empty extraction has one call. Both strict schemas derive from typed classes and require raw OpenAI finish/refusal metadata. Approximate extraction citations are assessed against the full original source and every indexed candidate. All assessments must explicitly support the entire claim/citation with no numeric/date/actor/negation/unsupported issues, regardless of score. A finite 0..1 score is a **transient, uncalibrated model assessment**, with no acceptance threshold and no DB/API score field.

The verifier selects deterministic source-span IDs, not literal evidence text. Code reconstructs final quotes directly from original title or content positions (a bounded contiguous envelope for multiple IDs); malformed, duplicate, unknown, other-input or mixed-field evidence IDs reject the entire batch. Final processing/service/database literal-evidence checks, provenance/hashes and review exactness are unchanged; no verification flag or persistence bypass is introduced. Model assessment is not proof of actual entailment, and fake contradiction tests establish gating only.

Each chat call uses the OpenAI SDK with response-size bounds, cancellation, a 90-second limit and zero SDK retries on the fact composition path. Extra verification can increase billing and latency; three-minute leases and durable job retries remain unchanged. The separate numerical `ICustomEmbeddingClient` is unchanged. Provider/key construction remains deferred until work, not enqueue or console dry-run; no migration, paid call, recovery or embedding execution is part of this source change. See [extraction limits and offline evidence](facts-initializer.md#detailed-extraction-and-validation-limits).

| Environment key | Actual behavior/default |
| --- | --- |
| `TimelineFacts__ProcessingEnabled` | `false`; no background queue drain or automatic legacy scan |
| `OPENAI_API_KEY` | Existing provider credential convention; inject privately |
| `TimelineFacts__ExtractionModel` | `gpt-4o-mini` for background extraction |
| `TimelineFacts__MaxJobsPerCycle` | 10, clamped to 1–100 |
| `TimelineFacts__PollSeconds` | 15, clamped to 1–300 seconds |

**Review requests can incur paid agent calls even when background processing is off.** The extraction model key above configures background extraction, not the review agent. Do not request reviews or enable processing without billing consent. Direct extraction completion and later review acceptance queue embeddings; backfill only enqueues work but may incur costs later when processing is enabled. Crash/reclaim can repeat remote calls and rebill even though database completion/replay is deduplicated. Cancellation cannot interrupt the existing tokenless review-agent call already in flight.

Saved entries keep durable extraction jobs. Initial extraction requires no existing facts (including invalid facts), proposals or competing jobs, and never enriches or changes existing accepted facts. Existing review records and accepted facts remain preserved. The legacy initializer and opt-in due-Retry recovery report `Saved` for direct persistence, not `Accepted`; [operator contract](facts-initializer.md). Jobs move through `Pending`, `Running`, `Retry`, `Completed`, `Failed` or `Skipped`; cancellation normally releases to retry. Leases last three minutes; expired running jobs can be reclaimed. Retry delay grows exponentially up to one hour; after eight non-cancellation attempts work becomes terminal `Failed`. The poller does not select terminal failed jobs. There is no public failed-job reset/retry endpoint: investigate privately before any separately reviewed recovery action. Re-enqueue backfill does not reset an existing job or replace manually prepared evidence.

## Authorized API and resumable backfill

All routes require authentication, an active user and project access; proposal/accept/backfill operations require write access. Bodies are bounded to 64 KiB on fact routes. Middleware independently checks unknown-length bodies in memory (at most 64 KiB + 1 bytes read before rejection), rather than relying only on a server-specific body-limit feature.

| Method and route | Contract |
| --- | --- |
| `GET /api/entries/{entryId}/facts` | Entry facts/proposals/job states plus coverage; max 500 facts, 50 newest proposals, 100 jobs. `truncated=true` means incomplete output; there is no read cursor on this endpoint. |
| `POST /api/entries/{entryId}/fact-proposals` | Initial body `{}`; clarification uses `previousProposalId` and `answers` containing `questionIndex`/`answer`. Server stores the replacement proposal; no client-supplied fact payload. May bill. |
| `POST /api/entries/{entryId}/fact-proposals/{proposalId}/accept` | **No body, not even `{}`.** Accept only the server proposal ID. `409` means stale/incomplete: refresh and regenerate. |
| `POST /api/timelines/{timelineId}/fact-backfill` | Enqueue only; body below. Does not resolve provider credentials. |

```json
{"afterEntryNumber":0,"throughEntryNumber":125,"pageSize":50}
```

Choose and persist a fixed `throughEntryNumber` before the first page; each page selects `afterEntryNumber < EntryNumber <= throughEntryNumber`. Page size is 1–100 (default 50); bounds must be nonnegative and ordered. Persist returned `lastEntryNumber` and use it as the next `afterEntryNumber`, retaining the same upper bound. Stop when the cursor reaches the upper bound; gaps can yield an empty final page whose cursor advances to the upper bound. `examined` counts scanned entries, `enqueued` counts new jobs: zero enqueued does **not** mean the scan is finished. Repeat pages safely; existing extraction work or proposals are skipped. Newer entries beyond the fixed bound belong to a later pass. No automatic paid legacy backfill occurs.

Keep incomplete-history and bounded-search/truncation warnings visible. `AbsentRequiresExplicitBackfill`, pending/retrying/failed extraction and unaccepted proposals must never be presented as proof that no facts/conflicts exist.

## Opt-in disposable PostgreSQL tests

`TimelineFactPostgresTests.cs` uses a custom Fact attribute that **skips before any connection** unless `PROJECT_TIMELINE_FACTS_POSTGRES_TESTS=1`. It then requires `PROJECT_TIMELINE_FACTS_TEST_CONNECTION` with an explicit `Database=postgres`, host and username. It never reads app credentials, launch settings or runsettings. Inject the dedicated test connection privately; never put its password in a test command or print it.

Prerequisites: an approved isolated PostgreSQL test server, official pgvector files, and a dedicated test role able to connect to `postgres`, create databases and install the extension in its own databases (or equivalent administrator-arranged privileges). Do not point this at a production/application server. Each case creates a random `timeline_facts_test_<32 hex>` database from `template0`, applies migrations and drops **only its exact successfully created database**. Pooling is disabled; no force-drop or session termination is used. Process interruption/cleanup failure may leave an owned database for private operator inspection; application databases are never selected for migration/drop. Provider exceptions are sanitized.

The opt-in cases cover actual migration application/current frozen-model compatibility, EF vector write/read using a fake embedding provider, serializable rollback after SQL writes, concurrent acceptance plus idempotent replay/no duplicate effects, and extraction completion replay with a fake provider. They do not cover real OpenAI semantics, lease-expiry races, or actual HTTP middleware/binding. The separate T5 TestServer tests exercise HTTP middleware/binding with fake authentication and InMemory storage, not PostgreSQL.

Default offline facts regression explicitly excludes PostgreSQL and paid LiveModel tests, regardless of external opt-in flags. Use cached assets and isolated output as described in [initializer verification](facts-initializer.md):

```powershell
dotnet test dotnet/tests/ProjectTimeline.Tests/ProjectTimeline.Tests.csproj --no-restore --artifacts-path dotnet/src/ProjectTimeline.FactsInitializer/artifacts -p:DesignTimeBuild=true -p:BuildProjectReferences=true -p:CustomAfterMicrosoftCommonProps=C:/Data/formacion/aishippinglabs-project-xabiarrieta/dotnet/src/ProjectTimeline.FactsInitializer/IsolatedBuild.props --filter 'FullyQualifiedName~TimelineFact&FullyQualifiedName!~TimelineFactPostgresTests&Category!=LiveModel'
```

A future approved relational run uses the same built tests filtered to `FullyQualifiedName~TimelineFactPostgresTests`, with both dedicated environment values injected. **Do not enable or execute that path during deferred setup.**

## Evidence boundary

| Check | Status |
| --- | --- |
| T1–T3 offline .NET regression | Parent recorded 124 passing tests before T4; not relational proof |
| UI helpers and mounted mock-browser workflow | Parent recorded 25 Node passes and mock browser acceptance/stale flows; not backend HTTP proof |
| Offline model/migration SQL metadata | Checked without connections; does not prove migration execution |
| T4 isolated build and default offline run | Build: 0 warnings/errors; 130 passed, 4 relational cases skipped, 0 failed. No relational case executed |
| Windows extension files | **Installed and independently verified:** official v0.8.7, PostgreSQL 18.6 x64, 47/47 matching file hashes and sizes |
| Per-database extension / migration execution | **Pending:** no database activation or application migration executed |
| Real vector roundtrip / PostgreSQL rollback/concurrency/replay cases | **Pending; opt-in cases not executed** |
| T5 fact-review endpoint pipeline and regression | 28 passing TestServer HTTP cases; independently verified at T5: 490 passed, 0 failed, 4 PostgreSQL skipped; paid live-model class excluded; isolated build: 0 warnings/errors |
| S3 fact information search | Independently verified: 550 .NET passed, 0 failed, 4 PostgreSQL skipped; 35 Node passed; clean build and isolated Vite; mounted Chrome workflow with 37 mocked API requests and zero runtime errors. No live DB/provider proof. |
| T5 verified contracts | Fake-auth 401/403 before agent construction; JSON binding/invalid bodies; known/unknown-length 64 KiB bounds; ID-only acceptance, idempotency, source preservation, stale/missing/cross-entry responses, safe DTO/502 traces; bounded enqueue-only backfill |
| Production Program / actual JWT / network server | **Unverified**; tests use an empty test-only WebApplication, fake header authentication, deterministic agents and disposable InMemory stores; no production startup or migrations |
| PostgreSQL transactions / real provider semantics | **Unverified**; InMemory acceptance is not relational atomicity/concurrency proof; live PostgreSQL and paid checks remain unrun |

HTTP verification: [TestServer host](../tests/ProjectTimeline.Tests/TimelineFactHttpHost.cs) and [HTTP contract tests](../tests/ProjectTimeline.Tests/TimelineFactHttpTests.cs). Unknown-length tests use content with no computable length and a chunked request header; TestServer exercises the HTTP application pipeline, not network chunk framing. Production startup/JWT and deployment checks remain separate.

Implementation authority: [hosting configuration](../src/ProjectTimeline.UI.WebApi/TimelineFactHosting.cs), [routes](../src/ProjectTimeline.UI.WebApi/TimelineFactEndpoints.cs), [processing](../src/ProjectTimeline.BusinessLogic/TimelineFactProcessing.cs), [migration](../src/ProjectTimeline.Database/Migrations/20261018000100_TimelineFacts.cs), [opt-in tests](../tests/ProjectTimeline.Tests/TimelineFactPostgresTests.cs).
