# ODD Task: Project Memory Timeline

## Objective
Describe the AI Engineering Buildcamp starter as a time-aware project and topic conversation system for evolving project knowledge.

## Problem
Project knowledge is distributed across meeting transcripts, uploaded documents, and changing objectives or specifications. Without a timeline-aware view, users struggle to retrieve relevant context, detect contradictions between lifecycle snapshots, and understand how decisions and requirements evolved.

## Why
The starter documentation and Python distribution metadata should accurately communicate the intended product so future implementation work has a clear, bounded direction without claiming features that do not yet exist.

## Scope
- Rewrite the README problem statement and `What It Does` section in clear English.
- Preserve the README `Setup` and `Notebooks` sections verbatim.
- Update only `name` and `description` in `pyproject.toml`.
- Run `uv sync`.
- Execute `notebooks/01-setup.ipynb` non-interactively to verify Jupyter and the OpenAI client.
- Commit only `README.md`, `pyproject.toml`, and this task file.

## Constraints
- Do not modify `.gitignore`, `.atl/`, or unrelated files.
- Do not invent implemented features beyond the intended product.
- Do not use or invent credentials; report missing credentials honestly.
- Do not push or create a pull request.
- Delivery strategy: `ask-on-risk`.

## Stable Checklist
- [x] Inspect current branch, status, README, and `pyproject.toml`.
- [x] Create feature branch when starting from the default branch.
- [x] Create this ODD task document before source edits.
- [x] Rewrite README project description while preserving `Setup` and `Notebooks` exactly.
- [x] Update only `pyproject.toml` `name` and `description`.
- [x] Run `uv sync` and record the result.
- [x] Execute `notebooks/01-setup.ipynb` with the project environment and record the result.
- [x] Restore notebook source/output if execution changes it; do not commit notebook changes.
- [x] Verify final diff, status, and unchanged README sections.
- [x] Commit only the authorized files with a Conventional Commit.
- [x] Record commit identity and rollback boundary here and in the Engram mirror.

## Acceptance Criteria
- README describes time-aware project/topic conversations, incremental meeting transcripts and documents, RAG over uploaded materials, lifecycle snapshots that detect and surface inconsistencies for completion, and objective/specification evolution tracing.
- README `Setup` and `Notebooks` sections are byte-for-byte unchanged from the inspected baseline.
- `pyproject.toml` changes only the distribution `name` and project `description`, and both match the intended product.
- `uv sync` completes successfully or its exact failure is recorded.
- Notebook execution result states whether Jupyter and the OpenAI client were verified; credential blockers are reported exactly.
- Final commit contains only the three authorized files.

## Checks
- `uv sync`
- `uv run jupyter nbconvert --to notebook --execute --inplace notebooks/01-setup.ipynb`
- Compare the final README `Setup` and `Notebooks` sections against the inspected baseline.
- Inspect `git diff`, `git diff --stat`, and `git status --short --branch`.
- Inspect the final commit file list.

## Delivery Strategy
`ask-on-risk`: stop and ask if an authorized check requires a scope expansion, remote action, credentials, or a modification outside the three intended deliverables and this task file. Do not push or create a PR.

## Forecast
Low implementation risk and small review workload: one documentation/metadata work unit plus required ODD evidence. Expected authored change is below the 400-line review threshold. Primary risk is notebook verification requiring an API credential.

## Task Route and Trigger Evidence
- Route: inspect -> branch -> ODD tracking -> README/metadata update -> dependency sync -> notebook verification -> final diff/preservation audit -> commit -> record commit identity.
- Trigger evidence: user explicitly authorized the README and metadata rewrite, `uv sync`, and execution of `notebooks/01-setup.ipynb`; current branch was `main`, so the feature branch was required before writing.

## TDD Status
Unknown. This task changes documentation and package metadata; no test-first evidence is present in the repository. The setup notebook is a verification check, not a TDD test suite.

## Verification

### Initially Pending
- README edits: completed; `Setup` and `Notebooks` were left unchanged in the source edit.
- `pyproject.toml` metadata edits: completed; only `name` and `description` were changed.
- `uv sync`: completed successfully on 2026-10-01; resolved 122 packages and installed 117 packages in the project `.venv`. It changed `uv.lock` metadata, which was restored because only `name` and `description` were authorized in `pyproject.toml` and `uv.lock` was not an intended deliverable.
- Notebook execution: completed successfully with `uv run jupyter nbconvert --to notebook --execute --inplace notebooks/01-setup.ipynb`; Jupyter ran all three cells, `OpenAI()` instantiated, and `responses.create` returned `Hello!`. No credential blocker occurred. The notebook was restored afterward and is not part of the change.
- README section preservation audit: completed; a baseline-vs-current assertion passed for the complete `Setup` and `Notebooks` sections. The zero-context diff shows changes only before `## Setup`.
- Final status/diff audit: completed before staging; `git diff --check` passed, and only the authorized tracked files plus the new ODD task are intended for staging. Pre-existing `.gitignore` and `.atl/` remain untouched.
- Commit identity and rollback boundary: completed; the final amended Conventional Commit is identified by `git rev-parse HEAD` and contains only `README.md`, `pyproject.toml`, and this task document. The prior pre-amend identity was `ea4bc1f`; the final amend necessarily changed the SHA. The rollback boundary is reverting the final HEAD commit, which removes only the authorized project-description changes and task evidence while preserving pre-existing `.gitignore` and `.atl/` work.

## Delivery Evidence
- Branch: `docs/project-memory-timeline`.
- Commit identity: final amended commit, retrieved with `git rev-parse HEAD`; commit message is `docs: describe project memory timeline`.
- Rollback boundary: revert the final HEAD commit; this removes only the authorized `README.md`, `pyproject.toml`, and ODD task changes while leaving pre-existing `.gitignore` and `.atl/` work untouched.
