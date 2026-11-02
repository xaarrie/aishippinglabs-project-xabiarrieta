# Strict entry_claims JSON schema fix

## Goal and authorization
Fix the reported OpenAI HTTP400 on entry creation: response_format entry_claims declares questions in properties but omits it from required. Strict OpenAI schemas require every property to be required. This is a source bugfix, not an entry-content error. No actual entry creation/provider/DB operations, restart or migration; preserve the user's current data after the separately completed global purge.

## Scope and constraints
Inspect the actual outbound SDK/native structured-response request. Add questions to the outbound strict schema required array while preserving legacy incoming responses without questions (DTO optional default[]), unknown-field/answer rejection, evidence grounding and error classification. Do not disable strict mode, remove questions or blindly normalize unrelated schemas. Preserve extensive dirty/untracked work; one source writer, targeted edits only, no staging/commit/push. All tests fake HTTP/SDK/provider, no credentials/.env/launchsettings reads. .NET uses fresh ignored --artifacts-path and -p:SkipClientBuild=true; no default locked outputs or wwwroot/Vite rebuild.

## Tasks
- [x] S1 (done): Add meaningful failing actual outbound strict-schema regression, minimally correct extraction schema, run focused fake-provider/native/legacy tests and document contract.
- [x] S2 (done): Independently verify actual emitted schema recursively, full offline regression/build and parent spot check; report live provider/deployment unperformed.

## Acceptance
Actual response_format JSON is json_schema name entry_claims strict=true; root and nested object required arrays cover ALL property keys, additionalProperties remains false. questions required in transport even when empty; valid no-question/questions responses accepted through real native pipeline with fake HTTP that rejects invalid schema as HTTP400. Legacy inbound omission remains supported. No weakened validation or real data mutations. Observe behavioral RED then GREEN, no compilation-only RED. Native ASSESS plan controls independent verification; never claim native approval if unavailable.

## Progress
Reconciled previous work against current source with an independent read-only explorer: extraction-specific required questions fix and actual SDK wire regression already exist. Prior writer recorded behavioral RED (3 failures), GREEN (249 passes), and isolated WebAPI build success in Engram observation 1206; these are historical evidence, not fresh results. No new source changes needed at this point. S2 now independently rechecks current tests and isolated build. Native ASSESS is unassessable due to undeclared untracked scope; its plan requires independent verification (RDD off). No commits authorized. Live provider/database operations and application restarts remain unperformed.


## Current independent verification
Strict-schema fake HTTP/native pipeline regression: 6 passed, 0 failed. Isolated WebAPI build: 0 warnings/errors. Broad offline run: 1189 passed, 11 failed because external artifact placement breaks upward repository-root lookup in source/fixture tests. LiveModel, PostgreSQL and credential-environment tests were explicitly excluded, not counted as skips. S2 remains incomplete; verifier is diagnosing/rechecking with unique repository-local already-ignored artifacts and the same offline filter, without source edits. No real provider calls or application restart performed.

## Final verification
Independent verifier reran the same offline filter with unique already-ignored artifacts under dotnet/tests/ProjectTimeline.Tests/bin/entry-claims-verify-20260720-b9d642a1: 1200 passed, 0 failed, 0 skipped. All 11 external-artifact discovery failures resolved without source changes. WebAPI build succeeded with 0 warnings and 0 errors using -p:SkipClientBuild=true. Parent spot check confirmed extraction-specific schema adds required questions and retains legacy DTO compatibility. LiveModel, PostgreSQL, and credential-environment tests remain excluded; no live provider/DB check or restart performed. RDD off; no native review approval claimed. No commits authorized or created. Next user action: stop/rebuild/relaunch the running application and retry entry creation against updated binaries; actual runtime version is not verified.
