# Search unknown-fact recovery

## Objective and authorization
User confirmed UnknownFactId in fact information search also causes a UI error and explicitly approved reproducing and correcting it. Preserve emitted-ID allowlist, authorization and evidence freshness. An invalid tool argument should permit bounded model recovery rather than needlessly aborting the search; never silently turn failure into a no-results answer.

## Evidence
Read-only exploration: TimelineFactInformationTools.SourceCoreAsync:97 rejects IDs absent from run-local emitted-facts dictionary before DB lookup. Search emits then records IDs; unknown is not the EvidenceChanged path. SearchEntriesAgent wraps unhandled tool errors as synthesis failures, endpoint maps to502 InvalidResearchAnswer. Exact real ID origin remains unknown; do not assert model hallucination as established cause.

## Constraints
Preserve extensive pre-existing dirty edits and prior contextual reconciliation fix. No production DB/provider/live requests, secrets, migrations, app restart, generated assets, process termination, commits, staging or publishing. Existing feature branch feat/agentic-fact-search; recheck scoped state. Release offline tests always -p:SkipClientBuild=true to avoid Vite and known Debug locks. Existing unrelated review-tools oversized batch assertion fails before this task; do not fix.

## Tasks
- [x] U1 (implemented and independently verified offline): Reproduce UnknownFactId through actual offline agent/tool seam; implement minimal bounded recovery for unknown emitted IDs preserving access/freshness/budget/cancellation and final evidence validation. Observe RED/GREEN and negative cases.
- [x] U2 (completed scoped offline verification): Assess and independently verify focused offline research/tool/API tests; inspect incremental changes and record limitations.

## Acceptance checks
Unknown source/fetch ID does not leak or fetch unauthorized data. Actual harness can receive sanitized recoverable error and continue with valid search-returned IDs. Repeated mistakes remain bounded by existing budgets and fail explicitly if not recovered. Permission/scope/revision/stale-evidence/cancellation remain hard failures. Existing valid flow works. No false success or fabricated evidence. Confirm SDK behavior before choosing fix; stop for real ambiguity or larger shared-harness changes.

## Writer evidence
Approximately270incremental lines across tools, SearchEntriesAgent, research/tools tests and docs. SDK-loop RED2failed before source changes; GREEN2passed. SDK captures ordinary tool exceptions and continues; new sanitized recoverable unknown-ID responses allow two errors, require subsequent successful emitted-ID fetch, retain fatal failures for final validation and account budgets. Focused116passed. Full offline suite1266passed7failed23skipped: one known review-tools oversize failure plus six Search_and_fetch_exclude_ineffective_or_out_of_scope_facts failures not measured before changes; causality unresolved. Do not call them pre-existing without evidence. No shared harness change or live/UI reproduction.

## Assessment
Native ASSESS unassessable because existing untracked files; high-risk fallback requires independent verification. RDD off.

## Independent findings
Verifier116passed6failed: recovery unknown-ID catch invokes CheckAsync but exceptions inside catch bypass sibling fatal retention; hard access/scope/revision failure can be lost if state restored before final validation. Must reproduce RED and retain failure permanently. Six failures are stale fixtures in TimelineFactSearchTests.cs:75-81: missing new semantic assessment prevents intended scope guard from running. Attributable to prior contextual fix, not proof of pre-U1 execution status. Add matching assessment and preserve original scope assertion, not weaken it. Parent derives narrow extra test surface TimelineFactSearchTests.cs as corrective regression repair for earlier authorized feature.

## Correction evidence
Worker observed RED12failed4passed across restored-state/scope cases, then GREEN16passed. Added six unknown-ID restored-access/scope/revision actual-SDK regressions and concurrent allowance/accounting regression. Outer sanitized non-budget failure retention now covers recovery-handler exceptions. Stale fixtures now supply correctly bound semantic assessment and preserve exact scope assertion. Focused129passed. Full offline1279passed1knownoversizefailure23skipped. Followup59netlines, no live/UI or database execution. Fresh ASSESS still unassessable -> independent verifier required.

## Final independent verification
Independent focused129passed0failed0skipped exit0. Fatal-retention gap confirmed closed; six actual-SDK restored-state unknown-ID regressions and six exact-scope fixtures pass. Concurrent shared allowance/output accounting covered; no new must-fix finding. Worker full-suite1279passed1knownoversizefailure23skipped was not independently repeated. Parent scoped git diff --check exit0; only existing line-ending warning. Global suite not claimed green. Native review off; ASSESS unavailable -> independent checks used.

## Remaining checks and next step
Known unrelated review-tools oversized-batch assertion remains failing outside authorized scope. Live-model and PostgreSQL tests disabled; no browser/runtime restart/live reproduction. Exact real offending-ID origin remains unknown; offline verified recovery does not guarantee every live model will correct arguments. Rebuild/restart application with updated code and retry original search; do not claim current running app updated. No database/data changes, staging or commits. Commit evidence: none (explicit request required).
