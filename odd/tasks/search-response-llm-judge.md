# Search response LLM judge

## Objective and authorization
Implement and run a .NET/C# LLM judge over the user's existing human-labeled search responses, with strict `reasoning` and `label` (`good|bad`), accuracy/precision/recall, and inspectable disagreements. User explicitly requires .NET. Preserve the dirty worktree and existing behavior. No commits, push, PR, or destructive changes are authorized.

## Evidence and reference
- Reference inspected before source changes: https://github.com/alexeygrigorev/ai-engineering-buildcamp-code/tree/main/documentation-agent/evals ; specifically README.md, manual/judge.py and manual/run_judge.py.
- Dataset stem: `C:/Data/formacion/TimelineSpecifications/search-facts-evaluation-citation-allowlists-20261102T062417Z-f35d9311`.
- Input JSONL has 50 responses. `.labels.csv` currently has 17 annotations: 12 Good and 5 Bad. Synthetic browser-test annotations are excluded.
- Expected dataset SHA256: `e136e2f53cd7157e791a0a6ae9baf99daa2b751b92433cc49ff9ae99dcb2d2f2`.
- Observed Bad failure patterns: unjustified refusal (incorrect-answer), entity/relationship confusion and overgeneralization (hallucination), and irrelevant evidence dressed as requirements (hallucination). Valid citation membership is not semantic support.
- Labels CSV has no answer or free-form labeling comments. Join to source JSONL by dataset hash, runId/scenarioId, ordinal and question validation, not question text alone.

## Scope, constraints and decisions
- Add standalone .NET console under `dotnet/src/ProjectTimeline.SearchFactsJudge/`, using existing OpenAI/framework infrastructure and default `gpt-4o-mini` without changing production search or legacy extraction judge behavior.
- Strict output with brief evidence-based reasoning, not hidden-chain-of-thought requirements. Reject missing/invalid fields, unexpected labels and blank reasoning.
- Judge receives original question, response, and relevant captured source/tool evidence, never human label, failure category, scenario category/type, expected answers or evaluation notes. Derive only a generalized rubric from annotations; no row-specific answers.
- Evaluate all 17 labeled rows. No rerun of search, database dependency, synthetic annotation inclusion or source-label mutation.
- Errors are separate results, never forced `bad`. Report coverage and metrics on successfully judged labeled rows, with undefined denominators explicit. Positive class is `bad`.
- Export predictions, metrics, and complete disagreements with stable identity, human label/category and judge reasoning. Use unique output directories; do not overwrite labels or source data.
- Results are calibration on rubric-development data, not held-out generalization claims.
- Single writer. No dependency churn or unrelated formatting. Safe build/test flag: `-p:SkipClientBuild=true`; exclude live-model and PostgreSQL-dependent tests from offline checks.
- RDD currently rendered off; assess each delegated writer result and follow the native verification plan. Native review only if the user-owned switch is actually enabled.
- No work-unit commits without an explicit user request; record this limitation instead of inventing commit evidence.

## Tasks
- [x] T1 — Implement strict search-response judge, grounded rubric, read-only labeled-data loading/join and focused deterministic tests. **Status: done.** Route: delegated worker and independent verifier. Strict core passed independent verification; generalized-rubric correction observed RED 6 failures / 50 passes then GREEN 56 passes. Legacy extraction judge API preserved. Commit: not authorized.
- [x] T2 — Add CLI orchestration, trustworthy metrics/coverage and disagreement exports, usage documentation and deterministic runner tests. **Status: done (writer checks observed; final independent check in T3).** Route: same single writer. Added 14 runner tests; observed RED 2 metric failures then GREEN 70/70 total. Standalone build and CLI help passed. Commit: not authorized.
- [x] T3 — Independently verify as required, run the judge on all real labeled responses using existing credentials, inspect outputs and report actual metrics/disagreements. **Status: done.** Route: delegated verifier `mvzu7k80-6-srz6`. One real gpt-4o-mini run completed 17/17 successful rows with no errors/cancellations; metrics independently recomputed. User-authorized launchSettings key used only in child environment; prior environment restored. Commit: not authorized.

- [ ] T4 — Revise only the judge rubric using actual disagreements, with deterministic prompt-contract regression checks and preserved baseline. **Status: canceled before source changes; user selected search_agent_only.** Route: delegated single worker (prompt + tests multi-file trigger). Generalize claim entailment, entity fidelity, partial-answer tolerance and specification-versus-mutation refusal; no row-specific answers in prompt. Commit: not authorized.
- [ ] T5 — Independently verify T4, create a non-destructive corrected-label snapshot for human-confirmed ordinals 1/3/17, rerun all labeled responses once and compare baseline/new predictions against identical corrected ground truth. **Status: canceled before source changes; user selected search_agent_only.** Route: delegated verifier (external paid evaluation). Produce a real prediction-change example and paste-ready English form answer; no fabricated fix. Commit: not authorized.

## Iteration 1 authorization and controls
- User confirmed on this turn: original Good labels for refusal rows 1, 3 and 17 should be Bad. Original source CSV/baseline remain immutable; use a separate corrected-label snapshot, document provenance/revisions, and record no other label changes.
- User requested an actual judge-prompt iteration based on disagreements and one fixed example for a form. This authorizes judge prompt/test changes and one new all-label model run, not search-agent changes, remote form submission or unrestricted paid optimization loops.
- Separate label-correction effects from prompt effects. Rescore saved baseline predictions offline and new predictions against the exact same corrected labels. On the original 17 rows, expected corrected distribution is 9 Good / 8 Bad; verifier must confirm rather than assume current file state.
- Corrected-label baseline expected from saved predictions: TP=6 FP=3 FN=2 TN=6, accuracy=12/17, precisionBad=6/9, recallBad=6/8. Independently verify these values.
- A fixed disagreement must involve an actual old/new judge-label change aligned to unchanged ground truth (for example former hallucination misses or partial-answer false positives), not rows corrected only by changing human labels. Report regressions and calibration limitations honestly.
- Preserve prompt/data/model identity in both output snapshots; use unique new report directory and explicitly authorized transient launchSettings credential reuse. No credential values in source, output, memory, argv or logs.

## Acceptance and checks
- Exact structured output contract; valid and invalid fake completions tested.
- Input validation catches dataset hash/identity mismatch and CSV metadata escaping; labels are read-only.
- Leakage-sentinel tests ensure human metadata never enters model context.
- All labeled rows attempted; a failing model row remains an error and does not bias quality labels.
- Accuracy, bad-positive precision/recall and TP/FP/FN/TN independently checked against fixtures; undefined denominators and zero successful predictions handled explicitly.
- Saved predictions and disagreements retain original identity and sufficient response/evidence context to improve the prompt.
- CLI help and focused tests/build pass without Vite-generated modifications.
- Actual live run has 17 accounted-for rows or explicit error/blocker evidence; report every unavailable or skipped check.

## Progress and verification evidence
- Read-only explorer `mvzsv271-1-d1bc` completed dataset and framework mapping; no source writes.
- Reference manual judge and runner reviewed before implementation; intentionally improve reference by not treating API errors as `bad`.
- T1 worker `mvzt0vyo-2-yjjd` returned completed: reusable core, strict schema/runtime checks, safe evidence projection, external rubric and read-only dataset join; approximately 766 authored lines including 50 deterministic tests. No package additions or legacy judge edits.
- T1 observed RED: permissive parser 10 failed / 6 passed. Final GREEN: 50 passed / 0 failed / 0 skipped. Focused build: zero warnings/errors; diff whitespace check passed. Initial no-restore attempt lacked assets; authorized restore succeeded.
- Native ASSESS could not assess ambient untracked files and returned unassessable/high verification: writer self-verification plus independent verifier. No review authority mutations attempted.
- Independent verifier `mvztelh6-3-a837` reproduced 50/50 passing tests, clean build (0 warnings/errors) and whitespace checks; validated corpus hash, input privacy, read-only join, isolation and error separation.
- T1 correction completed by `mvztijpy-4-b58a`: removed dataset-specific examples, retained generalized specification-versus-mutation, entity fidelity, instance-versus-universal and semantic-entailment rules. Six new exclusion checks failed against old prompt (50 existing passed); GREEN 56/56, whitespace clean. Parent progress updates explain the verifier's observed task-file changes; no concurrent source writer was active. Final independent verification will include the corrected prompt.
- Live-run blocker: launching-process `OPENAI_API_KEY` is absent/nonblank=false. Existing evaluator explicitly uses only that environment variable, not automatic .env/appsettings/user-secret loading. Credential validity is unverified; no paid calls attempted.

- T2 worker `mvztmd44-5-zobf` completed standalone CLI, bad-positive nullable metrics, every-row/error/cancellation bookkeeping, unique durable reports, prompt and input provenance, and README. Approximately 653 additional authored lines (284 production/wiring, 272 tests, 97 docs).
- T2 observed RED: 2 metric failures with original 56 passing. Final GREEN 70 passed / 0 failed / 0 skipped. Build 0 warnings/errors; CLI help and diff whitespace checks pass. No live/API/DB calls.
- Report contract: `predictions.jsonl`, `metrics.json`, `disagreements.md`, `rubric.md`, `run.json`. T2 itself performed no live calls; actual results follow below.

## Final verification and live calibration
- Independent verifier: 234 passing targeted/regression tests (70 judge + 164 regressions). Solution build, CLI help and whitespace checks passed. Full offline suite and UI/browser tests were not run.
- LaunchSettings contained one distinct nonblank OpenAI key in the WebApi profile; parsed without displaying/storing the key, injected only into the evaluation child process, previous environment restored. No source/config edits or automatic credential loading added.
- Exactly one paid gpt-4o-mini run, exit 0: 17 labels attempted, 17 successes, 0 errors/cancellations. Dataset has 50 saved responses, so label coverage is 34%; judging coverage over labels is 100%.
- Independently recomputed bad-positive confusion matrix: TP=3, FP=6, FN=2, TN=6. Accuracy=9/17=52.94%; precisionBad=3/9=33.33%; recallBad=3/5=60.00%.
- Eight disagreements by original ordinal: human Good -> judge bad: 1,3,7,9,15,17; human Bad -> judge good: 8,16. Judge rationales are not independently endorsed verdicts.
- Model-quality observations: completeness penalties despite the partial-answer allowance (7,9,15); missed human-labeled hallucinations (8,16); refusal judgments sometimes reference potentially available knowledge instead of establishing captured support (1,3,17).
- Source and labels remained byte-identical. Source SHA256 `e136e2f53cd7157e791a0a6ae9baf99daa2b751b92433cc49ff9ae99dcb2d2f2`; labels SHA256 `a704ba82d039461ac6d76a71aea1beeaa42f72dcdb2502e416257ef5da23aabe`. Rubric snapshot and input provenance verified.
- Output directory: `C:/Data/formacion/TimelineSpecifications/search-facts-evaluation-citation-allowlists-20261102T062417Z-f35d9311.judge-20261102T2237495800801Z`. Contains all five report artifacts; full answers and reasoning in disagreements.md.
- Native review remained off; no review authority mutations, commits, push or PR. Parent structural metrics readback and repeated whitespace check passed.

## Scope clarification and next step
After the parent explained search-agent versus judge-prompt changes, user explicitly selected `search_agent_only`: improve search responses and reevaluate them, keeping judge fixed. T4/T5 above are canceled plans, not completed work; no judge prompt/test changes or new paid runs occurred. Human corrections for refusal rows1/3/17 remain valid. Read-only exploration is mapping a separate search-agent prompt iteration. Do not claim this satisfies the English judge-prompt iteration assignment; any final example is a search-agent behavior improvement.
