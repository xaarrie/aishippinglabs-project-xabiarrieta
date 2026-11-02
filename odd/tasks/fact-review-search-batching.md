# Batch fact review searches

## Goal and authorization
User approved optimizing TimelineFactReviewTools reconciliation by batching search queries, deduplicating repeat searches per run, and guiding the agent toward bounded batched search and batched fetch. Reduce redundant backend work and model/tool round trips without weakening authorized fact scope or recall guarantees. No guaranteed live call reduction without a trace.

## Scope and constraints
Preserve extensive existing dirty work. Single scoped writer. No staging/commits/push, live provider/database operations, credentials reads, restarts, dependency changes, or migrations. Technical artifacts English. Keep per-query ranking/limit and provenance, bounded batch and aggregate work, cancellation, serialized DbContext access, existing 60 backend-read budget (cache hits do not query backend). Do not concatenate queries into one shared ranking or increase budget through batching. Preserve scalar search compatibility where needed but expose batch tool to planner as primary search. Cache only within a tool instance/run; include effective limit in key; do not cache failures. Follow established backend validation rather than silently widening or truncating inputs.

## Tasks
- [x] B1 (done): Add behavioral failing regression for actual batch tool schema/invocation, bounded independent results, dedup/cache/limit budget and cancellation; implement minimal batch tool and prompt and run focused tests.
- [x] B2 (done): Assess candidate, independently verify per returned plan, run offline suite and API build, spot-check final diff and record limitations.

## Acceptance and checks
Distinct queries retain independent backend semantics and identifiable result groups. Duplicate queries and repeated equivalent calls reuse successful run-local results; changed limit is distinct. Input size and total backend work bounded, authorization unchanged, no concurrent reads on shared context. Actual model tool schema exposes query array; prompt says batch searches, reuse seed/results and avoid repeated queries. Existing scalar callers/tests remain supported or deliberately updated within scope. Observe RED/GREEN; build and offline tests use unique ignored repo-local tests/bin artifacts and SkipClientBuild=true to avoid locked outputs and repository-root discovery failures. Exclude LiveModel/Postgres/credential tests and disclose exclusions.

## Progress
Read-only map and parent spot check confirm scalar search + ID fetch with 60 invocation budget, no batched search or repeated-query cache. Optimization agent registered scalar search only. Existing relevant files are dirty; preserve all unrelated edits. No source edits in this feature yet. No commits authorized.


## Approved test scope adjustment
Worker paused without edits on existing TimelineFactSearchTests budget assertion (reported eight identical searches). Human explicitly approved adding dotnet/tests/ProjectTimeline.Tests/TimelineFactSearchTests.cs solely to align budget regression with uncached backend reads. Original four paths retained. Writer resumed; production MaximumCalls remains 60. No RED/GREEN results yet. B1 remains in progress.


## B1 evidence
Worker completed five scoped files, net +290 lines relative to entry state. Actual SDK registration test observed behavioral RED then GREEN; focused tools/planner/search/fulltext suite passed 88/88. Initial --no-restore attempt failed NETSDK1004 on fresh artifacts, resolved by restore-enabled run (not behavioral RED). Batch accepts 1–8 queries, per-query grouping/ranking, run-local whitespace/case cache keys including limit, preflight validation and uncached backend budget of 60, serialized reads; scalar compatibility retained and model registered batch tool. No staging/commits/live operations. Native ASSESS returned unassessable due to untracked declaration, RDD off; exact plan requires independent verifier. B2 now performs offline suite/API build and semantic code review. Live call reduction remains unmeasured.


## B2 final independent verification
Independent verifier: 1218 passed, 0 failed, 0 skipped with filter Category!=LiveModel&FullyQualifiedName!~ProjectTimeline.Tests.TimelineFactPostgresTests&FullyQualifiedName!~ProjectTimeline.Tests.BusinessTests.OpenAI_client_requires_environment_api_key. Exclusions are not skips. API build succeeded with zero warnings/errors; scoped git diff --check clean. Unique ignored artifacts: dotnet/tests/ProjectTimeline.Tests/bin/batch-review-verify-20260720-independent-a71f, SkipClientBuild=true. No severe batching regression found: actual SDK array invocation, independent ranking/limit/scope, instance-local cache equivalence, shared 60 backend-attempt budget, serialized reads, cancellation and failed-read retry verified. Parent spot check read full tool implementation. Native ASSESS unavailable/unassessable; RDD off, no native approval claimed. Worker behavioral RED/GREEN and 88 focused tests remain distinct evidence from independent suite GREEN.

## Completion and next step
Implementation and offline verification complete. No commits/staging/push/restart or live provider/DB execution. Live latency, provider round trips and PostgreSQL execution remain unmeasured; in-memory ranking is an existing approximation. Rebuild/relaunch updated application and measure a representative reconciliation trace to establish real savings; batching is not a guarantee of a fixed number of model calls. User owns runtime retry and any real data/provider operations.
