# Logfire HTTP sampling correction

## Objective
Restore agent trace export beneath ASP.NET requests by instrumenting the HTTP server root coherently, without overriding upstream sampling decisions or exporting sensitive request data.

## Authorization and diagnosis
- User explicitly approved minimal ASP.NET request instrumentation plus an HTTP integration regression test after read-only diagnosis.
- Standalone synthetic export succeeded HTTP200 and user confirmed visibility in Logfire. Actual TestServer reproduction using production registration proved: no parent exports1; unrecorded local HTTP parent drops agent child (exports0); temporary AlwaysOn control exports1 but overrides incoming unsampled intent and is NOT the fix.
- Live WebAPI effective environment remains inaccessible (AccessDenied); this fix addresses the proven sampling defect, not a claim that all live runtime configuration was inspected.
- Preserve current feat/agent-framework-telemetry work, Search Facts correction, user config and unrelated dirty/untracked files. No commits/push/PR without explicit request. Do not read or print secrets.

## Scope and constraints
- Keep default ParentBased sampling. Add minimal ASP.NET server-root instrumentation so framework agent/session descendants are sampled coherently; honor sampled and unsampled incoming traceparent.
- HTTP exported data is an explicit allowlist: normalized method, fixed route template if safely available, status code. Safe static display names only. No raw URL/path/query (Search Facts query contains user question), headers, host/user-agent, request/response bodies, credentials, arbitrary exception messages/stacks/events or untrusted tags.
- Register privacy sanitation before exporter processing; verify the final OTLP payload, including error paths. Restrict to server HTTP activities; preserve existing safe agent/session attributes and parentage.
- Export remains opt-in with valid Logfire endpoint+token. No extra outgoing HTTP/client/SQL/model/tool instrumentation or metrics/log export. No unconditional AlwaysOn, no broad package upgrades or audit suppression.
- Add only the stable compatible ASP.NET instrumentation dependency needed for the fix; existing core/exporter/hosting remain1.15.3 unless hard compatibility evidence requires a narrow correction.
- No production restart, additional real telemetry submission, paid model request, DB migration or live host mutation. All verification uses TestServer and fake exporter transport.

## Tasks
- [x] H1 (done) Add deterministic actual HTTP regression RED, implement server-root instrumentation with fail-closed request privacy and update guide. Route: delegated gentle-ai-worker; mandatory multi-file implementation/preparation.
- [x] H2 (done) Assess exact candidate and run independent verification/build, remote sampling and wire-privacy checks. Route: native ASSESS then independent gentle-ai-verify when required; unknown assessment treated high.

## Acceptance criteria
- A fresh actual in-memory HTTP request produces a recorded server root and agent.harness.run child sharing trace/parent identity using production registration and fake model/transport.
- Explicit incoming sampled context propagates; explicit incoming unsampled context produces no HTTP/agent/session exports. Root sampling is not forcibly changed to AlwaysOn.
- Query/header/body/exception/payload sentinel never appears in exported OTLP bytes or span display names/attributes/events/status descriptions; route template cannot include user values.
- Existing framework/session accounting and source privacy tests remain green. Missing Logfire config still registers no provider/exporter from this feature.
- Relevant .NET suites and host build pass; no live Logfire or process claim beyond observed evidence.

## Verification plan
Test-first missing HTTP child export before behavior change, minimum GREEN, then sampled/unsampled/malformed-parent/error/privacy alternatives.
- dotnet test dotnet/tests/ProjectTimeline.Tests/ProjectTimeline.Tests.csproj --filter "FullyQualifiedName~LogfireHttpTelemetryTests|FullyQualifiedName~LogfireTelemetryTests|FullyQualifiedName~AgentTelemetryTests|FullyQualifiedName~AgentCostSessionTests|FullyQualifiedName~AgentCostIntegrationTests|FullyQualifiedName~TimelineFactInformationTests"
- dotnet build dotnet/src/ProjectTimeline.UI.WebApi/ProjectTimeline.UI.WebApi.csproj
- Broad offline regression at H2 only after safety readback: dotnet test dotnet/tests/ProjectTimeline.Tests/ProjectTimeline.Tests.csproj --no-build --no-restore --filter "Category!=LiveModel&FullyQualifiedName!~BusinessTests&FullyQualifiedName!~TimelineFactPostgresTests"
- Existing build-generated wwwroot updates were already present and deliver the Search Facts fix; do not restore to HEAD or manually edit compiled assets. Report incidental churn.

## Evidence and progress
- Pre-implementation source spot check confirms LogfireTelemetry currently AddSource(ProjectTimeline.Agents) and OTLP only, no explicit sampler or ASP.NET instrumentation.
- Official SDK troubleshooting explicitly documents this ASP.NET unsampled-parent scenario: https://github.com/open-telemetry/opentelemetry-dotnet/blob/main/docs/trace/customizing-the-sdk/README.md.
- Contrib docs describe default raw URL/path/query/user-agent tags; default query redaction alone is insufficient because tag names and user data must be excluded entirely. Privacy processor must precede exporter; https://github.com/open-telemetry/opentelemetry-dotnet-contrib/blob/main/src/OpenTelemetry.Instrumentation.AspNetCore/README.md.
- H1 implemented: AddAspNetCoreInstrumentation retains default ParentBased; request enrichment captures normalized method and clears tracestate before descendants. Privacy processor precedes exporter and emits only fixed HTTP request name, allowlisted method and valid status. Routes omitted entirely; all other HTTP tags/descriptions removed. Event/link-contaminated HTTP roots are dropped rather than exporting payloads; this may leave parent gaps but preserves privacy.
- H1 dependency: OpenTelemetry.Instrumentation.AspNetCore1.15.2 (stable catalog requires core>=1.15.3); existing core/exporter/hosting1.15.3 retained. Low-severity/all-dependency auditing enabled, no suppression or warnings.
- H1 RED8failed/161passed before implementation; GREEN169/169 zero skipped.10 new actual HTTP cases cover matching root/session/harness parentage, sampled/unsampled/malformed parent, opt-in, final wire privacy, errors and unknown methods. TestServer activity completion awaited before flushing. Host build0warnings/errors, scoped whitespace check passed. Existing bundleindex-D5Um0w5p.js unchanged by build regeneration.
- Native ASSESS after writer unassessable due pre-existing untracked declaration requirements; high-risk fallback requires independent verifier (H2). No native review approval claimed; RDD off.
- H2 independent PASS:169/169 focused,748/748 broad offline zero failures/skips, build0warnings/errors and scoped diff whitespace check. No retries, audit warnings or severe candidate-caused findings. Final protobuf tests verify query/path/header/body/host/useragent/tracestate/baggage/exception/straytags/displaynames absent; incoming unsampled HTTP yields zero exports despite fake model execution.
- Parent post-verifier spot check: dotnet test dotnet/tests/ProjectTimeline.Tests/ProjectTimeline.Tests.csproj --no-build --no-restore --filter "FullyQualifiedName~LogfireHttpTelemetryTests" passed10/10, no skips. New source/helper is untracked but fully reviewed/tested; normal diff --check alone does not cover untracked files.
- Minimal HTTP roots may now appear even when no facts cause agent execution; that exact no-match OTLP combination has no dedicated wire test, while existing no-model-construction tests and HTTP root tests both pass. Contaminated HTTP root drop may leave safe child-parent gaps.
- Live WebAPI and configuration remain untouched/unverified; no extra real telemetry or model requests. User must restart WebAPI with launch profile to load changed registration/dependency, run their search and inspect ProjectTimeline.WebApi / HTTP request -> agent.harness.run after exporter delay. Actual post-restart delivery remains pending. Commit evidence: none (not authorized).

## Next step
Offline correction and independent verification complete on feat/agent-framework-telemetry. User restarts their WebAPI using ProjectTimeline.UI.WebApi launch profile and verifies new real request/agent spans in Logfire; current process was not restarted. No commits/push/PR created.
