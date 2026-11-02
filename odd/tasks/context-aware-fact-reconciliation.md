# Context-aware fact reconciliation

## Objective and authorization
User approved fixing reconciliation that invalidates existing requirements using unrelated newer facts. Compare both claims with their original contexts; invalidate only a same-scope contradiction or explicit supersession. Absence of evidence in a new entry is not contradiction. Preserve independent facts; ask on genuine ambiguity.

## Scope and constraints
Backend reconciliation prompts, evidence/context payloads, structured result and validation, focused offline regression tests and documentation. No database mutation, migrations, live provider calls, runtime restart, publishing or commits authorized. Existing branch: feat/agentic-fact-search. Worktree contains extensive pre-existing edits including target files: preserve all existing work, never reset/stage/commit it. No automatic repair of previously accepted data. RDD is off per runtime.

## Tasks
- [x] C1 (implemented and independently verified offline): Implement context-aware comparison with both source contexts, explicit semantic invalidation evidence, deterministic contract guards and focused regression tests. Observe meaningful RED/GREEN where applicable; prompt-only assertions cannot prove live semantic accuracy.
- [x] C2 (completed within approved scope; known baseline exception below): Assess changed candidate, independently verify required offline tests/build, inspect incremental changes and document limitations.

## Acceptance and checks
Cover login/password recovery versus unrelated toolbar/mobile facts, dotnet run versus silence, genuine same-scope contradiction, explicit supersession, ambiguity and unavailable historical context. Preserve provenance, freshness and authorization checks. Avoid brittle natural-language scope heuristics. Verify actual context availability through search/fetch tools before selecting schema. Keep schema changes internal/compatible where possible; fail closed rather than invalidate without sufficient evidence.

## Evidence
Read-only exploration found provenance/replacement linkage validation but no semantic same-scope guard. Original agent instructions omit explicit context/scope and absence-of-evidence protections. Git state read confirmed pre-existing dirty worktree and feature branch; no writes to existing source yet.

## C1 implementation handoff
Worker added bounded original-context delivery and internal per-invalidation assessments bound to both claims, source fingerprint, quotes and replacement index. Scope/relationship guards fail closed; semantic correctness still depends on model judgment. Approximately 484 incremental changed lines including tests/docs. Context-delivery RED observed; user-example tests only observed GREEN after fixture corrections. Focused 118/118 passed; tools 22 passed/1 pre-existing oversized-batch assertion failed (test expects max8, current baseline max20). Debug DLL lock avoided with Release, no process stopped. Initial builds invoked Vite outside permitted surfaces; final asset status matches baseline but baseline content hashes unavailable. No restoration authorized or performed.

## Assessment
Native ASSESS unassessable due to existing untracked files; returned high-risk fallback requires independent verifier. RDD off. No review authority mutation.

## Independent C2 evidence
Verifier observed 140 passed/1 pre-existing oversized-query failure in focused suite and 95 passed/0 failed in strict-schema, clarification-review and service tests (235 passed total, 1 failed). Existing strict-schema coverage concerns extraction, not optimizer output. Must add optimizer-specific outbound schema coverage and regression for replacement-quote canonicalization before comparison. Generation-only assessments are not persisted; pre-existing pending proposals can still be accepted using older guards. This is an uncovered compatibility boundary, not a new bypass introduced by C1. User explicitly declined adding a legacy pending-proposal acceptance block and will regenerate the proposal manually. Keep existing acceptance behavior; protection is limited to newly generated reconciliation. Do not extend service/application/persistence scope. No live data repair or model execution authorized.

Generated assets reference existing JS/CSS, status/stat unchanged during verification. No evidence of additional accidental changes, but original-byte equality cannot be proven; no restoration performed. No independent separate build (test builds succeeded with SkipClientBuild=true). Model semantic accuracy remains untested live.

## Follow-up RED and scope stop
Worker added 111 regression-test lines only. Offline Release/SkipClientBuild run: 2 passed, 3 failed. Meaningful RED proves valid original literal rejected after canonicalization, mismatched canonical assessment incorrectly accepted, and optimizer outbound json_schema lacks strict. GREEN not reached. Correcting outbound strict configuration requires additional existing dirty file dotnet/src/ProjectTimeline.BusinessLogic/Agents/Framework/LocalCustomHarnessAgent.cs, outside delegated surfaces. Worker stopped before production edits and requests explicit approve/decline expansion. No live calls or Vite in follow-up.

## Follow-up GREEN
Worker corrected original-vs-canonical quote binding without rewriting unvalidated assessments, added optimizer-specific nested strict schema and optional structured harness overload preserving default callers. Follow-up total +316/-12 including prior RED tests. New regressions6/6, context/schema39/39, related planner/provider/API/structured/research160/160; tools22/23 with same pre-existing oversized assertion. Non-overlapping final writer suites221passed/1baselinefailure. No live provider validation. Second ASSESS remains unassessable due untracked files and requires independent verification; RDD off.

## Final independent verification
Verifier confirmed quote-binding and strict-schema corrections, opt-in backward behavior, tool continuation and final metadata checks; no new must-fix defects. Combined context/schema/planner/provider/API/structured/research/tools run221passed1knownfailure0skipped; clarification-review/service89passed0failed0skipped. Total310passed1pre-existingbatchfailure. All used Release --no-restore -p:SkipClientBuild=true. Test builds succeeded; no separate build/live provider/DB/browser execution. Parent repeated scoped git diff --check on correction source/tests: exit0, no diagnostics. Exact commands retained in verifier handoff and evidence doc. All task-specific corrections verified; global suite deliberately not claimed green.

## Remaining baseline exception (not authorized follow-up)
- [ ] B1: Existing Invalid_batches_fail_before_any_backend_work oversize assertion expects9queriesrejected while baseline allows20. Observed before changes and unchanged. Do not fix automatically or claim all tests passed.

## Next step
Rebuild/restart application with updated code before user manually regenerates reconciliation. No runtime restart or regeneration executed here. Review semantic results before acceptance: offline structural evidence does not prove model judgment or live provider acceptance. Legacy acceptance block explicitly excluded. Generated-asset baseline-byte uncertainty remains documented. No existing data repaired. Commit evidence: none; explicit user request required, no commits performed.
