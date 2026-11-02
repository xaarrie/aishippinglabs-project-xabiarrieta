# Initialize facts for legacy timeline entries

## Safety contract

`ProjectTimeline.FactsInitializer` finds entries with **zero physical `TimelineEntryFacts` rows**. An invalid fact still counts as a row and excludes the entry. It does not equate missing effective facts with missing physical facts.

The default is **dry-run**: database reads only, no provider construction, extraction calls or writes. `--apply` explicitly authorizes paid initial extraction and **direct persistence of validated facts**, effective immediately. Initial extraction creates no review proposal and never invokes acceptance. Direct completion queues embedding jobs, but this console **never executes embeddings or drains any queue**. Later review remains separate: accepted corrections/additions/invalidations preserve fact history.

This is a dedicated console, not Web API startup: it performs no migrations, seeding or background hosting. The operator must first arrange the deployed schema, including pgvector and forward migrations `20261018000200_DirectTimelineFactExtraction` and `20261018000300_FactInvalidationCause` (do not rewrite existing migrations); see [timeline-facts.md](timeline-facts.md). Do not start the Web API as a schema workaround.

## Operator usage

Inject secrets privately through the environment or a secret manager. Never put connection strings or API keys in command arguments, shell history or captured output.

| Environment | Requirement |
| --- | --- |
| `ConnectionStrings__ProjectTimeline` | Required even for dry-run; explicit Host, Database and Username. No application/local database fallback. |
| `OPENAI_API_KEY` | Required for `--apply`, not read for dry-run. |
| `TimelineFacts__ExtractionModel` | Optional; existing provider default `gpt-4o-mini`. |

Use an existing **active user ID** with normal project write access. Administrator status is read from the user's database role (`Admin`), never supplied by a CLI flag. Project owners, Creators and Contributors can write; Readers cannot. Authorization is rechecked per entry and immediately before and after extraction, before direct completion; source-author access is also enforced by the shared processor/guards. Choosing an actor ID is an operator responsibility; this utility does not authenticate an interactive login.

After offline build, run the built executable directly from the repository root (replace the example actor UUID with the approved active identity):

```powershell
dotnet dotnet/src/ProjectTimeline.FactsInitializer/artifacts/bin/ProjectTimeline.FactsInitializer/debug/ProjectTimeline.FactsInitializer.dll --actor-id 11111111-1111-1111-1111-111111111111
```

Review the dry-run output, database target privately, permissions and billing intent. Only then repeat with `--apply` to extract and save directly. Optional `--page-size 1` through `--page-size 100` controls bounded database pages (default 50), **not a total entry limit**. `--help` does not read configuration or connect.

## Explicit existing-job recovery (opt-in)

Without `--retry-existing`, **all existing jobs are skipped**, including historical completed work.
To inspect retries in exactly one timeline, supply both flags in addition to the active actor:

```powershell
dotnet dotnet/src/ProjectTimeline.FactsInitializer/artifacts/bin/ProjectTimeline.FactsInitializer/debug/ProjectTimeline.FactsInitializer.dll --retry-existing --timeline-id 22222222-2222-2222-2222-222222222222 --actor-id 11111111-1111-1111-1111-111111111111
```

This remains **dry-run** (`WouldRetry`): no provider construction, calls or database writes.
Only a separately approved future invocation with `--apply` authorizes paid retry and **direct fact persistence** without review preparation/acceptance. Do not run apply as a development check.

Recovery captures one entry-number upper bound and reads bounded pages in increasing entry order,
only inside the requested timeline. Page size is not a total limit. It selects exactly one existing
`Extraction` job in `Retry`, due now, with matching source and no active lease. It never creates a
replacement extraction job or resets attempts. A selected job is processed once per invocation;
failed attempts retain its ID and increment its attempt count using the existing backoff policy.
Pending, Running (even expired), terminal, future-due and competing jobs are not recovered here.
Any physical fact (including invalid facts), or any pre-existing proposal, excludes the entry.

Assigned model evidence must match `TimelineFacts__ExtractionModel` (default `gpt-4o-mini`);
`unassigned` is assigned by the existing processor. No new prompt-version or other absent model
criteria are inferred. Retry acquisition is bound to the selected job version and Retry state.
Active-user status and the current database role/write permission, source, competing evidence and
lease ownership are checked again immediately before and after extraction, before direct completion. No transaction
is held over the provider call. Shared completion validates source evidence and timeline revision, saving facts,
completion evidence and embedding jobs atomically. Embeddings are queued only, never executed by this console.
No global work queue is scanned. Existing accepted facts are never enriched, overwritten or re-extracted.

Additional outcomes: `WouldRetry`, `NotDue`, `ActiveLease`, `IncompatibleModel`. Existing or competing
work remains `ExistingWork`; changed source/job version or lost lease is `Stale`. Provider failures
remain sanitized `ExtractionFailed`. Skips preserve durable evidence; no deletion, terminal reset,
acceptance of arbitrary pending proposals, enrichment or force override is offered. Historical completed work
or pending review proposals remain untouched, not accepted on a later invocation. Inspect them through a
separately approved workflow. The previously observed retry-row snapshot is stale after manual deletions;
no operational rows are reconstructed and current eligibility must be inspected read-only before any future run.
Fake tests do not establish PostgreSQL isolation, live-provider completeness, or current deployed-row eligibility.

## Results and resuming (legacy mode)

Output contains entry UUIDs, fixed outcome codes, optional allowlisted `Diagnostic=<code>` and summary counts, never source text, entity keys, claims, connection strings, provider bodies or raw exceptions.

- `WouldSave`: dry-run eligible at inspection, not a guarantee of extraction/persistence success.
- `Saved`: this invocation's extraction job completed direct persistence; facts are effective, no review was fabricated or accepted.
- `ExistingWork`: any existing job (including completed, failed or expired jobs) or proposal is preserved. Nothing is resumed, reset, replaced or silently accepted.
- `Questions`: unanswered questions in an inspected pending proposal; no answers are invented. Existing proposals remain untouched. The extraction provider currently returns candidates only, not clarification questions.
- `Empty`: extraction completed without candidates; no proposal or facts created. Its durable job prevents repeated billing on later runs.
- `Stale`: source/eligibility changed, competing evidence appeared, or a concurrency/lease conflict occurred. Pending evidence and jobs are retained for inspection.
- `AccessDenied`, `ExtractionFailed`, `Failed`: invocation did not complete a direct save. Later entries can still proceed; failure payloads are intentionally suppressed.

Exit codes: `0` completed (including ordinary existing-work skips and empty extractions); `1` invalid arguments/configuration; `2` incomplete work, questions, access/stale/processing failures or fatal database failure; `130` cancellation. Ctrl+C requests cancellation. An interrupted provider call can already have billed; cancellation is not a refund.

Timeline UUIDs use bounded keyset pages within an initial UUID upper bound. Each timeline captures an entry-number upper bound when visited, then scans increasing unique entry numbers in bounded pages. Eligibility is checked again before reserving a new job and before/after extraction, before direct persistence. Concurrent timeline changes can invalidate completion even when another entry changed; the job retains retry history and no partial facts are saved. There is no database transaction held over HTTP and no whole-run snapshot. Newer entries beyond captured bounds are deferred; concurrent inserts below bounds can appear or be missed until the next run. Rerunning safely discovers remaining eligible entries, but **does not recover pre-existing work**. A crash after reservation/extraction deliberately leaves durable work that a later run skips. Investigate through the existing separately approved operational workflow; the explicit retry mode above is limited to due Retry jobs, not a general force/resume switch.

## Detailed extraction and validation limits

Extraction now uses `TimelineFactExtractionAgent` over the existing framework `ICustomChatClient`; its strict JSON schema is generated from the `TimelineFactExtractionResult` class, not maintained separately. The chat boundary is intentional: the existing typed `ICustomAgent.RunAsync<T>` does not expose request options, cancellation or raw finish/refusal metadata. Missing/unknown metadata fails closed. `CustomOpenAIClient` uses the installed OpenAI SDK for both chat and numerical embeddings; the thin fact provider contains no HTTP transport. The explicit-credential SDK path bounds responses before parsing, disables SDK retries and applies a 90-second call timeout. Existing durable job retries still apply and can rebill.

The separate `ICustomEmbeddingClient` returns numerical arrays validated for 1536 finite, nonzero values using `text-embedding-3-small`; persistence owns pgvector conversion. Offline tests inject framework chat clients or SDK HTTP transports without credentials/network. Console apply still calls extraction only, never this embedding API.

The extraction prompt requests every independent explicitly supported statement across the full title/content, including **entity descriptions, definitions and attributes**, not only events. A car's explicit color is a fact. An explicit table definition includes its purpose and individual columns/types/lengths/nullability/keys/defaults/relationships/constraints, with units/dates only when stated. No schema convention or outside knowledge may fill gaps. Every claim names its entity and retains required project/system/table/scope and conditions, preserving original names and language; entity keys alone do not make pronouns or relative fragments self-contained. Distinct facts about the same entity are retained, not deduplicated by entity. A bounded complete definition may be one claim; larger definitions split into meaningful self-contained components within the 4000-character claim limit without losing context/details or silently truncating. Logical atomicity must not erase context. The prompt also covers quantities/units, dates, actors, conditions, dependencies, negations and relationships. It forbids lossy summaries, invention, count padding and treating plans/conditions as completed unconditional events. It prefers verbatim citations but admits approximate `SourceQuote` candidates after shape/count/duplicate/source-kind validation; it does not reject a model-written citation merely for substring mismatch. No arbitrary minimum count is imposed.

Every **nonempty** extraction adds **one separate semantic-verifier call** using `TimelineFactSemanticVerifierAgent`, the same configured model and the existing SDK chat path. Empty extraction has only the extraction call. The verifier gets the full original title/content and all indexed candidates, with explicit untrusted-data instructions. Its strict schema derives from `TimelineFactSemanticVerificationResult`. It recognizes supported descriptions/definitions/schema attributes as legitimate claims, while checking self-contained scope and conditional meaning. It must assess the entire claim and approximate citation, not similarity alone, without dropping or repairing claims/entities. Exact unique complete candidate-index coverage, explicit `Supported=true`, finite `SupportScore` in 0..1 and no `Numeric`, `Date`, `Actor`, `Negation` or `Unsupported` issues are required. **No numeric acceptance threshold exists.** The score is a transient model assessment, not a calibrated probability; it is neither stored nor added to API responses.

The verifier selects IDs of deterministic source spans, never replacement evidence text. Spans partition title/content losslessly at UTF-16 positions, at most 256 code units each, without splitting surrogate pairs (at most 81 spans per entry). IDs bind the complete original source, field and offset. Code reconstructs final `SourceQuote` directly from the original string; multiple selected IDs must belong to one field, and their contiguous envelope includes any intervening text. Unknown, duplicate, malformed, other-input or mixed-title/content IDs reject the batch. Mechanical span boundaries can produce broader evidence than the original approximate citation. The model is responsible for assessing support within that evidence; a literal substring alone does not prove entailment.

There is no persisted verification flag, bypass or receipt. Processing/service/database literal-evidence guards, source fingerprints, candidate/result hashes and review exact-quote checks remain in force. Final hashes use the grounded candidates. Custom fake providers still face the final literal-evidence checks. Any extraction/verifier failure rejects the **whole batch**, never a valid prefix. Extra verification increases cost and latency; each call retains cancellation, a 90-second bound, zero SDK retries and safe finish/refusal handling. Existing three-minute leases and durable retries are unchanged; near-limit calls can lose the lease and retries may rebill.

Input is sent intact or rejected: title at most 200 characters, content at most 20,000. The request uses a 12,000 completion-token budget and schema ceiling of 200 candidates; the HTTP response is capped at 512 KiB. The verifier also uses 12,000 output tokens and a 512 KiB response bound, with a 768 KiB serialized input bound (source, span catalog and all candidates). Model-specific context/output capacity is not negotiated. A `length` finish reason is rejected, not parsed as partial success; refusal/filter output, malformed envelopes/schema, unsupported semantic assessments, invalid evidence IDs, invalid fields and duplicates are also rejected. At **200 candidates** the extraction agent conservatively rejects the batch because ceiling saturation may conceal omissions; it never silently keeps a prefix. These are safety ceilings, not desired extraction sizes. Dense entries can therefore fail safely and require a separately designed workflow; there is no automatic chunking or enrichment.

Granular diagnostics are allowlisted: `InputLimit`, `ResponseLimit`, `ResponseEnvelope`, `ResponseSchema`, `ResponseTruncated`, `ResponseRefused`, `CandidateLimit`, `CandidateShape`, `UnsupportedQuote`, `DuplicateCandidate`, `SemanticUnsupported`, `MalformedAssessment`, `InvalidEvidence`. The exception and this invocation's output carry only the fixed class. Durable `LastErrorCode` retains the compatible `InvalidResponse` category (or existing timeout/rate-limit/unavailable categories); granular diagnostics are **not** a new persisted history field and cannot explain old rows retroactively. An invalid candidate rejects the entire batch before persistence.

Offline fake responses verify request instructions, intact source/candidates, grounded evidence reconstruction and all-or-nothing gating. Contradictory number/date/actor/negation fixtures supply fake model findings; they **do not prove actual semantic reasoning, completeness or accuracy of a live model**. Exact-quote validation proves quote presence, not entailment of the claim. Successful output below all limits may still omit statements or receive an incorrect model assessment; no exhaustive-coverage or live semantic guarantee is claimed. This source-only change performs no paid calls, existing-job recovery, database mutation/migration, review mutation or embedding execution.

## Future derived-data regeneration: dependency gate

This document does **not** authorize a reset or migration execution. Original timeline entries (IDs, numbers, titles, content and authors) **must survive** any separately approved regeneration. Preserve proposals, review answers, invalidations and audit history too; the initializer is not a reset tool.

Before future operations, inspect the current migration state, target timeline/entry IDs, row counts, active leases and all references privately. Obtain a fresh approved database backup, archive listing and hash; backup creation is not proof of tested restoration. The new forward cause migration is required before code using cause columns can run. Review its SQL and frozen metadata; historical nullable causes are preserved, never guessed/backfilled.

A facts/jobs-only reset may now be impossible without destroying retained evidence: facts can reference other facts through `InvalidatedByFactId`, and preserved proposal invalidations, accepted additions/reviews or other external references can depend on targeted facts. Stop if preserving those rows prevents the scoped reset; do not cascade, disable guards/FKs, delete reviews, or expand scope. Parent/operator must resolve that dependency before any mutation. If safe and separately authorized, the operation must order dependent embedding jobs before targeted facts before extraction jobs, preserve original entries and all retained evidence, and recheck timeline revisions/concurrent proposals atomically. Do not copy old row-count snapshots as current proof.

After independent offline verification and fresh operational approval, inspect dry-run eligibility before one bounded regeneration invocation. Embeddings remain queue-only. Existing proposal/job exclusions still apply; do not remove them to force eligibility. Failures are reported, not blindly replayed. Curated car/schema fake fixtures demonstrate prompt, exact-output transport and semantic rejection contracts, not a guarantee that a live model extracts every attribute.

## Offline verification (no remote feeds)

Dependencies must already exist in the local NuGet package cache. `Offline.NuGet.Config` clears package sources and fallback folders; auditing is disabled during restore. A missing cached package fails rather than downloading anything. All new restore/build/test artifacts stay under the initializer's ignored `artifacts/` directory.

From the repository root in PowerShell:

```powershell
$base = (Resolve-Path dotnet/src/ProjectTimeline.FactsInitializer).Path
$artifacts = Join-Path $base artifacts
$props = Join-Path $base IsolatedBuild.props

dotnet restore dotnet/tests/ProjectTimeline.Tests/ProjectTimeline.Tests.csproj --configfile "$base/Offline.NuGet.Config" --artifacts-path "$artifacts" -p:NuGetAudit=false -p:DesignTimeBuild=true "-p:CustomAfterMicrosoftCommonProps=$props"
dotnet test dotnet/tests/ProjectTimeline.Tests/ProjectTimeline.Tests.csproj --no-restore --artifacts-path "$artifacts" -p:DesignTimeBuild=true -p:BuildProjectReferences=true "-p:CustomAfterMicrosoftCommonProps=$props" --filter 'FullyQualifiedName~TimelineFactInitializerTests|FullyQualifiedName~TimelineFactRetryTests'
dotnet test dotnet/tests/ProjectTimeline.Tests/ProjectTimeline.Tests.csproj --no-build --no-restore --artifacts-path "$artifacts" -p:DesignTimeBuild=true -p:BuildProjectReferences=true "-p:CustomAfterMicrosoftCommonProps=$props" --filter 'FullyQualifiedName~TimelineFact&FullyQualifiedName!~TimelineFactPostgresTests&Category!=LiveModel'
dotnet build dotnet/src/ProjectTimeline.FactsInitializer/ProjectTimeline.FactsInitializer.csproj --no-restore --artifacts-path "$artifacts" -p:DesignTimeBuild=true -p:BuildProjectReferences=true "-p:CustomAfterMicrosoftCommonProps=$props"
```

`DesignTimeBuild=true` bypasses the Web API frontend hook when building test references. `IsolatedBuild.props` excludes legacy `bin/obj` and relocated artifacts from source globs. No launch settings, user secrets, database connection or paid provider is required by these initializer tests. Regression filtering excludes actual PostgreSQL tests and live-model cases regardless of externally supplied opt-in flags. The opt-in safety-unit tests can run offline.

EF InMemory/fake-provider tests and offline SQL translation are **not** proof of PostgreSQL transaction isolation, live-model accuracy or deployment readiness. Do not run the utility against a real database, relational tests or paid models as part of offline verification.
