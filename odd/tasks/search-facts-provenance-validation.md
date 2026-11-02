# Search Facts provenance validation

## Objective
Fix the frontend rejection of valid Search Facts evidence with nullable review provenance, preserving fact identity and citation-to-source validation.

## Authorization and scope
- User approved fixing the diagnosed nullable-provenance incompatibility and adding regression tests.
- Backend TimelineFactSearchHit and TimelineFactInformationCitation expose nullable CreationReviewId and optional ExtractionJobId; frontend currently requires a nonblank creationReviewId for every fact.
- Confirm direct-extraction provenance from production paths/tests before changing accepted shapes. Do not merely disable validation or accept missing provenance without domain support.
- No model/provider/telemetry/accounting changes, storage migrations, paid calls or real external export. Preserve earlier uncommitted telemetry feature and unrelated work. Stay on feat/agent-framework-telemetry; no commits without explicit request.
- Existing generated wwwroot churn was present at bug-fix start following user builds; do not revert it to HEAD or erase user work. Builds may legitimately update the frontend bundle needed to serve the fix; report these generated effects.

## Tasks
- [x] S1 (done) Validate extraction provenance semantics, observe regression RED, align frontend validation/citation comparison and add deterministic regression/rendering/serialized-contract coverage. Route: delegated gentle-ai-worker, mandatory multi-file writes and write preparation.
- [x] S2 (done) Assess changed candidate, run independent verification if required, build updated UI and validate offline functional rendering/browser flow where applicable. Route: native ASSESS then gentle-ai-verify; no live model requests.

## Acceptance criteria
- Valid extraction-backed facts/citations with creationReviewId:null are accepted; legacy review-backed evidence remains accepted.
- Invalid/missing/contradictory provenance is rejected or citation narrative omitted as appropriate; extraction IDs are validated and compared where they establish origin.
- Duplicate/unreturned/mismatched citations and malformed facts remain protected; no ungrounded narrative accepted.
- Existing rendering remains safe (escaped content, correct source labels) and result/request lifecycle unchanged.
- Exact affected frontend tests and relevant backend tests pass; frontend production build succeeds. Do not claim reproduction of user's exact HTTP response without runtime evidence.

## Verification
- Test-first default: failing direct-extraction case before fix, minimum GREEN, alternate malformed cases and focused refactor.
- node --test dotnet/src/ProjectTimeline.UI.WebApi/ClientApp/src/timelineSearch.test.js
- node --test dotnet/src/ProjectTimeline.UI.WebApi/ClientApp/src/*.test.js
- dotnet test dotnet/tests/ProjectTimeline.Tests/ProjectTimeline.Tests.csproj --filter "FullyQualifiedName~TimelineFactInformationTests|FullyQualifiedName~TimelineFactDirectExtractionTests"
- npm run build --prefix dotnet/src/ProjectTimeline.UI.WebApi/ClientApp
- S2 offline browser flow should use mocked API/local static frontend if applicable; no production startup/migrations/paid models. If runner unavailable, report limitation and use deterministic actual component rendering evidence.

## Evidence
- Read-only diagnosis located exact message in timelineSearch.js normalization. Frontend fixture currently contains only nonnull review IDs; backend returns direct domain response with default camelCase serialization.
- No causal evidence links error to OpenTelemetry. Actual failing runtime JSON has not been captured.
- S1 domain confirmation: DataAccessLogic/TimelineFacts.cs30–39 permits exactly one origin: historical extraction-only or accepted-review-only; TimelineFactModel.cs15–16 requires direct Entry evidence without clarification. TimelineFactDirectExtractionTests.cs92–109 proves searchable direct facts with null review. Current generation produces drafts, so fix preserves historical valid evidence rather than bypassing draft acceptance.
- S1 changes: normalizer accepts exactly one valid origin and compares both nullable origin IDs for citations; direct evidence requires Entry and empty clarification. Renderer labels Extraction job vs Creation review correctly. Legacy review-only validation stays supported; malformed/mixed origins rejected, unverified citations omitted and narrative suppressed.
- S1 observed RED9 passing5failing, including exact reported frontend error from valid direct evidence; GREEN14/14. Full frontend33/33; backend baseline59/59, final61/61 with two actual HTTP serialized-contract tests using fake synthesis. Production Vite build and diff whitespace check passed. Intermediate stale asset reference/test-role setup failures corrected.
- Build updated pre-existing generated bundle from index-CKXhVRaN.js to index-D5Um0w5p.js and index.html. Preserve current updated bundle delivery; no manual compiled edits. Actual user failing response remains unobserved.
- Native ASSESS unassessable on unrelated untracked declarations; follows high-risk fallback with independent verifier. RDD off; no native review transaction.
- S2 independent PASS: focused frontend14/14, all frontend33/33, backend61/61, production build and scoped diff whitespace check all pass without retries. No severe candidate-caused findings.
- Actual built React browser check passed three fully mocked scenarios: valid extraction/null review, valid legacy review with clarification, and mismatched extraction citation suppressing narrative.10 API requests all mocked; zero external/unexpected requests, console or runtime errors. Loaded index-D5Um0w5p.js. Verifier-owned preview5179 stopped; user's server5000 untouched. Browser evidence: C:/Users/biarunxa/AppData/Local/Temp/search-facts-s2-kHS2j1/ (evidence.json,cleanup.txt,three screenshots). Temporary harness setup errors were corrected before observed browser PASS; no candidate changes made to workaround them.
- Parent spot-check14/14 frontend tests passed; source diff whitespace check passed. Updated generated frontend bundle intentionally preserved; no restore to HEAD. Previous feature/user changes untouched.
- Remaining validation: user's original live HTTP response not captured and actual Search Facts session must be retried by user after hard reload. No paid calls, real export, production startup or migrations. No live-reproduction/telemetry-causation claim.
- Commits: not created; no explicit commit authorization.

## Next step
Implementation and independent offline/browser verification complete. User should hard-refresh (Ctrl+F5) and explicitly retry Search Facts to load new index-D5Um0w5p.js. If original failure persists, collect sanitized actual HTTP response to diagnose a different shape. No commits/push/PR created.
