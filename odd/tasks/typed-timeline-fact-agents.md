# Typed timeline fact agent execution

## Objective and authorization
Migrate TimelineFactExtractionAgent and TimelineFactSemanticVerifierAgent to typed `ICustomAgent.RunAsync<T>` execution following SearchEntriesAgent. User explicitly selected this migration, including minimal framework adaptation to preserve current validation and cancellation.

## Why
Public results already use classes, but agents currently consume text and deserialize manually. The desired outcome is typed execution inside the agents, not just typed public signatures.

## Scope and constraints
- Reuse existing extraction/verifier result models, regular LocalCustomAgent, and agent initialization conventions.
- Preserve all current Markdown prompt content and formatting, models/model defaults, provider contracts, strict schemas, input/output/depth limits, refusal/truncation/missing-envelope checks, caller cancellation, and diagnostic/error semantics.
- Keep existing injected chat clients and existing agent consumers/fakes compatible where practical; no new agent loop, tools, persistence, or ambient harness behavior.
- A minimal framework structured-run extension may be introduced. Do not assume AgentResponse<T> retains raw OpenAI completion metadata; enforce existing gates before producing typed results.
- No unrelated worktree changes, dependencies, project configuration, staging, branches, commits, publishing or live-model calls. Commits require explicit user request.
- One coherent prospective work unit: `refactor(agents): use typed execution for timeline facts`.

## Tasks
- [x] T1 — Add minimal typed structured-run support and migrate both agents with deterministic regression tests. Status: done; observed typed-routing RED and focused GREEN.
- [x] T3 — Diagnose and refresh stale referenced binaries without rebuilding frontend or changing configuration. Status: done; full reference build passed without frontend invocation.
- [x] T2 — Independently verify source/result contracts, compatible failures and cancellation, focused tests, and compilation. Status: done; focused/provider and complete offline regressions GREEN.

## Acceptance and checks
- Both agents initialize regular ICustomAgent instances and use typed RunAsync<T>, obtaining existing result classes through AgentResponse<T>.Result; no plain-text response parsing in their orchestration methods.
- Observe RED before implementation for typed-run routing; GREEN after minimal changes and checks after refactoring.
- Regression tests cover exact result types, strict schema/token/serialization options, envelope/refusal/truncation rejection, invalid result shape and semantics, limits and cancellation, while existing tests retain their assertions.
- Preserve constructor/client compatibility and no instructions/source changes outside intended migration.
- Build the complete test-project graph with --no-restore -p:DesignTimeBuild=true -p:BuildProjectReferences=true, then run tests with --no-build --no-restore to avoid frontend generation while using coherent references. Verify built graph DLLs match test-output copies and exercise current changes.
- Run git diff --check on scoped files. Run full deterministic suite where applicable without live/external dependencies; report every failed, skipped, blocked or pending check.
- RDD is off. After writer returns, call native ASSESS and follow the returned verifier plan. No authority/lifecycle mutations for unrelated changes.

## Progress and evidence
- Exploration established that public methods already return typed results; user confirmed the desired change is the internal ICustomAgent.RunAsync<T> pattern.
- Current ICustomAgent has no options/cancellation overload. Current direct-chat helper checks raw ChatCompletion metadata; these checks must remain before typed results are returned.
- Existing Markdown prompt edits and unrelated dirty-worktree files are the starting state and must be preserved.
- T1 implemented in seven source/test files: CustomAgent.cs, LocalCustomAgent.cs, new StructuredAgentRunOptions.cs, TimelineFactStructuredResponse.cs, both domain agents, and new StructuredAgentRunTests.cs. CustomOpenAIClient and existing test files required no changes.
- Structured overload retains legacy implementations through an explicit unsupported default; LocalCustomAgent decorates its per-run chat client to enforce raw metadata gates before AgentResponse<T> admission. A private serializer-options copy is prepared with SDK type metadata before reading Result.
- Observed behavioral RED: 4 failing tests / 208 passed (missing typed routing and cancellation). Initial scaffolding compile error CS9113 was corrected before that RED. Final focused GREEN: 237 passed, 0 failed, 0 skipped.
- Final BusinessLogic --no-restore build: 0 warnings, 0 errors. Built/test-output DLL SHA-256 matched: 1d64447e1919eef98dea6f6be3f8ca18de7aae1289d39188bb319f36c800fa24.
- Worker preserved both prompt constants byte-for-byte against its starting worktree snapshots. Scoped diff --check passed; parent repeated scoped check (exit 0, newline-conversion warnings only) and spot-reviewed LocalCustomAgent typed execution.
- Native ASSESS returned unassessable due ambient untracked paths, requiring independent verification. RDD remains off; no lifecycle mutation performed.
- Independent source review found no migration defect; verified production typed route, metadata admission before typed deserialization, strict controls, legacy compatibility, and actual IChatClient cancellation tests.
- Broader focused verification: 477 passed, 21 failed, 4 PostgreSQL opt-in tests skipped (502 total). Offline suite: 753 passed, 21 failed, 0 skipped (774 total), excluding paid LiveModel and real PostgreSQL tests. The same 21 failures concern API/HTTP/hosting MissingMethodException signatures from stale WebApi/reference binaries, not a demonstrated source regression.
- BusinessLogic build passed 0 warnings/errors, DLL hash matches both output locations. All scoped whitespace checks passed; wwwroot hashes/status unchanged.
- Initial independent verification was partial due DesignTimeBuild reference-build suppression; T3 diagnosed and resolved that tooling issue without source, configuration or dependency changes.
- T3 diagnosis confirmed SDK 10.0.401 defaults BuildProjectReferences=false only when unset during design-time builds; explicit global true enables reference builds while WebApi independently suppresses BuildClientApp.
- `dotnet build dotnet/tests/ProjectTimeline.Tests/ProjectTimeline.Tests.csproj --no-restore -p:DesignTimeBuild=true -p:BuildProjectReferences=true` passed: 0 warnings, 0 errors; rebuilt the complete six-project graph with no npm/Vite invocation.
- WebApi was stale and rebuilt to SHA-256 dba16f9445cde7cc47e0ffc8367f624e48fdc84cdad3097c3ee1a517a916af60; BusinessLogic retained 1d64447e1919eef98dea6f6be3f8ca18de7aae1289d39188bb319f36c800fa24. WebApi/DataAccessLogic/BusinessLogic output DLLs match their test-output copies.
- Build refresh left git status and frontend asset hashes unchanged; no source/config/dependency edits or cleanup were needed.
- Final independent focused/provider replay with --no-build --no-restore passed: 498 passed, 0 failed, 4 expected PostgreSQL opt-in skips (502 total).
- Final complete offline replay with --no-build --no-restore --filter "Category!=LiveModel&FullyQualifiedName!~ProjectTimeline.Tests.TimelineFactPostgresTests" passed: 774 passed, 0 failed, 0 skipped. Paid OptimizeEntryLiveModelTests and real-database TimelineFactPostgresTests were excluded; deterministic PostgreSQL safety tests remained included.
- All 21 historical API/HTTP/hosting MissingMethodException failures resolved after rebuilding current WebApi. No migration source defect was found.
- Final scoped whitespace checks passed; new/untracked files were separately checked too. All six graph DLLs remained unchanged during replay and all five referenced DLLs match test-output copies. Frontend asset hashes and git status remained unchanged.
- Limitations: ordinary frontend/static-web-assets build and live model/real database checks were not run. No model-output equivalence claim. No remaining blocker in authorized scope; no commits authorized or made.

## Next step
Review the typed RunAsync<T> calls and structured .Result consumption in both agents. Implementation and deterministic verification are complete; commit/publishing remain explicit user decisions.
