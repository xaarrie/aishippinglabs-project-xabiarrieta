# Search-agent prompt calibration

## Objective and authorization
User explicitly selected `search_agent_only` after clarification: improve the search-agent prompt and reevaluate responses, keeping the existing judge unchanged. Show an honest before/after example if observed. This is not a judge-prompt iteration and must not be described as satisfying that different assignment. Preserve dirty worktree and original artifacts. No commits, push, PR, schema/routing changes or destructive operations authorized.

## Evidence
- `SearchEntriesAgent.cs:37–60` has separate PlanningInstructions and SynthesisInstructions. Tool-free gpt-4o-mini planning may refuse before any retrieval; otherwise server performs one bounded search batch and source fetch, then isolated synthesis.
- Human Bad cases 5/6/11 were wrongly refused before retrieval. Cases 8/16 asserted entity/relationship or workflow requirements not entailed by cited evidence.
- User clarified old labels for refused cases 1/3/17 must also be Bad. Preserve original CSV; any corrected-label snapshot must state exactly these changes, separate from prompt effects.
- Old dataset stem: `C:/Data/formacion/TimelineSpecifications/search-facts-evaluation-citation-allowlists-20261102T062417Z-f35d9311`; source SHA256 `e136e2f53cd7157e791a0a6ae9baf99daa2b751b92433cc49ff9ae99dcb2d2f2`, labels SHA256 `a704ba82d039461ac6d76a71aea1beeaa42f72dcdb2502e416257ef5da23aabe`.
- Old fixed-judge output: same stem plus `.judge-20261102T2237495800801Z`. Judge source rubric and built copy must match this snapshot byte-for-byte.
- Original scenario manifest has 50 questions; only 17 annotated questions belong in this experiment. Runner has no ordinal filter, so derive exactly-17 CSV preserving ordered scenario fields and stable source lineage.

## Scope and constraints
- Source edit surfaces: only PlanningInstructions/SynthesisInstructions in `dotnet/src/ProjectTimeline.BusinessLogic/Agents/SearchEntriesAgent.cs`, plus a new `dotnet/tests/ProjectTimeline.Tests/SearchEntriesPromptContractTests.cs`.
- Planning: distinguish documentation about state-changing operations from requests to execute changes; permissions/screens/requirements remain read-only information questions. Unknown evidence availability is not OutOfScope. Preserve exact entities and purposeful lexical variants.
- Synthesis: exact claim-to-evidence entailment, entities/fields/direction/actor/conditions/modality; never turn instance state into universal requirement or unrelated display facts into workflow rules. Answer supported parts and explicitly limit unsupported parts. Keep existing citation/JSON shape and fact/context distinction, untrusted-data protections, budgets and no-tools/no-mutations rules.
- No dataset-specific expected answers, ordinals or invented requirements in production instructions. No changes to judge, search implementation, retrieval/ranking, budget, schemas or authorization.
- Deterministic tests can establish prompt delivery/contracts but cannot prove model semantic behavior. Observe meaningful structural RED/GREEN and label those checks honestly; actual behavior requires model replay.
- Historical actor `0e6047b4-385d-4d7c-bbcf-4926e7c8fc70`, timeline `76ba87c5-71a4-4bf6-a87b-d1a40a3ec86c`, expected project `8d3559af-77d8-476d-b763-5f085889861d`. Preflight must confirm same authorized scope, otherwise stop full rerun; never substitute another database/project or modify data.
- Live runner uses launching-process `ConnectionStrings__ProjectTimeline` and `OPENAI_API_KEY`. Previously user explicitly authorized transient OpenAI key use from WebApi launchSettings; never log secrets/raw config, persist credentials or place them in argv. Connection provisioning initially blocked. User now explicitly authorized reading `dotnet/src/ProjectTimeline.UI.WebApi/appsettings.json` for the application's existing database connection, transient child-process use for read-only preflight/evaluation, no display/storage of credentials. Do not consult unrelated secret stores or alter configuration.
- One paid full 17-question search rerun after successful read-only preflight; expected about 34 model calls, up to 51,000 output tokens, indicative $0.03–$0.05 not a guaranteed ceiling. A fixed-judge evaluation is additional. No automatic full-run retries.
- New run IDs differ; join by explicit source lineage + exact question/scope. Live database is not a frozen historical snapshot; report evidence/source changes and avoid attributing every response difference to the prompt.
- Old human labels belong to old answers, NOT replacements. No accuracy/precision/recall against copied old labels for new answers. Judge predictions are secondary and need no new invented human labels. If required, use a generated .NET artifact harness calling existing reusable SearchResponseJudge directly for unlabeled predictions; do not alter judge CLI/production code or fabricate human truth.
- When full replay is blocked, report that blocker. Controlled planner/synthesis replay over captured inputs may demonstrate a stage-level change, but must be identified as such, never as an end-to-end run.
- Safe test/build flag `-p:SkipClientBuild=true`. One writer only; applicable independent verification follows native ASSESS (RDD currently off). No commits without user request.

## Tasks
- [x] S1 — Improve the two search prompt constants and add focused prompt-contract regression tests. **Status: done (writer checks observed; independent validation in S2).** Route: delegated worker `mvzv48pf-9-trpf`. Structural RED15 failures/82 existing passes; GREEN97/97. Two prompt constants only, approximately14 authored lines plus120-line test file; all bytes outside constants preserved. Commit: not authorized.
- [ ] S2 — Independently verify, prepare exact-17 lineage-preserving replay, execute read-only same-scope preflight and the bounded authorized evaluation if possible; review actual evidence and deliver one truthful before/after example. **Status: partial (in_progress): full replay executed; case16 technical failure and quality defects remain.** Route: delegated verifier `mvzvcnh9-a-vnbm` (tests/external evaluation). Record unavailable checks and separate new model labels from human ground truth. Commit: not authorized.

## Acceptance and checks
- Only the two prompt constants change in existing production file; new tests inspect actual injected planning/synthesis instructions via scripted IChatClient.
- Existing isolation, question fidelity, bounded batch, exact schemas/citation IDs and read-only behavior regressions remain passing.
- Fixed judge rubric/source/built copy and original dataset/labels remain unchanged; separate derived artifacts have unique paths and provenance.
- All 17 selected questions round-trip exactly with source identity metadata; no accidental 50-row run.
- Report successful/refused/error counts and costs separately from correctness. Independently inspect the example's cited evidence; do not rely only on weak judge.
- Final example quotes an actual old/new response and clearly distinguishes planning-only, frozen-evidence synthesis or full search rerun. No fabricated improvement or remote form submission.

## Progress and evidence
- Scope chosen by explicit user selection `search_agent_only`; previous judge-iteration T4/T5 canceled before source edits or paid runs.
- Read-only explorer `mvzuwkt6-8-8op3` mapped prompt/test seams, exact-17 derivation and same-scope preflight.
- S1 worker `mvzv48pf-9-trpf` completed only two prompt constants and new SearchEntriesPromptContractTests.cs. Structural RED15 failed/82 passed, GREEN97 passed/0 failed; safe evaluator build0 warnings/errors and whitespace check passed. Initial no-restore missing-assets NETSDK1004 resolved by authorized safe restore.
- Worker confirmed all bytes outside the two constants match prewrite snapshot; original source/labels unchanged. Fixed judge snapshot/source/all four built copies retain SHA256 `4d9d3788a647bdeed81004c7b10391e8c04c4235f9d098e9fb1eef95a77c0992`.
- Native ASSESS after S1 again returned unassessable/high for ambient untracked files and required independent verification.
- Independent verifier `mvzvcnh9-a-vnbm` passed167 focused/regression tests (97 search+70 judge), evaluator build0 warnings/errors, whitespace and preservation checks. Judge fixed rubric/hash and original data/labels unchanged.
- Prepared external artifacts at `C:/Data/formacion/TimelineSpecifications/search-agent-prompt-iteration-20261102T231101Z-f1468fb6/`: derived-17.csv (exact questions/fields round-trip with lineage), corrected-OLD-labels.csv (only1/3/17 Good->Bad plus required annotation metadata), provenance/preservation/verification, corrected-baseline.json and honest comparison/example placeholders. Retained failed-preparation diagnostics/partial artifacts; no overwrites.
- Corrected OLD baseline independently scored TP6 FP3 FN2 TN6, accuracy12/17=70.59%, precision6/9=66.67%, recall6/8=75.00%. This is relabeling, not model/prompt improvement.
- Blocking environment: process ConnectionStrings__ProjectTimeline absent; process OPENAI_API_KEY absent but one authorized launchSettings OpenAI key exists. That launchSettings has no exact DB environment property matching ConnectionStrings__ProjectTimeline. No alternative credential stores consulted, preflight not executed, zero new paid calls and no replacement answers. No fixed example can yet be claimed.
- Full offline suite and UI/browser tests not executed.

## Live iteration evidence
- Continuation verifier `mvzvqnhr-b-a7u9` safely mapped Program.cs:19–21 to ConnectionStrings:ProjectTimeline in user-authorized appsettings.json; one matching DB connection and one distinct authorized launchSettings OpenAI key. Only child environments used; credentials/config and prior environment unchanged.
- Same-scope read-only preflight passed:17 questions,8 entries,40 accepted-valid facts.
- Exactly one paid search replay:17 processed,16 returned,1 failed;14 Partial,1 Complete,1 Refused.33 model calls, estimated $0.02792100,82.8s. No retries/timeouts. Exit2 from one technical failure.
- Case16 failed SynthesisUnavailable / DuplicateContextCitations, no answer released. This remains unresolved and S2 is not checked off as wholly successful.
- Fixed judge performed16 real calls (one NoAnswer error for failed search),13 predicted good/3 bad, estimated $0.01794690,25.3s. Combined estimated cost $0.04586790. Predictions are unvalidated; no new correctness accuracy/precision/recall claimed.
- Verified full-search improvement case5: old answer 'This request cannot be answered by read-only timeline fact search.' New answer includes 'The user can navigate to an existing record from the grid to update (edit) it.' Cited accepted fact383cf24f-e833-406b-8bc5-3639730d1149 states 'From the grid you can delete a record or navigate to one to update it'. Other cited facts support create/consult/delete actions. This is evidence-reviewed observed recovery, not a general correctness claim.
- Unresolved/regressed quality: case6 unsupported consult-grid restriction,8 User/UserRoles identifier confusion,11 false refusal persists,9 unsupported IsAdmin certainty, plus unsupported claims7/12/14/15. Judge falsely approves some of these, so its good counts are not ground truth.
- All17 source-lineage/question joins passed;40 overlapping accepted facts and7 source fingerprints match. Live data/stochastic variation still prevents clean prompt-only causal attribution.
- Live artifact directory: `C:/Data/formacion/TimelineSpecifications/search-agent-prompt-iteration-20261102T231101Z-f1468fb6/replay-20261102T231930Z-75d2ae90/`. Includes output.jsonl, unlabeled-judge.jsonl, comparison-live.json/md, independent-evidence-review.json, lineage-cost-evidence.json, final-preservation.json and english-form-example.md.
- Generated .NET judge harness built0 warnings/errors. Prior independent167 passing tests were not repeated after unchanged source. Broad suite/UI/browser not run. Parent read back form example/comparison and repeated clean whitespace check.
- Original source/labels, derived input artifacts, credential files and judge rubric/source/four built copies unchanged; fixed rubric SHA256 remains4d9d3788a647bdeed81004c7b10391e8c04c4235f9d098e9fb1eef95a77c0992. No source fixes after replay, native review(RDDoff), commits, push or remote form submission.

## Next step
Deliver the observed case5 before/after and honest remaining failures. No additional paid run is queued or authorized automatically. Human review/new labels are needed for replacement answers; a separately scoped fix is needed for duplicate context citations and remaining semantic issues. Keep S2 partial because one functional replay failed; do not describe this as full correctness or as a judge-prompt improvement.
