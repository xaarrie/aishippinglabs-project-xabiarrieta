# Source entry ID normalization

## Objective and rationale
Accept repeated source-entry IDs by normalizing to distinct GUIDs in first-occurrence order before validating the aggregate request. Repeated IDs must not invalidate an otherwise valid request or emit duplicate contexts.

## Scope and constraints
- Cover the public tool wrapper, SDK argument snapshots/binding, and defensive core validation.
- Preserve the complete emitted-entry-set invariant, authorization/provenance checks, malformed-input failures, one-shot phases, and output budgets.
- Enforce MaxDistinctFacts against distinct IDs, not the raw duplicate-bearing length.
- Preserve pre-gate caller-argument snapshot isolation and first-occurrence context ordering.
- Update focused regression tests, tool/agent instructions, and current-contract documentation only.
- Preserve all pre-existing staged and unstaged changes. Do not stage, commit, reset, or change branch; commits require an explicit user request.
- Avoid unrelated enumerable-binding redesign, dependency changes, endpoint/UI changes, and historical-document rewrites.

## Tasks
- [x] T1 (completed): Add regression tests, observe RED, implement normalization across supported invocation paths, and align current-contract instructions/documentation. Observe GREEN for the focused research tests.
- [x] T2 (completed): Assess the candidate, perform the prescribed independent verification and parent structural spot check, and record results and remaining limitations.

## Acceptance criteria
- Duplicate requests succeed through direct calls and supported SDK array/list/JSON arguments.
- Each entry yields at most one context, in order of first occurrence, with all emitted fact anchors.
- Raw request length above the configured cap succeeds when the distinct entry set fits.
- Missing/unknown IDs, excessive distinct counts, malformed GUIDs, null, and empty requests remain invalid.
- Caller-owned mutable inputs remain isolated before asynchronous gate suspension.

## Verification plan
- Writer RED/GREEN: dotnet test dotnet/tests/ProjectTimeline.Tests/ProjectTimeline.Tests.csproj --no-restore --configuration Release -p:SkipClientBuild=true --filter "FullyQualifiedName~TimelineFactResearchBatchTests|FullyQualifiedName~TimelineFactResearchToolsTests|FullyQualifiedName~TimelineFactResearchTests"
- Independent verification: follow native ASSESS plan after the writer returns; functional tests remain required regardless of review settings.
- Parent spot check: scoped git diff --check and focused structural readback.
- Read the user-owned review switch without changing it; run native review only if enabled.

## Progress and evidence
- Read-only exploration located duplicate rejection in core validation and raw-size rejection at public/SDK boundaries.
- Existing branch: fix/fact-search-outcomes-source-batch. Extensive staged and unstaged user changes are present in the target files.
- Writer implementation completed across tool normalization/snapshots, agent instructions, focused regressions, and three current-contract documentation sections.
- Writer observed RED: 17 failed / 131 passed / 0 skipped; initial implementation: 2 failed / 146 passed (required prompt phrase restored); final GREEN and strengthened rerun: 148 passed / 0 failed / 0 skipped.
- Scoped git diff --check passed twice; LF/CRLF warnings only.
- User-owned native review switch read twice: globally off, clone-local unset; no review started or setting changed.
- Native ASSESS returned unassessable because existing untracked files lack an explicit review declaration; its fail-closed plan requires a separate independent verifier (treated as high risk). No review authority was started or changed.
- Parent structural readback confirmed defensive Distinct before length/exact-set validation; parent reran scoped git diff --check successfully (LF/CRLF warnings only).
- Independent verifier observed 148 passed / 0 failed / 0 skipped (7 seconds) using the same focused Release test command; no defects found in the scoped readback.
- Independent scoped git diff --check passed; only LF/CRLF warnings. All required functional/structural checks passed.
- Limitations: full suite, npm/browser, live-provider, and PostgreSQL checks were not run; no UI changes require browser verification. Native review remained off. The failed native risk assessment was handled by its prescribed independent-verifier fallback.
- Citation duplicate rejection remains unchanged; only source-entry request IDs are normalized.
- Work-unit commit: pending explicit user authorization; no commit will be created automatically.

## Next step
Implementation and scoped verification are complete. User can retry the failing request. Broader integration checks and any commit remain separate, explicitly authorized actions.
