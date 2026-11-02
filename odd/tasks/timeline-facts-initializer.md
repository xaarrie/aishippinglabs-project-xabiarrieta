# Timeline facts initializer

## Objective
Create a console application to extract and automatically accept facts for TimelineEntries with zero physical TimelineEntryFacts rows.

## Scope and constraints
Reuse existing extraction and acceptance validation, with explicit existing actor identity and normal write authorization. Default dry-run; explicit --apply authorizes runtime mutations/paid extraction. Never fabricate clarification answers or accept unrelated pre-existing proposals. Skip/report existing work, unanswered questions, empty extractions, stale/concurrent changes and access failures. No database migrations, production connections or paid calls during development. Preserve pre-existing dirty/untracked files. No commits without explicit user request; commit evidence remains pending.

## Tasks
- [x] T1 (done): Implement dedicated console, register solution, add deterministic fake-provider tests and usage documentation. Observe focused RED/GREEN where runnable; isolated output avoids locked WebApi outputs and UI hooks.
- [x] T2 (done): Assess candidate and independently verify focused tests/build, inspect structural integration and document remaining limitations.

## Acceptance criteria
Only entries with no fact rows are eligible, including invalid rows as existing. Dry-run has no provider calls/writes; apply uses an active authorized actor and persisted validated proposals. Existing evidence/work is not overwritten; repeated runs are safe. Connection secrets are injected, never command arguments/logs; no automatic migrations. Cancellation and per-entry failures are reported with meaningful exit status.

## Evidence
Exploration: TimelineFactService.AcceptAsync validates actor, revision, evidence and questions; extraction creates pending proposals; existing enqueue skips proposals/jobs. Git branch: main-to-facts-interaction; extensive pre-existing changes protected.
Writer evidence: meaningful initial RED, GREEN acceptance, 30 initializer tests passed; 167 offline fact tests passed (relational/live excluded); console build 0 warnings/errors. Offline-only cached restore explicitly approved by user. Native ASSESS unavailable because worktree has undeclared untracked files; conservative high-risk plan requires independent verifier. No native review requested (RDD off). Work-unit commits withheld because user has not requested commits.

## Next step
Operator: privately configure database connection and active actor, review dry-run, then explicitly run --apply if approved. PostgreSQL/live provider and concurrency execution remain pending; no commits created.

## Independent verification
PASS: 30 initializer tests, 167 total offline fact regression tests (includes initializer), 0 failed/skipped. Console build 0 warnings/errors. DLL --help passed independently and parent spot-check passed. No blocking offline finding. Exit codes 2/130 inspected structurally, not process-exercised. Historical RED is writer-observed only. PostgreSQL transaction/concurrency/schema/pgvector and real provider remain unverified; no DB or paid calls. Native assessment unavailable from undeclared pre-existing untracked files; high-risk fallback independent verification completed. RDD off.
