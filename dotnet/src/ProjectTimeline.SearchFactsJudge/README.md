# Calibrate a judge against saved search responses

This .NET 10 console evaluates **already captured answers**, not a new search run. It joins the supplied labeler CSV to its source JSONL, judges every labeled row once, and exports inspectable disagreements. It does not access the database or relabel the source files. Results are **calibration, not held-out evaluation**: the human annotations informed the rubric.

## Quick path

1. Run `--help` without credentials:

   ```powershell
   dotnet run --project dotnet/src/ProjectTimeline.SearchFactsJudge/ProjectTimeline.SearchFactsJudge.csproj -c Release -p:SkipClientBuild=true --artifacts-path dotnet/tests/ProjectTimeline.Tests/bin/search-judge-build -- --help
   ```

2. For an **authorized paid run**, provision `OPENAI_API_KEY` in the launching PowerShell process through your normal secure tooling. Do not paste it into arguments, source files, or reports. The following reproducible command uses the real calibration inputs; it has not been executed as part of implementation:

   ```powershell
   if ([string]::IsNullOrWhiteSpace($env:OPENAI_API_KEY)) {
       throw 'OPENAI_API_KEY must be supplied to this launching process.'
   }
   $stem = 'C:/Data/formacion/TimelineSpecifications/search-facts-evaluation-citation-allowlists-20261102T062417Z-f35d9311'
   $out = "${stem}.judge-$(Get-Date -Format 'yyyyMMddTHHmmssfffZ')"
   dotnet run --project dotnet/src/ProjectTimeline.SearchFactsJudge/ProjectTimeline.SearchFactsJudge.csproj `
       -c Release -p:SkipClientBuild=true `
       --artifacts-path dotnet/tests/ProjectTimeline.Tests/bin/search-judge-build -- `
       --input "${stem}.jsonl" --labels "${stem}.labels.csv" --output $out `
       --expected-sha256 e136e2f53cd7157e791a0a6ae9baf99daa2b751b92433cc49ff9ae99dcb2d2f2
   ```

3. Check final coverage and `run.json` status, then inspect `disagreements.md`. No measured live metrics are claimed here. A missing key fails before model creation or output creation; help needs no key.

## CLI contract

Run from the repository root. All paths are resolved to absolute paths. The output directory must not exist, and its parent must already exist.

| Argument | Meaning / default |
|---|---|
| `--input <jsonl>` | Required saved source responses. |
| `--output <directory>` | Required new report directory; no overwrite or resume. |
| `--labels <csv>` | Defaults to the input's sibling `.labels.csv`. Only this file is loaded; no synthetic annotation discovery. |
| `--model <id>` | Defaults to `gpt-4o-mini`. |
| `--prompt <md>` | Defaults to copied `Prompts/SearchResponseJudge.md` beside the executable. |
| `--expected-sha256 <64 hex>` | Optional additional pin for the source dataset. CSV dataset hashes are always checked. |
| `--timeout-seconds <1..600>` | Per-row deadline; default 60 seconds. |
| `--help` | Prints usage without reading credentials or input files. |

Only the launching-process `OPENAI_API_KEY` is used and explicitly passed to `SearchResponseJudge.CreateOpenAI`. There is **no automatic `.env`, appsettings, or user-secret loading**, no key argument, and no mock production fallback. Content telemetry is disabled; console progress contains counts/statuses, never credentials or provider exception details. Exported answers/evidence are still potentially sensitive data; store reports accordingly.

Exit **0** means all labels were successfully judged, even if there are disagreements. Exit **1** means row errors or cancellation/incompleteness. Exit **2** means arguments, credentials, preflight, or output/execution failure. A reservation or disk failure may leave a partial new directory; inspect it and use another new directory, rather than overwriting it.

## Output and schema

All five files are reserved with create-new semantics before the first judge invocation. The rubric snapshot is flushed before it is passed to the core. Predictions are appended and flushed after each row, before the next attempt.

| File | Contents |
|---|---|
| `predictions.jsonl` | One terminal record per label: `identity` (dataset SHA/run/scenario/ordinal), original `question`/`answer`, safe `responseEvidence`/`toolEvidence`, separate `human` annotation, `status`, and either `judgment` or `errorCode`. |
| `metrics.json` | Counts, coverage, confusion matrix, nullable ratios, and `evaluationKind: CalibrationNotHeldOut`. |
| `disagreements.md` | Every successfully judged disagreement, with full question/answer, original identity, human label/category, and judge label/reasoning. Errors are not disagreements. Text is rendered as literal blocks. |
| `rubric.md` | Exact bytes of the rubric actually used; edits to the original prompt cannot change this run. |
| `run.json` | Schema version, model, timeout, dataset/labels/prompt SHA256, timestamps, and status (`Running`, `Completed`, `CompletedWithErrors`, or `Canceled`). |

Structured model output has exactly two required fields: `reasoning` (nonblank brief grounded rationale, at most 2000 characters) and `label` (`good` or `bad`), with no extra fields. Malformed, refused, truncated, or invalid output becomes a coded error, never a forced `bad`.

Rows are ordered by run ID (ordinal string comparison), original ordinal, then scenario ID. There is one attempt per row, no retries. Row failures do not stop later rows. Ctrl+C cancels the active attempt; it is recorded as `Canceled`, with remaining rows `NotAttempted`. Cancellation before the first attempt leaves all rows `NotAttempted`. Graceful cancellation still writes every bookkeeping record and final reports; abrupt process termination or disk failure cannot guarantee finalization (`Running` is not completion).

## Metrics: bad is positive

Only `Success` rows enter the confusion matrix and quality denominators. Human labels retain their original `Good`/`Bad` casing; model labels are lowercase.

| Measure | Definition |
|---|---|
| TP / FP | Judge says bad; human says Bad / Good. |
| FN / TN | Judge says good; human says Bad / Good. |
| Accuracy | `(TP + TN) / success` |
| PrecisionBad | `TP / (TP + FP)` |
| RecallBad | `TP / (TP + FN)` |
| Judging coverage (`coverage`) | `success / totalLabels` |
| Label coverage (`labelCoverage`) | `totalLabels / totalSource` |

Counts include total source, total labels, unlabeled, attempted, success, error, canceled, and not-attempted. `attempted = success + error + canceled`; all labels are accounted for by those statuses plus not-attempted. Errors reduce judging coverage but are not silently counted as quality failures or disagreements. A zero denominator is JSON `null` and displayed as `undefined` with an explanation. Zero successful judgments is not measured 0% accuracy.

## Rubric rationale and tuning

The external rubric generalizes the observed failure classes: unjustified refusal of specification questions, entity/field/relationship distortion, instance-to-universal overgeneralization, and semantically irrelevant evidence dressed as requirements. Citation membership is not entailment. Accurate partial answers or appropriate refusals can be good; execution statuses are not quality labels. No row-specific entities, answers, or ordinals belong in the system rubric.

The judge receives only legitimate question/answer and allowlisted response/source/tool evidence, including source-domain `clarificationEvidence`. Human labels/categories, scenario types, expected answers, and evaluation comments stay outside model context. Embedded instructions in the evidence remain data, not authority. Dataset SHA, run/scenario identities, ordinal, and exact question are validated at the read-only CSV join; reversible spreadsheet metadata escaping is preserved.

To tune: inspect disagreements alongside safe evidence in predictions, revise generalized criteria in an external prompt, and rerun with `--prompt` into a new directory. Compare model/prompt/dataset/labels hashes and coverage before comparing ratios. Do not add expected row answers to improve agreement. Use independently held-out labels before making generalization claims.

Reference: [documentation-agent manual evaluation](https://github.com/alexeygrigorev/ai-engineering-buildcamp-code/tree/main/documentation-agent/evals), especially `manual/judge.py` (`reasoning` + `good|bad`) and `manual/run_judge.py` (bad-positive metrics). Unlike error-as-bad approaches, this runner reports failures separately.

## Offline verification

```powershell
dotnet test dotnet/tests/ProjectTimeline.Tests/ProjectTimeline.Tests.csproj -c Release -p:SkipClientBuild=true --artifacts-path dotnet/tests/ProjectTimeline.Tests/bin/search-judge-build --filter 'FullyQualifiedName~SearchResponseJudge' --no-restore
```

`SearchJudgeRunner.RunAsync(command, judgeDelegate, progress, cancellationToken)` is the focused reusable runner seam. Its delegate receives safe `SearchJudgeInput`, the reserved rubric-snapshot path, and cancellation; deterministic tests never need model credentials. `SearchJudgeConsole.RunAsync` owns CLI parsing and the launching-process credential boundary.
