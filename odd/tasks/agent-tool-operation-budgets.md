# Agent tool operation budgets and local web-search removal

## Objective and authorization
After the search citation repair, audit tool-call limits across all project agents, expose each effective maximum to the model, prevent counter leakage between executions after exceptions, and disable web search in LocalCustomHarnessAgent. The user explicitly requested this follow-up during the citation implementation.

## Scope and constraints
- Start implementation only after the citation fix has been implemented and checked. Read-only mapping determines exact edit surfaces before any worker writes.
- Check all agent/tool implementations rather than assuming every counter has the same lifetime or counts the same operation.
- Preserve hard per-execution limits, distinct provider/tool/search accounting, concurrency safety, cancellation, authorization and prior bounded recovery semantics.
- A new execution must start with fresh counters. Never reset counters inside a continuing execution/retry in a way that bypasses its cap; use appropriate per-run ownership and exception-safe cleanup after confirming the actual lifecycle.
- Tell each tool-using agent its real configured maximum, including relevant operation distinctions. Do not invent limits for agents without tools or create prompts that disagree with enforcement.
- Disable built-in web search in LocalCustomHarnessAgent without removing explicitly supplied project tools or changing unrelated provider wrappers unless required and justified.
- Preserve extensive dirty/untracked work on feat/agentic-fact-search. No staging, commits, publishing, paid provider calls, real DB operations, application restart, process termination, telemetry export, migrations or generated UI changes.
- RDD is off. Follow read-only assessment after delegated implementation and its independent-verification plan.

## Tasks
- [x] B1 (mapped read-only): Map all agent tool limits, counter ownership/lifetimes, exception paths, reuse/concurrency, prompt maximums and local harness built-in tool configuration. Return precise evidence and narrow implementation surfaces.
- [x] B2 (implemented; focused checks passed): Test-first correction of proven counter-lifecycle defects and model-visible effective maxima. Cover a failed run followed by a new run, successful-run reuse, continuing-run caps and applicable concurrent isolation. Avoid arbitrary budget changes.
- [x] W1 (verified and locked down offline): Verify and lock down the already-disabled web-search setting through LocalCustomHarnessAgent with an offline regression proving it is absent while explicit project tools remain available. Do not change production code unless this wrapper-level regression establishes a gap.
- [x] B3 (independently verified; stable current candidate): Assess and independently verify focused and proportionate wider offline regressions. Record limitations and avoid a live-provider guarantee.
- [x] I1 (diagnosed; no Git mutation): Investigate the concurrent index change. User confirms intentional staging for their own commit. Preserve it; do not stage/unstage/commit. Historical byte preservation is unavailable, so rerun final checks with per-path content hashes.

## Acceptance criteria
- Effective tool limits are stated consistently to the relevant agents and enforced server-side.
- Exceptions do not leave stale counter state that incorrectly rejects a later independent execution.
- Errors/retries cannot reset or bypass a current execution's hard cap; shared/reused agents do not leak counters across runs.
- LocalCustomHarnessAgent does not enable built-in web search; explicitly supplied project tools continue to work.
- Deterministic regression evidence supports all implemented changes; existing unrelated failures/skips remain visible.

## Verification plan
Use existing fake model/SDK fixtures with isolated ignored build artifacts, Release and SkipClientBuild=true. B2 focused command: dotnet test dotnet/tests/ProjectTimeline.Tests/ProjectTimeline.Tests.csproj --configuration Release -p:SkipClientBuild=true --artifacts-path dotnet/tests/ProjectTimeline.Tests/bin/operation-budgets-b2 --filter "FullyQualifiedName~AgentResearchExecutionTests|FullyQualifiedName~TimelineFactResearchTests|FullyQualifiedName~TimelineFactResearchToolsTests|FullyQualifiedName~TimelineFactReviewToolsTests|FullyQualifiedName~TimelineFactReviewPlannerTests|FullyQualifiedName~TimelineFactStrictSchemaTests" --verbosity minimal
W1 focused command: same project/configuration/SkipClientBuild with artifact path dotnet/tests/ProjectTimeline.Tests/bin/operation-budgets-w1 and filter FullyQualifiedName~CustomOpenAIClientIsolationTests.
Observe RED/GREEN for changed prompt/description behavior; lifecycle/no-web regressions may start green because no defect is established. Record this honestly rather than creating artificial RED. No paid providers or application startup.

## Progress and evidence
- User reports maximum-tool-call failures after earlier errors. This is an observed symptom, not yet proof of counter leakage; B1 must distinguish stale counters from legitimate budget exhaustion.
- Prerequisite odd/tasks/search-context-citation-contract.md C1-C2 completed: independent 348/348 offline tests passed, no failures/skips. Full suite deferred to this feature's B3.
- B1 completed read-only. No enforcement-counter leak confirmed. Search creates fresh tools/provider budget per RunAsync; review creates fresh tools per PlanAsync; acquired gates and subscriptions use finally cleanup. Tracker history can accumulate but does not enforce budgets.
- Research effective defaults: 20 provider admissions, 20 attempted tool invocations, 8 uncached search attempts; configurable reductions must be reflected in instructions. Denied tool attempts can exceed accepted cap in reporting; provider admissions are not necessarily physical HTTP requests with arbitrary injected clients.
- Review: 60 counted backend-read attempts, 1 seed debit before model execution leaving 59; batch enforcement 20 while prompt/description says 8. Preserve enforced 20 and 60, advertise them accurately from the owner, and align the stale oversized test to actual maximum+1. This does not increase a limit.
- Tool-free extraction, semantic verifier and judge have no executable-tool cap to advertise. Generic wrappers have no standalone project cap; SDK default 40 request iterations/3 consecutive error iterations are not 40 tool calls and must not be relabeled or silently changed.
- B1 source inspection confirms CustomOpenAIClient.CreateHarnessAgent already sets DisableWebSearch=true independent of isolation. Parent spot-check confirmed the setting. W1 is a wrapper-path regression, not a speculative production rewrite.
- Exact stale baseline within the now-relevant review-budget scope: TimelineFactReviewToolsTests.Invalid_batches_fail_before_any_backend_work(kind: oversize) rejects nine queries although production permits twenty. Update its boundary to twenty-one using the enforcement owner's value; do not hide/exclude the case.
- B2 writer mvpk4znr-6-ljus completed nine allowed source/test files. Research/review prompts and tool descriptions derive effective limits from owners; no enforcement limits changed. AgentExecutionBudget.cs was not edited.
- B2 RED: 242 passed/5 expected prompt-description failures; GREEN: 247 passed, 0 failed/skipped. Lifecycle reuse after success/provider failure/tool failure/cancellation, current-operation caps, ambient/concurrent isolation and handler cleanup were already green before production changes. No counter leak reproduced; no resets/refunds added.
- Review seeded remaining59 and maximum20 batches verified; stale oversize assertion now tests21, without changing enforcement. Tracked/untracked scoped whitespace clean; no extra source changes or live checks.
- W1 writer mvpkicp5-7-2kgi changed only the two permitted test files. Added11wrapper cases; focused14/14 passed,0failed/skipped. Both injected-client constructor overloads and all5run overloads plus empty-tools initialization covered. HostedWebSearchTool absent on both request rounds; explicit project function executes once and result preserved through follow-up and completion notification. Production already disabled web, so no toggle/source edit needed and no meaningful RED exists.
- W1 non-isolated runtime intentionally not executed because SDK defaults create filesystem memory and discover local skills. Source confirms DisableWebSearch=true independent of isolation. Scoped whitespace checks passed.
- ASSESS after B2/W1 remained unassessable due pre-existing untracked declaration requirement; independent verifier required. No native review approval is claimed.
- B3 verifier mvpkrmw3-8-vuli built current Release and passed306focused plus1325broad offline tests,0failed/skipped (counts overlap). Scoped16files whitespace clean. Authorized broad filter excluded live providers/BusinessTests/TimelineFactPostgresTests; no external calls or generated UI changes were performed.
- During B3,106paths moved into staging. User explicitly confirms they staged those changes to commit themselves. This is not an unauthorized mutation; never alter their index or commit for them.
- I1 follow-up mvpkztfo-9-i9t0 could not attribute earlier aggregate digest drift: no historical per-path hashes retained. This was not proof of source mutation; all16scoped files equaled staged content after CRLF normalization. The final current-candidate rerun below supersedes that historical preservation uncertainty for completion.
- FINAL B3 mvpl4eqw-a-d3s4 PASS: both exact authorized commands rerun once, compiling current Release first. Focused306passed/0failed/0skipped; broad offline1325passed/0failed/0skipped; no retries/filter changes. Counts overlap and are not a unique-test sum.
- Current-candidate per-path raw-byte SHA-256 snapshots matched before, after each suite and at completion:232source/build/config/static-UI-input files, including all16scoped source/test files; zero changed paths/hashes. Deterministic sorted/deduplicated enumeration was independent of staging classification; IDE/build/task artifacts excluded. Map digest e3a691fb3ac1324214b6c2948d19a70b5d6cdc8d43d22efa9222810f411aa347. No snapshots written.
- User staging preserved. No source/test/index mutation, UI generation, app startup, live providers, real DB operations, telemetry export or agent-created commit. Final tests validate current candidate, not earlier historical preservation or future live-model compliance.
- Commit evidence: none; explicit user request required.

## Next step
All authorized implementation and offline checks are complete. User is preparing their own commit; do not alter their index or commit on their behalf. User-controlled next step: rebuild/restart the API and retry the original search against the intended timeline. Live-provider quality, real PostgreSQL execution and non-isolated harness runtime were not tested. No pending source change remains; native assessment was unavailable/unassessable, not an approval.
