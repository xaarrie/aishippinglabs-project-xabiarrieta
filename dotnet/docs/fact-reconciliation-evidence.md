# Context-aware reconciliation: C1 evidence

## Contract and boundary

The optimizer must compare both claims with their original contexts. Independent requirements coexist;
a later entry's silence is not contradiction. Uncertain scope or supersession should produce a question,
not an invalidation. Examples include login/password recovery versus toolbar/mobile changes, and
`dotnet run` instructions versus a later entry that does not discuss startup.

Search/fetch hits were inspected: they include quotations, fingerprints and clarification provenance,
but **not** original entry title/content. The planner now delivers `seed_contexts`; the new bounded
`fetch_fact_contexts` tool supplies original contexts for followup hits. It first fetches effective,
authorized facts, excludes missing/stale source entries, shares the existing read budget and gate,
and honors cancellation. Existing search/fetch response types remain unchanged. Context reads use
one budget slot even when no context is available. No full-history fallback was added.

Each invalidation requires exactly one internal `InvalidationAssessments` item. Validation binds it
to the fact ID, addition index, both claims, historical fingerprint and replacement quotation;
it requires delivered historical context, a literal historical entry/answer excerpt, a nonempty
scope/explanation, SameScope=true and Contradiction or ExplicitSupersession. Missing, duplicate,
extra, mismatched, independent, ambiguous and absence-of-evidence assessments fail closed.
Existing quotation, clarification provenance, authorization, chronology, source-freshness and
revision checks remain. No database schema or public proposal/draft shape changed. Assessment
metadata is generation-only, not persisted audit evidence; direct manual proposal APIs are unchanged.

**These are structural guarantees, not independent semantic verification.** A model can still
incorrectly label a pair same-scope and produce a structurally valid explanation. No deterministic
entity equality or natural-language heuristics pretend to prove semantic truth. Scripted agents
and prompt assertions do not establish live-model semantic quality. No live model/provider/DB
calls, migrations, runtime restarts, staging or commits were performed.

## Baseline and incremental work

Before edits, the worktree was extensively dirty. Existing tracked diffs and the complete permitted
source/test content were inspected in tool output; no temporary snapshots were written. Baseline
tracked numstat (+/-): optimizer 10/4, tools 56/6, planner 54/13, planner tests 173/4,
provider tests 4/3. Review tools tests were already untracked (220 lines); preserved and extended.
The new reconciliation test and this document were absent. Provider tests were not edited.

Incremental change accounting before this document: approximately 411 changed lines, including
192 new reconciliation-test lines and 66 additions to the existing untracked tools tests. This is
not the much larger branch/worktree diff. Existing tracked changes remain in place.

## Observed tests

All commands used `dotnet test dotnet/tests/ProjectTimeline.Tests/ProjectTimeline.Tests.csproj`.
Exact option sets and outcomes are listed below; concatenate that prefix with each option set.

| Options | Observed result |
| --- | --- |
| `--no-restore --filter "FullyQualifiedName~TimelineFactReviewPlannerTests\|FullyQualifiedName~TimelineFactReviewToolsTests\|FullyQualifiedName~TimelineFactProviderTests\|FullyQualifiedName~TimelineFactApiTests" --verbosity quiet` | Baseline: 110 passed, 1 failed. |
| `--no-restore --filter "FullyQualifiedName~TimelineFactContextReconciliationTests" --verbosity quiet` | First attempt: missing test namespace imports; corrected. Second: 0 passed, 3 failed before implementation. Only the missing seed-context failure is meaningful behavior RED; two examples failed in fixture setup. Third, after source edits: Debug build blocked by locked WebApi DLL. |
| `--no-restore --configuration Release --filter "FullyQualifiedName~TimelineFactContextReconciliationTests" --verbosity quiet` | 1 passed, 2 fixture failures. |
| `--no-restore --configuration Release --filter "FullyQualifiedName~TimelineFactContextReconciliationTests" --logger "console;verbosity=normal"` | Confirmed the two failures were unsupported fixture quotes, not intended guard RED. Corrected setup. |
| `--no-restore --configuration Release -p:SkipClientBuild=true --filter "FullyQualifiedName~TimelineFactContextReconciliationTests\|FullyQualifiedName~TimelineFactReviewPlannerTests\|FullyQualifiedName~TimelineFactReviewToolsTests\|FullyQualifiedName~TimelineFactProviderTests\|FullyQualifiedName~TimelineFactApiTests" --logger "console;verbosity=normal"` | 133 passed, 2 failed: baseline batch assertion and API fixture generation prerequisite. |
| `--no-restore --configuration Release -p:SkipClientBuild=true --filter "FullyQualifiedName~TimelineFactContextReconciliationTests\|FullyQualifiedName~TimelineFactReviewPlannerTests\|FullyQualifiedName~TimelineFactReviewToolsTests\|FullyQualifiedName~TimelineFactProviderTests\|FullyQualifiedName~TimelineFactApiTests" --verbosity quiet` | After added negative cases: 138 passed, 3 failed: baseline assertion, lexical overlap in missing-context fixture, old expected API claim. |
| `--no-restore --configuration Release -p:SkipClientBuild=true --filter "FullyQualifiedName~Valid_looking_assessment_without_delivered_historical_context_is_rejected\|FullyQualifiedName~Invalidations_return_existing_claim_quote_and_reason" --logger "console;verbosity=normal"` | Confirmed two fixture errors; corrected lexical separation and expected superseding claim. |
| `--no-restore --configuration Release -p:SkipClientBuild=true --filter "FullyQualifiedName~TimelineFactContextReconciliationTests\|FullyQualifiedName~TimelineFactReviewPlannerTests\|FullyQualifiedName~TimelineFactProviderTests\|FullyQualifiedName~TimelineFactApiTests" --verbosity quiet` | Final GREEN: 118 passed, 0 failed, 0 skipped. Includes all 25 reconciliation cases. |
| `--no-restore --configuration Release -p:SkipClientBuild=true --filter "FullyQualifiedName~TimelineFactReviewToolsTests" --verbosity quiet` | Final: 22 passed, 1 failed, 0 skipped. Only pre-existing `Invalid_batches_fail_before_any_backend_work(kind: "oversize")`; test expects 9 queries rejected but baseline maximum is 20. Not changed here. |

The initial runner invoked the existing WebApi Vite build target, which can rewrite generated
`wwwroot` outputs outside the requested surfaces. Subsequent builds explicitly disabled it with
`SkipClientBuild=true`. Final generated-asset git status matches the initial status, but no initial
content hashes were captured for those unrelated outputs; the parent should check them independently.
No process was stopped to resolve Debug locks; Release used normal local build outputs.

The context-delivery regression observed RED then GREEN. The user-example guard tests were corrected
after implementation and passed; they do not have valid preimplementation guard RED evidence.
Alternate coverage includes contradiction, explicit supersession across different entity keys,
question routing, undelivered/stale history, forged assessment fields, tool bounds, budget,
authorization and cancellation. Required fixture validation still has the baseline failure, so this
handoff is partial rather than claiming all required checks passed. Independent C2 assessment and
live semantic evaluation (only if separately authorized) remain outstanding.

## Authorized quote/schema follow-up

The user explicitly authorized only `Agents/Framework/LocalCustomHarnessAgent.cs` as an additional
production surface. Existing dirty edits in that file and all other surfaces were preserved.
The user explicitly declined a legacy pending-proposal acceptance block and will regenerate manually.
Existing pending proposals remain outside the new generation-only assessment guard by that decision;
service, application, acceptance, persistence and database schemas were not changed.

The planner snapshots model additions before canonical citation reuse. Each invalidation assessment
must bind to the original addition's quote, which is independently checked against the original entry
or actual clarification answers. The canonical quote still receives its existing validation and is
used in the resulting draft. Assessments are never silently rewritten. The regressions cover a valid
longer literal citation, a forged quote, a quote matching only the later canonical citation, and an
unsupported original quote. Non-invalidation canonical quote reuse retains its previous behavior.

The new harness structured-run overload is explicit opt-in; existing text and inferred typed overloads
are unchanged. Only the optimizer's native harness opts into the exact strict schema and serializer.
Injected custom agents retain the previous typed seam and still undergo planner validation. Optimizer
outbound schema closes every nested object, requires every defined field, and explicitly sets strict.
A fake HTTP provider exercises the actual SDK/harness serializer, recursively checks object contracts,
roundtrips a populated assessment, and verifies a real context-tool continuation under the same schema.
Two further cases prove the final-response validation hook rejects length/content-filter finish states.
No shared harness default, result model or unrelated batch limit was changed.

Meaningful preimplementation RED (recorded before the scope stop): 2 passed, 3 failed. The valid literal
was falsely rejected; a mismatched original/canonical assessment was incorrectly accepted; the actual
optimizer request omitted `json_schema.strict`. After implementation the quote cases passed; the schema
test's casing expectation needed correction to match the explicitly configured camelCase contract
(4 passed, 1 failed). Then the new quote/schema cases passed 6/6, including a tool continuation.

All follow-up commands below used this exact prefix:
`dotnet test dotnet/tests/ProjectTimeline.Tests/ProjectTimeline.Tests.csproj --no-restore --configuration Release -p:SkipClientBuild=true`

| Exact remaining arguments | Observed result |
| --- | --- |
| `--filter "FullyQualifiedName~Canonical_quote_reuse_preserves_valid_original_assessment_binding\|FullyQualifiedName~Optimizer_native_harness_sends_strict_nested_assessment_schema_and_roundtrips_result" --logger "console;verbosity=normal"` | Preimplementation RED 2 passed/3 failed; first postimplementation run 4 passed/1 casing-expectation failure. |
| `--filter "FullyQualifiedName~Canonical_quote_reuse_preserves_valid_original_assessment_binding\|FullyQualifiedName~Optimizer_native_harness_sends_strict_nested_assessment_schema_and_roundtrips_result" --verbosity quiet` | GREEN: 6 passed/0 failed. |
| `--filter "FullyQualifiedName~TimelineFactContextReconciliationTests\|FullyQualifiedName~TimelineFactStrictSchemaTests" --verbosity quiet` | First 37 passed/0 failed; after adding final-metadata checks, 39 passed/0 failed (29 context, 10 strict-schema). |
| `--filter "FullyQualifiedName~TimelineFactReviewPlannerTests\|FullyQualifiedName~TimelineFactProviderTests\|FullyQualifiedName~TimelineFactApiTests\|FullyQualifiedName~StructuredAgentRunTests\|FullyQualifiedName~AgentResearchExecutionTests" --verbosity quiet` | 160 passed/0 failed. Includes unchanged text/typed harness callers, cancellation, budgets and structured-run contracts. |
| `--filter "FullyQualifiedName~TimelineFactReviewToolsTests" --verbosity quiet` | 22 passed/1 unchanged baseline failure: `Invalid_batches_fail_before_any_backend_work(kind: "oversize")`. Not repaired. |

Final non-overlapping follow-up suites: **221 passed, 1 pre-existing failure**, no skipped tests.
All transports were fake/injected and database fixtures were in-memory; no live provider/database calls,
Vite, generated-asset writes, process stopping, staging or commits occurred during this follow-up.
Test builds used normal Release outputs. No separate broad build or full repository suite was run.

Incremental source/test accounting against the pre-follow-up content (not HEAD), reconstructed read-only
from the exact edits: optimizer +60/-1; harness +34/-0; planner +19/-11; context tests +35/-0;
strict-schema tests +111/-0: **+259/-12**, plus 57 evidence lines: **+316/-12 (328 changed lines)** total.
The approved resumption alone adds +205/-12 (217 changed lines); the earlier RED tests account for 111 additions.
`git diff --check` passed for the changed production surfaces. Existing C1, branch and unrelated worktree
changes are excluded from those counts. The baseline test failure remains, so the handoff remains partial.

The offline fake provider establishes outbound schema structure and harness plumbing, not acceptance by
a live provider or semantic model accuracy. Same-scope assessment remains model judgment; these changes
only enforce evidence binding and structural fail-closed behavior. Independent C2 disposition is parent-owned.
