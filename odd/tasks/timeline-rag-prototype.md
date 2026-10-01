# ODD Task: Timeline RAG Prototype

## Objective
Build a bounded RAG prototype over local synthetic, time-aware project records and demonstrate an evidence-grounded answer about an inconsistency between the latest objective and specification.

## Problem
The course notebook currently indexes inline setup examples, so it cannot answer timeline questions over dated meetings, objective versions, specification versions, and lifecycle snapshots.

## Why
A compact local dataset keeps the prototype reproducible within the 30-minute scope while making dated facts, supersession, and one unresolved inconsistency visible to retrieval and the model.

## Scope
- Add `data/project_records.json` with realistic project timeline records.
- Update `notebooks/02-rag.ipynb` to load that file relative to the notebook, configure MinSearch, preserve the course function call chain, and query the inconsistency.
- Verify deterministic notebook cells, generated notebook output, required function names, final invocation, diff hygiene, and commit evidence.

## Constraints
- Do not modify `.gitignore`, `.atl/`, README, `pyproject.toml`, or unrelated files.
- Use English technical artifacts and notebook strings.
- Use no credentials that are not already configured; do not invent credentials.
- Commit only the data file, notebook, and this task document in one conventional commit.
- Do not push or open a pull request.
- Preserve pre-existing user changes and stage only the authorized files.

## Stable Tasks
- [x] TLR-001 Inspect branch/status, course reference, and target notebook. Confirmed clean `main`, created `feat/timeline-rag-prototype`, and inspected both references.
- [x] TLR-002 Create local synthetic timeline records with one dated inconsistency. Added eight records covering meetings, objective versions, specification versions, and lifecycle snapshots; the offline-first/cloud-sync conflict remains unresolved.
- [x] TLR-003 Update the notebook data loading, index fields, instructions, prompt metadata, and representative query. The loader resolves the notebook path with `pathlib` and searches ancestor data directories, avoiding a fixed process cwd.
- [x] TLR-004 Execute and inspect the notebook without overwriting source. `uv run jupyter nbconvert ...` succeeded; the generated notebook returned an evidence-grounded answer and no LLM credential/network blocker occurred.
- [x] TLR-005 Convert/inspect cells for required functions and final invocation; run diff checks and inspect status. Script conversion showed all four functions and the representative inconsistency query; final diff checks are recorded below.
- [ ] TLR-006 Update this task and Engram mirror with outcomes, commit SHA, and rollback boundary; commit the three authorized files. Commit SHA remains pending until the final single work-unit commit.

## Acceptance Criteria
- `data/project_records.json` exists and every record has `id`, `title`, `content`, `record_type`, `occurred_at`, `project_id`, `version`, `source`, and `supersedes_id`.
- The dataset includes meetings, objective versions, specification versions, lifecycle snapshots, and one explicit dated inconsistency or unresolved status.
- The notebook loads data relative to its own location using `pathlib`, not process cwd assumptions.
- MinSearch uses text fields `title`, `content` and keyword fields `record_type`, `project_id`, `source`.
- Standalone `search`, `build_prompt`, `llm`, and `rag` functions remain and `rag` calls them in course order.
- Instructions require context-only answers, dated fact/inconsistency distinction, and an explicit unknown response when context is insufficient; they do not claim automatic conflict resolution.
- Prompt context exposes record dates, types, versions, and supersession metadata while retaining `<QUESTION>` and `<CONTEXT>`.
- The final notebook cell queries the latest objective/specification inconsistency.
- Verification results, exact LLM failure if applicable, commit SHA, and rollback boundary are recorded here and mirrored in Engram.

## Checks
- `uv run jupyter nbconvert --to notebook --execute --output 02-rag.executed.ipynb notebooks/02-rag.ipynb` (or equivalent outside source).
- Inspect generated output and report the representative answer or exact credential/network blocker.
- Convert notebook to script or inspect cells for `search`, `build_prompt`, `llm`, `rag`, and final invocation.
- `git diff --check`.
- `git diff --stat`, `git diff --cached --stat`, and final `git status --short --branch`.

## Forecast
Small single work unit; expected authored change is below the 400-line review budget. Runtime risk is the external LLM call and available API credentials.

## Delivery Strategy
`ask-on-risk`; do not split unless the forecast or actual authored diff exceeds the review budget or a material risk requires a bounded decision.

## Task Route / Trigger Evidence
- Route: bounded implementation worker for an authorized local RAG prototype.
- Trigger evidence: user explicitly required a local JSON source, notebook updates, ODD tracking, verification, and one conventional commit.
- Course evidence: `C:\Data\formacion\ai-engineering-buildcamp\01-Foundation\02-rag\rag.py` demonstrates MinSearch indexing and the search/build-prompt/LLM/RAG flow; `notebooks/02-rag.ipynb` is the target starter implementation.

## TDD Mode
Unknown. No repository configuration or explicit test harness was inspected that proves a TDD mode; verification will use the requested notebook execution and structural checks.

## Verification Outcomes
- Notebook execution: PASS. Output artifact was written outside the repository at `C:\Users\biarunxa\AppData\Local\Temp\opencode\02-rag.executed.ipynb`.
- Representative answer: Objective v3 (2026-03-17) prioritizes offline-first evidence review; Specification v2 (2026-03-12) requires cloud synchronization by 2026-03-18; the timeline does not establish a resolution.
- First execution exposed a path-resolution failure (`FileNotFoundError` at `notebooks\\data\\project_records.json`); the loader was corrected to search from the resolved notebook path through its ancestors, and the rerun passed.
- Structural inspection: PASS. Notebook-to-script conversion showed standalone `search`, `build_prompt`, `llm`, `rag`, and the final `rag` invocation.
- Diff/status checks: pending final staged inspection and commit.

## Rollback Boundary
Remove exactly `data/project_records.json`, the changes to `notebooks/02-rag.ipynb`, and `odd/tasks/timeline-rag-prototype.md` from the single feature commit; no unrelated files are included.

## Pending Verification
- [ ] Final staged diff and `git diff --check` recorded.
- [ ] Final commit SHA recorded in the delivery evidence and Engram mirror.
- [ ] Engram mirror read back and confirmed equivalent to this document after final commit.
