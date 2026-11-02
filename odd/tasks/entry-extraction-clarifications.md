# Entry-linked extraction clarifications

## Status and authorization
Implementation and independent OFFLINE verification complete. User approved extraction-stage questions for genuinely missing/ambiguous information, actual human answers linked to the entry without rewriting original Content, and explicitly required both entity/fact extraction and semantic verification to read those answers. User requested continuing through final verification.

This is source completion, NOT running-host activation or completion of the separate live bulk operation. No commit/staging/push was authorized or performed. Current branch: feat/agentic-fact-search; extensive pre-existing dirty/untracked work preserved.

## Delivered behavior
- Separate entry-linked question/answer records with immutable question identity, generation/source binding, server-assigned actual-answer author/time and concurrency controls. Original Content and existing accepted fact evidence remain unchanged.
- Frozen, durable original-plus-answer extraction inputs, effective fingerprint and complete supporting span identities. Claims, EntityKey and EntityType and the semantic verifier consume the same context. Literal Entry versus actual Clarification provenance is reconstructed/validated server-side.
- Only explicit valid missing-information/ambiguous-reference questions pause a job. Schema/citation/semantic/SDK/provider errors and cancellation remain failures, not human questions. Questions alone are never factual evidence; model-supplied answers are rejected.
- AwaitingClarification work is excluded from automatic model retries. Partial answers persist without model calls. An explicit observed-generation resume captures applicable answers before predecessor supersession, validates lineage and creates a successor. Source edit/restoration never silently revives historical evidence.
- Normal reconciliation imports actual answer references and checks the complete generated ledger/support by AdditionId. Canonical quotation restoration is exact and unique, not fuzzy. Generated drafts cannot be accepted directly: normal Pending -> explicit Accept is still required. Prepare/accept/ORM boundaries revalidate provenance.
- Both initializer guarded adapters and the hosting adapter preserve full structured outcomes through their existing controls and report waiting honestly.
- Authorized API and separate entry UI show current questions, validated carried answers and read-only history. Saving an answer and model-run resume are distinct actions. Reviewer/read-only controls, conflict draft retention, stale-request fencing, text escaping and refresh-only recovery are implemented. Existing proposal reconciliation Q&A stays separate.

## Completed task checklist
- [x] C1: Scoped entry clarification storage, immutable human answers, source/version/provenance guards, additive migration source and domain tests.
- [x] C2a: Same frozen answer context for entity/fact extraction and verifier, strict literal citations, genuine typed questions and legacy compatibility.
- [x] C2b: Durable input/support, atomic waiting, no waiting retry, explicit resume/carry-forward and Generated-only completion.
- [x] C3a: Provenance through normal review/acceptance and complete structured forwarding in both guarded initializer adapters.
- [x] C3b: Authorized current/history, actual-answer and explicit-resume API; lifecycle/permissions/body bounds; host adapter forwarding.
- [x] C4: Separate entry clarification UI, readonly history, explicit save/resume, Node/build and independent real-browser validation.
- [x] C5: Independent final offline suite, builds, Node regressions, parent spot checks and explicit activation limitations.

Work-unit commits: none; explicit session restriction overrides the default commit convention.

## API and persistence contracts
Entry-relative routes:
- GET /api/entries/{entryId}/fact-clarifications
- POST /api/entries/{entryId}/fact-clarifications/{questionId}/answer with { expectedGenerationId, answer }
- POST /api/entries/{entryId}/fact-clarifications/resume with { expectedGenerationId }

Read response includes entryId, generationId, lifecycleState, current, history, truncated, canAnswer, canResume and refreshRequired. Row bindings distinguish CurrentQuestion, validated CarriedAnswer and History; history caps at 100 without displacing current rows. Server derives actor/admin/scope/readiness; clients never supply extraction snapshots/provenance/source replacements.

TimelineFactJobs adds ExtractionInputJson (TimelineExtractionSnapshot), EffectiveInputFingerprint and ExtractionSupportJson (TimelineExtractionSupport[]). Complete support is correlated by durable AdditionId. Imported question references ExtractionGenerationId and ExtractionAnswerId retain actual evidence without duplicating span arrays. See dotnet/docs/entry-extraction-clarifications.md for callable contracts.

## Observed verification evidence
| Unit | Independent result | Parent spot |
|---|---|---|
| C1 | 84 focused, clean WebAPI build | 18 clarification tests |
| Corrected C2a | 185 focused, clean WebAPI build | 41 provider tests |
| Corrected C2b | 366 expanded / 1120 broad, clean WebAPI build | 76 processing/structured/self-contained tests |
| C3a | 364 focused / 1157 broad, clean WebAPI + initializer builds | 37 review tests |
| C3b | 229 focused / 1183 broad, clean WebAPI build | 26 HTTP tests |
| C4 | 62 Node, isolated Vite 37 modules; real Chrome 3 journeys / 24 assertions | Read browser proof; reran 34 Node cases |
| Final C5 | 1183/1183 offline .NET; 62/62 Node; both builds 0 warnings/errors | 6 HostedUi tests |

All final commands have zero failures/skips. Existing MODULE_TYPELESS_PACKAGE_JSON warnings remain in Node (four test modules); no unrelated package configuration change made.

Final .NET root: dotnet/tests/ProjectTimeline.Tests/bin/entry-clarifications-final-verified. Tests/builds use -p:SkipClientBuild=true and fresh ignored --artifacts-path roots to avoid running-host locks and Vite wwwroot deletion. Node runner: node --test src/*.test.js from ClientApp. Vite build output was explicitly redirected to ignored test bin roots, never default wwwroot.

Independent browser proof: dotnet/tests/ProjectTimeline.Tests/bin/entry-clarifications-c4-browser/proof.json, browser.mjs, baseline.json and JSON/PNG captures. Real preinstalled Chrome/CDP, disposable profile, dummy auth, 40 fake API requests and blocked external probe. Writer two-question partial/full saves produced zero automatic resume/model POSTs; explicit resume exactly one and both GET refreshes; Reviewer controls absent; 409 retained draft with no write replay; literal hostile-looking text did not execute; source/carried/history remained intact. All 324 baseline hashes, including all three wwwroot files, and git status were unchanged. Async selection race was exercised in Node, not separately in the browser. Only harness-owned processes were closed; no actual host or user browser touched.

### Meaningful RED and corrected regressions
- C1 observed unauthorized direct ORM question creation succeeding before guards.
- C2a uncovered loss of TimelineId/EntryNumber/AuthorId during Freeze despite initial default-metadata GREEN. Realistic regression RED15/170pass, then full immutable identity fix and independent185 GREEN. Hash validation was not weakened.
- C2b observed question/lifecycle RED; expanded/broad found four stale fake-envelope/citation fixtures. Captured actual job-bound span IDs and source.content parsing fixed them without changing production/assertions; independent366/1120 GREEN.
- C3a RED5 missing imports/wrapper guards -> GREEN364; C3b genuine missing route RED -> GREEN229. Intermediate fixture/compiler errors were corrected, not represented as meaningful behavior RED.
- C4 meaningful normalization/waiting-regeneration/draft-retention RED -> Node62 and browser PASS.
- C5 exact JSX-tag assertion broke on the legitimate refreshKey integration. HostedUi RED5/1 -> GREEN6; scoped per-prop/source/permission/refresh assertions replaced the obsolete whole-tag check, production unchanged; independent final1183 GREEN.
- Migration/documentation checks used structural/offline SQL/model consistency, not invented behavioral RED. Frozen migration targets and existing FTS metadata were verified.

Native ASSESS remained unassessable because of pre-existing undeclared untracked scope; RDD was off/unknown. The returned high-risk plan required independent verification, which was fulfilled. No native approval/receipt/closure is claimed.

## Unperformed operations and next step
- Source-only migrations 20261025223000_EntryExtractionClarifications and 20261025233000_ClarificationExtractionInputs remain UNAPPLIED; prior FTS migration 20261025163000_TimelineFactFullTextIndex is also pending from earlier work.
- No live PostgreSQL migration/concurrency/FTS execution, real provider/model-semantic smoke, actual Logfire delivery or actual host activation was performed. Live/LiveOpenAI/LiveModel/Integration, BusinessTests and TimelineFactPostgresTests are excluded from offline verification. InMemory/fake tests are not live transactional/model reasoning proof.
- Deploy/build assets and backend, apply reviewed migrations to the authorized target, restart the host and run controlled live smoke checks only after separate authorization. Current wwwroot/running application was deliberately preserved.
- Bulk facts remain a separate partial operation in odd/tasks/bulk-fact-activation.md: last audit had 29 original +5 new valid facts. No further paid retries or acceptance of entry-6 unjustified invalidations occurred during this feature. Do not mark its B2/B3 complete from source tests.

## Final outcome
Approved development scope is complete and independently verified offline, including real-browser interaction against fake APIs. Next action is separately authorized activation, then human responses for any genuine extraction questions and explicit re-extraction/review; technical failures remain technical investigations, not forced fact acceptance.
