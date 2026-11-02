# Timeline extraction Judge evaluation

## Objective
Use the existing framework JudgeAgent to evaluate TimelineFactExtractionAgent with the framework OpenAI provider and gpt-4o-mini.

## Scope and constraints
- Initialize JudgeAgent with agent-evaluator instructions via Init.
- Pass acceptance criteria, serialized extraction output, and actual captured function calls via RunAsync.
- Preserve the user's existing JudgeAgent, IJudgeAgent, solution, and unrelated working-tree changes.
- Use current live-model gating and credential handling; never embed credentials.
- Live LLM evaluation is nondeterministic, not an isolated unit test; cover framework prompt construction deterministically.
- No commit, push, or PR without an explicit user request. Commit evidence: pending authorization.
- RDD is off globally; no native review will be started.

## Plan
- [x] T1 (verified): Correct JudgeAgent tool-call prompt construction minimally and add deterministic regression coverage. Route: delegated writer; trigger: multiple nontrivial files. Checks: observed RED/GREEN for missing call evidence and representative arguments.
- [x] T2 (verified offline; live pending opt-in): Add a gated Judge-style test of TimelineFactExtractionAgent using the real OpenAI framework provider, gpt-4o-mini, evaluator instructions, explicit acceptance criteria, extraction output, and captured function calls. Route: delegated writer. Checks: build, focused offline tests and gated test discovery. Paid live evaluation requires existing explicit opt-in or fresh user authorization; do not enable the gate automatically.

## Acceptance
- The judge receives all required evidence, including captured calls and arguments.
- The test asserts nonempty, complete criterion results and reports failed judgments clearly.
- Existing extraction behavior and unrelated tests/configuration remain unchanged.

## Delivery
Forecast: approximately 200–350 authored changed lines, one cohesive review slice. Strategy: ask-on-risk. No work-unit commits authorized.

## Evidence
Exploration: JudgeAgent discards ToollCalls.Append return value; IJudgeAgent already defines the requested Init and RunAsync contracts. Existing live tests use LiveModelFact and isolated agent fixtures.
T1 writer evidence: observed RED 5 failed / 2 passed; GREEN 7 passed. Focused diff check passed. Independent verifier repeated 7 passing tests using --no-build --no-restore and found no scoped defects; binary freshness not independently established. Native assessment unassessable due to pre-existing untracked scope; high-risk fallback applied.
Incident resolved: test build invoked WebApi BuildClientApp/Vite and changed four generated wwwroot assets that were initially clean. User explicitly selected restore_generated_assets. Parent restored exactly the three tracked files from HEAD and removed exactly the new index-CKXhVRaN.js. Subsequent UI asset status is clean. Future builds must avoid building the WebApi reference; use targeted BusinessLogic build, then test project build with BuildProjectReferences=false, then --no-build test runs.

T2 writer evidence: added Judge_evaluates_real_extraction_against_explicit_criteria with five semantic criteria and exact complete feedback coverage, actual tracker events detached in finally, shared framework client and explicit gpt-4o-mini for both agents. README documents focused opt-in command. Source-fresh BusinessLogic build and test build with BuildProjectReferences=false both passed with zero warnings/errors. Focused checks: 10 passed, 7 live skipped, 0 failed; diff check passed; UI assets stayed clean. No meaningful deterministic RED applies to a live model evaluation; no paid live requests were made.

## Next step
Completed implementation and offline verification. Final full offline suite: 621 passed / 0 failed / 5 skipped (626 total). The previous 620 passed / 1 failed / 5 skipped result was resolved by a one-line expected extraction live inventory correction from 6 to 7 in LiveModelCoverageTests.cs (parent inline route: small mechanical edit after deterministic failure). Independent verifier repeated fresh safe test build: 0 warnings/errors, then full suite GREEN. New Judge test was discovered and safely skipped with gate=0. Scoped whitespace checks passed; UI assets remain clean. Parent repeated scoped diff check and inspected the one-line inventory correction.

Native assessment was unassessable due to existing untracked scope; independent high-risk fallback verification completed. RDD stays off; no native review started. No commits made because user did not request them. Implementation is on feat/timeline-extraction-judge.

Next optional step: explicitly opt in and run Judge_evaluates_real_extraction_against_explicit_criteria against OpenAI using the README command and existing credential resolution. Live quality remains unverified; no paid requests were made.
