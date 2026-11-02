# Search Facts Evaluation Scenarios

## Objective
Create exactly 50 agent-specific evaluation questions as CSV at `C:/Data/formacion/TimelineSpecifications/search_facts_evaluation_scenarios.csv`, beside the eight source specifications.

## Problem and rationale
Search Facts needs coverage of successful evidence retrieval, different wording, partial or ambiguous evidence, unsupported requests, and hallucination/batch-isolation failures. Questions must be grounded in timeline specifications rather than generic chatbot examples.

## Scope and constraints
- CSV columns, in order: `question,category,type`.
- Allowed types: `happy_path`, `varied_phrasing`, `edge_case`, `out_of_scope`, `breaking`.
- Allocation: 24 happy-path questions (two per topic), eight phrasing variants, six edge cases, five out-of-scope cases, seven breaking cases.
- Treat these specifications as one fixed authorized evaluation corpus; importing/extracting them into a real batch is not verified.
- Distinguish documented requirements from current deployed behavior. Do not manufacture missing specification details or expected answers.
- Preserve all existing project/specification files and unrelated staged/unstaged work. No application changes, external services, live agent runs, Git commits, pushes, or PRs.
- English questions; preserve exact schema/domain terms when relevant. Do not expose credentials or personal seed-user data.

## Tasks
- [x] T1 — Map all eight specifications and the actual agent's retrieval, scope, citation, and uncertainty contracts.
- [x] T2 — Author exactly 50 specific questions in the requested three-column CSV.
- [x] T3 — Independently validate CSV structure, counts, question diversity, source specificity, and adversarial coverage; read back the delivered artifact.

## Acceptance criteria and checks
- Standard CSV parser reads exactly 50 data rows, exactly three fields per row, exact header, nonempty values, unique questions, allowed types, and at least five breaking rows.
- Two happy-path questions per each of 12 grounded topics; phrasing cases include direct, vague, and wrong-terminology variants of supported questions.
- Seven breaking questions cover collection/project isolation, fabricated citations, prompt injection, unauthorized mutation, invented details, and unwarranted completeness guarantees.
- All expected-answer questions are answerable from the selected corpus or deliberately target a documented ambiguity/gap. Negative scenarios do not assert unobserved facts exist elsewhere.
- No meaningful RED/GREEN cycle applies to static question authoring. Run structural checks and an independent semantic review instead; do not claim agent performance from dataset validation.

## Progress and evidence
- T1 complete: read-only explorer task `mvtjivfo-1-db9d` inspected all eight specifications and production agent/tool/scope implementations. Parent spot-checked `1.ProjectTimeline.txt`.
- Search is scoped to ProjectId + TimelineId; query batching does not switch collections. Source context is not accepted-fact evidence. Specification 6 has no textual requirements.
- Existing local lessons do not establish the exact five-type taxonomy; the user's explicit framework controls.
- Existing worktree has unrelated staged and unstaged changes; preserve them. Commits are not requested or authorized.
- T2 complete: CSV written and fully read back. Authoring checks found 50 unique questions, allocation 24/8/6/5/7, and maximum question length 187 characters.
- T3 complete: independent verifier task `mvtjqtgd-2-v994` passed Python CSV assertions and semantic review against all eight specifications and agent scope/tool contracts. Confirmed exact header, 50 complete rows, NFKC/casefold/whitespace-normalized uniqueness, allocation 24/8/6/5/7, 12 happy-path categories with two questions each, UTF-8 readability, and maximum question length 187. No blockers, unintended duplicates, or real credentials/personal seed-user data found.
- Parent reran a local Python csv parser spot check: exit code 0; exact header, 50 complete unique rows, and allocation 24/8/6/5/7 confirmed.
- Application tests and RED/GREEN are not applicable to static dataset authoring. Live agent execution, actual performance, and corpus import/extraction remain unverified and were not requested. No existing source/specification files modified; no Git commits created.

## Next step
Completed. Optional next step: load the specification-derived facts into the intended batch/timeline and run the 50 questions against the agent; that execution is not part of this deliverable.
