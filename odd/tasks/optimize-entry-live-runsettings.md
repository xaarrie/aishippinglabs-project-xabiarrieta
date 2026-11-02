# OptimizeEntry live test local settings and explanations

## Intent
Create local runsettings enabling paid OptimizeEntryLiveModelTests, copy OPENAI_API_KEY privately from the WebApi launchSettings.json, and ignore the generated file. Explain each scenario, assertion and evaluation inline without requiring helper navigation.

## Constraints
Preserve all existing tracked and untracked changes. Never print credentials or put them in memory, tracked files or logs. Do not run paid tests. Do not modify test behavior. No commits without explicit user request. Technical comments follow existing English convention.

## Tasks
- [x] T1: Create ignored local runsettings with opt-in and credential; verify XML and git ignore without displaying secrets.
- [x] T2: Add detailed inline explanations of scenarios, fixture expectations, helper assertions, evaluation limitations and shared harness assertions.
- [x] T3: Verify comment-only source changes and nonpaid tests; report failures and skipped paid execution.

## Acceptance and checks
Settings select the live test class and enable PROJECT_TIMELINE_RUN_LIVE_AGENT_TESTS=1. OPENAI_API_KEY comes from dotnet/src/ProjectTimeline.UI.WebApi/Properties/launchSettings.json. Ignore is in .gitignore, not only local excludes. XML is valid and git check-ignore succeeds. No paid requests during verification. Test code behavior unchanged. Build/nonpaid tests as environment permits; no meaningful RED for comments and local configuration, so structural and ordinary functional checks apply.

## Evidence
Environment has no OPENAI_API_KEY. Metadata-only source inspection found the key in WebApi launchSettings.json (value not displayed). Existing worktree has substantial unrelated modifications and untracked AgentTests.

## Next step
Complete. User may explicitly run paid tests using the local settings. No paid execution or commits performed.

## Writer verification
T1/T2 completed. XML and source credential equality verified privately; git check-ignore passed twice and parent spot-check passed. Comment-stripped executable source equals pre-edit baseline. Nonpaid test run: 353 passed, 0 failed. Paid runs skipped. Initial XML-comment generation/regex checks failed and were corrected before successful verification. No commits or staging.

## Independent verification
PASS: 353 passed, 0 failed, 0 skipped; no warnings in the authorized nonpaid test command. XML, gate, exact class filter, key nonempty/source equality all true (private checks only). Ignore rule confirmed at .gitignore:1. No inaccurate comments found against assertion helpers and fixtures. Existing tracked/untracked status inventory preserved. Executable-token comparison is writer evidence only because the existing untracked source has no HEAD baseline. Test command also builds Vite; ignored output byte preservation not checked. Native ASSESS unavailable due to untracked scope; treated conservatively with independent verifier, RDD off. Paid tests intentionally skipped.
