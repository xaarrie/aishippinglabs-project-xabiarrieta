# Agent prompt Markdown formatting

## Objective
Make the five existing BusinessLogic agent prompts readable as Markdown while preserving their complete wording, instruction order, language, examples, constraints, and agent behavior contracts.

## Scope and constraints
- Only prompt string contents in `EntryReviewAuditAgent.cs`, `SearchEntriesAgent.cs`, `TimelineFactExtractionAgent.cs`, `TimelineFactOptimizationAgent.cs`, and `TimelineFactSemanticVerifierAgent.cs` under `dotnet/src/ProjectTimeline.BusinessLogic/Agents/`.
- Keep prompts inline; add headings, list markers, and whitespace only. Do not paraphrase or translate existing text.
- Do not modify models, tools, initialization arguments, execution logic, external loading, or unrelated worktree changes.
- No commits, staging, push, or branch changes without explicit user request.
- One coherent prospective work unit: `style(agents): structure existing prompts as Markdown`; commit pending user authorization.

## Tasks
- [x] T1 — Reformat all five inline prompts using Markdown sections and lists. Status: done; structural preservation observed for all five files.
- [x] T2 — Verify text preservation, unchanged surrounding C# code, buildability, and relevant deterministic tests. Status: done; independent verification passed with the frontend-build workaround documented below.

## Acceptance and checks
- Each original prompt's ordered wording and punctuation are preserved after removing only added Markdown structure and normalizing whitespace.
- Source outside the prompt literals remains unchanged; all five prompts have clear sections.
- Run `git diff --check` scoped to the five files.
- Run the focused EntryReviewAudit, TimelineFactExtractionAgent, TimelineFactSemanticVerifier, TimelineFactInformation, and TimelineFactReviewPlanner test classes through `dotnet/tests/ProjectTimeline.Tests/ProjectTimeline.Tests.csproj`.
- Formatting-only change: no meaningful behavior RED test; use structural preservation checks and existing deterministic tests instead. Do not claim model-output equivalence from deterministic tests.
- Native review switch is off in this session; use read-only assessment after delegated writing and follow its verification plan.

## Progress and evidence
- Read-only exploration mapped all five inline prompts and the Markdown precedent in `OptimizeEntryAgent.cs`; no external prompt loader is needed.
- Existing unrelated tracked and untracked changes were observed and must be preserved.
- T1 implemented: 34 Markdown headings and 15 list prefixes; 1,461 original ordered tokens and punctuation preserved. Worker checked surrounding source and newline styles; parent independently reran text and surrounding-source checks (5/5 PASS).
- Worker scoped `git diff --check` passed, with newline-conversion warnings only.
- First focused `dotnet test` failed during build; zero tests executed. BusinessLogic compiled, but the Vite build replaced a tracked asset before static-web-assets processing could locate it.
- Incident diagnosed separately: WebApi's `BuildClientApp` runs before build without a normal skip property. Only the three test-generated web asset changes were restored to their observed pre-test state; unrelated files were preserved.
- Native ASSESS returned unassessable because ambient untracked files require explicit declaration; its plan requires independent verification. No native review was started (RDD off).
- Independent verifier: `dotnet build dotnet/src/ProjectTimeline.BusinessLogic/ProjectTimeline.BusinessLogic.csproj --no-restore` passed with 0 warnings and 0 errors.
- Independent verifier: `dotnet test dotnet/tests/ProjectTimeline.Tests/ProjectTimeline.Tests.csproj --no-restore -p:DesignTimeBuild=true --filter "FullyQualifiedName~EntryReviewAuditTests|FullyQualifiedName~TimelineFactExtractionAgentTests|FullyQualifiedName~TimelineFactSemanticVerifierTests|FullyQualifiedName~TimelineFactInformationTests|FullyQualifiedName~TimelineFactReviewPlannerTests"` passed: 191 passed, 0 failed, 0 skipped.
- DesignTimeBuild skips frontend generation and project-reference builds. Separate BusinessLogic build plus identical built/test-output DLL SHA-256 `bc3d0fd6e7c5db32d13b1ff075884c783e31b81abd9ac9c0838124d7f963463c` and inclusion of all five current prompt strings established that tests used the updated code.
- Independent ordered wording, punctuation, surrounding-source, and diff review passed for all five prompts. Parent repeated scoped `git diff --check`: exit 0, only newline-conversion warnings.
- Web asset content restoration verified: no `git diff --numstat` content changes under wwwroot, no deleted/new generated JS remains. `wwwroot/index.html` still appears M in status despite byte-equivalent HEAD content after checkout newline normalization; no content edit is included there.
- Limitations: the original ordinary test/build invocation failed before tests; workaround does not prove the ordinary full build. Full suite and live-model tests were not run; deterministic tests do not establish model-output equivalence. Native risk assessment unavailable because of unrelated untracked paths; independent verification completed instead.
- No commits, staging, push, branch changes, dependency changes, or project-configuration edits were performed.

## Next step
User can review the five formatted inline prompt strings. No further formatting work remains; the unrelated ordinary frontend/static-web-assets build issue is outside this request. Commit remains pending explicit user authorization.
