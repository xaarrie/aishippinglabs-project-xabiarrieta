# .NET Evaluation Labeler

## Objective
Build a local .NET web labeling tool for the existing agent-evaluation JSONL files: show the exact question and full response, label Good/Bad with one click, select a failure category for Bad, and durably save/resume human labels in CSV.

## Problem and reference
The project has complete evaluation artifacts but no human-labeling UI. Latest authoritative input is `C:/Data/formacion/TimelineSpecifications/search-facts-evaluation-citation-allowlists-20261102T062417Z-f35d9311.jsonl` (50 outcomes:25Refused/23Partial/2Complete). These statuses are not human correctness labels.

The user-provided `documentation-agent/evals/label_evals.py` URL no longer contains the file. Repository README and contents identify the current manual labeling app at https://github.com/alexeygrigorev/ai-engineering-buildcamp-code/blob/main/documentation-agent/evals/manual/app.py . Parent read the full current reference: file selection, question/answer, expandable details, progress, next unlabeled/resume, human decision and persistence. Adapt the workflow in .NET, not its Python dependencies, unsafe HTML rendering, destructive reset or automated-judge integrations.

## Scope and decisions
- Add standalone `ProjectTimeline.EvaluationLabeler` ASP.NET Core Razor Pages project to the .NET solution, with no BusinessLogic/DataAccess/model/DB dependencies. Existing production WebApi migrates/seeds and must not host this tool.
- CLI `--input <JSONL>`, optional `--labels <CSV>` defaulting to the same dataset basename plus `.labels.csv`, and optional validated local port. Bind loopback only regardless of ambient URL configuration. No remote assets, outbound agent/API/DB requests, credentials, automatic grading or automatic labels.
- Load generic JSON, preserving full response objects and existing optional metadata. Show exact untruncated question and `response.content`, expandable complete response JSON, saved messages, and errors/diagnostics for legacy failed rows. Clearly distinguish no final response from an empty successful answer. HTML-encode all question/model/source text; no executable HTML/Markdown or automatic remote fetching.
- Navigation: Previous/Next, jump list, labeled/total and pending-category progress, default resume at first unlabeled then Uncategorized Bad. All saved labels remain editable. No destructive reset is needed.
- Good/Bad submit and persist immediately without an extra Save button. Bad initially saves `uncategorized` and focuses/enables the dropdown; category changes autosave. Good clears category. Pending category is visibly incomplete, not silently assigned hallucination. Do not auto-advance before the user can categorize Bad; explicit navigation remains available after successful save.
- Fixed initial category codes: `uncategorized`, `hallucination`, `wrong-scope`, `incomplete`, `incorrect-answer`, `wrong-citation`, `unnecessary-refusal`, `technical-error`, `other`. Server validates labels/category combinations; client input never controls paths, questions or record metadata.
- Stable label identity: result-dataset byte SHA256 plus `(runId, scenarioId)`, with ordinal for display. Existing inputSha256 hashes the original question CSV and is NOT the result-dataset identity. Duplicate question text must not merge labels.
- CSV stores a version and dataset/run/scenario identity, ordinal, question, canonical label/category, revision and UTC update time. Proper UTF8/RFC4180 quote/newline handling; neutralize spreadsheet formulas in metadata text if needed, documenting that JSONL remains exact source. Never duplicate huge full response cells in label CSV merely to join data.
- Validate entire JSONL/CSV and reject malformed/duplicate/unknown/mismatched identities, bad enums/revisions and file path collisions before serving or overwriting. Preserve existing labels/evaluation artifacts on any error. No labels may be pre-populated by the assistant.
- Durable persistence: serialize writes, flush same-directory temporary CSV, atomically replace, then publish new in-memory revision. Exclusive sidecar process lock prevents two labeler processes from overwriting the same labels. Per-record optimistic revision prevents stale tabs from silently replacing decisions. Detect externally changed CSV before saving and fail safely instead of overwriting edits. No success indicator on failed/uncertain saves; clear recovery message.
- Security proportionate to sensitive local results: loopback/Host restriction, same-origin/antiforgery POSTs, no permissive CORS or arbitrary path endpoints. Avoid global/user-profile setup when a local ephemeral anti-forgery key provider suffices; no enterprise auth feature.
- Only the new project, two labeler test files, one test-project reference and solution registration. No existing agent/evaluator/client/DB behavior changes, no package/version churn, no root README or unrelated dirty-file edits. New project README provides a copy-paste command for the actual dataset, CSV path, categories, resume/error behavior and reference attribution.
- Existing branch `feat/search-facts-evaluation-runner`, HEAD `0c98e53b58d37c7b7615468e06c2937594f437a1`; preserve all prior uncommitted features. User has not explicitly requested commits: no commit/push/PR/merge; work-unit commit evidence remains pending explicit authorization per safety policy.
- RDD globally off. Read-only ASSESS after writer, independent high-risk verification if unassessable due existing untracked declaration. No review authority/switch changes.

## Tasks
- [x] T1 — Implement the standalone labeler, safe full-response display, one-click controls, durable CSV store, tests and usage documentation. Verified: 93 focused tests, 1,673 deterministic .NET tests, solution build and CLI help. No commit authorized.
- [x] T2 — Independently verify persistence, security, compatibility, deterministic suites and build. Same checks passed independently; no blocking findings. No commit authorized.
- [x] T3 — Browser-test real rendering, labeling/categories, reload/restart/resume, conflicts, long text and encoded markup using separate test labels. All 94 browser checks and final preservation checks passed; owned services and tabs closed.
- [x] T4 — Confirm final data preservation and exact real-data launch/CSV handoff. Final independent validator, built-DLL help, README/CLI readback and Git-state checks passed. Real annotation CSV remains absent; no production service or real annotation store was started.

## Acceptance and verification
- Latest real JSONL loads exactly50 records; all original data remains read-only. Question, narrative, full response JSON and optional raw details are accessible without truncation. Refused/Partial/Complete metadata is not displayed as a human Good/Bad decision.
- Good/Bad is one click and immediately survives reload; bad category dropdown autosaves, Good removes stale failure category, edited labels survive application restart. First-unlabeled and pending-category navigation resumes correctly; no extra Save click or auto-labeling.
- CSV parser/writer round-trips commas, quotes, CR/LF, Unicode; duplicate questions stay separate by identity. Wrong dataset CSV, invalid/malformed rows, concurrent processes, stale tab, external file change and failed writes never silently discard existing work. In-memory state updates only after a durable successful write.
- HTTP tests run with existing Microsoft.AspNetCore.TestHost10.0.0 and xUnit2.9.3; no extra packages are expected. Antiforgery/origin/Host/path attacks rejected; untrusted markup encoded and no secrets/arbitrary exceptions in UI diagnostics.
- Applicable tests use observed feature-specific RED→GREEN, then focused and full deterministic .NET suites excluding gated LiveModel/PostgreSQL fixtures, and solution build with SkipClientBuild. No paid agent run is part of this feature.
- Browser tests use actual app with separate ignored labels path under `dotnet/tests/ProjectTimeline.Tests/bin/labeler-browser/**`; never fake human labels in the real output `.labels.csv`. Use real50-row dataset read-only plus synthetic long/HTML-shaped fixtures when needed. Verify displayed full text, label/category changes, restart recovery, saved CSV content, and no arbitrary script execution.
- If a real ready local UI is left running, it must use the actual dataset and real target CSV without writing any annotation until the human clicks. Stop only owned test services/tabs; never kill unrelated servers. Otherwise provide exact one-command launch instructions.
- New technical artifacts/UI/docs in English. No package installs or browser installs without necessity/authorization; use existing shared framework and parent MCP browser access.

## Routing and workload
- T1 one foreground gentle-ai-worker: mandatory multi-file/preparation route; generic verifier stays read-only and executes only parent-authorized commands. Single source writer, no parallel edits.
- T2 independent gentle-ai-verify according to ASSESS; builds/test outputs only ignored test bin.
- T3 parent bounded MCP browser actions over an owned loopback app; a worker may prepare only ignored test fixture/launcher files if the verifier cannot author them.
- T4 independent readback/hash/CSV validation and parent concise handoff. No semantic grading or paid evaluation reruns.
- Forecast approximately900–1300 authored changed lines including UI, persistence/HTTP tests and docs. This is a cohesive standalone-tool scope directly requested, not a multi-area agent refactor; keep existing production untouched. Advisory, not code-golf: preserve tests/readability and report honest overage.
- Delivery strategy ask-on-risk; no delivery/commit authorized. Any later PR/splitting decision remains separate.

## Progress and evidence

### T1 — Implementation
- Initial worker `mvzhye3i-13-ycdt` failed after 34 turns/40 calls with only "assistant reported an error". Its earlier test lifecycle is unknown. Partial files were preserved and inspected; no historical RED/GREEN was invented.
- A fresh writer completed the existing standalone implementation. It fixed an inherited `IFormCollection.Values` compilation issue separately from behavioral RED, added selective reversible spreadsheet escaping with legacy decoding, CSV download, documentation and real-process serving tests.
- Observed behavioral RED: raw opaque IDs incorrectly acquired an apostrophe prefix (68 passed/1 failed); invalid download behavior returned 404 instead of the expected 400 (91 passed/1 failed). Final focused GREEN: **93/93**.
- Ordinary run/scenario IDs and questions remain directly joinable. Formula-shaped or leading-apostrophe metadata is reversibly escaped; legacy always-prefixed v1 CSVs resume correctly.
- Full deterministic .NET suite: **1,673/1,673** passed. Restore succeeded; solution build reported **0 warnings/errors**; built-DLL `--help` exited 0. LiveModel and PostgreSQL opt-ins stayed disabled.
- Measured implementation footprint: **1,618 source/test/documentation lines**, above the advisory 900–1,300 forecast. Scope remained a standalone tool; no production agent, evaluator, frontend or DB source was changed. Earlier untracked project-file churn cannot be separately attributed.

### T2 — Independent verification
- Independent verifier repeated **93 focused tests**, **1,673 deterministic .NET tests**, solution build and CLI help successfully; no blocking findings.
- Reviewed byte-SHA identity, opaque IDs, serialized/atomic CSV replacement, sidecar lifecycle, optimistic revisions, observed external-change detection, legacy/formula handling, complete encoded Razor output, Host/Origin/loopback/antiforgery restrictions and isolated startup.
- Native ASSESS was unassessable because of existing undeclared untracked files. Followed its separate independent high-risk verification plan. RDD remained off; no review authority or setting changed.
- Explicit nonblocking coverage limits: no dedicated first-save-failure or failed-load/reopen regression was separately exercised. Source cleanup was reviewed. Uncoordinated external editors cannot participate in a transaction; observed changes are detected, but concurrent manual CSV editing is not a supported coordination mechanism.
- The existing frontend build and live-model/PostgreSQL execution were intentionally excluded. No production service was started and no paid evaluation was rerun.

### T3 — Actual browser and persistence checks
Parent used MCP browser access with isolated contexts; verification helpers and fake annotation files live only under ignored `dotnet/tests/ProjectTimeline.Tests/bin/labeler-browser/`.

| Phase | Observed result |
| --- | --- |
| Main UI | **55/55**: real 50-row input loaded; sampled source questions/narratives/full response and raw JSON match hashes; one-click Good/Bad, category autosave/focus, Good clears category, save-time navigation lock, reload persistence, stale-tab 409, HttpOnly/Strict cookie attributes and no observed external requests. |
| Process restart/resume | **10/10**: root selects the third/first-unlabeled record; first Good/rev1 and second Bad/wrong-scope/rev7 persist; progress 2/50; refreshed antiforgery field; no annotation POSTs. |
| Synthetic safety/full text | **29/29**: exact 41,159-character question, 45,159-character response and 86,616-character raw JSON; literal HTML with no injected markup/script/probe requests; legacy null response explicitly shows no final answer, saved messages/error; zero synthetic annotations. |

- Main test CSV contains **only two fake test decisions**. SHA-256 stayed `40d6c12d91d67be2a65f36e4169f245e1fbe00bfb94c0e537092b82442071984` before/after restart and synthetic checks. Dataset hash, raw run/scenario IDs, questions, categories and revisions were validated directly.
- Real default annotation CSV and synthetic annotation CSV remained **absent**. Real JSONL bytes, 17 strict source/test/docs/wiring hashes, compiled DLL and synthetic input hashes remained unchanged.
- A test-harness false positive initially classified this intentionally mutable ODD document as immutable source. Fixed **only** ignored `verify.py`: exact `odd/tasks/evaluation-labeler.md` variance is audited with baseline/current hashes and not counted unchanged. All other paths remain strict; nine changed-path regression probes were rejected. No baseline/source/data waiver.
- Owned main test service: initial PID21848, restarted as PID11692 on localhost:61116. Synthetic: PID37124 on localhost:61117. `control.mjs stop-all` completed with both running=false after ownership/argument validation, exit, sidecar and IPv4/IPv6 port-release checks. No unrelated process was stopped.
- Both exact owned MCP contexts/tabs were closed; unrelated/default browser tabs were preserved.
- Run evidence: `dotnet/tests/ProjectTimeline.Tests/bin/labeler-browser/run-2026-11-02T18-33-20-266Z-f45560c0-4d71-4b80-b25b-dc1361216cf2/`. Saved after-labeling/restart proof and process metadata are present. Final after-synthetic proof is verified command stdout, **not** a claimed separate proof file.

### Data and delivery invariants
- Real input: `C:/Data/formacion/TimelineSpecifications/search-facts-evaluation-citation-allowlists-20261102T062417Z-f35d9311.jsonl`.
- Real input byte SHA-256: `e136e2f53cd7157e791a0a6ae9baf99daa2b751b92433cc49ff9ae99dcb2d2f2`.
- Real run ID: `5913302c-3f71-4696-b72a-d5bc7796b256`. Research statuses are not human correctness labels.
- CSV header: `version,datasetSha256,runId,scenarioId,ordinal,question,label,failureCategory,revision,updatedUtc`.
- Verified built DLL: `dotnet/tests/ProjectTimeline.Tests/bin/labeler-build/bin/ProjectTimeline.EvaluationLabeler/release/ProjectTimeline.EvaluationLabeler.dll`. It served compiled Razor and local JS/CSS from repository root.
- Default URL: http://localhost:5198/. Labels default to the input basename with `.labels.csv`; no human annotation exists until a human saves one.
- No commits, pushes, PRs, resets, new evaluations or real human labels were created.

### T4 — Final handoff verification
- Final independent after-synthetic preservation validator passed again after test-service shutdown: input SHA/50 rows, 17 strict sources and compiled DLL unchanged; real/synthetic annotation CSVs absent; main test CSV unchanged with only two fixture decisions. Only this progress document changed intentionally.
- Built-DLL help, README autosave/resume/schema/category/external-edit guidance and exact CLI contract were verified. Full suites were not needlessly rerun: their sources and binary are unchanged.
- Branch remains `feat/search-facts-evaluation-runner`; HEAD remains `0c98e53b58d37c7b7615468e06c2937594f437a1`. Existing modified/untracked work was preserved. No commit, push, PR or reset was performed.
- Compiled DLL SHA-256: `7fe2e626844b7e670cee37cc3573977d4b04264e13b35c33e0677c3b140254cb`.

### Small follow-up — Visible scenario type
- User approved showing **Scenario type** in scenario metadata above Question. Read `scenario.fields` by case-insensitive `type` header; preserve exact HTML-encoded nonblank values and show `Not available` for missing/invalid legacy metadata. JSONL, CSV schema, identities and human labels are unchanged.
- Three source/test files changed (+65/-1). Observed RED: 18 missing-span failures. GREEN: 111/111 labeler tests, 1,691/1,691 deterministic .NET tests, solution build with zero warnings/errors and CLI help. Independent verification repeated 111/111, three consecutive Unicode CSV regression passes and help; parent repeated help as a spot check.
- Actual isolated browser checked three synthetic records: `happy_path`, HTML-shaped literal text without execution, and missing-type fallback. Field is visible before Question; zero saves, external requests or probe fetches. Synthetic annotation CSV stayed absent.
- Original running build/user processes and real annotations were not accessed or restarted. Updated DLL is in `bin/scenario-type-build`; use the updated command below, not the previous `labeler-build` binary.
- One existing synthetic Unicode CSV test reported `save-failed` once during implementation; later focused/full and three independent isolated runs passed. The cause remains undiagnosed, not claimed fixed. An auxiliary cleanup probe incorrectly expected the persistent `.lock` file to disappear; controller exit/sidecar/port-release checks passed, and persistent sidecars are intentional.
- Owned browser PID17900 on localhost:61118 stopped at `2026-11-02T21:49:58.807Z`; main placeholder never started, and the exact owned MCP context was closed. Copied safety controller matched its reference byte-for-byte. Runtime evidence is under `dotnet/tests/ProjectTimeline.Tests/bin/scenario-type-browser/run-b6ff1cdd-a02f-402a-8098-0d84ffc3b50e/`.
- ASSESS remained unavailable due existing untracked-file declarations; independent fallback completed. Live-model/PostgreSQL and existing frontend build remained excluded. No commits or evaluation reruns.

## Human handoff
Stop the previous labeler yourself if it is running, then from the repository root run:

```sh
dotnet dotnet/tests/ProjectTimeline.Tests/bin/scenario-type-build/bin/ProjectTimeline.EvaluationLabeler/release/ProjectTimeline.EvaluationLabeler.dll --input "C:/Data/formacion/TimelineSpecifications/search-facts-evaluation-citation-allowlists-20261102T062417Z-f35d9311.jsonl"
```

Open http://localhost:5198/. Preserve any previous `--labels` override to resume that same CSV. The app saves each human decision immediately and resumes after restart. Default output:

```text
C:/Data/formacion/TimelineSpecifications/search-facts-evaluation-citation-allowlists-20261102T062417Z-f35d9311.labels.csv
```

Usage and recovery guidance: `dotnet/src/ProjectTimeline.EvaluationLabeler/README.md`. Stop the app before externally editing its CSV; a page reload does not import manual file edits. Test services remain stopped. No pending implementation/check task; human labeling and any future Git delivery are the user's decisions.
