# Direct optimizer agent tests

## Objective
Test OptimizeEntryAgent directly using injected IOptimizationAgentTools and reusable, synthetic in-memory conversations for software projects, travel preparation, and business goals.

## Scope and constraints
- Preserve existing DB constructors, EF context windows and IOptimizationAgentTools function names/signatures. T6 intentionally removes their SDK exposure; the application calls them directly.
- Reuse the existing IOptimizationAgentTools and IFunctionCallTracker contracts.
- Fake context is not a fake final model response. Separate deterministic infrastructure checks from real-model evaluations.
- Ask only when missing information prevents faithful interpretation; optional planning fields alone do not require questions.
- Any context-tool failure stops review with a clear sanitized error, including failure after partial reads. No successful partial draft or persistence.
- Live evaluations must not use filesystem memory, workspace skills, web search, a real database, or production conversation data.
- Keep tests and support under dotnet/tests/ProjectTimeline.Tests/AgentTests.
- No API startup, migrations/seeding, destructive Git, commits, push, or PR creation. Commits were not explicitly requested.
- Local credential setup is authorized: copy only OPENAI_API_KEY from the unique ignored launch-settings source to dedicated test User Secrets outside the repository. Never display/store credentials in tools output, source, argv, logs or memory. User Secrets is not encrypted at rest.
- Credential ingestion must preserve environment-first override, environment-only default production constructors and the separate paid-live-test opt-in gate. No live calls as part of setup.
- Current feature branch: feat/optimize-entry-review. Preserve unrelated .atl changes, untracked task documents, screenshots and local files.

## Work routing and review units
All source work is delegated to one gentle-ai-worker at a time (multi-file and preparation triggers). Verification is delegated to gentle-ai-verify.
Initial forecast: approximately 900-1400 authored changed lines. T1-T3 measured scope: 2615 authored diff lines (2361 new test/support/doc lines, 102 guard lines, 152 tracked production diff lines). Most volume is fixtures and deterministic negative controls. Review separately by the three units below; no delivery or oversized-PR strategy is selected because no commit/PR is authorized. Do not compress tests to fit a line budget.
RDD: off (global), confirmed by gentle-ai review mode status. No native review START.

## Tasks
- [x] T1 — Inject context tools and add reusable fake conversations. DONE
  - Add an interface-injection constructor without breaking existing DB/anchor compatibility.
  - Make the existing concrete FunctionCallTracker implement its interface.
  - Add a domain-neutral fake repository, tools adapter, and software/travel/business conversation fixtures.
  - Deterministic tests establish chronology, isolation, boundary/read-only behavior, explicit tool schemas and direct agent injection.
  - Observe applicable RED before implementation and GREEN afterward.
- [x] T2 — Enforce failure stopping and precise clarification instructions. DONE
  - Guard registered tool execution per run; latch and sanitize failures, preserve failed-call evidence, block later reads and reject recovered success drafts.
  - Verify SDK termination behavior, not merely an exception from a repository.
  - Update prompt for optional-field questions, context provenance and stable partial answers.
  - Deterministic direct-agent tests cover list/first-fetch/later-fetch failures, tracker evidence, no persistence and reset/isolation.
- [x] T3 — Add isolated real-model evaluations and verify deterministic regressions. DONE
  - Direct OptimizeEntryAgent calls with fake context and actual configured model; assertions on semantics, typed status, question references and tool order.
  - Cover optional fields/ambiguous control, team enrichment/Marta, detailed conditions, partial and complete answers, and tool failures.
  - Reuse three-domain corpus and existing tracker. Isolate harness local facilities.
  - Nonparallel, explicitly enabled live evaluations; missing key when explicitly enabled must fail clearly. Default disabled tests must be visibly skipped, not passed.
  - Document exact commands, opt-in, model identity, limitations and honest outcomes.
  - Run independent fresh-build deterministic regressions and check live gating; no fake quality claims.
- [x] T5 — Configure local test credentials outside Git. DONE
  - Add a dedicated test UserSecretsId and scoped credential resolver: nonempty environment override, then optional User Secrets.
  - Inject resolved credentials through the existing real-IChatClient seam without changing default production constructors or mutating process-global environment.
  - Test with synthetic injected credential sources; ensure default live tests still skip and missing-key controls remain isolated.
  - Parent migrates only the approved source key programmatically, with no raw-value output, no argv secret and no overwrite of conflicting destination secrets.
  - Independently verify deterministic regressions, store binding, preservation/precedence, no versionable-file leakage and no paid calls. Update README.
- [x] T6 — Move mandatory context loading into C# and migrate coverage. DONE
  - Store IOptimizationAgentTools and, under the existing consumer-identity gate, list once and await each allowed fetch once in returned order before Init/RunAsync.
  - No model history functions; defer consumer initialization until successful preload. Remove obsolete SDK guard/wrapper/latch machinery.
  - Preserve original caller string exactly inside an internally built anchored envelope; authoritative review_context entries are separate, ordered and untrusted as content. Null-tools legacy input remains unchanged.
  - Preserve actual tracking with local call objects/no invented SDK IDs, sanitized list/first/later failures and cancellation, no consumer calls on failure, fresh context each round and shared-consumer isolation.
  - Do not sort by GUID, silently Distinct, truncate, add arbitrary caps/cache or change repository/provider/credentials/other agents. Reject malformed duplicate/empty/null windows rather than re-read or hide them; valid repository windows are nonempty with unique IDs.
  - Observe meaningful deterministic RED, then migrate obsolete tool-registration/SDK-termination tests to exact sequence/count, zero exposed history tools, zero consumer/model calls on failure and full-envelope preservation.
  - Keep semantic oracles unchanged. Move three pure OE05 load-failure cases to deterministic coverage; remaining live suite6 methods/11 cases, not executed here. Update README and direct service fixtures for real persisted anchors/deferred Init stage.
  - Independent full-source regression with protected UI assets; no paid tests, secret reads, commits or publishing.
- [ ] T4 — Execute real-model evaluations for OE-01 through OE-04. IN_PROGRESS / POST-T6 PAID PASS
  - Requires OPENAI_API_KEY from environment or dedicated local test User Secrets, plus explicit live opt-in.
  - Run opt-in real-model suite, record exact model/scenario results, actual tool traces and any failing semantic criteria. No silent pass/skip when explicitly enabled.
  - Do not mark complete from mocked responses, skipped tests or credential checks.

## Verification evidence
- Source/tool map: gentle-ai-explore task mvcpqpyw-2-bsed, completed.
- Initial status: application source clean; unrelated .atl modifications and local untracked files preserved.
- Preflight: gentle-ai-verify task mvcpv8n8-3-x9br completed. .NET SDK 10.0.401; OPENAI_API_KEY absent (Boolean-only check).
- No-build baseline: 162 passed, 0 failed/skipped; not fresh-source proof.
- WebApi BuildClientApp has no dedicated skip switch. Unit work will first build BusinessLogic normally, then build/test the test project with standard BuildProjectReferences=false, reusing unchanged prebuilt WebApi/Database references. Final full-source verification must protect generated wwwroot assets before normal build. Do not use DesignTimeBuild to claim a real build.
- Live model execution is blocked on an externally configured OPENAI_API_KEY. Implement visible default skips and fail clearly when explicitly enabled without credentials; do not expose or request the secret in chat.
- T1 writer mvcpxmbi-4-ognd: implemented injectable tools, concrete tracker interface, reusable fake repository/adapter and four English/Spanish conversations spanning three domains.
- T1 observed RED: missing-constructor CS1503/CS1739 and missing-interface CS0266 before production implementation.
- T1 writer GREEN: fresh BusinessLogic build (0 warnings/errors), 32 AgentTests, 7 OptimizationAgentToolsTests and 10 EntryReviewDetailTests passed.
- T1 independent verification mvcq76kx-5-rqvb: source outcomes verified, fresh BL build clean, 194 deterministic tests passed, git diff --check clean apart from unrelated .atl line-ending warnings.
- Reconciled verifier's concurrent-document note: the parent intentionally updated only this task document/mirror during the read-only verification; no implementation files changed. Current disk and mirror were read back before T1 closure. This is bookkeeping, not a source-snapshot change. Historical RED remains writer-observed, independently observed GREEN only.
- Native ASSESS: unassessable due untracked declaration; returned high-risk fallback requires independent verification. RDD remains off; no START.
- T2 writer mvcqantq-6-yzjx: implemented per-run guarded functions, serialized consumers, immediate sanitized tracking, failed-read latch, SDK termination and success veto; narrowed clarification/provenance/answer rules.
- T2 observed behavioral RED: 27 failed / 25 passed before guard; final focused GREEN: 60 passed. Intermediate compilation/SDK/constructor regressions were corrected without weakening existing tests.
- T2 writer full GREEN: 222 deterministic tests passed, 0 failures/skips; fresh BL build clean and git diff --check passed.
- Important SDK evidence: Terminate followed by throwing a tool exception still issued another request. Returning a sanitized terminal tool result stops the installed SDK; the optimizer's run boundary then throws the authoritative failure.
- T2 independent verification mvcqnmwp-7-6biv PASS: 222 deterministic tests, clean fresh BL build, no severe findings; actual installed FunctionInvokingChatClient termination independently observed.
- T2 verification limit: production AsHarnessAgent path has not yet been exercised end-to-end. T3 should add an isolated actual-harness scripted-transport control if feasible; not a model-quality substitute.
- T3 writer mvcqs69g-8-afaa: implemented gated real-model OE-01..05 suite, three-domain fixtures, Entry-only relation assertions and mutation controls, actual production-harness scripted-transport success/failure controls, opt-in client isolation and README.
- T3 writer RED: missing isolation API (1 fail/222 pass), missing assertion API and behavioral oracle mutations; final GREEN: 286 deterministic tests, no failures/skips, fresh BL build clean.
- Disabled live filter: 7 visibly skipped methods, 0 passed/failed. No semantic model RED/GREEN possible without credentials.
- T3 independent verification mvcra7q8-9-eq27 PASS: normal full-reference test build (including WebApi/Vite), 286 deterministic tests, 0 failed/skipped; disabled live filter visibly skipped7 methods.
- Explicit enabled-without-key child check: expected exit1 / one prereq failure before provider construction; this is a negative gate check, not a failed semantic evaluation or model invocation.
- UI protection: all3 wwwroot assets byte-identical; SHA256 inventory381eff7ead09ae38d93287152b679bee1c436efabcb52b3eb8b7f8ca2fc597b1; all12 frontend inputs and84 selected source files unchanged. No restore needed.
- Low provenance finding corrected by worker mvcrep1m-a-i7gj in three test files only. Linked ownership/answer attribution rejects source repeats, date-only/wrong-owner attribution and summary echoes; actual-first-result checks reuse dynamic submitted answer IDs without forcing question grouping.
- Follow-up observed behavioral RED:6 failed/286 passed, expanded mutations12 failed/289 passed; final writer GREEN303 deterministic tests,7 visibly skipped live methods. Clause-local lexical limits remain documented.
- Final independent follow-up mvcrngvt-b-l6j4 PASS: fresh normal full-reference build,303 deterministic tests passed,0 failures/skips;7 live methods visibly skipped. Previous provenance finding resolved within documented lexical scope.
- Production/fixtures/README unchanged by follow-up; only3 test files changed. All84 selected source files stable during final verification; all12 frontend inputs and3 assets byte-identical. No restoration needed.
- Parent final git diff --check passed; only pre-existing unrelated .atl line-ending warnings. No commits, publishing, API startup, migrations/seeding or external model calls.
- New credential-source inspection: only the WebApi Properties/launchSettings.json has a nonempty key; exactly1 distinct value across both launch-settings candidates. Both files are ignored and not tracked. No values/prefixes/digests printed.
- Scout mvcs68j9-c-0naq verified User Secrets APIs are already available via transitive Microsoft.AspNetCore.App; no new package expected. Parent completed sanitized source metadata inspection because scout lacked execution tools.
- T5 starts from303 deterministic passes and7 skipped live methods. Worker mvcsd6d5-d-kdus added dedicated test UserSecretsId, gated environment-first resolver, real transport injection through existing seam and37 deterministic controls. No production constructors or new packages changed.
- T5 observed RED: missing UserSecrets assembly identity (1 failed/303 passed), then missing resolver compilation. Writer GREEN:340 deterministic tests,0 failed/skipped;7 disabled live methods visibly skipped.
- Parent migration completed: only approved source key copied atomically into dedicated test User Secrets outside repo. In-memory checks confirmed key equality, unchanged launch-settings sources, preserved other settings and no secret in tracked/setup files. No values/digests/argv secrets or global environment mutations.
- Migration incident resolved separately: initial broad scan hit locked unrelated .vs index files and aborted before writing; read-only state probe confirmed target absent. Retried only after narrowing scan to tracked and authorized setup source, leaving VS files untouched; no universal claim over locked caches.
- T5 independent verifier mvcsr4zo-e-dqzo: core credential verification PASS. Normal full-reference run340 passed,0 failed/skipped;7 live methods still visibly skipped with credentials present. Source/store values equal in memory and files unchanged; no actual secret in tracked/authorized setup text.
- Actual compiled .NET resolver probe: nonempty=True, matches_source=True, status=OK, using the dedicated store with blank environment override. Literal gate1 was passed only to resolver, never installed in environment; no client/provider/model invoked.
- UI/source preservation:3 assets,12 frontend inputs and84 selected source files unchanged. No restore needed. Verifier external scaffold/summary parsing issues were corrected outside repo; completed build/test/probe/diff commands passed.
- Read-only diagnosis mvcsxk84-f-njka completed: original before/after full Git-status inventories were not retained, so historical delta/cause is unrecoverable. Same flags/stdout-only comparison rules out grouping or stderr as historical cause. No source mutation is established by that Boolean mismatch.
- Current Git inventory:22 grouped/57 full records, identical tracked records, every path in expected scope; directory expansion accounts for18 .vs records and19 AgentTests records. No cleanup, restoration or additional source changes occurred.
- T5 core outcome PASS: actual local-store ingestion,340 full-reference deterministic passes,7 visible disabled-live skips, source/store equality and scoped source/UI/Git-secret checks verified. Historical whole-worktree preservation remains unestablished; do not claim universal preservation or infer the cause.
- No live model invocations occurred before the explicitly authorized T4 run below.
- Work-unit commit identities: none; awaiting explicit user authorization, not part of current delivery.

## Next step
T6 is implemented and verified deterministically. User now explicitly authorizes one post-T6 paid pass of the current6-method/11-case suite. Run once, record all outcomes/current C# read traces and clarification phases, then report without source changes or paid reruns. Old14-case baseline is historical and has a different case mix.

## Remaining limitations
- Prior disabled-live runs were not quality evidence. T4 now has one actual paid pass:14 expanded cases,2 passed,12 failed,0 skipped, with10 structured responses. Most semantic checks were blocked by earlier chronology assertions; final OE04 answer rounds and stability remain unverified.
- User Secrets is local plaintext outside the repository, not an encrypted vault. Only its nonsecret binding ID belongs in the test csproj.
- Original whole-worktree Git-status delta was not retained and cannot be reconstructed. Current expected inventory and scoped source/UI preservation are verified; no universal historical preservation claim.
- Structural and bounded lexical assertions have mutation controls but can reject valid cross-sentence paraphrases or miss semantic contradictions. Review real outputs and provenance.
- SDK traces may contain a blocked already-planned call; repository read counts establish that no underlying access occurred after the failure.

## Authorized paid run
- User explicitly requested execution of the seven paid test methods. This authorizes one pass of the existing LiveModel/AgentTests suite, including its parameterized variants and clarification rounds; not seven fixed API requests.
- Requested production model: gpt-4o-mini. Actual model behavior and key authentication were previously unverified.
- Delegate command execution to gentle-ai-verify; compile current BusinessLogic/tests without frontend regeneration, then enable PROJECT_TIMELINE_RUN_LIVE_AGENT_TESTS=1 only in the test child process.
- No automatic suite/case reruns, retries by orchestrator, fixes, weakened assertions, model/provider changes, new fixtures, commits, API/DB startup or package installs. SDK-internal behavior stays as configured.
- Sanitize/suppress credentials and raw provider diagnostics before any console/artifact persistence. Detailed safe logs and BEFORE/AFTER Git inventories belong in an external owned directory, never repository scratch.
- Verifier task mvcwx6b5-g-b4p5 started for fresh builds and the single authorized paid pass. No manual reruns.
- Execution outcome: one paid suite invocation, requested gpt-4o-mini,14 actual cases,2 passed,12 failed,0 skipped,199.346 seconds, exit1. Both prerequisite builds passed with0 warnings/errors. No reruns or source changes.
- Passed: OE05 list failure and first-fetch failure, with actual failed context attempts, authoritative sanitized error and no draft.
- First failures:8 reversed prior/current read order (OE01 clear3 + ambiguity1, OE02 Marta1, OE03 details3);1 OE02 team-context NeedsClarification instead of Ready;1 controlled OE04 local date-answer provenance criterion despite retained date/Q2;1 actual-first-result OE04 unclassified runtime/context failure;1 OE05 later-fetch precondition expected3 reads vs2 because current was fetched first.
- Eight order failures prevented subsequent semantic assertions. Controlled OE04 final Q2 round and actual-first-result answer round were not executed. Runtime diagnostics were suppressed and cannot be reconstructed; do not attribute to auth/quota.
- Secondary inspection: unnecessary questions in clear assignments; Marta addition treated as conflict; business details unnecessarily asked acceptance criteria and questioned an explicit finance condition. Some reviews had71/75/72 repeated underlying reads, not API-request counts.
-10 structured real responses preserved. Actual cost, token usage and API-request count were not observed. One pass is not a stability/completeness proof; lexical oracle limits persist.
- Safe external evidence: %TEMP%/optimize-entry-t4-g5f1t37t/{summary.json,case-evidence.json,live-test-sanitized.log}; parent spot-read summary.json confirmed14/2/12/0.
- Retained BEFORE/AFTER Git inventories identical;86 selected source/asset hashes identical, including12 frontend inputs and3 assets. Gate absent before/after; no global env mutation or credential copy. git diff --check passed (existing .atl line-ending warnings only). No full deterministic regression rerun was requested or performed in this paid pass.
- Wrapper-only heredoc failure happened before execution; final output encoding error happened after saved results/postchecks. Outcome reconciled from saved artifacts without replay. No raw console log persisted; full-key artifact scan found no matches. This run authorization is exhausted; failures do not authorize fixes or another paid pass.

## T6 routing and migration rationale
- User chose moving context loading into C# after explanation of model/harness responsibilities; authorization recorded in decision940.
- Scout mvdfwopi-i-5oy3 mapped code/test dependencies. One bounded writer will make this cohesive root-class change with tests/docs; multi-file/preparation triggers apply. No parallel writer.
- Prefer small private typed C# tracking helper, not a new AIFunction state machine. Delete OptimizationToolExecutionGuard and optimizer-only SDK termination tests once replacement zero-model coverage is observed. Snapshot currently untracked deleted files outside repo first.
- Anchored payload shape: original_message retains entire original input, review_context contains ordered entry IDs/content prepared by C#. No merging caller-supplied context into authoritative entries; all text remains untrusted. No silent compression.
- Existing EF/fake repository contracts remain. Empty/duplicate/null ID results are malformed for persisted-anchor review and must stop before consumer; no silent normalization.
- Source footprint will touch optimizer and related tests/docs, including OptimizationAgentToolsTests/EntryReviewDetailTests/EntryReviewTests. No shared provider/interface/credential changes.
- Historical T1/T2 SDK registration/guard mechanism is superseded, not its scope/read-only/fail-closed invariants. Exact context count/order replaces Distinct-based checks.
- Bounded writer mvdg8202-j-f4lc stopped before edits: its role prohibits external snapshots and file deletions. No source/tests changed and no commands ran beyond status.
- Parent owns mechanical retirement, not business logic. Verified original untracked source/test snapshots at %TEMP%/optimizer-t6-retirement-8v76pxm4/manifest.json.
- RED-only worker mvdgd6wf-k-1xyt added11 deterministic cases:9 failed/2 passed,0 skipped, no compile errors; fresh BL build clean. Failures prove early constructor Init, zero mandatory reads, exposed history functions and absent anchored envelope; null-tools JSON/plain-text forwarding controls passed.
- A retirement wrapper had a Windows-path Python escaping syntax error before execution; separate read-only state check confirmed both originals/snapshots unchanged. Corrected to forward-slash paths, with no deletion replay uncertainty.
- After behavioral RED, parent reverified BOTH current files byte-for-byte and SHA256 against snapshots, confirmed still untracked, then removed exactly OptimizationToolExecutionGuard.cs and OptimizationSdkTerminationTests.cs. Snapshots retained externally; source contents recoverable.
- Staged retirement leaves old optimizer references unresolved until full migration edits them. This intermediate compile state is not new TDD evidence. Full writer mvdglo4o-l-s419 resumed after confirmed retirement; no other deletions/external writes delegated. Existing async test file will be migrated in place.
- Source implementation remains single-writer. No shared provider/tracker/credential/oracle/corpus changes authorized.
- Full writer mvdglo4o-l-s419 completed T6: sequential per-run C# preload, exact original-message envelope, empty model history tools, deferred Init, local tracking and safe failures. Retired files were not recreated; async tests migrated in place.
- Writer GREEN:18/18 context-loading tests,353/353 deterministic regressions,0 failed/skipped. BusinessLogic builds passed twice with0 warnings/errors; disabled live suite visibly skipped6 methods twice,0 paid calls. Historical RED9/11 came before source changes/deletion.
- Semantic oracles/corpus/credentials/provider/other-agent source untouched. Direct service fixtures now persisted and deferred Init diagnostic row reflects invocation stage; EF scope tests retained.
- Independent verifier mvdh2hzs-m-fnev: retrieval contract and normal full-reference regression PASS,353 tests/0 failures/skips;6 live methods visibly skipped. Actual-harness scripted control receives envelope with no history tools; all3 preload failures issue0 transport requests.
- Verifier overall preservation verdict PARTIAL: normal/tracked inventories identical and85 selected source files/12 frontend inputs/3 assets unchanged, but full/untracked inventory assertion failed on2 removed+2 added .vsidx paths outside candidate scope. Contents/cause not inspected; no cleanup or restore.
- Parent read-only diagnosis of retained before/after files confirms ALL4 delta records are untracked dotnet/.vs/ProjectTimeline/FileContentIndex/*.vsidx. This is a bounded unrelated inventory limitation, not a clean whole-worktree verdict.
- Parent recovered86 prior public source/asset hash records from the pre-T6 paid-run artifact and verified10 protected files byte-identical NOW: both semantic-oracle files, both corpus fixture files, fake repository, credential resolver/gate, shared provider/tracker and EF context tool. This resolves the missing pre-T6 identity evidence for those protected files without reading secrets or rerunning tests.
- Parent metadata inspection initially had a Python backslash syntax error before execution; corrected with chr(92), read-only only, no build/test/source replay. Final git diff --check passed with line-ending advisories only.
- T6 core implementation and scoped verification complete; .vs inventory caveat remains recorded, no full-worktree-preservation claim.
- Native ASSESS again unassessable because of untracked declaration; followed returned high-risk independent-verifier plan. RDD stays off, no native START.
- No new paid-run authorization.

## T6 closure limitations
- No paid calls or real credential reads during T6. Six live methods were skipped;11 expanded rows are source-derived, not observed live executions.
- Full-worktree equality was NOT verified because four untracked Visual Studio index paths changed; selected source/asset preservation and tracked inventories were verified. Do not attribute the process causing index churn without evidence.
- C# data preparation order is guaranteed by the application flow; model attention, question quality, factual completeness, provenance compliance and stability still need real-model evaluation.
- No commits or publishing. Retired-file recovery copies remain in the external manifest recorded above.

## Authorized post-T6 paid run (run 2)
- User explicitly requested 'lanza las pruebas de pago' after T6 completion. This is a NEW one-pass authorization for the current6 methods/11 cases, not reuse of the exhausted pre-T6 grant.
- Requested model remains gpt-4o-mini; variants and clarification rounds can produce more API requests than test methods. Actual cost/token/request counts must not be invented.
- Fresh BL/tests builds may reuse unchanged WebApi/Database references to avoid frontend generation, then run only the existing LiveModel/AgentTests filter with child-only live opt-in. No full regression rerun or new scenarios requested.
- No source/oracle/fixture/provider/credential changes, installs, API/DB startup, commits, manual failed-case reruns or extra model judge calls. SDK retry behavior stays unchanged.
- Stream-capture and sanitize credentials/masked provider diagnostics BEFORE console/artifact persistence; safe logs/inventories/hash manifests only in a new owned external directory. No raw TRX/provider dumps.
- Retain BEFORE/AFTER path/status inventories and scoped source/assets. Classify any .vsidx-only drift separately from test outcomes; never clean up unrelated files.
- Track C# retrieval once per RunAsync, not once per whole clarification test; later answer rounds legitimately reload. Separate actual model failures, oracle limitations, provider errors and unreached phases.
- Verifier mvdhlrhn-n-gy6i started for fresh builds and this single post-T6 paid pass. Detailed sanitized outputs and retained inventories will use a new external directory; old reports are not overwritten.
- Run outcome: pending.
