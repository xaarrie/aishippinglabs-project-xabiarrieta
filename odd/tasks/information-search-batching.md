# Information search batching

## Objective and authorization
User approved implementing batched information fact searches with deduplication/cache and partial completion on budget exhaustion, and explicitly increasing default searches from4to8. This follows read-only comparison with reconciliation batching.

## Evidence and design constraints
Information research currently charges every search invocation, including repeated queries; no cache. Reconciliation batches preserve per-query groups and cache successful normalized query+limit results. Batch tool reduces model roundtrips; distinct uncached searches still consume per-query allowance and backend reads. User explicitly approved default8 AND validated maximum8 after worker found current maximum4. Change both search-query bounds only; preserve every other limit. Do not conflate one batch with one search. Maintain source verification, emitted-ID allowlist, evidence limits, current authorization/revision/freshness, cancellation/deadline, and previous UnknownFactId bounded recovery/permanent fatal retention. Cache only successful validated results; no cached bypass of current security checks. Do not silently produce no-results after budget failure.

## Scope and non-goals
Information tools, search agent planning, options default, focused offline tests and evidence documentation. No new source-fetch batching unless essential and separately justified. No reconciliation behavior changes, migrations, DB/provider/live calls, secrets, app restart, assets/Vite, process termination, staging, commits or publishing. Preserve extensive pre-existing dirty work and prior fixes. Existing unrelated review-tools oversized-batch assertion remains outside scope.

## Tasks
- [x] B1 (implemented and independently verified offline): Test-first batched search, normalized successful-result cache/deduplication, default8queries and bounded partial completion through actual offline SDK tool loop. Preserve guarantees and backward test seams proportionately.
- [x] B2 (completed within approved offline scope): Assess candidate and independently verify focused tests plus proportionate wider checks. Record exact evidence and limitations.

## Acceptance checks
One batch can run distinct queries with separate results, counts distinct uncached searches rather than toolcalls, reuses normalized successful queries across calls. Different limits are not incorrectly conflated. Failed attempts not cached; retries remain bounded. Current permissions/revision/freshness checked even on reuse. Partial batch exhaustion surfaces explicit limits and only validated emitted evidence; no arbitrary IDs/no false complete result. Unknown-ID correction and fatal errors still work. Aggregate fact/output/source/tool/provider/deadline limits maintained. Default8 and exhaustion at next uncached query covered. Actual SDK-loop exhaustion behavior verified, not assumed. No promised live latency savings.

## Verification
Offline dotnet tests use --no-restore --configuration Release -p:SkipClientBuild=true; avoid Vite/locked Debug outputs. Observe meaningful RED/GREEN. Full offline suite only if safe (live provider/Postgres remain disabled), report known failure/skips honestly.

## Scope resolution
Worker stopped without writes/tests because preserving maximum4 contradicted default8. User selected approve_default8_maximum8 explicitly. Resume same writer; no additional paths.

## Writer evidence
B1 implemented across8authorized files: grouped batch tool, normalized query/effective-limit successful cache, aggregate emission/accounting, current-evidence revalidation, loop-only stop for validated partial closure without extra provider call, prompt batching, default/max8. Reported5meaningful RED failures; focused143passed. Full offline1299passed1knownreviewoversizefailure23skipped. No live/provider/Postgres/latency measurement or SQL count instrumentation. Writer partial because global suite baseline failure. Preserve earlier fixes. ASSESS unassessable due untracked files -> independent verifier required; RDD off.

## Independent verification
Research/information filter143passed0failed0skipped; AgentResearchExecutionTests/TimelineFactStrictSchemaTests48passed0failed0skipped. Total191independently passed. No must-fix defect identified. Verified cache auth/revision/source rechecks, attempted uncached query accounting, aggregate output fitting/one charge, emitted-only IDs, concurrency gate and partial closure preserving fatal/recovery/freshness/cancel checks without extra provider call. Default/max8 inspected, other current bounds unchanged by authorized implementation. Writer fullsuite1299passed1knownoversizefailure23skipped not independently repeated. Parent repeated scoped git diff --check exit0 only line-ending warning. Untracked files not covered by git diff/check; content inspected by verifier. No global green claim.

## Remaining checks and next step
Rebuild/restart application with updated code and retry original search. No runtime restart,live provider/Postgres/browser execution, SQL-count instrumentation or latency measurement performed. Known unrelated review-tools oversized-batch failure remains outside scope; live and Postgres checks disabled/skipped. Search batches reduce tool roundtrips, not distinct uncached query count; cached results still incur security/freshness checks. No data changes, staging or commits. Commit evidence:none; explicit user request required.
