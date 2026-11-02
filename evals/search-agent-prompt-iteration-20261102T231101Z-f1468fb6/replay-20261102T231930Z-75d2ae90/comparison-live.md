# S2 live result: SEARCH-agent prompt calibration, fixed judge

## Authorization and execution
SEARCH planning/synthesis prompts changed, NOT judge prompt. Model gpt-4o-mini unchanged. No source/config/ODD edits, commits, nested agents, service startup, database writes or remote submission.
Program.cs:19-21 names ConnectionStrings:ProjectTimeline. Authorized appsettings.json has one matching app DB connection; authorized launchSettings.json has one distinct OpenAI key. Child-only environment injection; parent environment unchanged. No credentials displayed/stored. Credential files unchanged; artifact secret scan zero matches.
Preflight exit0: exact historical actor0e6047b4-385d-4d7c-bbcf-4926e7c8fc70, timeline76ba87c5-71a4-4bf6-a87b-d1a40a3ec86c, project8d3559af-77d8-476d-b763-5f085889861d;8entries/40accepted-validfacts. Actual evaluator parsed17rows. Input copy is byte-identical, required by adjacent-path rule.
ProductionBackend.cs:22-29: NoTracking, SET TRANSACTION READ ONLY, never commit. One sequential replay, no retry,1800secondbound;82.78seconds, exit2:17persisted,16Returned/1Failed,0cancelled/unstarted. Research statuses14Partial/1Complete/1Refused/1NoResponse. Case16 SynthesisUnavailable/DuplicateContextCitations after2calls; no answer released.
Search33calls,160144input/6499outputtokens, estimated USD0.02792100. Case11used one refusal call. Usage/cost complete.

## Fixed unlabeled judge and metrics boundary
Generated .NET harness references existing built judge, calling Dataset.Load, SearchJudgeInput.FromRecord, SearchResponseJudge.CreateOpenAI/JudgeAsync. No synthetic/copied human labels. Harness build0warnings/errors; cleared package sources, existing assemblies only. One pass:16successful predictions=13good/3bad(7,11,13);1NoAnswer error(16), no provider call for missing answer. Judge16calls,112954input/1673outputtokens, estimated USD0.01794690;25.28seconds, exit0. No retry.
Combined49providercalls, estimated USD0.04586790, not an invoice. NEW accuracy/precision/recall unavailable until human labeling. Corrected OLD baseline separately remainsTP6 FP3 FN2 TN6, accuracy12/17, precisionBad6/9, recallBad6/8. OLD1/3/17 relabeling is not prompt improvement.

## Verified before/after: source ordinal5
Question: Which actions should the initial maintenance grid provide for creating, editing, consulting, and deleting records?
Before: “This request cannot be answered by read-only timeline fact search.”
After exact excerpt: “The user can navigate to an existing record from the grid to update (edit) it.”
Decisive source quote: “From the grid you can delete a record or navigate to one to update it”. Accepted fact383cf24f-e833-406b-8bc5-3639730d1149 directly supports this. New answer also covers create button, existing-data consultation and deletion, supported by5f663b21-1774-49f7-b87d-29bdb4535987 and0e45e929-4a41-4da9-9c39-af89c4130cc5. Context entry4/evidenceIdde3884e6-7e9a-44b4-87eb-0680d6e61401; fingerprintB15944497A661C61588851191ACCC3D26C16DF00184C68AC84C3F1D328FDD244. See original JSONL:5 and new output.jsonl:5. Independently checked; judge good not taken as proof.
Generalized delta: descriptive CRUD/specification questions are informational, not execution requests; exact entailment and supported partial answers required. One observed full-search improvement, not universal correctness or judge-prompt improvement.

## Unresolved and regressions
6now answers but invents consult-grid absence of create/edit options; cited grid actually offers actions. Judge good misses this.
8still infers immutable UserId from UserRoles lacking UserId, mixing entities/constraints. Admin-state generalization remains. Judge good misses it again.
11repeats old refusal.16fails runtime citation validation: no released answer, not a semantic fix; delivery regression from former returned-but-hallucinated answer.
9removes prior explicit uncertainty about IsAdmin despite cited facts not establishing its meaning: uncertainty-handling regression.
7/12/14/15retain unsupported permission/entity/cardinality assertions. Old answers were similarly problematic; old Good labels alone do not establish new regressions.13removes unsupported Admin-only assertion but still extrapolates generic maintenance to participant management.
Five of six former refusals now release answers, not necessarily correct. Partial/status is not quality. Independent review notes are not human truth annotations; exact answers/citations in independent-evidence-review.json.

## Preservation and limits
Exact17question/field/identity/scope joins hold; all new run/scenario IDs differ. All40observed fact records overlap unchanged; all7overlapping context entry fingerprints unchanged. Live DB is not frozen historical data: query differences and stochastic variation prevent prompt-only causal attribution.
Original dataset/labels, prepared/corrected CSVs, source/tests, judge source and credential files unchanged. Fixed rubric snapshot/source/four built copies retain4d9d3788a647bdeed81004c7b10391e8c04c4235f9d098e9fb1eef95a77c0992. Prior167tests/build not repeated; broad suite/UI/browser not run. Earlier reports/placeholders/diagnostics untouched. Exact argv/results are in preflight.json, paid-search-attempt.json, paid-judge-attempt.json, command-evidence.json and process-result files. No paid retry/fix attempted.
