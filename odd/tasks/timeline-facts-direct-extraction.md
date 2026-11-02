# Direct timeline fact extraction

## Confirmed architecture
Initial extraction persists only explicit source-grounded facts DIRECTLY to TimelineEntryFacts, without review proposal or acceptance. Later review corrections/additions/invalidations preserve history and update effective fact validity. Keep pending review delta tables; user did not authorize dropping them. Direct extracted facts are effective immediately and queue embedding jobs, but no embeddings are executed now.

## Authorization and constraints
Authorized source implementation with deterministic offline verification only. No DB connections/writes/migrations, paid calls, actual retry/apply, resets or commits. Existing accepted facts/proposals/jobs preserved. Supersedes odd/tasks/timeline-facts-recovery.md autoaccept/enrichment assumptions. Seven Retry jobs remain unchanged operationally. Extensive pre-existing worktree changes must survive. Applied migration state unknown: add new forward-compatible migration, never rewrite existing migration. No secrets in output/artifacts.

## Tasks
- [x] D1 (done): Add durable extraction provenance and direct atomic/idempotent persistence, compatible model/guards/new migration/read-search/embedding eligibility and deterministic tests. Existing accepted-review facts remain valid and readable.
- [x] D2 (done): Align initializer with direct completion (no autoaccept), preserve safe existing-job recovery intent separately, strengthen comprehensive detailed extraction and safe diagnostics with tests and operator docs. Do not silently launch retry/enrichment or change existing accepted content.
- [x] D3 (done): Assess and independently verify offline focused regression/build/CLI structure; report migration and live-behavior limitations.

## Acceptance criteria and checks
D1: no extraction proposal; fact origin anchored to durable extraction job and matching entry/source fingerprint, database uniqueness/replay key; review IDs optional only for extraction origin and required for review origin. Job completion/facts/revision/embedding jobs atomic, same completed job replay no duplicate facts/embeddings. Wrong source/job/review bypass rejected. Effective queries include direct and accepted review facts, invalid facts excluded. Existing review invalidation/history preserved. New migration SQL generated offline, do not apply. Preserve relational nullable-unique semantics.
D2: initializer reports direct persistence, never Prepare/Accept for extraction. Complete detailed faithful coverage retains quantities/dates/conditions/relationships, exact quotes, no arbitrary count or invention. Payload-free diagnostic codes distinguish key rejection classes. No semantic-completeness guarantee from fake fixtures. Canceled retry writer edits must be inventoried before writing; do not assume none.
D3: meaningful observed RED/GREEN in writer; fake provider/InMemory tests, offline migration SQL/translation and isolated console build. No PostgreSQL/live tests; cached assets only, generated output beneath initializer artifacts. Assess failure conservatively requires independent verifier. RDD currently off.

## Evidence
Source exploration shows mandatory review IDs/guards, extraction->pending proposal, EffectiveAsync accepted-proposal join, embedding review prerequisite. Existing DB operational history: 1 accepted entry/3 facts and7 Retry/InvalidResponse. No exact InvalidResponse cause proven. Embedding policy confirmed by user: queue, do not execute now. This coherent provenance change crosses schema/service/guards/read paths and may exceed advisory 400 lines; tests and safety checks must not be omitted to reduce size.

## Next step
D1 implementation observed: focused RED (pending proposal) then GREEN, 140 D1 tests passed. Integration remains partial: 42 initializer/retry passed, 14 failed old acceptance/outcome assumptions/fixtures. Do not mark D1 complete until integration green. Delegate D2 serially. No DB/provider/migrations/commits. Parent owns task state and mirror.

## Reconciliation update
User reports manually deleting facts/jobs for failed entries: previous seven Retry job snapshot is stale; no restoration or runtime action inferred. Before next execution inspect actual eligibility read-only. User approved D1 expansion to TimelineFactInformationSearch.cs, Agents/AgentModels/TimelineFactInformationResult.cs, Agents/SearchEntriesAgent.cs, WebApi/TimelineFactApplication.cs, and TimelineFactInformationTests.cs for nullable review provenance and direct-fact retrieval. Writer initially stopped without edits, now resumed with exact expanded scope.

## D1 evidence
Direct fact origin uses nullable review provenance with durable extraction job/candidate identity, check constraint, FK/unique index. Atomic direct completion/revision/embedding queuing, replay/source guard tests, dual-origin eligibility/citations, new 20261018000200 migration and frozen metadata. 19 files touched; authored delta unknown due pre-existing untracked files. Initial migration untouched. SQL/metadata checked offline only, no PostgreSQL proof. D2 must remove autoaccept console behavior and update 14 legacy integration expectations.

## D2 scope and RED update
Full all-TimelineFact offline baseline observed 235 passed /16 failed of251:14 initializer/retry plus2 hosting fixture incompatibilities. No D2 edits before boundary. User explicitly approved adding dotnet/tests/ProjectTimeline.Tests/TimelineFactHostingTests.cs; repair fixtures to satisfy canonical job/provenance constraints, never weaken production guards. D2 resumed serially; console build/help and new diagnostic/prompt GREEN pending.

## D2 GREEN and D3 transition
D2 completed direct Saved/WouldSave console+retry outcomes without proposal/AcceptAsync, queue-only embeddings, detailed supported-claim prompt and closed-vocabulary invocation diagnostics; historical durable error categories preserved. Inherited16 failures resolved without weakened guards. Observed new provider RED7 pass10 fail and diagnostic RED30 pass1 fail; final full offline fact regression274 pass0 fail0 skip (includes D1), provider23 pass, initializer32 pass. Isolated console build0 warnings/errors, --help exit0. No DB/provider/migrations executed. Limit: >=200 candidate capacity conservatively rejected, no chunking/enrichment, fake coverage not live semantic completeness. New migration required, applied status unknown. ASSESS unassessable from pre-existing undeclared untracked paths; RDD off, conservative independent verifier required. D3 underway.

## D3 independent result and next step
Independent PASS:274 offline fact tests passed0 failed0 skipped; isolated console build0 warnings/errors; built --help exit0, parent spot-check also exit0. No actionable source defect established. Independent inspection covered atomic completion/provenance/CAS/dual-origin eligibility/invalidation history/retry scope/SQL generation+translation and safe diagnostics. Historical writer RED not independently observed. Native assessment unavailable; independent conservative verification completed, RDD off.
Before operational use, separately authorize/read applied migration history and resolve historical placeholder202610180001 versus actual20261018000100 identity; source comment not deployment proof. New20261018000200 migration NOT applied. No PostgreSQL/live provider/execution or exhaustive semantic completeness proof. Current limits200 title/20000 content/12000 tokens, >=200 candidates rejected, no chunking/enrichment. Existing DB deletion report remains unverified; recheck eligibility before future execution. No commits. Implementation tasks complete; deployment and actual rerun pending user decision.
