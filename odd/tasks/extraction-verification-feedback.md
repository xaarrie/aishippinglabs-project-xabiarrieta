# Extraction and semantic verification feedback loop

## Objective
Implement the approved human clarification cycle for both entry creation and regeneration: extraction -> answer questions -> validation -> actionable feedback/questions -> explicit successor extraction -> validation -> Generated completion. Never activate facts solely because verification approves.

## Problem and decisions
- Existing extraction questions already persist and reach detail UI, but candidates are verified before those questions persist. Semantic rejection throws and schedules blind Retry without corrective feedback.
- Return extractor questions immediately, discarding provisional candidates/evidence and skipping verifier until a later explicit resume yields a question-free extraction.
- Add structured verifier outcomes Approved, NeedsClarification and Rejected, with complete candidate assessments and bounded validated questions. Derive disposition server-side; no numeric score threshold.
- NeedsClarification atomically persists feedback and questions into AwaitingClarification. Saving answers is free of model calls; explicit resume creates the successor, re-extracts and revalidates.
- Rejected without genuine questions persists feedback, releases lease and yields a terminal Failed job exposed as NeedsCorrection. Existing explicit regeneration consumes that feedback. No automatic corrective model call or unchanged semantic retry.
- Technical schema/citation/provider/cancellation failures retain technical handling; do not fabricate user questions from errors. Strict legacy VerifyAsync/ExtractAsync APIs must remain fail-closed for all nonapproved outcomes.
- Feedback lives in new nullable ExtractionFeedbackJson, not mutable frozen ExtractionInputJson/completion support. Use bounded versioned output with generation/source/effective-input binding and candidate findings/questions; carry only from exact predecessor with validated identity.
- Feedback is untrusted correction guidance, NEVER source evidence, answers, spans, or fingerprint input. Freeze must preserve it without allowing historical feedback IDs as new citations.
- Preserve authorization, leases, transactions, revision/fingerprint checks, immutable answers,20aggregate inherited answers+new questions, and separate reconciliation/acceptance.
- Mount existing clarification controls on entry editing as well as detail, with dirty/save/review/busy fences and safe cross-refresh. Expose explicit NeedsCorrection/regenerate messaging, not a paid auto-loop.

## Scope and constraints
- Backend contracts/prompt/provider/job persistence/guards/successor context, one additive nullable-column migration and snapshot, deterministic tests, minimal UI integration/tests and current documentation.
- Migration: 20261026000100_ExtractionGenerationFeedback plus Designer and snapshot. Prepare only; never apply DB updates or restart running hosts.
- Update canonical agent reports and entry clarification guide after verification; mechanically republish reports to C:/Data/formacion/TimelineSpecifications.
- Preserve extensive pre-existing staged/unstaged work on fix/fact-search-outcomes-source-batch, including prior single-pass search and concurrent wwwroot assets. No staging, commits, resets, branch changes, publishing/PRs, live models/cloud/PostgreSQL, installs/restores or secret reads.
- RDDoff: do not start native review. After each writer returns use read-only ASSESS and follow its plan; unavailable/unassessable is high and requires independent verification.

## Tasks and routing
- [x] T1 — Implement the backend feedback cycle, additive persistence, minimal UI integration and deterministic RED/GREEN regression coverage. Route: one bounded gentle-ai-worker; mandatory multi-file-write trigger. Backend and UI are sequential parts of the single coherent human-loop behavior, with separate focused checks.
- [x] T1b — Correct and verify the stale latest-migration test expectation without changing historical targets. Route: parent inline mechanical one-file edit; gentle-ai-verify runs its focused check.
- [x] T2 — Independently verify safety, migration metadata/SQL, full deterministic backend/frontend suites, isolated builds and browser human-loop paths. Route: gentle-ai-verify; mandatory verification trigger.
- [x] T3 — Update the current entry clarification guide and source-backed per-agent reports after behavior verification. Route: one bounded documentation worker.
- [x] T4 — Independently validate reports, mechanically republish exact copies beside specifications and record final evidence. Route: gentle-ai-verify plus parent mechanical copy/readback.

## Acceptance criteria
- New entry and regeneration can pause on extractor questions before verifier; no unverified candidates/proposals/facts persist.
- Verifier questions become durable, neutral/bounded source-anchored clarification rows and indexed feedback; all candidates assessed exactly once. Unsupported/no-question outcomes become NeedsCorrection without scheduling blind retries.
- Successful resume/regeneration feeds frozen human answers plus non-evidentiary predecessor feedback into extraction, with verifier using the same trusted source/evidence. More rounds possible within unchanged limits.
- Lost leases, access/revision changes, forged feedback, stale predecessor/source restoration and tampered snapshots fail closed; no wrong-generation resurrection, feedback replacement or embedding-job feedback.
- Only approved question-free outcomes can complete Generated; old strict adapters never silently return rejected candidates or empty apparent success. Manual acceptance remains required for active facts.
- Legacy response fixtures without questions remain inbound-compatible; strict outbound schemas require new questions contract. Approved/technical paths retain meaningful safety coverage.
- Additive migration, Designer frozen target and current snapshot agree offline; old migrations unchanged. No DB migration applied.
- UI shows actual question controls in both entry detail/edit, blocks stale/dirty/busy writes and preserves drafts. Answers trigger no paid calls; only explicit resume/regenerate does.
- Updated Spanish reports accurately describe post-change implementation, limits, prompts, tools and enforcement caveats; no secrets or unsupported deployed guarantees.

## Verification plan
Applicable behavior changes use observed baseline -> RED new regressions -> minimal GREEN -> relevant alternate/refactor checks. Passive report edits have no meaningful RED; verify source/structure instead. Exact focused backend command (existing restored Release outputs; no network restore):
```sh
dotnet test dotnet/tests/ProjectTimeline.Tests/ProjectTimeline.Tests.csproj --no-restore --configuration Release -p:SkipClientBuild=true --filter "Category!=LiveModel&FullyQualifiedName!~TimelineFactPostgresTests&(FullyQualifiedName~TimelineFactGenerationFeedbackTests|FullyQualifiedName~TimelineFactClarificationProviderTests|FullyQualifiedName~TimelineFactClarificationProcessingTests|FullyQualifiedName~TimelineFactSemanticVerifierTests|FullyQualifiedName~TimelineFactStrictSchemaTests|FullyQualifiedName~TimelineFactGenerationTests|FullyQualifiedName~HostedUiTests)"
```
Frontend focused checks, from ClientApp:
```sh
node --test src/factReview.test.js src/entryClarifications.test.js
```
At independent closure run the applicable full backend Category!=LiveModel&FullyQualifiedName!~TimelineFactPostgresTests suite, all local frontend tests, build with envDir:false and OS-temp output (never regenerate wwwroot), and actual React/flow browser checks with fake APIs/no network/model calls. Migration SQL/metadata assertions are offline only. Historical passes are not current evidence.

## Workload and delivery
Forecast backend/tests850–1250changed lines, UI/tests120–220, docs30–60plus source-report refresh. This exceeds the advisory400line work-unit heuristic because contract, persistence, provenance and lifecycle safety must stay coherent. Do not compress code, remove tests or split incoherently for size. Delivery strategy ask-on-risk; no commits/PR requested or authorized, so any future oversized delivery/chain decision is deferred to explicit user delivery authorization. No fabricated work-unit commit evidence.

## Progress and evidence
- Prior read-only scouts mvw5fhpx-a-zsf4 and mvw5film-b-zgm7 established backend/UI gap; parent spot-checked provider order.
- User explicitly approved implementation with 'adelante'. Delta scout mvw5q2tp-c-kxjr mapped exact edit surfaces and recommended persisted feedback with explicit regeneration, not automatic paid repair; no unresolved product decision.
- New feedback field/migration required: existing frozen input/support/hash fields cannot safely store mutable output. No conflicting ExtractionGenerationFeedback migration found.
- T1 implemented by mvw61z5u-d-b6p4. Writer baseline178backend/34UIpassed; observed RED177backendpassed/3failed and34UIpassed/1failed; final GREEN225backend/38UIpassed,0failures/skips. Intermediate fixture compilation/obsolete assertion failures corrected. Parent spot-checked provider question-first and strict legacy paths.
- Implementation covers atomic pause/nonapproval feedback, exact-predecessor non-evidentiary carry/freeze, terminal NeedsCorrection, UI detail/edit guards, additive migration/frozen target/snapshot and minimal guide. Codec v1 caps512KiUTF-16chars,199findings,81evidence IDs/item and20aggregateQ&A. Approximately1150changedlines/31files; pre-existing index/unrelated changes preserved.
- New migration prepared only:20261026000100_ExtractionGenerationFeedback. No database update/model/PG/cloud/restart/restore/install/staging/commit/external report copy.
- Native ASSESS unassessable due undeclared untracked scope; RDDoff, runtime writer large. Plan requires writer self-verification plus independent verifier; no review authority operations attempted.
- T2 PARTIAL via mvw6srpf-e-dxmn: focused225passed; full offline1385passed/1failed/0skipped. Blocker TimelineFactModelTests.Npgsql_model_has_real_vector_mapping_and_migration_snapshot_matches_without_connecting compares historical ClarificationExtractionInputs target to current snapshot containing ExtractionFeedbackJson. Historical target is correctly unchanged; update test to select latest registered migration target. New focused migration SQL/metadata tests pass.
- T1b PASS via mvw6yzsf-f-w2w2:36targeted TimelineFactModelTests/TimelineFactGenerationFeedbackTests passed,0failures/skips. Parent's mechanical one-file correction selects latest IMigrationsAssembly target with existing assertions intact; historical targets unchanged. Scoped diff-check clean. Original full-suite RED remains recorded.
- T1/T1b/T2 complete. Combined independent verifier mvw71169-g-e85p PASS: full1386/1386offlinebackend, focused38/38andfull95/95frontend,0failures/skips; prior focused225valid. Isolated Vite build passed37modules with envDir:false/OS-temp output. Actual production React Chrome detail/edit33assertions passed,66interceptedfakeAPIrequests(56GET/10writes),0unexpecteddestinations/pageerrors; no backend/model/PG services.
- Independent safety audit passed question-first/strictlegacy/disposition/atomicfeedback/exactpredecessor/non-evidentiary/oldcitation/bounds/Generated-only/UIprivacy/fencing guards. Migration SQL/current/frozen/historical targets passed, NOT applied. Index/wwwroot hashes and status preserved. Question neutrality remains structural/prompt guidance, not semantic proof. Historical writer RED not independently rerun.
- Parent reran frontend focused command:38passed,0failures/skips,exit0. Existing MODULE_TYPELESS and LF/CRLF warnings remain. Temp browser harness setup/timing mistakes corrected only outside repo before final PASS.
- T3 complete via mvw7actu-h-8rs2: guide164lines and reports search193/extraction202/optimization220/semanticverifier235lines updated from current code. Writer static audits passed166explicit source ranges,42inline subranges,88reference labels and115source assertions after correcting26bad ranges. All5readback/fences/headings/whitespace/privacy checks passed; index/assets SHA unchanged. No code/test/task edits or export.
- Current docs explicitly cover question-first discard, derived dispositions, strict legacy distinctions, exact-predecessor non-evidentiary feedback, terminal NeedsCorrection, explicit resume/regenerate, detail/edit fencing, Generated-only completion, current bounds and migration PREPARED/NOTAPPLIED. Existing search immediate-activation warning remains documented follow-up, not a new source fix.
- Post-document-writer ASSESS remains unassessable due untracked scope/RDDoff; independent verification fallback completed, no review maintenance/start attempted.
- T4 PASS via mvw7scem-i-svij:all5docs read,166explicit source ranges/42inline subranges/88labels(266uses) resolved,139sourceassertions+1protectedintegrityassertion passed, no factual blockers. One oversized static-audit invocation failed parsing before execution; split checks passed without source changes. Migration still PREPARED NOTAPPLIED.
- Parent republished/readback-verified exact canonical reports in C:/Data/formacion/TimelineSpecifications:searchagent.md193lines,extractionagent.md202,optimizationagent.md220,semanticverifieragent.md235. Specifications/CSV/unrelated assets untouched. Guide remains canonical in dotnet/docs/entry-extraction-clarifications.md.
- All5tracked tasks complete (including recovered T1b). No outstanding required test/doc failures; full1386backend/95frontend/33browserassertions passed. Deliberately not run: realmodels/PG/deployedintegration, DB migration apply, hostrestart/assetdeploy; nativeassessmentunavailable with independent fallback complete. Existing warning about immediate activation remains documented follow-up, no unauthorized fix. No staging/commits/push/PR.
- Independent before/after SHA checks preserved .git/index and existing wwwroot assets; no mutation or DB application. Scoped whitespace check clean apart from known LF/CRLF warnings.

## Next step
Completed and source-verified. Next human-authorized operational step: apply20261026000100_ExtractionGenerationFeedback before using the new feedback persistence with a real database; deployment/live checks remain separate.
