# Search context citation contract

## Objective and authorization
Fix the SearchEntries final synthesis confusing a fact's SourceFingerprint with a source-context EvidenceId. The user explicitly requested the fix after supplying the failed synthesis and search-tool result. Work is authorized locally; no staging, commits, publishing, application restart, paid provider calls, real database operations or telemetry exports.

## Confirmed diagnosis
- The first malformed citedContextEvidenceIds value exactly matches Facts[0].SourceFingerprint in the supplied tool result. All three supplied context citation values are 64 hexadecimal characters, not GUIDs.
- The described response contains a search call, tool result containing Facts, and final synthesis. If no source context was fetched, citedContextEvidenceIds must be empty.
- TimelineFactInformationResult uses List<Guid>; accessing AgentResponse.Result fails before final citation-ledger validation.
- Tool context identities are generated server-side as GUIDs. Fact IDs, context EvidenceIds and source fingerprints have different purposes.

## Scope and constraints
- Limit changes to research tool presentation, synthesis citation instructions/contract, and focused deterministic regression tests.
- Project model-visible fact/context tool payloads to omit server-only fingerprints/provenance while retaining original records for ledger validation and public API metadata. Use the same projection for serialized budget measurement and emission, including the separate batch serialization route. Do not annotate shared records with JsonIgnore.
- Strengthen synthesis instructions and DTO property descriptions: fact Id GUIDs versus fetched context EvidenceId GUIDs; no fetched context means an empty context-citation array.
- Preserve real context-only and mixed citations, authorization/revision/fingerprint checks, emitted-ID membership, batching/cache/query budgets, cancellation and prior fixes.
- Never convert/truncate hashes into GUIDs, invent IDs, silently discard invalid citations, relax membership checks or add model repair calls.
- Preserve the existing dirty worktree (152 paths at entry) on feat/agentic-fact-search. No unrelated formatting, generated bundles, framework redesign, migrations or configuration changes.
- RDD is off (global); assess after writer return and follow the resulting verification plan. Native review is not enabled by this task.

## Tasks
- [x] C1 (implemented; focused checks passed): Implement the smallest citation-contract fix with deterministic regression tests. Observe relevant RED before production changes, then GREEN; cover facts-only output without context citations, correct context identities, withheld fingerprint metadata if projected, and rejection of invalid/unknown/mixed IDs. One delegated writer; exact surfaces are the tools file, research/result models, SearchEntriesAgent, and the existing ResearchTools/ResearchBatch/Research test files.
- [x] C2 (independently verified offline): Assess the candidate and independently verify the focused suite plus proportionate wider offline checks. Record source readback, failures, skips and real-provider limitations.

## Acceptance criteria
- Search/fetch tool outputs do not encourage citation of SourceFingerprint as EvidenceId; synthesis instructions distinguish fact GUIDs from context GUIDs and require an empty context citation array when no source context is used.
- Internal fingerprint/freshness validation and server-side provenance remain intact.
- Facts-only and real context/mixed SDK flows work offline; malformed or unissued citation IDs remain rejected.
- No extra provider request, budget increase, paid execution, real data modification or silent recovery is introduced.
- Applicable tests pass; pre-existing unrelated failures and unavailable checks are reported honestly. Offline tests cannot guarantee future live-model compliance.

## Verification plan
- Use the existing deterministic fake IChatClient/SDK fixtures, not a paid provider.
- Focused command (RED then GREEN): dotnet test dotnet/tests/ProjectTimeline.Tests/ProjectTimeline.Tests.csproj --artifacts-path dotnet/tests/ProjectTimeline.Tests/bin/fact-research-verification-citation-wire-mvpj9 --configuration Release -p:SkipClientBuild=true --filter "FullyQualifiedName~TimelineFactResearch|FullyQualifiedName~TimelineFactInformationTests|FullyQualifiedName~AgentResearchExecutionTests" --verbosity minimal
- After a successful current-candidate build, broader offline command uses the same project/artifacts/configuration/SkipClientBuild plus --no-build --no-restore and filter "Category!=LiveModel&FullyQualifiedName!~BusinessTests&FullyQualifiedName!~TimelineFactPostgresTests". Independent verifier reviews safety/exact known baseline before execution.
- Known prior wider-suite baseline: one unrelated review-tools oversized-batch assertion failed in the preceding batching feature; verify its exact identity before treating a new occurrence as known. No full-suite green claim without observing it.
- No live-model, PostgreSQL, browser, application startup, migration or telemetry-export checks are authorized or required for this backend contract unit.

## Progress and evidence
- Prior read-only diagnosis and user debugger evidence establish the field confusion; no implementation or test run yet.
- Scout completed read-only mapping. Fact/context output currently serializes internal records; batch has a separate envelope path. Production changes must preserve original ledger records and fit/clipping accounting while emitting projections. No SDK/framework changes are needed.
- C1 writer mvpjg20d-3-yjs5 completed all seven allowed files. Model-facing projections omit fingerprint/provenance metadata; original ledger/API records, strict GUID conversion and citation allowlists remain intact.
- Observed RED: focused 187 tests, 182 passed and 5 failed as intended (fingerprint exposure and missing instructions). Final GREEN: 187 passed, 0 failed, 0 skipped. Facts-only SDK flow uses 2 provider calls/1 tool and no context citations; malformed 64-hex citations fail closed without repair.
- Scoped tracked/untracked whitespace checks passed; only LF/CRLF advisory. No live execution or source changes outside the seven allowed paths.
- Native ASSESS returned unassessable because pre-existing untracked files require declaration; RDD off, independent verifier required. No native approval is claimed.
- C2 independent verifier mvpjqveu-4-raj9 compiled current candidate and observed focused 187/187 plus related offline 161/161: total 348 passed, 0 failed, 0 skipped. No must-fix defect; scoped tracked/untracked whitespace clean, only line-ending advisories. Before/after status matched.
- Parent spot-checked shared projections/Fits/Emit and repeated tracked whitespace check (exit 0). Original ledger/API provenance retained. Full suite deferred to B3; live-model/PG behavior remains unverified.
- Commit evidence: none; commits require an explicit user request.

## Queued follow-up
The user additionally requested an all-agent tool-counter/maximum audit and disabling web search in LocalCustomHarnessAgent after this fix. Track that separate scope in odd/tasks/agent-tool-operation-budgets.md (B1, B2, W1, B3); do not expand the citation writer's surfaces.

## Next step
C1-C2 and the follow-up in odd/tasks/agent-tool-operation-budgets.md are complete within the authorized offline scope. Final combined current-candidate verification passed306focused and1325broad offline tests,0failures/skips (overlapping counts);232source/build-input hashes including all16scoped files remained identical. User staging was preserved and user plans their own commit. User-controlled next step: rebuild/restart and retry the original live search; no restart or paid verification was performed.
