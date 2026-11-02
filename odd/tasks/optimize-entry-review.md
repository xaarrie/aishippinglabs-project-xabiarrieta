# Optimize entry review

## Objective
Expose timeline-scoped context tools to OptimizeEntryAgent and provide interactive review for a new-entry draft or the latest persisted timeline entry. Generated content remains a draft until explicit Save; discard preserves stored content and unrelated editor state.

## Scope and decisions
- Add `OptimizationAgentTools` in `dotnet/src/ProjectTimeline.BusinessLogic/Agents/Tools`.
- Fetch entry content and chronological entry IDs, restricted to the selected timeline.
- User confirmed: window starts strictly after the maximum EntryNumber in the latest snapshot preceding the current entry, and includes current. Without a preceding snapshot, include all entries through current. Ignore later entries/snapshots.
- Reuse structured review results and answer integration; no automatic persistence or digest updates.
- Superseded original rule: Review was enabled for saved entities only. User now explicitly requires Review enabled on NEW entries and the most recent saved timeline entry only; hide/disable it with an explanation on older entries, and enforce eligibility on the backend. Preserve existing update authorization and statuses.
- New-entry draft review must not require saving/creating an entry first. Use the authorized timeline and existing latest-entry context when available; empty timelines must work without fabricated persisted identities.
- Follow-up authorized 2026-10-17: address generic review 502 and support explicit editor-draft review plus review of the most recent persisted entry in the timeline, even when another entry is open. User selected `latest_timeline_entry`, not merely saved content of the current editor.
- Resolve latest target server-side using timeline entry order with deterministic tie-breaking; enforce existing write authorization. Return explicit reviewed target/source metadata. Never copy another entry's review into the currently open editor or save automatically.
- Preserve in-flight drafts, answers and explicit Save/Discard semantics. Reject stale/mismatched draft result application after editor target/content changes; no new database versioning feature.
- The actual upstream 502 cause is not established: route catches all non-cancellation exceptions, and no runtime trace is available. Add sanitized exception classification/stage and correlation reference (no exception messages, secrets, prompts or model content); only fix a root cause when evidence supports it. Live provider confirmation remains pending unless reproducible with authorized environment.
- Follow-up authorized: improve detail retention in optimizer prompt/result schema, add side-by-side textual differences and a separate semantic auditor with estimated coverage 0..1 and per-fact evidence, omissions, contradictions and unsupported additions. Score is not a probability, completeness guarantee, or automatic Save approval.
- Preserve complete original title/content independently for comparison, populated from the server source rather than trusting model output. Do not automatically append raw source into the editable entry; user approved visual comparison plus audit rather than an appended-source format.
- Audit the exact proposal title/content shown to the user using a separate read-only agent/context, not the optimizer's self-assessment. Prefer an explicit authorized audit endpoint to avoid divergent server/client formatting and to support re-auditing edited proposals.
- Bind audit to exact source/proposal identities/text; reject stale source and invalidate visible score on edits/new rounds/navigation. Validate literal evidence against the corresponding source/proposal and compute coverage from preserved facts / all inventoried source facts; empty/invalid/unavailable inventory is unknown, never perfect coverage. Inventory itself remains model-estimated and may omit facts.
- No new provider package, database migration, automatic persistence, auto-retry or live paid test call. Existing latest/new-only modes and actual 502 diagnosis remain pending; prioritize the new detail-preservation/comparison/audit request.
- Keep unrelated untracked files and snapshot work untouched, including CustomOpenAIClient.cs and agent-file-memory contents (do not read private memory).
- Single writer; test-first for deterministic behavior. Live model calls are not deterministic tests.
- No commits/push without explicit user request. Work-unit commit identities: pending user authorization.

## Tasks
- [x] T1 (verified; commit pending authorization): Add scoped context tools and wire into OptimizeEntryAgent, with deterministic window and isolation tests.
- [x] T2 (verified; commit pending authorization): Add authorized, non-persisting review API with structured input/output and tests using a fake reviewer.
- [x] T3 (verified; commit pending authorization): Add persisted-entry Review UI, questions/answers, editable draft and explicit save/discard; run applicable UI/build and regression checks.
- [x] T4 (writer and independent verifier passed): Added sanitized actionable problem fields and event 4101 diagnostics, with failure-stage/category classification and deterministic regression checks. Live provider root cause remains unknown and is NOT fixed; tracked in T8.
- [ ] T5 (pending): Add explicit new/current-latest draft and latest-timeline-entry backend review modes with target metadata, authorization, older-entry rejection, context isolation, input validation and non-persistence tests. Latest resolution must be server-side; new-entry drafts must not require persistence.
- [ ] T6 (pending): Add explicit UI source selection, actual-target preview/actions, safe server-error display and stale/mismatched-result protection. Enable only new/latest editor entries, disable older entries with an explanation. Preserve drafts and test eligibility/request/flow behavior.
- [ ] T7 (pending): Independently verify solution build/tests, Node tests and mocked browser interactions, preserve original hosted assets, and report live-provider limitations.
- [ ] T8 (pending; awaiting live diagnostic): Actual reported 502 cause remains unknown. User was asked to restart the serving API and provide safe stage/category/traceId; no such evidence yet. New explicit priority is detail preservation/audit. Deterministic tests alone cannot close this item.
- [x] T9 (writer functional checks passed; independent integration verification in T12): Strengthened preservation/provenance/no-invention instructions and additive detail schema with compatible positional constructors. Relevant scoped checks passed; global whitespace check fails only in untouched user CustomOpenAIClient changes, recorded below.
- [x] T10 (writer checks passed; independent integration verification in T12): Added independent exact-text auditor, authorized source-bound endpoint, conservative quote validation, deterministic estimated coverage and server-owned review source metadata. Full suite 156 passed; live-provider semantics remain untested.
- [x] T11 (writer functional checks passed; independent integration verification in T12): Added immutable original comparison, bounded lossless line diff, audit evidence/coverage/issue display, one audit per generation, explicit re-audit, synchronous stale-response guards and preserved explicit Save/Discard.
- [x] T12 (independently verified): Combined detail/audit slice passed final normal solution build, 156 C# tests, 21 Node tests, 22 production and 23 development mocked-browser groups. Parent repeated156 tests. Source/assets preserved; known out-of-scope whitespace and live-model/502 limitations reported.

## Acceptance and checks
- No-snapshot, noncontiguous snapshots, preceding/latest snapshot, current-inclusive/future-exclusive ordering, cross-timeline isolation.
- Review requires authentication and write permission, rejects missing entries, never changes storage directly.
- Answers and previous result reach the agent; original content remains unchanged until Save.
- Follow-up replaces the original new-entry restriction: Review is available on new-entry drafts and the latest saved timeline entry, not older entries. Busy/error states are visible; preview can be saved/discarded explicitly.
- A review of another saved target never overwrites the currently open draft. Target identity/source are shown and checked; stale responses do not change a different or modified editor.
- Provider failures produce sanitized actionable diagnostic references without logging/returning exception messages, API keys or submitted content. Unknown root causes remain explicitly pending.
- Detail retention fixtures must include distinct people, quantities, dates, constraints, criteria, decisions, risks and otherwise uncategorized facts; verify serialization and final rendered/saved proposal do not drop detail-bearing fields. Preserve original exactly and never invent missing details to satisfy schema.
- Audit coverage is derived from explicit source-fact inventory with grounded quote evidence; invalid/missing evidence cannot be counted as preserved. Contradictions, omissions and unsupported additions are displayed even when numeric coverage is high. Unknown/failed/empty audit never becomes 1.
- UI comparison renders input safely as text (no untrusted HTML), distinguishes reorganization from deletion without claiming lexical diff proves semantic loss, and prevents stale audit display/race updates. Editing either reviewed text invalidates coverage.
- Run focused RED/GREEN tests, final dotnet suite and client build; functional UI checks when available.

## Progress and evidence
- Read-only exploration completed; agent currently has search tools only and lacks a UI/API review invocation.
- Original implementation could not mirror to Engram. Memory tools are now working; parent is reconciling this full document into the existing feature topic before follow-up source edits.
- Native RDD switch is off in this session; assess delegated writer output for verification plan.

### T1 evidence
- BusinessLogic build passed, zero warnings/errors.
- Repository test project blocked by pre-existing BusinessTests.cs references to missing IbernovaOpenAIClient and obsolete SearchTools. Verified unchanged against HEAD cf7abe8dd8a65d9a0117589fb866863969241632.
- External isolated net10.0 test project linking new tests: 7 passed, 0 failed, 0 skipped. Initial 6/7 run exposed serialized JsonElement assertion mismatch, corrected in tests.
- No meaningful pre-implementation behavioral RED observed; initial compilation failure was not behavioral RED.
- EF InMemory does not prove PostgreSQL translation compatibility.
- Independent verifier inspected window scoping and confirmed baseline failures. Native assessment unassessable due untracked declaration; independent verification required.

### T2 evidence
- Isolated HTTP/service tests observed 13 intended RED failures for absent route, then 14 GREEN, expanded to 19/19 passing.
- `dotnet build dotnet/src/ProjectTimeline.UI.WebApi/ProjectTimeline.UI.WebApi.csproj` passed with zero warnings/errors.
- Standard repository test command remains blocked solely by confirmed baseline BusinessTests.cs errors.
- POST `/api/entries/{id}/review` uses `{}` initially and `{previousResult, userAnswers: {Q1: answer}}` subsequently; returns camelCase EntryReviewResult, numeric enums (0 NeedsClarification, 1 Ready).
- API requires write access; fake reviewer tests cover auth, missing entry, input limits, no persistence and safe failures.
- Cancellation cannot interrupt current ICustomAgent in-flight provider invocation; checked before/after.

### T3 and final independent verification
- Node formatter/request/permission tests: meaningful RED (2 omitted-detail failures), GREEN 7/7.
- In-memory Vite production build passed, 33 modules transformed; avoids deliberate hosted-asset regeneration.
- Independent verifier: T1 7/7, T2 19/19; external aggregate runner linking all tests except baseline-broken BusinessTests.cs passed 39/39 (26 feature + 13 unaffected regression tests).
- Standard full `dotnet test dotnet/tests/ProjectTimeline.Tests/ProjectTimeline.Tests.csproj` remains blocked by 3 confirmed pre-existing compilation errors. Initial static-assets build failures cleared on rerun; no full standard-suite pass claimed.
- Independent Node tests 7/7 and in-memory Vite build passed.
- Real browser via cached Playwright and installed Edge: 7 grouped checks passed, with mocked APIs. Covered new-entry disabled Review, permission gate, saved Review, provider error/retry, dirty-editor warning, answer submission/retention, editable preview, no writes before Save, Discard preserving dirty title/content, explicit PUT-only Save preserving status.
- UI writer initially lacked browser tooling; independent verifier supplied actual functional evidence. Initial browser launch/locator failures were corrected in external verification harness and not counted as passes.
- Independent source inspection found no confirmed candidate-caused defects.
- .NET builds unexpectedly regenerated tracked wwwroot assets. Restored only the exact originally-clean generated files to index state and deleted only our generated replacements. Final `git status --short -- .../wwwroot` empty and `git diff --check` passed; unrelated user files preserved.
- Native assessment risk unassessable because of untracked declaration; mandated independent verification performed. RDD remains off, no native review lifecycle started.
- Live provider behavior/cost, PostgreSQL relational translation, and adversarial in-flight navigation races not tested; deterministic tests use fakes/InMemory.
- Engram full mirror remains pending due incompatible installed binary. No commits or publishing performed.

### T4 follow-up diagnostics evidence
- Changed ApiContracts.cs, EntryReviewService.cs, new AgentModels/EntryReviewFailure.cs, and EntryReviewTests.cs only. Safe stages: initialization, agent_invocation, result_validation, unknown. Categories use allowlisted types/provider statuses, not exception messages.
- Endpoint returns sanitized 502 fields `code: entry_review_failed`, `stage`, `category`, `traceId`; detail includes actual reference. Logs event 4101 / EntryReviewFailed under ProjectTimeline.UI.WebApi.EntryReviewEndpoints with FailureCode, FailureStage, FailureCategory, ExceptionType, nullable ProviderStatusCode, DiagnosticReference. No exception objects/inner messages, prompts, responses, answers or keys logged.
- Observed deterministic behavioral RED: 18 failures/20 passes. Final focused GREEN: 42/42. Full suite with OPENAI_API_KEY unset: 74 passed, 0 skipped, using BuildProjectReferences=false after actual BusinessLogic/WebApi compilation.
- BusinessLogic build passed zero warnings/errors. WebApi CoreBuild passed without frontend hook; final build had transient file-lock retry warnings from existing app/IDE processes. No restart, live model call, live database call or git mutation performed.
- Original wwwroot 3 files and 27 other manifest files match exact path/size/hash after checks; .vs filename churn not attributed or modified. Evidence outside repo: entry-review-t4-jtjg83n0 under session temp directory.
- Native assess again unassessable due unrelated untracked declaration; independent T4 verification completed. RDD remains off.
- Independent verifier ran normal solution tests with `--no-build --no-restore`: 74 passed, 0 failed, 0 skipped, exit 0. DLL timestamps/hashes support fresh compiled T4 outputs. Source inspection found no confirmed diagnostic redaction/cancellation blocker; no live calls, restarts or edits performed.
- Parent repeated the same solution test command: exit 0, 74 passed, 0 failed, 0 skipped. This verifies offline behavior only; actual user-reported 502 still awaits live stage/category/traceId.

### T9 preservation evidence
- Added OptimizedEntry.Objectives/AdditionalDetails; objectives contain Title/Description/Details/AcceptanceCriteria/PendingQuestionIds. TaskItem adds Details/Constraints/Quantities using default-empty init properties; existing positional constructors remain compatible.
- Prompt now distinguishes reorganization from summary, preserves specific facts/uncertainty/provenance and forbids inventions/truncation; self-check cannot claim completeness or assign a score.
- Behavioral RED: 9 new C# cases failed including unknown-field roundtrip loss. GREEN: 10 detail tests, 49 existing review/tool tests, 9 Node tests; full suite with OPENAI_API_KEY unset: 84 passed, 0 skipped. BusinessLogic/Database compile and WebApi CoreBuild passed zero warnings/errors; fresh dependency outputs then reused for tests.
- Formatter required no change; new nested fields and legacy output retained. No provider/database calls, user-process restart, hosted-asset writes or git mutations.
- Scoped diff check passed. Repository-wide `git diff --check` exited 2 for trailing whitespace in untouched user CustomOpenAIClient.cs lines 45/48; do not clean those external changes. Worker reported partial only because of this pre-existing out-of-scope check failure; required functional/scoped checks passed.
- Native assess unassessable due untracked scope; separate independent verification remains required in T12, not replaced by writer tests.

### T10 audit evidence and API
- Added EntryReviewAuditAgent, EntryReviewAuditService and AgentModels/EntryReviewAudit; audit uses fresh plain context, no tools/optimizer history. Existing /review five fields unchanged; added server-owned `source: {entryId,title,content,fingerprint}`.
- POST `/api/entries/{id}/review/audit` request: `{proposalTitle,proposalContent,expectedSourceFingerprint}`. Auth/project write checks and safe409 source mismatch precede model initialization; no persistence/retries.
- Result: `{entryId,sourceFingerprint,proposalFingerprint,coverage,inconclusive,preservedFacts,totalFacts,facts,unsupportedAdditions,limitation}`. Fact fields: fact/status/reportedStatus/sourceQuote/proposalQuote/explanation/sourceQuoteVerified/proposalQuoteVerified/evidenceWarning. Status strings Preserved/Omitted/Contradicted/Uncertain. Addition fields finding/proposalQuote/evidenceVerified.
- Coverage is server-computed preserved/deduplicated inventoried facts; literal source+proposal quotes required for credit. Ungrounded facts stay uncertain in denominator. Empty/invalid inventory yields null coverage. Unsupported additions remain visible even at coverage1.
- Source fingerprint: lowercase SHA256 over default System.Text.Json UTF8 JSON array [entry-review-source-v1,entryId D-format,title,content]. Proposal recipe: [entry-review-proposal-v1,title,content]. No trimming/newline normalization. Failed audit returns sanitized502 with null coverage/inconclusive=true and binding fingerprints.
- Observed behavioral RED: three missing-route/source-metadata failures. Final focused114/114, full156/156, 0 skipped, with OPENAI_API_KEY unset. BusinessLogic/Database builds and WebApi CoreBuild passed; scoped/no-index whitespace checks passed. No live provider/database calls, UI/hosted-asset writes, restarts or git mutations.
- Four new files750 lines, including566 test lines, exceeded advisory naturally as one coherent endpoint/security/scoring unit; no cosmetic splitting/minification. Known global whitespace in untouched CustomOpenAIClient remains.
- Native assess again unassessable due unrelated untracked scope; independent T12 verification required.

### T11 routing note
- Initial writer correctly stopped before edits: authenticated `api()` is in ClientApp/src/api.js, not App.jsx, and discards structured failure metadata. Parent authorized that exact helper path solely to preserve HTTP status and safe allowlisted entry-review diagnostic fields; no new product scope or authentication changes. Existing UI surfaces remain allowed; App.jsx limited to necessary prop wiring.
- Writer resumed with test-first T11 implementation; no tests or edits were claimed during the scope stop.

### T11 UI evidence
- Changed EntryReview.jsx/css, entryReview.js/tests and api.js only; App.jsx untouched. Original server source remains immutable for comparison; no original text is generated/trusted from the optimizer.
- Side-by-side accessible lexical line diff preserves all text and uses coarse full-block fallback on large comparisons; responsive stacking and escaped text prevent untrusted HTML rendering.
- Automatic audit exactly once per review generation; manual edits synchronously clear score without a provider call; explicit re-audit sends exact title/content and source fingerprint. Immutable generation snapshots reject stale responses, even after edits revert or navigation/discard/new rounds occur.
- Display estimated0..1 coverage with count/limitation and omissions/contradictions/uncertainty/unsupported additions even at coverage1. Empty/failed/stale/malformed inventory/counters suppress numeric score; failure preserves editable proposal. Save remains explicit, not score-approved.
- API helper retains HTTP status and safe allowlisted diagnostics, not arbitrary raw server/provider errors; auth/success semantics preserved.
- Behavioral RED9 new failures/9 existing passes; initial GREEN18/18; extra malformed-inventory regression RED1/20, final Node21/21. In-memory Vite build passed33 modules/3 assets. Writer ran14 grouped Playwright/Edge mocked-browser checks covering StrictMode, exact whitespace, escaped text, responsive layout, score/issues, failures/409, delayed/navigation/discard races and explicit Save/status.
- Scoped whitespace passed; all three original wwwroot paths/SHA256 match. Two browser harnesses hit shell limits and one locator timed out before corrected passes; no live provider/database calls, process restarts, staging or commits.
- Native assess unassessable due unrelated untracked scope requires independent T12 verification, now running.

### T12 independent verification and closure
- Independent restore exited0. Normal full solution build initially exited1 twice because Vite regeneration removed a static asset referenced by evaluated .NET inventory; repeating the unchanged normal build inside protected output window succeeded, exit0, zero warnings/errors. No bypass/cleanup used.
- Normal solution tests after successful build:156 passed,0 failed/skipped, exit0; Node21/21, exit0; vulnerability scan no vulnerable packages, exit0. Parent repeated `dotnet test dotnet/ProjectTimeline.sln --no-build --no-restore`:156 passed,0 failed/skipped, exit0.
- Independent real Edge browser with mocked APIs:22 production groups +23 development groups, including observed StrictMode effect replay with one auto-audit request. Covered detail text, immutable source/diff, coverage1/null/0/malformed inventory, issues even at1, exact re-audit text, edit/revert/new-round/discard/navigation/unmount races, source409/failures, escaped text, responsive/large diff and only explicit Save PUT preserving target/status; Discard preserved original dirty editor state.
- Scoped whitespace checks passed. Global check exited2 solely for untouched user CustomOpenAIClient.cs45/48 trailing whitespace. Untracked candidate checks reported expected file-difference exits/line-ending warnings without whitespace errors. These external changes were not cleaned.
- All3 original hosted files restored with identical paths/lengths/SHA256: JS237603B,CSS13448B,HTML376B. All37 targeted source hashes matched before/after; visible Git status unchanged. No commits, restarts, live model/database calls or private-file reads.
- External evidence: session temp directory t12-independent-ujafo16y, containing logs, browser harnesses/screenshots/results, manifests and final-summary.json. Current GREEN independently observed; historical RED not replayed.
- No confirmed candidate-caused defect. Live LLM inventory/completeness, relational database behavior and actual prior502 remain untested. Literal evidence/score are estimates, not semantic proof.

## Next step
Current T9/T10/T11 detail-preservation/comparison/audit slice is complete and verified. User must rebuild/restart their serving API to load new code/UI; verification restored pre-existing hosted assets deliberately. Remaining authorized feature work T5/T6/T7 (draft/latest-only selection/eligibility) is still pending; T8 actual502 requires safe live evidence. No feature-wide completion or real-provider guarantee is claimed. T9/T10 deterministic tests do not prove live-model semantic completeness. T5/T6/T7 latest/new modes and T8 actual 502 root cause remain pending, not silently completed. No user-owned processes will be restarted automatically. Commits/publishing only if explicitly requested.
