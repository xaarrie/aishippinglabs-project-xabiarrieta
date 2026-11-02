# Web dependency build repair

## Objective and problem
Restore the .NET web build blocked by security audit errors for System.Security.Cryptography.Pkcs 9.0.0 and the downstream missing WebApi reference DLL.

## Scope and constraints
- Preserve unrelated pending application changes, generated frontend assets and existing task documents.
- Keep NuGet auditing and TreatWarningsAsErrors enabled; do not suppress NU1902/NU1903.
- Confirm the dependency chain before applying a minimal targeted PackageReference change in dotnet/src/ProjectTimeline.UI.WebApi/ProjectTimeline.UI.WebApi.csproj.
- Both official advisories GHSA-2fmv-rv2c-59x7 and GHSA-846f-f764-76mx list 9.0.21 as patched in the 9.x line (10.0.13 in 10.x). Prefer the minimal compatible patched 9.x line over broader upgrades.
- Current branch: feat/optimize-entry-review. No commits, staging, publishing, unrelated source changes or memory-runtime repair without an explicit request.
- Original dependency fix uses restore/build as the functional regression check.
- Follow-up authorization: user said `corrigelo` after the SearchTools test compiler blocker was explained. Align the stale test with existing SearchEntries API; preserve snapshot/order coverage and explicitly verify timeline isolation and no-match behavior. Do not implement speculative entry-fetch API or change production behavior.

## Tasks and acceptance
- [x] T1 (completed): Observe baseline restore failure, confirm resolved dependency path, apply the smallest patched dependency change and self-verify restore and audit. Preserve all baseline dirty files. Acceptance: audit remains enabled, vulnerable version no longer resolves, and targeted restore and vulnerability listing pass. Full build belongs to T2 because it regenerates existing frontend assets.
- [x] T2 (completed): Independent normal solution restore/build, complete tests, vulnerability scan, DLL existence and exact original asset restoration all verified. Parent repeated complete tests successfully. No commits requested.
- [x] T3 (completed): Corrected stale BusinessTests.cs search API usage to SearchEntries, with ordered-list, no-match and cross-timeline assertions. Observed compiler RED then focused/full GREEN; preserved earlier dirty imports/client changes and snapshot assertions. No production behavior changed; no commit requested.

## Checks and evidence
- Read-only exploration found EF Core Design 10.0.0 and net10.0 with warnings treated as errors; restore and dependency graph subsequently confirmed the chain.
- Baseline RED: `dotnet restore dotnet/src/ProjectTimeline.UI.WebApi/ProjectTimeline.UI.WebApi.csproj` exited 1 with NU1902 and NU1903. SDK 10.0.401.
- `dotnet nuget why ... System.Security.Cryptography.Pkcs` confirmed EF Core Design 10.0.0 -> Microsoft.Build.Tasks.Core 17.14.28 -> PKCS 9.0.0, and paths through Microsoft.CodeAnalysis.Workspaces.MSBuild 4.14.0.
- Frontend Vite configuration uses emptyOutDir; T2 must snapshot and restore existing wwwroot bytes before/after full build, with no permanent frontend changes.
- T1 GREEN: same restore exited 0 after adding a direct `System.Security.Cryptography.Pkcs` 9.0.21 reference with `PrivateAssets=all`; exactly two csproj lines added (comment and reference).
- `dotnet nuget why` resolves all PKCS paths to 9.0.21. `dotnet list ... package --vulnerable --include-transitive` exited 0 and reported no vulnerable packages. Writer diff check passed.
- `gentle-ai review mode status`: off globally, clone-local unset; native review not started.
- Native assessment failed because unrelated untracked files lack an explicit declaration; returned risk unassessable and required an independent verifier. No authority mutation attempted.
- Independent verifier: targeted restore, full WebApi build including npm/Vite, vulnerability scan and diff check all exited 0. Web build: zero warnings and errors. PKCS resolves to 9.0.21.
- Reference DLL exists (22,528 bytes); incremental build reused it. Newly regenerated timestamp is not required to resolve the original missing-DLL symptom; no forced rebuild performed.
- `dotnet test dotnet/tests/ProjectTimeline.Tests/ProjectTimeline.Tests.csproj --no-restore` exited 1: CS1729 at BusinessTests.cs:116,118; no tests executed. Calls use `new SearchTools(db)` while SearchTools.cs:63 declares `SearchTools()` with no arguments. Read-only diagnosis found no evidence linking this API mismatch to PKCS. Origin could not be established from the dirty state. This was the initial blocker; follow-up T3 and final verification below resolved it.
- Original wwwroot path/length/SHA256 manifests matched after restoration, including untracked assets. Verifier observed .vs cache churn and BusinessTests.cs becoming dirty before its protected run; attribution unverified. No cleanup or source fixes attempted.
- Parent spot check reran targeted restore (exit 0) and read back exactly two added csproj lines.
- Evidence and complete asset backup: external temp directory web-t2-verification-e87a7ef80e334b9ab17de0ac8c4a041a.
- T3 RED: focused `dotnet test ... --no-restore -p:BuildProjectReferences=false --filter FullyQualifiedName~Snapshot_numbers_content_and_scoped_search_are_deterministic` exited 1 with CS1729 before edits.
- T3 GREEN: same focused command exited 0 (1 passed); full test command with BuildProjectReferences=false exited 0 (51 passed, 0 failed, 0 skipped). Writer diff check passed; bounded test edit 8 added lines, 3 removed. Existing project-reference outputs were reused in this initial self-verification; normal independent full build/tests subsequently passed as recorded below.
- Follow-up assessment again returned unassessable due untracked-file declarations; independent verifier required, native review remains off.
- Final independent checks: `dotnet restore dotnet/ProjectTimeline.sln` exit 0; `dotnet build dotnet/ProjectTimeline.sln --no-restore` exit 0, zero warnings/errors; `dotnet test dotnet/ProjectTimeline.sln --no-build --no-restore` exit 0, 51 passed, 0 failed, 0 skipped.
- Final vulnerability scan and `git diff --check` both exited 0; no vulnerable packages or whitespace errors (line-ending notices only). Reference DLL exists, 22,528 bytes.
- Original/backup/final wwwroot path, length and SHA256 manifests matched: 3 files, 251,427 bytes. Targeted diffs unchanged by verification; no source-manifest changes. Only .vs cache status churn observed, with attribution unverified.
- Final external evidence/asset backup: web-t2-t3-final-verification-f3ns34tc under the session temp directory. Initial verifier Python wrapper had a syntax error before executing any checks or mutations; corrected wrapper completed successfully.
- Parent spot check: repeated `dotnet test dotnet/ProjectTimeline.sln --no-build --no-restore`, exit 0, 51 passed, 0 failed, 0 skipped.
- Commit: not requested; no commit created.

## Continuity
Initial Engram context/search/review calls failed. Later save/readback succeeded (feature observation 877; bugfix observation 878). Memory configuration was not changed by this repair.

## Next step
Complete. The solution builds with zero warnings/errors, all 51 tests pass, and the vulnerability scan is clean. Existing source changes and hosted assets were preserved. No pending functional checks for this bounded fix; no browser/database checks required because production/frontend behavior did not change. No commits or publishing performed.
