# S2 SEARCH-agent prompt calibration — replay blocked

## Observed verification
Exact authorized test command: 167 passed, zero failures/skips. Evaluator build: zero warnings/errors. diff --check: exit0. Exact commands and observed test/build output: commands-observed.json.
Worker transcript shows structural RED15 Assert.Contains failures/82existing passes, then GREEN97/97. Current production bytes outside the two prompt contents match the worker prewrite masked hash; new test matches worker-written bytes. Git status matches worker final. Ambient worktree not attributed to S1.
Fixed rubric snapshot, source and four built copies match SHA256 4d9d3788a647bdeed81004c7b10391e8c04c4235f9d098e9fb1eef95a77c0992. Historical whole judge-source snapshot was not supplied; no judge edits performed here.
SearchEntriesAgent.cs:37-74 contains generalized prompt deltas; :131/:164 retain gpt-4o-mini. SearchEntriesPromptContractTests.cs:13-40 checks instruction text; :43-96 exercises actual injected options, isolation, evidence and citation shape. These prove contracts/wiring, not model semantic behavior.

## Exact17 preparation and corrected OLD baseline
Original dataset has50 rows, current17 labels. All17 identities/questions validated. Derived17 CSV preserves exact whitespace and original ordered fields, appending five collision-checked source-lineage columns. Python strict CSV roundtrip succeeded; production .NET preflight parser was not executed. provenance.json records mappings/hashes/corrections.
Only OLD1/3/17 Good->Bad, documented incorrect-answer category consistent with prior wrong-refusal labels, revision+1 and timestamp. Original CSV untouched. Offline reuse of unchanged historical fixed-judge predictions: TP6 FP3 FN2 TN6; accuracy12/17=70.5882%, precisionBad6/9=66.6667%, recallBad6/8=75%. NOT prompt improvement. Predictions9bad/8good; historical17/17success; label coverage17/50. Old responses6Refused/11Partial, all17Returned;28model calls; estimated USD0.02033580.

## Credential blocker
Launching process ConnectionStrings__ProjectTimeline=false and OPENAI_API_KEY=false. Authorized launchSettings parse: one distinct OpenAI key; matching DB property name=false. No secret values emitted/stored, no environment mutation/injection, no other secret stores read. Preflight NOT executed; exact historical-scope argv recorded as NOT EXECUTED in comparison.json. Zero new search/judge calls; no new responses, predictions, truth labels or metrics. No retry consumed. Need an authorized launching-process database connection before same-scope preflight can resume.

## Independent OLD-evidence inspection
5/6/11: actual answer “This request cannot be answered by read-only timeline fact search.” These are informational questions; old refusals retrieved/cited no evidence.
8: “the user should ideally hold the Admin role” generalizes cited “In the existing record, the user should have the role Admin.” It also transfers UserRoles' “It should not have the UserId” to User, claiming “User should not be assigned a UserId directly”. Citations do not establish immutable User identifiers. Historical judge missed this hallucination.
16: “proper authorization and navigation are essential before any merging can occur” is not entailed by cited “Timeline maintenance should be accessed from the Project consultation screen.” Neither500-character preview, file types nor conversation display establishes merge review/conflict-resolution requirements. Historical judge missed this hallucination.
Partial/refusal status alone is not correctness. All five remain unresolved for NEW prompts. Exact old answers, cited evidence and source fingerprints: comparison.json.

## Honest conclusion and limitations
No confirmed improvement or truthful before/after pair: no new answer exists. New answers require human labels. Future live data is not frozen historical data; compare source fingerprints before prompt attribution. No broad suite/UI/browser or remote submission.
Three inline script-construction attempts failed syntax parsing before mutation. Initial prepare-offline.py stopped after creating new CSVs because responseEvidence omits content. SearchJudgeInput.cs:29-32 confirms the projection; prepare-offline-followup.py validated answer separately and evidence without content. Failed script and partial outputs retained; no existing artifact overwritten. All final checks and blockers are separate from those resolved preparation diagnostics.
