# Global entry and fact database purge

## Status and authorization
COMPLETE — committed and independently audited on 2026-10-26. User explicitly requested all entries and everything related to facts globally, then selected confirm_global_entry_fact_purge_with_backup. Verified prior full backup was mandatory. Preserve users/projects/timelines/unrelated rows and physical artifact files. This was an administrative data reset, NOT a change to application deletion behavior (that proposed code change was canceled).

## Completed tasks
- [x] R1: Confined ignored operator, configuration/target/FK/trigger preflight and independent safety review.
- [x] R2: Same-snapshot full backup verified before explicit transactional purge; preservation/fence checks and confirmed commit.
- [x] R3: Independent fresh read-only database audit, full archive read/hash verification, preserved-row digests and physical/source nonmutation checks.

No application code, schema, migrations, provider/model calls, export, restart, staging or commit was performed. Operator/backups/receipts exist only in ignored dotnet/tests/ProjectTimeline.Tests/bin/global-entry-fact-purge/. Work-unit commits are explicitly unauthorized in this session.

## Verified destination
localhost:5432 / ibernovatimeline; actual address ::1; PostgreSQL18.6; database OID16771; system identifier7684935310142328392. Appsettings/user-secrets targets agree; no guessed default. All19 physical tables/FKs/triggers reviewed. Target fingerprint bad5be5d9b8f72515c5b9445ed655377ee10e0942209ed066e5d8e81d482ff0c. Scope fingerprint54c5d5790cae2d16d4b69928f06b1b28c3dfe645c60352da5815f4f497d52469. Credentials loaded privately; never exposed in arguments/output/receipts.

## Actual database result
| Target table | Before | After |
|---|---:|---:|
| TimelineEntries | 8 | 0 |
| TimelineEntryFacts (including embeddings) | 34 | 0 |
| TimelineFactJobs | 42 | 0 |
| TimelineFactProposals | 5 | 0 |
| TimelineFactAdditions | 21 | 0 |
| TimelineFactInvalidations | 5 | 0 |
| TimelineEntryClarifications | 0 | 0 |
| TimelineArtifacts (DB metadata only) | 0 | 0 |
| TimelineSnapshotEntries | 0 | 0 |
| TimelineSnapshots (entry-derived summaries) | 0 | 0 |

Preserved whole-row contents/digests: Users1,UserRoles2,Projects1,ProjectParticipants1,ProjectRoles3,Timelines1,MergeRequests0,PasswordRecoveryStates0,migration history10. Only Timeline.FactRevision incremented exactly once to fence stale work; other timeline metadata unchanged. Numeric revision values were not retained individually; exact Id/revision+1 digest matched (one row):6ca59f4015b3e4704db2f3bd3a54374b4da1a0d413d133be787d5aebd0552f89.

All physical artifact file metadata remained unchanged, digest6238b0cf35c26e4cbfbec5d0fefbe7781889d5bef6a8cd30868600fdab552b4d. No physical files were removed. Application source and existing Git status were unchanged.

## Backup and receipts
Archive: dotnet/tests/ProjectTimeline.Tests/bin/global-entry-fact-purge/database-20261026T131914319.dump
- Full database custom-format pg_dump using exported snapshot0000000C-00000054-1 from the same held transaction.
- Size74085bytes.
- SHA256:8de3a0844e78c3d09923db8d97d241ae03b4f4b60a8915017c7f7bb67a15ed02.
- PGDMP header, size/hash and complete pg_restore decode to discarded stdout verified BEFORE mutation and independently again afterward. No restoration connection/SQL execution.
- Private backup-directory permissions hardened only when creating the authorized backup; archive contains application data and must not be published.

Prepared receipt: apply-20261026T131915549.json.
Durable committed receipt: apply-20261026T131915549.json.committed.json (Committed=true).
First fresh audit: audit-20261026T131916097.json.
Independent R3 audit: audit-20261026T132214756.json.
All receipt names above are relative to the ignored operator root.

## Execution and verification evidence
Write-blocking locks compatible with pg_dump reads held throughout export/backup/purge. Only explicitly reviewed target tables were truncated, without CASCADE, identity reset, schema drop, trigger/guard disabling or unrelated deletes. Preservation/zero/fence checked before commit. Parent invoked reviewed corrective APPLY exactly once after user confirmation and independent R1 PASS. Worker role refused destructive execution before invocation; no earlier APPLY or ambiguous commit occurred. Parent received exit0 and durable committed receipt, then separate connections confirmed zero targets.

R1 test-first guard checks observed missing-mode/target-alias RED before correction. Final72 self-tests passed independently and in parent spot check; fresh operator build0warnings/errors. Independent review corrected all11 installed framework connection-prefix aliases/direct normalized keys and moved ACL hardening out of read-only modes. Independent read-only preflight verified identical target/scope/counts and no ACL/source/file mutation. No destructive live RED was attempted.

Independent R3 executed audit exactly once, exit0, on a fresh explicitly read-only SQL connection. Whole preserved-row digests, expected revision fence, actual archive full decode/hash and zero counts passed. Only a new safe audit receipt was written; no database/ACL/source changes or second APPLY.

## Limitations and reporting notes
Actual restore/recovery was NOT executed; archive full readability is verified, not a restore drill. Audit JSON currently displays ReadOnly:false due to a labeling defect (only preflight is labeled true), but audit code explicitly uses/validates SHOW transaction_read_only=on; independent inspection confirmed read-only execution. This cosmetic operator-report defect was documented rather than changing the reviewed binary after the purge.

Native ASSESS remained unassessable due to existing untracked scope, so independent review was required and fulfilled; no native approval is claimed. Application deletion guards remain unchanged. Existing bulk activation B2/B3 are superseded/canceled by this confirmed reset and must not be resumed against deleted IDs; prior34facts are historical only. User may refresh the application to observe the empty entry/fact state.
