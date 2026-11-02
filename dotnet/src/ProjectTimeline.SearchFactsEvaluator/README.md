# Capture Search fact evaluation runs

Run CSV questions sequentially through production `SearchEntriesAgent`, without correctness scoring, retries or corpus changes. Outputs contain sensitive facts and source text: keep them local, outside the repository, and never commit or upload them.

## Configuration

Use the existing FactsInitializer environment convention. Provision privately in the launching process:

| Variable | Required for |
| --- | --- |
| `ConnectionStrings__ProjectTimeline` | All modes; PostgreSQL connection with Host, Database and Username, plus privately supplied authentication |
| `OPENAI_API_KEY` | Execution only; not required or read by discovery/preflight |

No `.env`, appsettings, runsettings or user-secret files are loaded or changed. Connection values and credentials are never printed or written to artifacts. Prefer a database account restricted to SELECT. Every database operation runs inside a PostgreSQL read-only transaction, disposed without commit; no migrations, seeding, WebApi, workers or extraction are started. Production revalidates persisted active identity, role and authorization, not historical IDs.

## Quick path (PowerShell, repository root)

Build once with the same isolated artifacts path as the tests:

```powershell
dotnet build dotnet/ProjectTimeline.sln -c Release -p:SkipClientBuild=true --artifacts-path dotnet/tests/ProjectTimeline.Tests/bin/search-eval-build
$runner = 'dotnet/tests/ProjectTimeline.Tests/bin/search-eval-build/bin/ProjectTimeline.SearchFactsEvaluator/release/ProjectTimeline.SearchFactsEvaluator.dll'
dotnet $runner --list-scopes
```

Discovery prints only currently authorized candidate actor/owner IDs, project IDs/code/name, timelines and coverage (including zero eligible facts). It does not select a scope. Choose IDs from that output; do not reuse historical IDs blindly.

```powershell
$csv = 'C:/Data/formacion/TimelineSpecifications/search_facts_evaluation_scenarios.csv'
$out = Join-Path (Split-Path $csv) ('search-facts-' + [guid]::NewGuid().ToString('N') + '.jsonl')
$scope = @('--actor-id', '<chosen-actor-guid>', '--timeline-id', '<chosen-timeline-guid>', '--expected-project-id', '<chosen-project-guid>')
dotnet $runner --input $csv @scope --output $out --preflight
# Only after inspecting coverage and authorizing paid execution:
dotnet $runner --input $csv @scope --output $out
```

`--preflight` validates the entire CSV, both output paths, persisted authorization, timeline/project association and eligible coverage. It makes no model requests and creates no result or manifest files. Unknown/inactive actors, unauthorized/mismatched scopes and zero eligible facts are refused. Missing extraction and pending reviews are recorded, never repaired automatically.

## Input and isolation

CSV is strict UTF-8 (optional BOM), with a unique, nonblank header row and an exact `question` column. Quoted commas, escaped quotes and embedded CR/LF are preserved. Header order, all decoded field values, whitespace and exact questions are retained. Duplicate headers (case-insensitive), blank records/questions, malformed quoting, missing/extra fields and questions rejected by production validation fail the whole input before execution. Empty metadata values are valid and retained. Ordinals are one-based logical scenario rows, not physical line numbers; any positive scenario count is supported.

Only `question` reaches Search; category/type/other fields cannot control paths, options or scope. Each row creates a new DbContext, agent, tracker and cost session. Production uses `gpt-4o-mini` and the same operation budgets, with isolated tool-free typed planning and synthesis stages. The server performs one direct search batch and one complete aggregate source fetch when facts were emitted; no model copies or selects source IDs. No eligible facts skips the model, and a zero-emission plan skips fetch/synthesis. A typed application refusal returns fixed `Refused / SafeRefusal`, not arbitrary model prose; native provider refusal remains an error. Harness file memory, skills, mode/todo providers and web search are disabled. No telemetry exporters/host are initialized, and SDK sensitive-content telemetry is disabled. Launch without external auto-instrumentation that independently exports process content.

## Local output contract (schema version 1)

- `--output` must be a new `.jsonl` beside the input. Both it and `<output>.manifest.json` are checked, then reserved using `CreateNew` before model calls. Existing artifacts are never overwritten or resumed. A reservation race can leave an empty reserved file; use a fresh name.
- Each JSONL line is flushed to disk before the next row. It includes run/scenario IDs, byte-level source SHA-256, original ordered fields/question/ordinal, scope/observed role and coverage, UTC start/end and monotonic elapsed milliseconds, outcome, the complete typed `TimelineFactInformationResponse`, returned messages, cost snapshot and safe error type/code. Failed/cancelled outcomes have no response. A returned `Partial`, `Empty`, `InsufficientEvidence` or `Refused` is a response, not an execution exception or correctness verdict. `Refused / SafeRefusal` has fixed server-owned text and empty facts/citations/source contexts; retain that distinction when reconciling outcomes.
- A runner-owned `ICustomAgent` decorator forwards the explicit structured-run overload and **accumulates returned messages across planning and synthesis**, including across stage `Init` calls. It captures text, compatibility function names/arguments and JSON-like results **before lazy typed-result access**; this survives later synthesis/provenance failures. Production Search has zero registered model tools, so direct server retrieval is represented by the final response/evidence and synthesis input, not fabricated tool messages. A structured adapter/leaf validator can reject before any response reaches the decorator; such a failed stage has no captured returned messages, while an earlier completed plan remains captured. Raw SDK representations, additional properties, headers and exception objects are excluded. Unsupported content kinds are marked; unsupported payload objects become null. The interface exposes no in-flight messages: mid-agent failures/cancellation can lose partial messages. This is not a wire transcript or raw provider-response capture.
- Cost snapshots are production observations/estimates, not invoices. Unknown usage/cost stays null or explicitly incomplete; a null snapshot means no session evidence was available. The manifest keeps observed per-row cost sessions rather than inventing provider totals.
- The adjacent manifest includes schema/run ID, source hash/path, initial scope/coverage/model, UTC times, returned/failed/cancelled/persisted/unstarted counts and cost sessions. It starts `Interrupted` and becomes `Completed` only after all terminal rows are durable. `Completed` can include row failures. It is refreshed after each line; after a hard crash JSONL is authoritative and manifest accounting may lag (or a manifest rewrite may be incomplete). Reconcile before evaluation; there is no resume or automatic retry.

## ID-only citation diagnostics

Production sessions also emit optional `citationDiagnostics`, snapshotted after the operation and session disposal for returned, failed and cancelled rows. Legacy sessions default to null and omit this field; historical missing IDs cannot be reconstructed.

- `available`: copied fact IDs, context EvidenceIds, explicit omitted EntryIds, exact distinct counts and evidence truncation from the **actual synthesis input projections**, recorded before synthesis. It is null when synthesis availability was never captured.
- `observed`: separate fact/context cited GUID sets, valid-GUID occurrence/distinct counts, unknown sets/counts and invalid-GUID counts. Leaf admission observes both arrays before membership/content rejection; typed injected agents have a fallback. Repeated validation replaces observations, not increments them. Malformed JSON/envelopes or missing/ambiguous citation arrays leave observation unavailable (null), not empty.
- Each diagnostic ID list retains at most 128 sorted GUIDs, with exact unclipped counts and `diagnosticClipped`. The actual immutable membership catalogs/schema enums are **never clipped**. No raw invalid strings, question/evidence/model text or exception details enter this snapshot. Returned messages remain the separate sensitive-content contract above.
- `finalValidationAttempted` means final **ledger** validation was attempted; `finalLedger` exists only after it succeeded. Early leaf rejection has neither an attempted final validation nor fabricated final empty sets. `publicationAttempted`/`published` distinguish final response construction from success. No extra validation/read is introduced.

Synthesis uses fresh, separate GUID enums for projected fact IDs and context EvidenceIds. An empty catalog keeps a required array with typed GUID items and `maxItems: 0`, never `enum: []`; leaf validation independently requires `[]`. Parsed GUID membership accepts uppercase D-format equivalents, while emitted enums are canonical. Final response guards remain unchanged. Offline wire tests do not establish live provider acceptance or semantic answer quality.

## Safe failure diagnostics

The optional `agentFailure` row field now classifies the **terminal outer row exception**, including session construction/disposal, lazy typed-result parsing, final ledger revalidation and synthesis rejection. Search/tool translations preserve original inner exceptions internally; only safe projections enter artifacts. The decorator still rethrows the identical exception and provides supplementary evidence, never overriding the final domain failure. Successful rows omit `agentFailure`; old rows/diagnostics without the additive fields remain readable. Missing historical causes cannot be recovered retroactively.

Public HTTP error contracts are unchanged: normalized `error`, catch precedence, budgets and retries remain intact. The additive application outcome `Refused / SafeRefusal` is represented only as a validated response, never by converting an execution exception. HTTP still returns the original fixed bodies/statuses (403 access, 404 scope, 409 evidence conflict, 504 deadline, 499 request cancellation, 502 invalid answer); diagnostic fields are evaluator-only, not additions to public HTTP errors.

| Field | Fixed output vocabulary |
| --- | --- |
| `category` | `Research`, `Synthesis`, `Timeout`, `Evidence`, `Budget`, `Scope`, `Authorization`, `Database`, `Argument`, `InvalidOperation`, `Unsupported`, `HttpProvider`, `Transport`, `Cancellation`, `JsonSchemaParse`, `StructuredResponse`, `Other` |
| `exceptionType` | Fixed domain/framework labels in `AgentFailureDiagnostic.cs`, never runtime type names; domain subclasses precede generic bases |
| `reason` | Allowlisted research codes, synthesis rejection enum, budget/scope-safety reasons or fixed structured-response diagnostics (`ResponseSchema`, `ResponseEnvelope`, `ResponseRefused`, `ResponseTruncated`, `ResponseLimit`); unknown present reasons become `Other`, unavailable reasons stay null |
| `executionStage` | `Coverage`, `Initialization`, `Model`, `ResultParsing`, `Retrieval`, `Revalidation`, `Synthesis`; `Session` when no narrower Search boundary was observed |
| `causalChain` | Up to eight outer-to-inner safe classifications, including each node's reason and HTTP/provider metadata |
| `agentObservation` | Optional supplementary decorator classification when not already represented in the terminal chain |
| `httpStatus` | HTTP status 100–599 supplied by a client/HTTP exception; otherwise null |
| `providerCode` | `invalid_api_key`, `insufficient_quota`, `rate_limit_exceeded`, `model_not_found`; unknown present codes become `Other`, unavailable codes remain null |

The primary classification is the first recognized exception within eight `InnerException` nodes (only the first aggregate branch); the bounded chain also retains inner classifications. A terminal domain wrapper therefore wins over an SDK-loop cancellation observation. Provider codes come only from `error.code` in an existing SDK response's buffered content: at most 16 KiB, JSON depth 16. Missing/disposed/unbuffered, malformed or larger/deeper content leaves the code null. There is no message-text inference, stream read, new transport, tracing exporter, extra request or retry. Messages, bodies, headers, URIs, stacks and exception objects are never retained in the diagnostic; response JSON is inspected transiently only to select a fixed code.

This is failure-boundary evidence, not a root-cause verdict or proof of physical request/usage. Unknown causes remain `Other`; stage records where the terminal failure surfaced, not necessarily where its inner cause originated. A model's JSON `null` may fail in SDK lazy parsing before synthesis predicates run. In-flight/raw message capture remains unavailable. Returned source/model text remains captured as before, not redacted by the diagnostic projection. Preserve prior artifacts and authorize any new uniquely named run separately; no automatic replay is added.

Ordinary row errors are persisted and execution continues. Authorization/scope loss, no eligible corpus or database failure interrupts after persisting the active failure. Ctrl+C persists the active cancellation without using its cancelled token for output, then stops; unstarted rows are counted. Output/serialization failures stop immediately rather than risking unrecorded paid work. No arbitrary exception text is serialized. Exit codes: `0` successful preflight/discovery or all rows returned, `1` invalid arguments/input/configuration, `2` execution failure/interruption, `130` explicit cancellation. An uncatchable process termination cannot finalize accounting.
