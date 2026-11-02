# Single-pass fact search and agent reports

## Objective
Replace adaptive multi-tool fact research with one search_facts_batch invocation accepting N expressions, one source-entry fetch over distinct discovered entry IDs, and synthesis using the existing Content/CitedFactIds/CitedContextEvidenceIds contract. After verification, produce a current Spanish Markdown analysis for each of the four functional production agents beside the timeline specifications.

## Rationale and accepted decisions
- Remove the model-facing search_facts and fetch_facts tools; searched facts already contain the same fact details.
- No fixed count cap on expressions within the one search batch or across that operation. Keep per-expression validation, relevance/hit/fact limits, authorization, freshness, and global execution/output safeguards.
- Enforce one-shot phase order in code and prompt; no corrective tool retry cycle. Malformed/repeated/out-of-order calls fail closed without executing another phase.
- fetch_source_entry accepts unique entryIds derived only from emitted facts. Include EntryId in model-facing fact results; retain hidden sensitive provenance fields. Use all emitted fact anchors for each returned entry.
- User selected all_entries_bounded_partial: remove the three-entry restriction; process the aggregate list under global size/time budgets and explicitly disclose omitted/truncated context. Context-specific count/size ceilings must not silently preserve the old three-entry behavior.
- Keep the no-eligible-facts shortcut. After a successful zero-hit search, waive entry fetching and return insufficient evidence. With hits, require one aggregate fetch covering the discovered entry set before accepting normal synthesis.
- Resource clipping may yield Partial; it must not prematurely cancel the required next phase solely because search results were clipped. Do not bypass authorization, ledger/fingerprint validation or deadline. If resources prevent evidence delivery, disclose it rather than inventing completeness.
- Represent unlimited query metadata honestly (nullable unlimited ceiling with compatible UI handling), retaining the existing synthesis/citation response fields and envelope names.

## Scope and non-goals
- Backend tools/options/prompt, directly affected deterministic tests, minimal UI metadata rendering/normalization, and behavior documentation.
- Reports: searchagent.md, extractionagent.md, optimizationagent.md, semanticverifieragent.md in C:/Data/formacion/TimelineSpecifications. Author canonical reports under dotnet/docs/agents, then mechanically publish copies after verification; Spanish technical reports follow the user's requested existing Spanish report.
- Preserve unrelated staged/unstaged changes on current branch fix/fact-search-outcomes-source-batch. Do not stage, commit, push, create PRs, reset, or regenerate unrelated assets. No real model/cloud/PostgreSQL calls.
- RDD currently off; do not start or prompt for native review. After delegated writing, call native read-only ASSESS and follow its verification plan; unavailable assessment is treated as high.

## Tasks and routing
- [x] T1 — Implement single-pass tools, phase enforcement, N-expression accounting, bounded aggregate source recovery, prompt and compatible query metadata with deterministic RED/GREEN coverage. Route: one gentle-ai-worker; mandatory multi-file write trigger.
- [x] T2 — Independently verify backend and frontend regressions and inspect safety/response compatibility. Route: gentle-ai-verify; command-running verification trigger.
- [x] T3 — Audit post-change implementations and author four per-agent reports plus affected repository behavior docs. Route: one gentle-ai-worker; multi-file documentation trigger; only after T2 passes.
- [x] T4 — Independently validate reports, publish exact copies to the specification folder, and record delivery evidence. Route: gentle-ai-verify plus parent mechanical copy/readback.

## Acceptance criteria
- Exactly two registered research tools; no single-search/fetch-facts fallback pathway or prompt instruction.
- One batch accepts more than8 and20 distinct expressions without a count ceiling; execution remains bounded by deadline/output/fact limits.
- One search and one fetch at most; order and mandatory completion enforced, including concurrency, malformed binding and model early-final cases.
- All discovered entry IDs are deduplicated; unknown/cross-scope IDs rejected before source lookup/partial commitment. Freshness/access/citation protections remain intact.
- Aggregate source retrieval can cover more than3 distinct entries; global output/time constraints produce explicit partial/truncation information, never unmarked completeness.
- Zero eligible facts and zero-hit searches remain honest and do not require useless fetches.
- Existing Content/CitedFactIds/CitedContextEvidenceIds synthesis contract is unchanged; unlimited-query metadata works in API normalizer and UI.
- Four reports describe actual post-change prompts, tools, implementation constraints, limits, normal flow, enforcement versus guidance and verification caveats. No secrets/personal seed-user data or unsupported dependency defaults.

## Verification plan
Baseline first, then observed RED tests, minimum implementation GREEN, relevant alternate cases and refactor checks. Existing stale eight-query tests must be distinguished from new regressions. No meaningful RED applies to passive reports; use source-backed semantic/structural review.

Authorized focused backend command:
```sh
dotnet test dotnet/tests/ProjectTimeline.Tests/ProjectTimeline.Tests.csproj --no-restore --configuration Release -p:SkipClientBuild=true --filter "Category!=LiveModel&FullyQualifiedName!~TimelineFactPostgresTests&(FullyQualifiedName~TimelineFactResearch|FullyQualifiedName~TimelineFactInformation|FullyQualifiedName~TimelineFactDirectExtractionTests|FullyQualifiedName~AgentTraceContentTests)"
```
Relevant frontend node test command and production build will be derived from the existing package manifest; no installs or network restoration. Independent closure should run the applicable deterministic non-LiveModel/non-PostgreSQL suite, frontend tests/build, and proportionate UI checks where runnable. Report failures, skips and unavailable checks explicitly.

## Review workload and delivery
Estimated 200–350 production lines plus450–750 test lines, with30–80 possible UI lines; reports are a separate documentation work unit. Forecast is advisory, not a size target; do not compress code or omit tests. Delivery strategy ask-on-risk; no commit/PR requested, so any future oversized commit/PR decision remains pending explicit user delivery authorization. Work-unit commits are not authorized by the later explicit never-commit-without-request safety rule; record verified units without inventing commit evidence.

## Progress and evidence
- Explorer task mvtrn45m-5-3m8g mapped exact backend/test surfaces and four functional agents; no AGENTS.md found. Existing worktree modifications predate this feature.
- Genuine source-recovery decision resolved interactively as all_entries_bounded_partial.
- T1 implemented by worker mvts2jin-6-pkr7. BASELINE225passed/3pre-existing stale assertions failed; RED4new regressions failed (7total failures); final GREEN195backend tests,0failures/0skips. Frontend baseline55passed; nullable-limit RED55passed/1failed; GREEN56passed/0failed/0skipped. Additional omission/clipped-tail regressions observed RED then GREEN. Scoped diff-check clean; index unchanged.
- T1 result: two one-shot tools; emitted EntryId/complete-set entryIds; null query ceilings; globally bounded contexts with reserved omission envelopes; current minimal behavior docs. Parent spot-checked current options. Diff versus index+837/-1496 across13tracked paths plus3pre-existing untracked docs; smaller test count requires independent safety-coverage review, not assumed failure.
- Native ASSESS returned risk unassessable because untracked scope was undeclared; RDDoff, writerProfile runtime/large, nativeReviewOutcome unknown. Returned plan requires writer self-verification plus independent verifier; no review authority changes or maintenance attempted.
- T2 PASS via verifier mvtsv4yv-7-hb7g: focused195/195, full deterministic backend1339/1339, frontend56+35/91passed, no failures/skips. Isolated Vite7.1.7 production build passed with envDir:false and temporary output, no tracked asset changes. Actual React/normalizer Chrome check passed null/legacy ceilings, preserved grounded summary, rejected negative ceiling; zero page errors/network requests. Scoped worktree/cached diff-check passed, status/index unchanged.
- Independent source audit found no blocking safety defect. Reduced test count predominantly reflects removed adaptive semantics; one nonblocking maximal-prefix assertion reduction noted. Historical RED remains writer-observed, not independently rerun.
- Parent reran focused frontend command:56passed,0failures/skips, exit0.
- T3 complete via writer mvtt4pcf-8-vn9l: canonical Spanish reports under dotnet/docs/agents/{searchagent,extractionagent,optimizationagent,semanticverifieragent}.md,188/182/215/207lines. Writer read back all four; static checks passed UTF-8/single titles/topics/fences/whitespace,109file-line references and204internal references after correcting10invalid ranges. No code/test/task edits or external publication. Parent spot-checked search report.
- Report follow-up only: existing search coverage warning still describes immediate fact activation although current extraction worker saves Generated drafts; no unrelated behavior changed.
- Read-only incident isolation confirmed correct cwd/branch and preserved index asset identities; additional wwwroot worktree asset replacement appeared during T3. Origin not established, not attributed to this feature; no asset recovery/deletion/build attempted. Existing code/T2 verification remains unchanged.
- Post-document writer ASSESS again unassessable for undeclared untracked scope; RDDoff, independent verifier required and completed.
- T4 PASS via verifier mvttou30-9-g7xf:4reports,109source references,204internal references,0errors; independent source corroboration found no blocking factual issues. Initial checker falsely matched Spanish 'todo' as a placeholder; corrected checker passed. No source/doc edits during review.
- Parent published exact canonical copies to C:/Data/formacion/TimelineSpecifications/searchagent.md(188lines),extractionagent.md(182),optimizationagent.md(215),semanticverifieragent.md(207), then read back each complete file equal to source. Existing text specifications and scenario CSV untouched. Old searchagent.md intentionally replaced as requested.
- All four feature tasks complete. No pending required functional/document checks, no commits/staging/push/PR. Intentional limitations: no live model or PostgreSQL executions, no full-app browser journey, no deployed-config or binary SDK-default verification. Native assessment unavailable; independent verification fallback passed. Existing activation-warning follow-up remains documented, not silently repaired.
- Intentionally not run: live models, PostgreSQL and full-application browser journeys. Native ASSESS unavailable/unassessable as recorded; independent high-risk fallback completed. Only Node module-type/LF-CRLF warnings. No Git commits.

## Next step
Completed: deliver the verified single-pass behavior and four report paths. Optional future work, not part of this scope: live model/PostgreSQL evaluation and correcting the documented pre-existing activation warning.
