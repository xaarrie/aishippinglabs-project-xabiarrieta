# Fact search outcomes and source-entry batching

## Objective
Make fact-search failures and partial outcomes actionable, stop avoidable model calls after fatal tool errors, and retrieve multiple source entries in one fetch_source_entry call.

## Authorized scope
- Preserve existing provider, tool, query, time, and evidence limits.
- Distinguish final narrative, partial evidence, no eligible evidence, and safe HTTP errors using existing state/code contracts.
- Cancel the agent loop promptly after fatal tool failures without hiding the original failure or breaking recoverable UnknownFactId handling.
- Change fetch_source_entry from factId to factIds, validating facts emitted in the current operation and returning distinct source contexts by entry.
- Keep provenance, permissions, cache validation, clipping, ledger integrity, and aggregate batch-output accounting.
- Update relevant prompts/schema descriptions and deterministic regression tests.
- Follow-up T4/T5: simplify ONLY search results, keeping metrics, clear safe diagnostics, agent summary and cited facts; hide source-entry excerpts, uncited facts and irrelevant provenance IDs. Leave adjacent entry/snapshot management untouched.

## Constraints
- Branch: fix/fact-search-outcomes-source-batch.
- Preserve all pre-existing modified/untracked files, especially README.md, .gitignore, .atl/**, wwwroot/**, dotnet/docs/**, and other odd task documents.
- Single writer, sequential work units; no live model calls or dependency installation.
- Build client only to a temporary directory; do not regenerate dirty wwwroot assets.
- No new settings or outcome states; no increased budgets or reserved extra provider calls.
- Commits, push, and PR creation are not explicitly authorized. Do not commit; record pending commit authorization rather than inventing commit evidence.
- Approximately 400 authored diff lines per work unit is advisory, never a reason to omit tests or compress code.

## Tasks
- [x] T1: Correct result/error presentation and fatal-loop termination with deterministic regression tests. Status: verified; commit pending explicit authorization.
- [x] T2: Batch source-entry retrieval with complete tool/prompt/schema migration and regression coverage. Status: independently integrated-verified; commit pending explicit authorization.
- [x] T3: Independently verify the integrated changes, including UI behavior, focused/full non-live tests, and builds without modifying existing assets. Status: verified with six opt-in PostgreSQL skips documented below.

- [x] T4: Focus search results on metrics, safe actionable errors, agent summary and cited facts. Status: independently verified; commit pending explicit authorization.
- [x] T5: Independently verify the simplified results and unchanged entry/snapshot management with frontend tests, safe build and mocked full-App browser checks. Status: independently verified.

## Acceptance criteria
### T1
- Narrative heading/disclaimer appears only for a displayed model narrative.
- Partial/empty/insufficient-evidence and exhausted-budget results have specific safe explanations rather than the generic no-cited-answer fallback.
- Partial/no-answer outcomes are not announced with an unconditional green success banner.
- Known safe research HTTP error categories are readable without leaking provider messages.
- Fatal tool errors stop subsequent model iterations and retain failure precedence; the first two recoverable UnknownFactId failures remain recoverable.
- Existing limits, timeout, cancellation, provenance, and final fail-closed behavior remain intact.

### T2
- fetch_source_entry accepts a bounded nonempty unique factIds array, not arbitrary entry IDs, matching fetch_facts conventions.
- Multiple facts can anchor one or multiple entries; each entry appears once with stable server-owned provenance.
- All anchors are validated before emission; invalid batches do not partially commit ledger state.
- One batch consumes one tool call, no search calls, and accounts for the actual serialized aggregate output.
- Existing context-count/character/output limits and explicit truncation are preserved.
- All in-repository singular callers, SDK JSON fixtures, prompt text and schema expectations are migrated.

### T3
- Focused and full deterministic suites pass, excluding LiveModel tests.
- Server build and client build pass using safe output settings.
- Browser checks cover complete, partial, no-evidence, limit and HTTP-error outcomes without paid model calls.
- Every failure, skip or environmental blocker is recorded honestly.
- Existing unrelated dirty files remain preserved.

### T4
- Keep metrics visible, following the user's explicit clarification; no removal of coverage or research budget facts.
- Show only the actual valid agent summary and facts cited in that answer; do not render additional uncited facts or source-entry context excerpts.
- Remove raw evidence/fingerprint/job/review IDs, source-entry bodies and redundant long explanatory boilerplate from result presentation.
- Keep fact claim and useful entity/provenance-kind context concise; do not substitute uncited facts when no facts are cited.
- Existing validators and all received evidence remain intact for grounding; this is presentation-only, with no backend/tool/budget changes.
- Known safe research HTTP failures include a readable explanation plus existing allowlisted code/status suitable to share for debugging; never expose arbitrary provider text, stack traces or private response bodies.
- Adjacent timeline entry list, snapshot maintenance, routing, request fencing/loading and workspace context behavior remain unchanged.
- Observe deterministic RED then GREEN for the rendering/diagnostic changes.

### T5
- All frontend tests pass and client compiles to a temporary directory.
- Mocked full-App browser checks prove metrics/summary/cited facts remain, source-entry excerpts/uncited facts/irrelevant IDs disappear, and clear errors retain safe diagnostic identifiers.
- Verify entry/snapshot management still appears and no production mutation/model requests occur.
- Record independent assessment/verification and the parent spot check; no native review while RDD is off.

## Verification plan
Commands are executed by the bounded writer for its unit and by an independent verifier as required.
- T1: dotnet test dotnet/tests/ProjectTimeline.Tests/ProjectTimeline.Tests.csproj -p:SkipClientBuild=true --filter "FullyQualifiedName~TimelineFactResearchTests|FullyQualifiedName~TimelineFactResearchToolsTests"
- T1: node --test dotnet/src/ProjectTimeline.UI.WebApi/ClientApp/src/timelineSearch.test.js dotnet/src/ProjectTimeline.UI.WebApi/ClientApp/src/api.test.js
- T2: dotnet test dotnet/tests/ProjectTimeline.Tests/ProjectTimeline.Tests.csproj -p:SkipClientBuild=true --filter "FullyQualifiedName~TimelineFactResearch|FullyQualifiedName~TimelineFactInformationTests"
- T3: dotnet test dotnet/ProjectTimeline.sln -p:SkipClientBuild=true --filter "Category!=LiveModel"
- T3: node --test with all ClientApp/src/*.test.js files expanded by the shell.
- T3: dotnet build dotnet/ProjectTimeline.sln -p:SkipClientBuild=true
- T3: npm run build --prefix dotnet/src/ProjectTimeline.UI.WebApi/ClientApp -- --outDir <temporary-directory> --emptyOutDir false
- T3: isolated/mocked browser functional checks; no production API writes or model calls.
- Parent: one bounded local read-only spot check of a reported command.
- T4/T5: node --test dotnet/src/ProjectTimeline.UI.WebApi/ClientApp/src/*.test.js
- T5: client build to a fresh temporary directory with the safe --outDir override; mocked full-App browser checks, no paid models.

## Progress and evidence
- Read-only diagnosis and scope mapping completed.
- Existing fatal-error tests currently allow later model calls; use strengthened tests to observe RED before changing behavior.
- T1 writer observed RED (.NET 96 passed/18 failed; Node 29 passed/20 failed), then GREEN (.NET 119/119; Node 54/54), zero skipped.
- T1 independently verified: .NET 119/119 and Node 54/54; zero failures/skips. No blocking findings. The verifier did not independently observe RED.
- Same-response fatal+sibling SDK coverage gap addressed by T2 tests in both call orders.
- Engram readback normalizes a trailing newline; complete task document contents match after trimEnd.
- Native ASSESS could not assess due existing untracked files; risk is unassessable, so use independent verification as for high risk. RDD reported off.
- T1 authored diff reported approximately 293 lines including regressions.
- T2 worker timed out while generating tool arguments. No successful T2 verification report or recoverable RED evidence was returned.
- Incident diagnosis: branch is correct, partial T2 edits are confined to the authorized tool and batch test file; source method is already factIds-based, while prompt/schema/caller migrations remain incomplete. Preserve partial work and resume with a fresh bounded writer.
- T2 recovery completed: initial baseline failed compilation on 12 scalar callers. After migration, observed RED 178 passed/3 failed (prompt/description); separate schema subset RED 4 passed/1 failed. Final focused GREEN: 192/192, no skips.
- Original interrupted-writer T2 RED/GREEN evidence remains unavailable; no source was reverted to recreate it.
- T2 covers validation atomicity, multi-entry/same-entry batching, stable cache/provenance, exact aggregate output, clipping, mutable-array snapshots, cancellation and mixed/context-only citations.
- Six backend/test files total 669 additions/105 deletions versus HEAD, including inherited T1 work. Most growth is safety regression coverage; recovery increased this diff by about 364 changed lines.
- Post-T2 ASSESS again returned unassessable due existing untracked paths, RDD off; independent integrated verification is required.
- T3 independent .NET full non-LiveModel suite: 1,372 passed, 0 failed, 6 skipped (all TimelineFactPostgresTests; opt-in database guard not enabled). Real PostgreSQL remains unverified.
- T3 full Node suite: 89 passed, 0 failed/skipped/cancelled across all five test files; non-failing module-type warnings.
- T3 server build: passed, 0 warnings/errors. Client Vite build: passed, 37 modules, output exclusively in a newly created temporary directory.
- T3 full built React App browser checks with mocked API: 16/16 scenarios passed on two final runs. Final run checked computed feedback colors/backgrounds, observed 128 intercepted GET requests and no mutation requests, external requests or page errors.
- Browser scenarios covered complete/partial/no-answer outcomes, empty/insufficient evidence, model budget exhaustion, rejected fact/context citations, blank narrative, all six safe HTTP error categories and unknown fallback.
- Initial browser harness navigation failed due a temporary Windows-path check; only the temporary harness was corrected before passing. Temporary server/browser/build resources were cleaned up.
- Parent spot check: node --test dotnet/src/ProjectTimeline.UI.WebApi/ClientApp/src/timelineSearch.test.js dotnet/src/ProjectTimeline.UI.WebApi/ClientApp/src/api.test.js passed 54/54, no failures/skips/cancellations.
- Independent integrated review found no blocking code defect; scalar source-tool callers fully migrated, all-anchor validation precedes output selection, aggregate emission precedes context-ledger commit.
- Git state/scoped diffs were unchanged during verification; pre-existing modified/untracked files and built wwwroot assets remain intact.
- Explicitly not run: paid/live-model checks and real PostgreSQL tests; browser-to-live-backend integration not covered by mocked frontend checks. Native review not started because RDD is off.
- Work-unit commit identities: none; commit authorization pending.

- Follow-up authorized: user clarified only the search-result block is to change. Metrics must stay visible; timeline entries and snapshots outside results are not to change.

- T4 writer observed RED25passed/30failed, then focused GREEN55/55 and full Node90/90, zero skips. Five files changed, about270 T4-only authored diff lines.
- T4 retained metrics/warnings/safe diagnostics and changed result rendering only; normalized hidden evidence and validators unchanged. App.jsx hash unchanged, preserving entry/snapshot management and request lifecycle.
- T4 ASSESS again unassessable due existing untracked paths, RDD off; T5 independent verification follows the returned high-risk plan.

- T5 independently passed full Node90/90 with no failures/skips/cancellations, and safe temporary Vite build (37modules); existing non-failing module-type warnings remain.
- T5 actual production-built full-App browser verification passed22search/error scenarios plus1navigation flow. It observed189interceptedGETs and zero mutation/external/model requests or page errors.
- Browser checks verified exact metric values, only cited fact cards, valid context-only summaries without fact promotion, hidden invalid evidence rejection, clear safe error status/code, unknown/private-body fallback, computed feedback styles and no source-body/quote/transcript/UUID/disclaimer leakage in result DOM.
- Adjacent entry/snapshot previews, links, checkbox/draft behavior and new-entry navigation/Cancel still worked without mutations. App.jsx hash remains fea19042384802af8e0956367002a8135028341b; backend hashes match T3.
- T5 temporary harness initially hit command-length and selector issues; only temporary files were corrected, then the complete run passed and its resources were cleaned.
- T5 initial/final tracked-diff digest and56untracked-file hashes matched (pre-existing .vs indexes excluded); no unexpected repository mutation.
- Parent T4 spot check independently passed55/55 focused Node tests, zero failures/skips/cancellations.
- T4/T5 intentionally did not rerun .NET or real database/model/live-backend checks because changes are frontend-only; prior backend verification and PostgreSQL skips remain as documented.
- All5implementation/verification tasks complete within the documented boundaries. No commit/push; no served wwwroot regeneration.

## Next step
To load the simplified results in a running instance, rebuild the served client assets and restart the application. Existing dirty wwwroot was deliberately not overwritten. No commits/push without explicit authorization; optional real PostgreSQL/live-model verification remains outside the performed checks.
