# Search and present timeline information from accepted facts

## Objective and authorization
User explicitly requests that existing timeline-entry search and information presentation operate against TimelineEntryFacts instead of original entry content. Change the general TimelineSearch assistant and its /entry-search API, not only the already fact-based review optimizer. Original chronological entry listing, CRUD and source records remain unchanged. No GraphRAG or vector-query scope, paid test calls, database connections/migrations, process termination, commits or publishing.

## Baseline evidence before S1
- SearchEntriesAgent uses SearchEntries.search_entries over TimelineEntries.Content, then synthesizes an answer with gpt-4o-mini.
- GET /api/timelines/{id}/entry-search authorizes project read and returns only {content}; no structured evidence.
- App.jsx TimelineSearch (~502-513) renders this answer beside separate original-entry chronology/CRUD. It already has abort/pending/error handling; preserve that and prevent stale results leaking across timeline/request changes.
- TimelineFactSearch currently requires an entry anchor and bounds fact-source and creation-review chronology by it. Keep this review-specific behavior; add explicit timeline-wide latest-valid search, not an arbitrary latest-entry anchor.
- TimelineEntryFacts physical table/CLR TimelineFact/DbSet TimelineFacts store accepted facts with Valid and evidence. Pending proposals are separate. pgvector files installed, but per-database activation/app migration/relational execution still pending in odd/tasks/timeline-entry-facts.md D1.

## Requirements and non-goals
- Search only valid facts whose creation review is Accepted, scoped consistently through source and review to authenticated project/timeline. Model/caller cannot choose authorization scope.
- Database lexical/entity matching is bounded and independent of model/embedding calls. No raw-entry fallback, even with no facts/matches/incomplete processing. General search uses current latest-valid state across the timeline, not an entry chronology anchor.
- Return safe structured fact/evidence/source-entry references plus an answer and honest coverage/truncation/processing warnings. Never imply exhaustive contradiction detection. Do not expose vectors, credentials or unnecessary provider internals.
- Answer synthesis can use the existing model only after authorized retrieval; no real provider invocation in checks. Empty matches should not require model credentials or billing. Fake agents/transports validate behavior.
- UI presents facts/evidence/citations separately from narrative and links to original source entries, without rewriting them. Preserve cancellation/error/pending behavior; avoid stale answer state across timelines/requests.
- Preserve anchored review search semantics and existing security/source/acceptance invariants. Keep general search endpoint compatibility where useful (content field) while adding structured response.
- Existing unrelated dirty files, wwwroot and feature implementation must remain intact. One writer at a time; tests before behavior changes, independent verification after each unit. No commits without explicit request.

## Tasks
- [x] S1a (done; independent209pass4skip): Fix stale administrator privilege after in-flight synthesis; reproduce demotion in a separate context and require current persisted access at final read/return, then independently verify.
- [x] S1 (done; independently verified backend): Implement timeline-wide accepted-valid fact retrieval, read-only general search tool and fact-grounded answer agent/API contract, with deterministic scope/evidence/no-fallback/no-model-on-empty tests; minimally wire authenticated search endpoint.
- [x] S2 (done; independent S3/browser confirmed): Present structured fact answers, evidence/source links and coverage warnings in TimelineSearch; maintain request cancellation/stale-state handling and original chronology/CRUD; add UI tests and documentation.
- [x] S3 (done; independent offline source and mounted UI verified): Independent full deterministic .NET/Node/build and mounted mocked-browser regression checks; reconcile evidence and remaining deployment boundaries.

## Verification
Use existing isolated builds to avoid locked normal DLLs and dirty hosted assets:
- dotnet restore dotnet/tests/ProjectTimeline.Tests/ProjectTimeline.Tests.csproj (only if required)
- dotnet build dotnet/tests/ProjectTimeline.Tests/ProjectTimeline.Tests.csproj --no-restore -p:DesignTimeBuild=true -p:BuildProjectReferences=true -p:OutputPath=bin/T1/
- dotnet test dotnet/tests/ProjectTimeline.Tests/ProjectTimeline.Tests.csproj --no-build --no-restore --filter 'FullyQualifiedName~TimelineFact|FullyQualifiedName~BusinessTests|FullyQualifiedName~SearchInformation' -p:DesignTimeBuild=true -p:BuildProjectReferences=true -p:OutputPath=bin/T1/
- Full deterministic suite: same properties, filter 'FullyQualifiedName!~OptimizeEntryLiveModelTests'; inspect inventory for any newly introduced live classes before execution.
- node --test dotnet/src/ProjectTimeline.UI.WebApi/ClientApp/src/*.test.js
- npm run build --prefix dotnet/src/ProjectTimeline.UI.WebApi/ClientApp -- --outDir ../bin/T1/client-check
Before .NET tests confirm PROJECT_TIMELINE_FACTS_POSTGRES_TESTS is not1; abort if enabled. No production backend startup, live DB, paid tests or normal-output/frontend build. Generated bin/obj/NuGet outputs allowed. Parent also authorizes existing deterministic test-owned OS temporary PDF/artifact and recovery-outbox files/directories, including cleanup of only those test-owned paths; no actual email/network delivery, unrelated temporary deletion or secret exposure.

## Evidence and progress
S1 writer completed8files: timeline-wide latest-valid factquery, lazy isolated fact-only synthesis/citationvalidation, trusted-scope searchtool, authenticated structured endpoint and guarded fact-backed BusinessTests. ObservedRED: rawfallbackreturned originalstrings with no acceptedfacts; final201pass4PGskip/build0warnings/errors. Restorepassed after initialNU1105. NoDB/paidcalls. Independent build passed0warnings/errors, but reviewer identified stale-admin authorization after synthesis: role captured at endpoint is reused by post-model CanRead check even after demotion. Behavioral tests were withheld because existing BusinessTests write test-owned OS-temp PDF/recovery files; parent now explicitly authorizes those bounded test outputs/cleanup. S1a writer fixed fresh no-tracking current activeidentity/adminrole checks, premodel authorization/revision and final revalidation. Added8separate-context regressions: observedRED3fail (demotedadmin/memberHTTP200 and forgedtrueflag), final209pass4PGskip/build0warnings/errors. Denied responses contain noanswer/facts/IDs/coverage; legitimate demotedowner/memberaccessretained. Independent S1/S1a verification PASSED209.NET/4PGskip, build0warnings/errors. Reviewed current-role access, timelinewideacceptedvalidpredicate/scope, emptyresultlaziness, isolatedsynthesis, citations/revision/cancelguards andactualTestServercontracts; no remainingblockers ininspectedscope. Final fullregression/UI assurance is recorded under S3 below; liveDB/modelquality remain separatelypending. Prior490.NET/25Node remains baseline, not new-change verification.

S2 contract: GET /api/timelines/{id}/entry-search?text=... max300characters -> {content,facts,citations,coverage}. facts: id,entryId,entryNumber,claim,entityKey,entityType,sourceQuote,sourceKind,sourceFingerprint,clarificationEvidence,creationReviewId. citations: factId,entryId,entryNumber,creationReviewId. coverage: entries,entriesWithoutCompletedExtraction,pendingReviews,acceptedValidFacts,truncated,warnings[]. Empty results do not construct agent/provider. Query/review scopes are distinct; original anchored review search unchanged.

## S2 writer evidence
Changed App.jsx, new TimelineSearchResults.jsx/timelineSearch.js/timelineSearch.test.js and guide. Narrative/citedfacts/additionalcontext/clarificationevidence/sourcelinks/counters/warnings separated, text-only rendering; structuredresponse/citationmembership validation; querymax300; clearansweronrequest/failure/timelinechange and late/abortedrequest fencing. Existingchronology/CRUDunchanged. ReportedRED oldfactwiringexpectation and already-abortedrequest missingAbortError, final35Nodepasses; actualReactserverrender escaping/evidenceseparation checked. Full550.NETpass4PGskip, build0warnings/errors, isolatedVitePASS. Three newJS/JSXfiles224lines plusApp/docs edits. WriterdidnotfindcachedPlaywright/Chrome; browsernotperformedyet.

## Final independent S3 evidence
- Full deterministic .NET suite excluding OptimizeEntryLiveModelTests: 550 passed, 0 failed, 4 PostgreSQL skipped (554 total). PostgreSQL opt-in not1; no additional live test classes found.
- Isolated .NET build: PASS, 0 warnings/errors. Node:35passed. Isolated Vite:PASS (35modules). Whitespace check:PASS; existing line-ending/module-type warnings only.
- Mounted Chrome/cached Playwright workflow:PASS,37mockedAPIrequests,zero runtime errors/unexpected routes/external requests. Original chronology remained separate; narrative/citedfacts/additionalcontext/evidence/clarificationQ/A/source links/counters/warnings verified. Source link opened correct original detail and returned. Plaintext XSS payloads did not execute. Vectors/provider fields not displayed.
- Empty results and malformed legacy content-only responses did not fall back to raw entry answers.409/403/502 cleared prior evidence without private diagnostics. Querymax300 and pending controls verified. Old answer cleared on delayedrequest; an explicitly abort-ignoring late promise after project/timeline switch/unmount could not leak old evidence into new timeline.
- Artifacts: WebApi/bin/T1/s3-browser-check.cjs, s3-search-evidence.png, s3-search-pending.png. Browser/API traffic simulated; no backend startup/live DB/paid provider. Only verifier-owned server/browser closed.
- Guide assurance matrix updated to final counts. No commits/pushes performed; user authorization absent.

## Completion boundary and next step
All tasks complete for offline implemented-source verification. General information retrieval/presentation now uses accepted valid TimelineEntryFacts with explicit provenance/coverage and no original-content fallback; original timeline chronology/CRUD and anchored review search remain intact. Per-database pgvector activation/application migration, real PostgreSQL vector/transaction/concurrency, production Program/JWT/network framing and live-model semantic quality/billing remain unverified/pending in odd/tasks/timeline-entry-facts.md D1. Existing tokenless in-flight model cancellation limitation remains. Resume that separate deployment step only with target/credential authorization; do not enable paid processing automatically.
