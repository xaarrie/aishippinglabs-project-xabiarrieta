# Timeline entry facts and accepted reviews

## Objective and authorization
Implement immutable entry sources with atomic, evidence-backed facts. User selected PENDING facts until explicit review acceptance; GraphRAG is deferred. Local pgvector installation was initially authorized, then explicitly DEFERRED when told official Windows compilation requires VS C++ tools (several GB) and administrator UAC. Prepare official-source instructions; no third-party binaries, DB connections, installations, migrations or paid calls until explicitly resumed. No commits/pushes authorized. UPDATE: user has installed VS C++ tools and explicitly reauthorized official pgvector installation on local PostgreSQL. Scope for this resumption is building/installing/verifying extension files only; application migration, paid processing and relational test execution remain unapproved.

## Scope and constraints
- Preserve unrelated dirty .atl, .gitignore, hosted wwwroot assets and untracked user artifacts.
- Physical table: TimelineEntryFacts; CLR TimelineFact/DbSet TimelineFacts retained. Accepted facts only, immutable claims/evidence, Valid plus invalidation provenance.
- Pending proposals persist separately. Acceptance is server-owned, ID-only/bodyless, authorized, stale-safe, complete and idempotent; applies additions/invalidations/jobs together. Sources are never rewritten by acceptance; evidence/job guards prevent later edits/deletion.
- Durable extraction on new-entry save, replay-fenced CAS leases/retries/cancellation and accepted-valid-only embeddings. Provider calls outside transactions; crash recovery can repeat billed requests.
- OpenAI text-embedding-3-small, 1536 dimensions, vector(1536), model metadata; no vector queries yet.
- Read-only optimizer tools perform bounded SQL lexical/entity search over authorized accepted facts, with chronology and provenance. Preserve incomplete-history/truncation warnings; no full-history fallback or GraphRAG.
- Actual source and trusted indexed user answers provide evidence; quote presence does not prove semantic entailment. Human review remains required.
- Background TimelineFacts:ProcessingEnabled defaults false. Explicit review is still an on-demand paid operation. No automatic legacy backfill; explicit bounded resumable enqueue only.
- One writer at a time. Deterministic test-first where applicable; never invent RED or relational proof. RDD switch observed off; native ASSESS unassessable on undeclared untracked files, so independent verification required.

## Tasks
- [x] T1a: Resolve isolated build/reference failure. Verified DesignTimeBuild=true + BuildProjectReferences=true + OutputPath=bin/T1/ avoids Vite and locked normal DLLs.
- [x] T1: Persist facts/proposals/jobs, transactional acceptance and guards with EF migration. Independent 54/54 offline. Fixed migration ID to 20261018000100_TimelineFacts, complete frozen historical target and whole-proposal guard. No live PostgreSQL proof.
- [x] T2: Durable extraction/embedding processing and ingestion. Independent 75/75 offline. Proposal ID equals job ID; completion and revision reservation saved together. Fake HTTP/provider tests only.
- [x] T2b: Scoped accepted-fact search/planner/tools. Independent 110/110 offline. Typed additions/invalidations/questions with trusted server-stored clarification lineage. No vector queries or production model execution.
- [x] T3: Fact APIs and main UI. Independent 124 .NET/25 Node, build clean and mounted Chrome/Playwright mock flow (19 API requests, zero runtime errors). ADD/INVALIDATE evidence, incomplete warnings, indexed clarification, bodyless acceptance, stale409/regeneration, source preserved and no PUT. Actual HTTP endpoint proof added in T5; not actual Program/JWT/startup proof.
- [x] T4: Prepare official Windows pgvector deployment guide and opt-in relational tests. Independent static safety review passed. Writer 130 pass/4 skip; no actual relational execution. See dotnet/docs/timeline-facts.md.
- [x] T5a (done; independent full-suite recovery confirmed): Fixed 13 wider-regression test failures without weakening production: seed real timeline/project fixtures in EntryReviewDetailTests, EntryReviewTests, OptimizationAgentToolsTests; replace obsolete HostedUiTests review-to-PUT assertion with fact-review/source-preservation expectations.
- [x] T5 (done; offline implementation only): Final independent deterministic regression/build/UI verification and operational evidence reconciled: 490 .NET passed, 0 failed, 4 PostgreSQL skipped; 25 Node passed; isolated .NET/Vite builds and whitespace checks passed. Deployment/live boundaries remain explicitly pending in D1 and below.
- [x] D1a (done; independent artifact verification PASS): Compile official pinned pgvector v0.8.7 with installed VS C++ x64 tools for local PostgreSQL18.6, install extension files (UAC if needed), and independently verify installed binaries/SQL against built artifacts. No application migration, paid work or service interruption.
- [ ] D1 (remaining deployment deferred): Enable the extension in an explicitly selected database and execute application migration/vector/rollback/concurrency checks only with appropriate target/credential authorization. Do not run paid work.

## Implemented acceptance checks and evidence
- T1 guard regression RED 21 pass/7 fail, final 31/31; independent service/regression confirmation 54/54.
- T2 ingestion RED 54 pass/1 fail, final independent 75/75.
- T2b fabricated quotes/answers/unknown IDs reproduced before fixes, final independent 110/110.
- T3 independent .NET124/Node25, isolated Vite and mounted browser; screenshots WebApi/bin/T1/t3-browser-accepted.png and t3-browser-stale.png.
- T5 added 28 TestServer HTTP cases using test-only WebApplication, fake auth/providers and isolated InMemory; TestHost 10.0.0. Observed oversized unknown-length JSON returned200 instead of413, fixed independent bounded-read middleware. Exactly64KiB allowed; oversized/malformed/null/forged/body-bearing acceptance rejected. No production startup bypass introduced.
- Requested physical table name aligned in unapplied migration/runtime/frozen metadata/forward+reverse SQL; naming RED158pass2fail4skip then160pass4skip. EF63-character FK truncation covered. No second migration.
- Initial normal test build invoked Vite and hit locked WebApi/VS DLLs; never stopped those processes or restored preexisting dirty assets. Subsequent Vite checks use isolated output only.
- Work-unit commits not made: explicit commit authorization absent.

## Latest independent final verification (T5 PASS; deployment deferred)
- Isolated .NET build: PASS, 0 warnings/errors.
- Focused TimelineFact/Business/ApiPersistence: 160 passed, 0 failed, 4 PostgreSQL skipped.
- Wider deterministic suite excluding OptimizeEntryLiveModelTests: FINAL 490 passed, 0 failed, 4 PostgreSQL skipped (494 total). The earlier 477 pass/13 fail/4 skip result was corrected via T5a: valid scope fixtures and new fact-review UI assertions, no production weakening. Stable total confirms recovered failures were not excluded.
- Node tests: 25 passed; module-type warnings only.
- Isolated Vite: PASS, same artifact names as mounted-browser evidence; no later UI changes.
- git diff --check: PASS, existing line-ending warnings only. Feature status 43 entries (10 tracked modifications/33 untracked entries including collapsed docs); no unexpected mutations observed.

## Safe verification commands
```text
dotnet restore dotnet/tests/ProjectTimeline.Tests/ProjectTimeline.Tests.csproj
dotnet build dotnet/tests/ProjectTimeline.Tests/ProjectTimeline.Tests.csproj --no-restore -p:DesignTimeBuild=true -p:BuildProjectReferences=true -p:OutputPath=bin/T1/
dotnet test dotnet/tests/ProjectTimeline.Tests/ProjectTimeline.Tests.csproj --no-build --no-restore --filter 'FullyQualifiedName~TimelineFact|FullyQualifiedName~BusinessTests|FullyQualifiedName~ApiPersistenceTests' -p:DesignTimeBuild=true -p:BuildProjectReferences=true -p:OutputPath=bin/T1/
dotnet test dotnet/tests/ProjectTimeline.Tests/ProjectTimeline.Tests.csproj --no-build --no-restore --filter 'FullyQualifiedName!~OptimizeEntryLiveModelTests' -p:DesignTimeBuild=true -p:BuildProjectReferences=true -p:OutputPath=bin/T1/
node --test dotnet/src/ProjectTimeline.UI.WebApi/ClientApp/src/*.test.js
npm run build --prefix dotnet/src/ProjectTimeline.UI.WebApi/ClientApp -- --outDir ../bin/T1/client-check
```
Before any tests, confirm PostgreSQL opt-in flag is not1; abort rather than connect if enabled. Paid/model-network test class excluded. Never run default Vite build against dirty wwwroot. Generated project bin/obj/NuGet outputs authorized.

## D1a installation progress
Official v0.8.7 compiled with installed VS18 Professional x64 C++ tools for PG18.6, source commit f37c13f68b57d2c3472b2214fbcff699d6d34876 (tracked clean). Stage: dotnet/src/ProjectTimeline.UI.WebApi/bin/T1/pgvector-build/v0.8.7-41bfc20ca94a46568aa094b50ab06e71. Pre-install parent verification confirmed47artifact hashes/sizes and reviewed installer/manifest unchanged. DLL x64/PE32+, dependencies postgres.exe/KERNEL32.dll, build exit0/no compiler warnings.

First blocking UAC call timed out; reconciliation showed0installedfiles/no installer logs/no active installer after user confirmation. A separate retained launcher then requested fresh UAC, user confirmed, and official nmake install completed with exit0 (launcherFinished0 and install.exit0). Copied1DLL+1control+42SQL+3headers. No database connection, extension activation, app migration, service action or paid call. Independent verification PASSED:47/47 installed source/destination hashes and sizes, matching CSV/JSON manifests, no unexpected extension files, official tag/commit/tracked-clean source, control default_version0.8.7, baseSQL matches upstream, installedDLLx64/PE32+ imports postgres.exe/KERNEL32.dll only, pg_config18.6 and serviceRunning. No database/runtime verification implied.

## Deferred/unverified boundaries
Local Windows PostgreSQL18.6 service running; official pgvector0.8.7 extension files now installed and independently verified after compilation with VS18 Professional x64 tools and user-confirmed UAC. No database extension activation, application migration, runtime vector, rollback or relational concurrency check executed. Four PostgreSQL cases defaultskip and require exact optin1 plus explicit maintenance Database=postgres credentials, no app fallback; create/drop only self-generated confirmed-owned disposable database. Orphans after interruption require operator inspection.

Actual production Program/JWT/startup, network chunk framing and live model semantic/billing checks remain unverified. In-flight model call cancellation is limited by tokenless ICustomAgent. Current schema startup fails safely until deployment prerequisites satisfied; source implementation is not deployed/operational against existing local DB.

## Next action
D1a complete: official pgvector0.8.7 files installed and independently verified47/47; deployment guide updated. Next step is D1: explicitly select/authorize target database and credentials, activate extension, rehearse isolated relational tests and apply reviewed application migration as separately authorized. No paid calls/processing enablement or commits performed. Installed extension files alone do not establish per-database activation or operational readiness.
