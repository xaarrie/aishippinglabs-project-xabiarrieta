# Direct optimizer evaluations

These tests call `OptimizeEntryAgent` directly with synthetic conversation context. Normal tests need no API, database, frontend, credentials, or network. Live evaluations use the real OpenAI model **gpt-4o-mini**; skipped or scripted tests do not establish model quality.

## Context is loaded by C#, not by the model

Every anchored `RunAsync`, including clarification rounds and retries, holds the existing consumer-identity gate while it:

1. Lists persisted IDs once and snapshots the returned order. Null, empty, duplicate or empty-GUID windows fail closed; they are never silently normalized.
2. Awaits exactly one content fetch per ID in returned order, including the current entry last. There is no GUID sorting, parallel fetch, truncation or cross-run context cache.
3. Builds the internal envelope below, initializes the consumer with **no history tools**, and invokes the typed review once. Constructor-time consumer initialization is deferred.

```json
{
  "original_message": "<entire original caller string, preserved exactly>",
  "review_context": {
    "entries": [{ "entry_id": "<persisted ID>", "content": "<full persisted content>" }]
  }
}
```

`original_message` retains JSON or plain text, all unknown fields, original source, prior draft, answers, Unicode and whitespace. Caller-supplied IDs/context cannot replace the C# selection. Persisted entries are chronological prior context with the current entry last; all source/context text is untrusted data, not role or tool instructions. No digest is retrieved or invented. Null-tools legacy runs forward the original string unchanged.

Each actual application read reports a local `FunctionCall` with **no SDK ID**; progress and completion reuse that same object. A failed attempt is marked executed with a null result and sanitized exception. List, first/later fetch, malformed-window and canceled-read failures stop before consumer initialization or model invocation: **zero model calls**, including after partial reads. Downstream provider/consumer errors retain their own error boundary.

This guarantees application retrieval order, not internal model attention order or semantic completeness.

## Normal verification

From the repository root, with dependencies already restored:

```sh
dotnet build dotnet/src/ProjectTimeline.BusinessLogic/ProjectTimeline.BusinessLogic.csproj --no-restore --verbosity minimal
dotnet test dotnet/tests/ProjectTimeline.Tests/ProjectTimeline.Tests.csproj --no-restore -p:BuildProjectReferences=false --filter "Category!=LiveModel" --verbosity minimal
dotnet test dotnet/tests/ProjectTimeline.Tests/ProjectTimeline.Tests.csproj --no-build --no-restore --filter "Category=LiveModel&FullyQualifiedName~AgentTests" --verbosity minimal
```

The last command must visibly **skip all six live methods** with an opt-in reason when the gate is absent (zero paid calls). Enabled expansion is **11 cases**, before any additional clarification-round requests. `BuildProjectReferences=false` reuses existing WebApi/Database reference assemblies; this is not a fresh full-application build.

## Local credentials outside Git

Live evaluations automatically resolve `OPENAI_API_KEY` in this order:

1. A non-whitespace environment value overrides local credentials; the local store is not opened.
2. Otherwise, read the optional **dedicated test .NET User Secrets store**, bound to the test assembly's `UserSecretsId`: `ProjectTimeline.Tests-e1899e37-47c2-4d29-9aca-d7e656777469`.

Populate only the `OPENAI_API_KEY` entry securely in that dedicated store, outside the repository. It is **not the WebApi store**, a project environment file, or a launch-settings reader. User Secrets stays outside Git but is **not encrypted at rest**. Do not put credential values in command arguments, logs, chat, fixtures, or test reports, or use commands that dump the store. Existing launch settings are not changed or imported by these tests.

Credential sources are selected only during explicitly enabled live execution, never during test discovery or disabled runs. Missing/whitespace credentials fail clearly; configuration-read failures are sanitized without inner diagnostics. Resolution does not change process-global environment. Production constructors remain environment-only, and deterministic tests inject synthetic sources without opening the developer's store.

## Explicit live execution

1. Securely populate the dedicated test User Secrets entry above, or preconfigure the environment override outside the test process. Credential setup alone does not authorize model calls.
2. Set `PROJECT_TIMELINE_RUN_LIVE_AGENT_TESTS=1` in that environment (PowerShell: `$env:PROJECT_TIMELINE_RUN_LIVE_AGENT_TESTS = "1"`). Only the exact value `1` enables execution.
3. Run the live filter above, optionally adding `--logger "console;verbosity=detailed"` to see scenario identities, actual structured responses, and tool traces. This makes paid external requests. Unset the gate afterward.

With the gate explicitly enabled but no key, each executing live evaluation **fails clearly before model invocation and semantic assertions**. No fake keys are installed by this suite. The earlier paid-run permission is exhausted; setup or a previous authorization does not authorize another run.

All live tests use `Category=LiveModel` and a nonparallel collection that cannot overlap other collections, including legacy environment-key mutation tests.

## Scenarios and fixtures

| Evaluation | Contract |
| --- | --- |
| OE-01 | Clear software/travel/business assignments: Ready, no optional-field questions or invented planning fields; ambiguous CSV/PDF reference: clarification. |
| OE-02 | Enrich one prior planned login team with Luis/Pablo, retain provenance and planned tense, preserve existing Marta, no invented roles/exclusivity. |
| OE-03 | Entry-only UI/API ownership, password/no-SSO, tentative approval/date, lockout/exception/retention; Spanish travel and business condition/unit detail sets. |
| OE-04 | Controlled **input draft**: Q1=date, Q2=owner; date-only answer leaves stable Q2 pending, then owner answer yields Ready. Separate actual-first-result roundtrip accepts the model's own question grouping. |

OE-05's three pure retrieval-failure rows are now deterministic controls, not paid cases. `OptimizeEntryAgentFailureTests` and the actual-harness integration cover list/first-fetch/later-fetch failures with zero consumer/transport requests and sanitized local tracking. OE-01 through OE-04 semantic oracles and fixture facts are unchanged.

`Support/ConversationFixtures.cs` is the shared three-domain corpus. `Support/LiveEvaluationFixtures.cs` extends its immutable context windows and supplies the controlled OE-04 input. Every answer round sends the original source **alongside**, not replaced by, `previous_result`.

## Isolation and evidence boundaries

Live tests create a real in-memory `OpenAIClient` with the resolved key, select **gpt-4o-mini**, adapt it with `AsIChatClient()`, and pass that transport to `CustomOpenAIClient(transport, isolateHarnessFacilities: true)`. There is no fake live transport. Harness 1.24.0's supported disable options isolate file memory, cwd skill discovery, todo/mode providers, and web search. File access/background agents remain unconfigured (opt-in). Defaults for existing callers are unchanged. Apart from the optional dedicated credential store, live tests never open repository/private files.

`CustomOpenAIClient(IChatClient, bool)` is the injected-transport seam used consistently by `GetChatClient`, `CreateAgent`, and `CreateHarnessAgent`. `CustomOpenAIClientIsolationTests` exercises the **actual** production harness, local wrapper and optimizer with scripted transport: success receives the prepared envelope and no history tools in one request; each preload failure produces zero requests. Generic provider/factory isolation controls remain. Its wire drafts are not model output or quality evidence.

| Deterministic coverage | Replacement contract |
| --- | --- |
| `OptimizationContextLoadingTests` | Exact unsorted ID order/count, awaited reads before Init/Run, zero tools, intact envelope, forged-context isolation, malformed windows, per-run ID snapshot, changed clarification context, actual local tracking identity. |
| `OptimizeEntryAgentInfrastructureTests` | Injection/legacy constructors, scoped read-only windows and tracker wiring replace SDK registration assertions. |
| `OptimizeEntryAgentFailureTests` | Zero consumer calls on list/first/later failure or cancellation, sanitized attempts, fresh retry and unchanged downstream error boundaries replace recovery-veto/stale-wrapper checks. |
| `OptimizationAsyncGuardTests` | Async preload failure/cancellation, same/shared-consumer serialization through preload and model work, tracker isolation, independent-consumer overlap replace wrapper-lifetime checks. |

The optimizer-only SDK termination tests and execution guard are retired. EF window tests remain unchanged. Successful live traces require exact per-round read/tracker count and order, not `Distinct` normalization or invented SDK IDs.

`EvaluationOracleTests` tests the oracle itself with positive and mutated negative controls. `EvaluationAssertions` combines typed status/IDs, pending-reference invariants and bounded lexical relations within Entry components; Questions/ChangeSummary echoes do not count as factual coverage. It does not compare exact prose or search whole-result JSON.

**Lexical limits:** relation/negation/unit patterns can miss valid paraphrases or accept contradictions elsewhere. A regex hit is not independent semantic verification. Human inspection of the actual Entry, source provenance and trace is still required; passing these checks is not proof of completeness. Do not weaken an oracle merely to obtain a passing model score.

## Historical results and limits

The previous, separately authorized paid run used the old model-driven retrieval path: **14 expanded cases, 2 passed, 12 failed, 0 skipped**. Both passes were OE-05 retrieval failures; many semantic assertions were blocked by earlier chronology failures. That result is historical, not a quality score for this C# preload implementation. No new paid execution was authorized or performed for T6.

Current T6 verification: **353 deterministic tests passed** and **6 disabled live methods visibly skipped**. The initial behavioral RED was 9 failed / 2 passed before production changes; expanded context-loading checks now pass all 18 cases. Default skips, synthetic drafts and deterministic retrieval checks provide **no new model-quality evidence**.

Any future paid run or repetition requires separate authorization. Retain every run's model/scenario, passed/failed/skipped counts, structured response, application-owned read trace and criterion failures. Report denominators and repetitions, not only favorable samples. Requested model identity is not a guarantee of an immutable provider revision. Logs contain synthetic evaluation material and whitelisted call metadata, never raw provider errors or credentials.
