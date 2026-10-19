# Retained business-agent live tests

All four retained agents have real-model suites. **Paid execution has not been performed for T5 and requires separate authorization.** Offline passes, synthetic seeds, discovery, and skips are not evidence of model quality.

## Coverage at a glance

| Production agent | Suite | Discovered cases | Main oracle |
| --- | --- | ---: | --- |
| `SearchEntriesAgent` | `SearchEntriesLiveModelTests` | 3 | Accepted scoped evidence, answer/source membership, insufficient evidence, foreign-scope exclusion, coverage warnings |
| `TimelineFactExtractionAgent` | `TimelineFactExtractionLiveModelTests` | 7 | Five entry scenarios, sequential client/agent isolation, and JudgeAgent evaluation of real extraction evidence |
| `TimelineFactOptimizationAgent` | `TimelineFactOptimizationLiveModelTests` | 1 | Two-round ambiguous conflict → actual clarification → supported addition/invalidation cause; no agent writes |
| `TimelineFactSemanticVerifierAgent` | `TimelineFactSemanticVerifierLiveModelTests` | 6 | Supported two-candidate batch/evidence IDs; numeric, date, actor, negation, unsupported-detail rejection |

`LiveModelCoverageTests` reflects over concrete business `*Agent` types (excluding framework/tools and static helpers), requires an identifiable gated suite for each, and checks gated attributes, per-suite case counts, `Category=LiveModel`, and the nonparallel collection. Metadata inspection does not construct agents or clients or resolve credentials. New agents without a mapped suite fail offline.

### Extraction scenarios and limits

- **Multiple claims:** Atlas car C-7's color and Nora's ownership must both survive.
- **Quantities/units:** tank R-2's 24 liters of coolant and pump P-9's 3 liters per minute retain their relationships.
- **Date/condition/negation:** a planned shipment on 12 March 2027 depends on approval that has not happened.
- **Ambiguity/duplicate:** a possibly leaking valve remains uncertain; repeated source text must not pad exact duplicate claims.
- **Empty:** empty title/content returns no claims, not invented content.
- **Session isolation:** the same production extraction agent and real client receive car, equipment, then empty entries sequentially. The API accepts **one entry per call**, not a batch.

Assertions use per-claim semantic anchors, shared scope/entities, coverage, units, uncertainty, selected forbidden inventions, numeric provenance, and exact duplicate detection. They do not demand exact generated prose or an exact nonempty claim count. Draft `SourceQuote` may be approximate under extraction's contract; these tests do not demand literal substring grounding before independent semantic-verifier reconstruction. Anchors are bounded deterministic checks, not a complete entailment proof or a claim that all paraphrases work. Offline mutation tests demonstrate rejection of missing claims, changed units/quantities, dropped negation/conditions/uncertainty, duplicates, and selected invented facts. A positive alternate test allows combined claims and approximate draft quotes.

### Judge evaluation

`Judge_evaluates_real_extraction_against_explicit_criteria` uses the existing `Framework/JudgeAgent`, initialized as an agent evaluator, to assess the multi-claim Atlas car fixture. It receives explicit color/ownership, self-contained claim, source-grounding and no-fabrication criteria, the source, serialized real extraction output, and the real `FunctionCallTracker.Tools` list captured through the extraction agent factory. The tracker is detached in `finally`. Extraction configures no tools, so the captured function-call list is genuinely empty; no calls are fabricated or tools added.

The evaluator treats source/output/tool content as untrusted evidence, not instructions. Assertions require nonempty results, every stable criterion description exactly once, evidence-based judgements, and no failed criteria; failures include their judgements. This is a nondeterministic live evaluation, not an offline proof of extraction quality. Both extraction and judge use `CustomOpenAIClient` with `gpt-4o-mini`; the same default-off network/billing gate applies.

### Execution seams

Every model-backed path uses the actual production agent with `CustomOpenAIClient` and either `LocalCustomAgent` (typed extraction/verification) or the production harness (search/optimization). Harness local memory, skills, todo, and mode facilities are disabled. Each test owns synthetic InMemory users/projects/timelines; no PostgreSQL, ambient history, repository conversations, or target-agent transport fakes are needed.

Optimization seeds current `Generated` candidates using a synthetic **non-target extraction provider**, never a paid extractor or embedding client. Prior facts are explicitly accepted through `TimelineFactService`. The planner exposes only production read-only search/fetch tools. Only the deterministic service stores the intermediate clarification proposal; the agent must leave accepted facts, source text, revision, and proposal count unchanged. No proposal is automatically accepted.

Search runs actual synthesis for answerable and insufficient-but-matching evidence. Insufficiency must still satisfy the production outer contract (nonempty valid citations). The unrelated/foreign-only query exercises the deterministic no-hit path and deliberately makes no model call. Raw unaccepted entry content cannot supply a budget answer.

The semantic verifier must return every supported assessment with valid source-bound IDs and descriptive finite scores in `[0,1]`. **There is no score acceptance threshold.** Negative cases require `InvalidResponse` / `SemanticUnsupported`; provider outages, arbitrary exceptions, refusals, and malformed results do not count as semantic rejection. The semantic-verifier suite uses no extra LLM judge; the separate extraction Judge case above does.

## Safe verification (no paid calls)

From the repository root, with dependencies already restored:

```sh
dotnet build dotnet/src/ProjectTimeline.BusinessLogic/ProjectTimeline.BusinessLogic.csproj --no-restore
dotnet build dotnet/tests/ProjectTimeline.Tests/ProjectTimeline.Tests.csproj --no-restore -p:BuildProjectReferences=false
dotnet test dotnet/tests/ProjectTimeline.Tests/ProjectTimeline.Tests.csproj --no-build --no-restore --filter "Category!=LiveModel&FullyQualifiedName!~ProjectTimeline.Tests.TimelineFactPostgresTests"
```

Build BusinessLogic first, then compile tests without rebuilding project references to avoid WebApi's frontend asset generation. Do not use an ordinary recursive test build. The offline filter excludes paid and real PostgreSQL execution. For discovery and explicit disabled execution, **Git Bash/POSIX** child-process environment syntax is:

```sh
PROJECT_TIMELINE_RUN_LIVE_AGENT_TESTS=0 dotnet test dotnet/tests/ProjectTimeline.Tests/ProjectTimeline.Tests.csproj --no-build --no-restore --list-tests --filter "Category=LiveModel"
PROJECT_TIMELINE_RUN_LIVE_AGENT_TESTS=0 dotnet test dotnet/tests/ProjectTimeline.Tests/ProjectTimeline.Tests.csproj --no-build --no-restore --filter "Category=LiveModel" --logger "console;verbosity=normal"
```

Expected: **17 discovered, 17 skipped, zero passed/failed live cases**. The gate is default-off, even if an API key exists. It is enabled only by the exact value `1`. Each live scenario has its own gated fact method: xUnit collapses a disabled theory into one skipped group, so separate facts keep every case individually discoverable/skippable without enabling the gate. There are no credential-bearing constructors, static initializers, or `MemberData` providers.

To check just the new Judge case safely (no model calls):

```sh
PROJECT_TIMELINE_RUN_LIVE_AGENT_TESTS=0 dotnet test dotnet/tests/ProjectTimeline.Tests/ProjectTimeline.Tests.csproj --no-build --no-restore --filter "FullyQualifiedName~TimelineFactExtractionLiveModelTests.Judge_evaluates_real_extraction_against_explicit_criteria"
```

**Do not load the old ignored local `AgentTests/OptimizeEntryLiveModelTests.runsettings`.** It still filters the retired suite. Leave unknown credential-bearing config untouched and use the current category/suite filters above; no new secret config file is needed.

## Separately authorized user-run paid execution

**Billing warning:** the following commands enable real requests. They are documentation for a future explicitly authorized run, not commands executed during T5. Provision credentials privately first; credential presence alone is not permission to run.

```sh
# Focused Judge case: one extraction plus one judge run; requires network and paid-call opt-in.
PROJECT_TIMELINE_RUN_LIVE_AGENT_TESTS=1 dotnet test dotnet/tests/ProjectTimeline.Tests/ProjectTimeline.Tests.csproj --no-build --no-restore --filter "FullyQualifiedName~TimelineFactExtractionLiveModelTests.Judge_evaluates_real_extraction_against_explicit_criteria"
# One suite first (Git Bash/POSIX); do not use the obsolete local runsettings.
PROJECT_TIMELINE_RUN_LIVE_AGENT_TESTS=1 dotnet test dotnet/tests/ProjectTimeline.Tests/ProjectTimeline.Tests.csproj --no-build --no-restore --filter "Category=LiveModel&FullyQualifiedName~TimelineFactExtractionLiveModelTests"
# All four suites, only after cost approval:
PROJECT_TIMELINE_RUN_LIVE_AGENT_TESTS=1 dotnet test dotnet/tests/ProjectTimeline.Tests/ProjectTimeline.Tests.csproj --no-build --no-restore --filter "Category=LiveModel"
```

Credentials resolve only **inside opted-in method bodies** via `Support/LiveModelCredentials.cs`: a nonblank `OPENAI_API_KEY` environment value wins, otherwise the optional dedicated test User Secrets store (`ProjectTimeline.Tests-e1899e37-47c2-4d29-9aca-d7e656777469`) is consulted. No project `.env`, launch settings, or local runsettings are read by that helper. User Secrets is outside Git but **not encrypted at rest**. Missing credentials fail before requests; credential-read errors are sanitized. Do not put keys in commands, source files, chat, logs, or reports, and do not print HTTP headers/payloads. Commands above scope the gate to the child process; no permanent environment change is required.

### Cost and evidence limits

All scenarios currently use `gpt-4o-mini`. A full successful run starts **20 logical model runs**: extraction 9 (including three sequential calls and the Judge case's extraction), judge 1, search 2, optimization 2, semantic verification 6. Search's third case is intentionally local. Tool rounds and SDK structured-result processing can make multiple completions per logical run; **20 is not a request/cost ceiling**. The production planner bounds read tools, not total billing. The explicit-key client disables transport retries and uses a 90-second network timeout; typed extraction/verifier output is bounded by production limits. The Judge case reuses the framework client for grading; there is no embedding model or monetary budget cap. Run a single suite first and monitor billing outside test output; reruns incur cost.

Report model/scenario identities, passed/failed/skipped counts, actual call/cost evidence if available, and failures honestly after any authorized execution. T5 verifies implementation, compilation, offline oracles, discovery, and safe skip structure only; **live behavior remains unverified**.

## Retained deterministic infrastructure

`CustomOpenAIClientIsolationTests`, credential/gate tests, injected transport controls, immutable `ConversationFixtures`, and `FakeConversationRepository` remain unchanged. Scripted responses and fixture windows are synthetic infrastructure evidence, never model-quality evidence. All live suites reuse `IsolatedAgentCollection` (`DisableParallelization=true`) and the existing `LiveModelFact` gate; the shared `LiveModelTheory` gate is also retained unchanged.
