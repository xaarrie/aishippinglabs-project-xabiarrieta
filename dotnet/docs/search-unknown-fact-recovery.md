# Unknown-ID handling: recovery retired

## Current single-pass behavior

Research exposes only `search_facts_batch` and `fetch_source_entry(entryIds)`. The model must use the complete entry set from emitted facts; duplicate source entry IDs are normalized in first-occurrence order before distinct-count validation, producing at most one context per entry. All emitted facts for each entry become validated anchors. Unknown/unemitted/foreign IDs, missing entries, excessive distinct counts and malformed binding remain fatal, sanitized errors, not `RecoverableError` responses. There is no correction allowance or retry pathway.

The operation gate consumes the attempted phase before SDK argument binding. Errors remain fatal after state restoration and stop the SDK loop; final validation rejects early synthesis or missing phases. Only nonfatal resource clipping may continue to the required aggregate fetch and partial synthesis under remaining global capacity. No-eligible-facts and zero-emitted-fact shortcuts do not require fetching. Authorization, revision, fingerprint, provenance and real citation ID checks remain unchanged.

## Historical behavior (superseded)

The remaining sections record the old adaptive correction protocol and its observed verification, not current behavior.

### Former behavior

Only fact IDs already emitted by `search_facts` in the current operation may be fetched. Unknown IDs never expand that allowlist. They do not trigger a fact or source lookup.

The SDK-facing tools now return a JSON `RecoverableError` with code `UnknownFactId` for an unknown source or detail ID. The message asks the model to use an emitted fact ID, not an entry or context ID. It contains no submitted ID, fact, source text, fingerprint, or exception details. It is not an empty search result.

Two unknown-ID errors are recoverable per operation. They count against the existing tool-call and serialized-output budgets. A successful fetch with an emitted ID is required before final output can be released. Searching alone or ignoring the error does not clear that requirement. A third unknown-ID error permanently fails final validation, even if a later fetch succeeds or the provider-call limit is reached.

Malformed arguments and other non-budget tool failures remain fatal. Fatal tool errors are retained because the SDK can capture exceptions and continue. Existing budget-only partial responses are unchanged. Final authorization, scope, revision, fact freshness, source fingerprints, cancellation, and citation allowlists still apply. The direct public tool methods still throw for unknown IDs; recovery is at the SDK invocation boundary.

## Offline evidence

The real `LocalCustomHarnessAgent` and SDK tool loop ran against a scripted `IChatClient` and the existing EF in-memory fixture. No production database or model provider was used. Live-model and PostgreSQL opt-ins were unset.

Before the source change, the two recovery cases failed. At the next model turn, `FunctionResultContent.Exception` contained `TimelineFactResearchException`, with `UnknownFactId` for source fetch and `UnknownOrInvalidFactIds` for detail fetch. This SDK captures ordinary tool exceptions and continues; an exception alone does not guarantee that the search aborts. This does not establish the origin of the real offending ID or reproduce the user's exact UI request.

After the change, both cases passed: search emitted a genuine ID, an unknown fetch produced a sanitized error, the model retried the emitted ID, and final citations were validated.

Commands run:

- `dotnet test dotnet/tests/ProjectTimeline.Tests/ProjectTimeline.Tests.csproj --no-restore --configuration Release -p:SkipClientBuild=true --filter FullyQualifiedName~Unknown_ID_is_a_sanitized_recoverable_result`
  - RED: two failed, zero passed (run twice; the second run exposed the SDK exception in the assertion).
  - GREEN: two passed, zero failed.
- `dotnet test dotnet/tests/ProjectTimeline.Tests/ProjectTimeline.Tests.csproj --no-restore --configuration Release -p:SkipClientBuild=true --filter "FullyQualifiedName~TimelineFactResearchToolsTests|FullyQualifiedName~TimelineFactResearchTests|FullyQualifiedName~TimelineFactInformationTests|FullyQualifiedName~TimelineFactUnknownIdRecoveryTests"`
  - First run: 115 passed, one failed. The malformed-binding test still expected a releasable ledger after a fatal error. Its expectation was updated to require failure.
  - Final run: 116 passed, zero failed or skipped.
  - Covers source/detail recovery, recovery before searching, output/call charging, no unemitted data in error results, repeated misuse, ignored errors with and without evidence, fabricated final citations, restored-state fatal errors, existing valid flows, scope/auth/revision/stale evidence, deadlines, and cancellation.
- `dotnet test dotnet/tests/ProjectTimeline.Tests/ProjectTimeline.Tests.csproj --no-restore --configuration Release -p:SkipClientBuild=true`
  - 1266 passed, seven failed, 23 skipped; 1296 total.
  - Known failure: `TimelineFactReviewToolsTests.Invalid_batches_fail_before_any_backend_work(kind: "oversize")` expected an argument exception.
  - Additional failures: `TimelineFactSearchTests.Search_and_fetch_exclude_ineffective_or_out_of_scope_facts` for `foreignTimeline`, `futureReview`, `foreignProject`, `invalid`, `pendingReview`, and `future`. Each expected message text `authorized effective scope` but received an invalidation-evidence validation message. These paths were not edited; their pre-edit failure was not measured in this task.

## Verifier correction

The first implementation did not retain failures thrown inside the unknown-ID recovery handler. Its authorization check could fail, the SDK could capture that exception, and restored access/scope/revision could allow the old ledger to pass final validation. Six new actual-SDK cases reproduced this: both source and detail fetches with unknown IDs incorrectly succeeded after each state restoration.

Fatal retention now surrounds the whole sanitized invocation, including the recovery handler. Non-budget failures stay fatal after state restoration. Cancellation propagation and existing budget-only partial handling are unchanged. A concurrent source/detail unknown-ID test also confirms exact call/output accounting, the shared two-error allowance, required correction, and permanent failure on the third unknown ID even after a successful correction.

The six scope-test fixtures now include a correctly bound semantic assessment using the existing context-reconciliation helper. Their exact `authorized effective scope` assertion is unchanged; they now reach the intended scope guard instead of the earlier missing-assessment guard. No production scope rules changed.

Correction commands:

- `dotnet test dotnet/tests/ProjectTimeline.Tests/ProjectTimeline.Tests.csproj --no-restore --configuration Release -p:SkipClientBuild=true --filter "FullyQualifiedName~Fatal_tool_error_cannot_be_ignored_even_if_current_state_is_restored|FullyQualifiedName~Search_and_fetch_exclude_ineffective_or_out_of_scope_facts"`
  - RED before source/fixture fixes: 12 failed, four passed. Six new restored-state cases returned no exception; six existing scope cases failed at the missing-assessment guard.
  - GREEN after fixes: 16 passed, zero failed or skipped.
- `dotnet test dotnet/tests/ProjectTimeline.Tests/ProjectTimeline.Tests.csproj --no-restore --configuration Release -p:SkipClientBuild=true --filter "FullyQualifiedName~TimelineFactResearchToolsTests|FullyQualifiedName~TimelineFactResearchTests|FullyQualifiedName~TimelineFactInformationTests|FullyQualifiedName~Search_and_fetch_exclude_ineffective_or_out_of_scope_facts"`
  - 129 passed, zero failed or skipped: the original 116 cases, six new restored-state cases, one concurrency case, and six scope cases.
- `dotnet test dotnet/tests/ProjectTimeline.Tests/ProjectTimeline.Tests.csproj --no-restore --configuration Release -p:SkipClientBuild=true`
  - 1279 passed, one failed, 23 skipped; 1303 total. Only the known oversized review-batch assertion still fails. Live-model and PostgreSQL opt-ins were unset; their 23 tests were skipped.

## Limits

The real offending ID's origin is still unknown. These deterministic tests prove the configured SDK loop can recover; they do not prove how a live model will respond. No live calls, app restart, migrations, UI assets, shared harness edits, staging, or commits were performed. The remaining known oversized review-batch failure is untouched and still prevents a fully green suite.
