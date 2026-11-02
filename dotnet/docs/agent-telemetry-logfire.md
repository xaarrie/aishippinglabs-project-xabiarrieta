# Export agent runs and operation costs to Logfire

The Web API can export minimal HTTP server roots, native agent/chat/tool spans and an operation's final token/cost summary over OTLP HTTP/protobuf. **Conversation and tool content capture is ON by default, including production. Set `Logfire__CaptureAgentContent=false` to opt out.** Export itself remains **off without both endpoint and token**. The host owns provider startup, flushing and disposal; accounting works without Logfire or a listener.

**Privacy warning:** configured exports disclose SDK-visible questions, answers, instructions, tool definitions, arguments/results and identifiers. Secrets or personal data embedded in that content cannot be guaranteed absent: disclosure of conversation/tool content is intentional. Credential/HTTP/exception metadata filtering is not a content-secret detector. Restrict project access and retention; opt out before running sensitive conversations.

## Quick setup

1. Choose the Logfire project region explicitly. Use its full trace endpoint:
   - EU: `https://logfire-eu.pydantic.dev/v1/traces`
   - US: `https://logfire-us.pydantic.dev/v1/traces`
2. Inject the following settings externally. Supply the project's write token through your deployment secret manager or private environment injection, never source files, shell arguments/history, captured output, appsettings or launch settings.

   | Configuration key | Environment variable | Value |
   | --- | --- | --- |
   | `Logfire:Endpoint` | `Logfire__Endpoint` | One of the full regional endpoints above; required to enable export. |
   | `Logfire:Token` | `Logfire__Token` | Raw write token; required to enable export. Do **not** prepend `Bearer`. |
   | `Logfire:ServiceName` | `Logfire__ServiceName` | Optional non-sensitive service label; default `ProjectTimeline.WebApi` when absent or blank. |
   | `Logfire:CaptureAgentContent` | `Logfire__CaptureAgentContent` | Boolean, **default `true`**. Explicit `false` restores payload-private native export. Does not enable export by itself. |

3. Loading changed code or configuration requires a host restart. Obtain explicit approval before starting or restarting a running Web API; the offline checks do not restart it. With approval and the usual database, authentication and application settings already configured, start from the repository root:

   ```sh
   dotnet run --project dotnet/src/ProjectTimeline.UI.WebApi/ProjectTimeline.UI.WebApi.csproj --no-launch-profile
   ```

   Normal startup still initializes the database; telemetry does not bypass those prerequisites. An explicitly approved agent action can invoke a paid model. Do not invoke one merely to verify configuration.
4. After an approved HTTP agent run, look for the configured service, an `HTTP request` server span and its `agent.run` or `agent.harness.run` descendants in Logfire. An ordinary plain/harness wrapper automatically produces one `agent.session` summary around the run when no session is current; explicit caller sessions instead own the aggregate across runs. Real delivery requires valid credentials and network access; the offline tests below do not prove acceptance by Logfire.

## Group several agents into one cost session

1. Initialize agents normally, before or inside the operation. `AgentCostSession.Start()` uses the [dated built-in USD catalog](agent-model-pricing.md). To override it, supply an explicit dictionary of **decimal per-million-token** input/output rates and an optional cached-input rate, all in one currency. An explicit dictionary, even empty, is authoritative; other currencies need explicit rates, with no implicit FX.
2. Start one explicit `AgentCostSession` around all awaited work for the operation. Use a generated or trusted **opaque `Guid`**, never a customer name or prompt-derived identifier. Currency must be three uppercase letters (for example, `USD`); the scope does not perform currency conversion.
3. Read immutable snapshots as needed. Dispose in LIFO order after awaiting every call; disposal freezes the final snapshot and stops the session span exactly once.

```csharp
using ProjectTimeline.BusinessLogic.Agents.Framework;

static async Task<AgentCostSnapshot> RunOperationAsync(
    ICustomAgent firstAgent,             // Already initialized through the shared factories.
    ICustomAgent secondAgent,
    string input,
    IReadOnlyDictionary<string, AgentTokenPricing> externallyConfiguredPrices,
    Guid opaqueOperationId,
    string currency)
{
    var session = AgentCostSession.Start(
        externallyConfiguredPrices, opaqueOperationId, currency);
    using (session)
    {
        var first = await firstAgent.RunAsync(input);
        await secondAgent.RunAsync(first.Text);
        var progress = session.GetSnapshot(); // Point-in-time totals; IsDisposed is false here.
        // Check progress.Totals.IsEstimatedCostComplete before treating cost as a full estimate.
    }
    return session.GetSnapshot(); // Final frozen totals, also summarized on agent.session.
}
```

Construct each externally configured entry as
`new AgentTokenPricing(inputRatePerMillion, outputRatePerMillion, cachedInputRatePerMillion)`
under the **exact provider model ID**. Rates must be nonnegative; do not substitute zero for a missing rate. The session copies the dictionary, so later caller edits cannot change its pricing. `Start` can generate the opaque ID when its optional `sessionId` is omitted; default currency is `USD`. `AgentCostSession.Current` flows through async execution, not global process state. Nested sessions attribute calls to the innermost scope, restore their parent on disposal, and do not roll child costs into the parent.

### Accounting rules and limits

| Topic | Contract |
| --- | --- |
| Provider boundary | `CreateAgent` and `CreateHarnessAgent` install usage tracking immediately around SDK-backed or injected clients, before SDK tool aggregation. Each actual task-based provider call counts once, including each tool turn. `AgentResponse` aggregate usage is never added again. Structured validation sits outside this tracking boundary. |
| Exact model mapping | A nonempty response `ModelId` wins, even if unpriced. Only a missing response ID falls back to requested/configured model. Matching is case-sensitive; there is no alias or fuzzy lookup. |
| Known versus full cost | `KnownEstimatedCost` sums only priceable components; it can be zero while cost is unknown. `EstimatedTotalCost` is nullable and only represents a full estimate when `IsEstimatedCostComplete` is true. Neither is an invoice. Missing catalog entries are unknown, even for a zero-token call. |
| Tokens and subsets | Input and output are charged once. Cached input is a subset of input; a differing cached rate needs a valid reported cache count. Reasoning is a subset of output, never an additional charge. Missing optional subsets are not invented as zero. |
| Partial usage and failures | Missing, negative, contradictory or overflowing totals stay null/incomplete, not zero. Known valid components can still contribute partial cost. A provider exception/cancellation counts as an unknown-usage/cost call, since billing may have occurred. A structured-validation rejection after a response retains its observed usage. Cancellation before reaching the provider adds no call. Missing reported total tokens can make usage incomplete while valid input/output still permit a full cost estimate. |
| Lifetime | Unscoped plain/harness wrappers own a session through the awaited run and wrapper stop, including errors/cancellation. Nested wrappers reuse it. Explicit caller scopes remain undisposed and authoritative across runs; await all work before LIFO disposal in the creating logical flow. Early disposal freezes in-flight incompleteness and ignores late results. No persistence, restart recovery or automatic DI/business-operation boundary is added. |
| Raw clients and streaming | The public `GetChatClient` getter retains its existing behavior and injected-client identity; raw calls through it are untracked. Raw consumers may explicitly install one `UsageTrackingChatClient` at the provider boundary. Streaming delegates unchanged and is **untracked**; task-based plain/harness/structured runs use the tracked nonstreaming path. |

### Final session span

With a listener, `agent.session` starts at `Start` and ends at `Dispose`, including operations with no provider calls. It preserves any ambient request parent and parents wrapper run spans throughout the operation. The host already registers its source, `ProjectTimeline.Agents`; no extra host setup is needed. Without a listener or when sampling drops the span, accounting still works. Export remains subject to sampling and host flush/lifetime.

Every summary attribute uses the `agent.session.` prefix:

| Attributes (suffixes) | Type and meaning |
| --- | --- |
| `id`, `currency` | Opaque GUID text and validated single currency. |
| `call_count`, `in_flight_call_count`, `unknown_usage_call_count`, `unknown_cost_call_count` | Integer call totals. In-flight calls are separate from completed calls with unknown usage/cost. |
| `input_tokens`, `output_tokens`, `total_tokens`, `cached_input_tokens`, `reasoning_tokens` | Integers, emitted only when known. |
| `known_estimated_cost`, `estimated_total_cost` | Numeric doubles, emitted only when nonnull. Known partial cost must be read alongside completeness flags; it is not an unknown full cost replaced by zero. |
| `usage_complete`, `estimated_cost_complete` | Boolean flags; incomplete totals must not be interpreted as complete billing. |

All calculations and snapshots use decimal arithmetic. OTLP trace attributes have no decimal scalar, so cost exports convert to IEEE-754 doubles: binary rounding and precision loss are possible, especially for large values. No locale-dependent cost strings are emitted. The summary excludes model-price keys, arbitrary configuration/provider strings, payloads and exception details. An empty session legitimately has complete zero totals; missing usage/pricing does not.

## Configuration behavior

- Both endpoint and token absent/blank: only the immutable capture policy is registered, not an OpenTelemetry provider, listener or exporter. No default localhost collector is created. Service name or capture setting alone does not enable export.
- One immutable `AgentTelemetryOptions` snapshot drives both native instrumentation and export filtering. Extraction/verifier, review and Search Facts receive the same host policy; later configuration mutation does not split their behavior. Applying a changed setting requires a user-approved restart.
- Only one endpoint/token present: startup fails with a fixed configuration error, without printing supplied values.
- Invalid endpoint/token: startup fails before registration. Only EU/US HTTPS endpoints with `/v1/traces`, the default HTTPS port, and no user information, query or fragment are accepted. Base URLs and trailing slashes are rejected.
- Tokens may contain ASCII letters, digits, underscores, hyphens and dots. The exporter sends `Authorization=<write-token>` directly; it does not add a Bearer scheme. Header-list delimiters, whitespace and line breaks are rejected.
- Programmatic HTTP/protobuf endpoints are used **as-is**: `/v1/traces` is not appended automatically. This differs from generic OTLP base-URL environment behavior. `Logfire:*` explicitly supplies the trace endpoint, protocol, auth and service name; generic `OTEL_EXPORTER_OTLP_*` values are not a substitute for opt-in configuration.
- No process environment variables or sampler overrides are installed. The default parent-based sampler records fresh HTTP roots and preserves incoming W3C sampling intent: sampled parents retain their trace/parent IDs; unsampled parents suppress HTTP, agent and session exports, not accounting. Malformed parents are treated as fresh roots by ASP.NET. An unconditional `AlwaysOn` sampler would override upstream intent and is not the fix. Keep standard sampler settings, service names and externally configured resource attributes under trusted deployment control and free of secrets.

## What is captured—and what is not

The host registers `ProjectTimeline.Agents` (`AgentTelemetry.SourceName`), the exact `ProjectTimeline.Agents.Native` (`AgentTelemetry.NativeSourceName`) source and minimal ASP.NET HTTP server-root instrumentation. Instrumenting the root lets default parent-based sampling record agent descendants instead of inheriting an unrecorded local HTTP parent. The wrapper spans cover `LocalCustomAgent` run overloads and `LocalCustomHarnessAgent` runs, including structured validation, errors and cancellation. They preserve ambient parentage and export only allowlisted metadata: agent/model labels, wrapper/run kind, outcome and error type.

Both shared factories explicitly set the SDK's `EnableSensitiveData` from the immutable policy: **true by default**, false only when requested. With capture on, native conversation/tool values are preserved in full, not summarized or reconstructed. Only data supplied by the SDK is available; unavailable hidden model reasoning is neither guaranteed nor fabricated.

With `Logfire__CaptureAgentContent=false`, content tags and IDs are removed and labels are coarsened as described below. This flag alone is insufficient: SDK exception text still reaches raw activities, so the host sanitizes native spans **before** batch/export processing in both modes. Other consumers subscribing to the native source need an equivalent boundary; do not export it directly.

Non-host callers also default to capture on. Explicitly opt out per client, for example `new CustomOpenAIClient(chatClient, isolateHarnessFacilities: false, telemetryOptions: new AgentTelemetryOptions(false))`. Existing no-argument and credential constructors retain their signatures and use the same true default. The raw injected `GetChatClient` identity is unchanged; it is not silently decorated.

Direct agent-factory consumers, including `JudgeAgent`, receive native `invoke_agent`/`chat` spans and **accounting under an active session**, but no wrapper `agent.run` span is invented for them. Raw `GetChatClient` calls and streaming remain untracked unless a raw task-based client is explicitly decorated as described above. There is no metrics or log export, outgoing HTTP/client/database/embedding instrumentation, or SignalR/Blazor tracing. Other hosts must register their own tracing provider if they want export.

### Native operation tree and privacy boundary

Offline tests using installed Agent Framework **1.24.0** and real fake-client tool loops observe this sanitized tree for both plain and harness wrappers:

```text
HTTP request                         # When invoked through HTTP
└─ agent.session                     # Automatic for wrappers, or explicit caller scope
   └─ agent.run / agent.harness.run   # Wrapper, including structured validation
      └─ invoke_agent                # Native SDK agent invocation
         ├─ chat                     # First actual provider call
         ├─ execute_tool             # Actual function invocation, when requested
         └─ chat                     # Next actual provider call
```

Without a wrapper or explicit session, native spans parent directly to the ambient activity (or form a root), with no automatic cost aggregate. `JudgeAgent` and direct factory consumers follow that rule. Tool spans are siblings of provider chat spans, not inferred from completed response messages. Structured runs preserve this native tree; validation occurs after the native invocation and a rejection is recorded on the outer wrapper, not retroactively on a successful native span.

| Detail | Observed behavior / export policy |
| --- | --- |
| Plain factory | `UseOpenTelemetry` explicitly sets `EnableSensitiveData` to the shared capture value and activates the SDK's deferred chat slot below tool aggregation. |
| Harness factory | Disable the default-source wrapper with `DisableOpenTelemetry = true`; explicitly install `OpenTelemetryChatClient` below the harness tool loop with the **same capture value as the agent wrapper**. Outer auto-wiring alone emits no harness chat spans. No duplicate decorators are installed. |
| Source metadata | Installed native agent/chat/tool activities report an **empty scope version** and no scope tags. The boundary already permits this; arbitrary versions or scope tags fail closed. |
| Names | Display names remain fixed: `invoke_agent`, `chat`, `execute_tool`, or `other`. Native tool names and call IDs survive only as explicitly admitted string tags with capture on. |
| Labels | Capture on preserves native string agent/model/provider labels. With capture off: agent names `timelineFactInformation`, `timelineFactOptimization`, `timelineFactExtraction`, `timelineFactSemanticVerifier`; model `gpt-4o-mini`; provider `openai`. Other labels become `other`. |
| Counts | Only nonnegative SDK integer input/output usage counts survive. Usage tracking remains below aggregation, once per actual provider call; native aggregate counts never add accounting calls. |
| Failure privacy | Tags are rebuilt from the allowlist; status descriptions and tracestate are cleared. SDK error/cancellation spans with only mutable exception metadata survive sanitized; the original exception identity and wrapper outcome remain unchanged. |
| Immutable payloads | Native events or links cause the **entire native span to be dropped**, as do unsafe immutable scope metadata. No event/link reflection is used. Safe outer failure/session spans remain, but dropped parents or children can leave trace gaps. |

### Verified native content fields

Fake-provider/tool-loop tests against Agent Framework **1.24.0** and Microsoft.Extensions.AI **10.10.0** observe these **string tags**, not events. Only these vetted content/identifier keys are admitted with capture on; arbitrary `gen_ai.*` tags are not trusted.

| Exact keys | Native data |
| --- | --- |
| `gen_ai.input.messages`, `gen_ai.output.messages` | SDK JSON with roles, text, tool-call IDs/names/arguments and tool responses across turns. |
| `gen_ai.system_instructions`, `gen_ai.tool.definitions` | Instructions and function definitions, descriptions and parameter schemas; harness instructions are included where the SDK supplies them. |
| `gen_ai.agent.id`, `gen_ai.response.id` | Available native agent and provider-response identifiers. |
| `gen_ai.tool.name`, `gen_ai.tool.type`, `gen_ai.tool.description` | Executed tool metadata. |
| `gen_ai.tool.call.id`, `gen_ai.tool.call.arguments`, `gen_ai.tool.call.result` | Actual function invocation identifier and serialized arguments/result. |

Native string `gen_ai.agent.name`, `gen_ai.request.model`, `gen_ai.response.model` and `gen_ai.provider.name` are also retained with capture on. Unsupported or future SDK keys remain excluded until verified. Provider errors/status descriptions, HTTP headers (including HTTP user-agent), arbitrary tags, events, links and unsafe scope metadata remain filtered in **both** modes. A tool or conversation that intentionally returns a secret or exception as ordinary content still discloses that content.

No wildcard subscription, metrics/log exporter, sampler override, extra provider request or retry is added. Tool execution and per-provider chat counts are verified for plain, harness, typed and structured paths with fake providers. These are offline observations, not a running-host or live Logfire delivery receipt.

### HTTP privacy boundary

| Exported | Deliberately excluded |
| --- | --- |
| Fixed `HTTP request` display name, normalized allowlisted method (`_OTHER` for unknown values), numeric HTTP status code when valid. Trace/span IDs, parentage, duration and status code remain intact. | All raw URL/path/query data, including query names; headers, host, user-agent, request/response bodies, arbitrary tags, exception text/stacks and status descriptions. |

Route tags are omitted entirely, even when routing is available: the boundary does not trust mutable `http.route` attributes or dynamic display names. No user path value can become a route label. Incoming `tracestate` is stripped after context extraction and before agent descendants; traceparent identity and sampling still propagate. Baggage is not exported as attributes.

A narrow HTTP-only processor rebuilds allowlisted metadata **before** the OTLP batch processor can enqueue a span. SDK exception recording is explicitly disabled; query-redaction environment defaults are not the privacy control. Since `Activity` cannot remove events or links, an HTTP span containing either is dropped rather than exporting their potentially sensitive payloads. Its safe agent/session descendants remain exportable with their original parent IDs, so a deliberately dropped HTTP parent can leave a trace gap. Ordinary error responses still export sanitized HTTP status and no exception description. This boundary does not alter safe agent/session attributes.

## Offline verification

Run from the repository root; no Logfire credentials or model calls are required:

```sh
dotnet test dotnet/tests/ProjectTimeline.Tests/ProjectTimeline.Tests.csproj --filter "FullyQualifiedName~NativeAgentTelemetryPrivacyTests|FullyQualifiedName~NativeAgentIntegrationTests|FullyQualifiedName~LogfireTelemetryTests|FullyQualifiedName~LogfireHttpTelemetryTests|FullyQualifiedName~AgentTraceContentTests" -p:SkipClientBuild=true --artifacts-path dotnet/tests/ProjectTimeline.Tests/bin/agent-trace-c1

dotnet build dotnet/src/ProjectTimeline.UI.WebApi/ProjectTimeline.UI.WebApi.csproj -p:SkipClientBuild=true --artifacts-path dotnet/tests/ProjectTimeline.Tests/bin/agent-trace-c1
```

The ignored **in-repository** artifacts path avoids IDE/live-host DLL locks without stopping those processes and preserves repository-relative test fixtures. `SkipClientBuild=true` explicitly skips Vite's destructive output-directory cleanup during .NET telemetry verification; normal builds still rebuild the frontend. These checks do not touch existing frontend assets. `NativeAgentIntegrationTests` exercises actual factory native activities and the production host export pipeline with a sealed fake OTLP transport: tool loops, structured results/rejection, provider errors/cancellation, direct/Judge paths, concurrent parentage and absent-config/unsampled behavior. It decodes final protobuf and compares provider/native-chat/session counts and numeric costs, while explicit-false payload sentinels must be absent from all wire bytes. Default/true tests compare complete native content tags against final OTLP values, including instructions, definitions, arguments/results and IDs; costs and span counts remain unchanged. `AgentTraceContentTests` checks immutable host-policy registration, extraction/verifier client identity, actual search synthesis/tool execution and source-level production review/search composition without reading credentials or booting database initialization. `NativeAgentTelemetryPrivacyTests` additionally exercises immutable event/link/scope contamination and invalid usage values. The original raw SDK exception-leak characterization remains unchanged to prove the egress boundary is necessary.

`LogfireTelemetryTests` uses in-memory configuration and dummy credentials, replacing the SDK HTTP transport with a fake handler. It checks opt-in/invalid configuration, provider lifetime, source isolation, raw authorization, regional signal paths, resource service names, session parentage and actual protobuf numeric cost/token attributes. It also checks that a payload/model-price sentinel never reaches the wire. Integration tests exercise injected and fake-HTTP SDK clients, tool turns, structured validation, concurrent/nested sessions and unknown totals. `LogfireHttpTelemetryTests` runs real in-memory TestServer HTTP requests through production registration and an injected fake harness model. A propagation-only test listener models an unrecorded local hosting parent without forcing recording. The tests await HTTP activity completion before flushing, then decode final OTLP protobuf to verify the five-span HTTP → session → harness wrapper → native invocation → native chat hierarchy, sampled/unsampled/malformed parents, opt-out behavior, unknown methods, HTTP errors and fail-closed event/link handling. HTTP sentinels in query names/values, path, headers, body, exception, stray tags and display names remain absent in both capture modes, independently of deliberately disclosed agent-content sentinels. Nothing is sent to Logfire or a live model; Vite is explicitly skipped.

The host references `OpenTelemetry.Extensions.Hosting` and `OpenTelemetry.Exporter.OpenTelemetryProtocol`, both **1.15.3**, plus stable `OpenTelemetry.Instrumentation.AspNetCore` **1.15.2**. The official instrumentation release targets core **1.15.3**; NuGet restore audit remains enabled for all dependencies at low severity, with no audit suppression. Offline regression/build evidence does not prove that the running Web API loaded these changes or that Logfire accepted a live request.

## References

- [ASP.NET instrumentation 1.15.2 release and compatibility](https://github.com/open-telemetry/opentelemetry-dotnet-contrib/blob/96ef3bf6df7c93c59dce07fc0b0d2682d6797e76/src/OpenTelemetry.Instrumentation.AspNetCore/CHANGELOG.md)
- [ASP.NET instrumentation options and defaults](https://github.com/open-telemetry/opentelemetry-dotnet-contrib/blob/96ef3bf6df7c93c59dce07fc0b0d2682d6797e76/src/OpenTelemetry.Instrumentation.AspNetCore/README.md)
- [Official Logfire .NET setup](https://github.com/pydantic/logfire/blob/main/docs/languages/dotnet.md)
- [Official Agent Framework integration](https://github.com/pydantic/logfire/blob/main/docs/integrations/agent-frameworks/agent-framework-dotnet.md) (local factory composition and privacy policy are described above)
