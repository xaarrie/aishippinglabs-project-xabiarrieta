# Engram runtime and WebApi build repair

## Objective
Restore Pi memory integration without losing stored data, and unblock WebApi compilation without suppressing security audits.

## Scope and constraints
- Engram 1.20.0 is incompatible with installed gentle-engram 0.1.16 (requires instance-id from >=2.0.0-rc.11).
- Use compatible stable Engram 2.2.1 for the existing Pi package; do not jump to v3 whose mutation contract requires expected_project absent from currently loaded tools.
- Preserve existing memory, global configs, pending feature changes, generated assets and other clients. Do not kill unverified processes or reset/reinitialize databases.
- Four existing engram.exe processes found; none listening on7437 at investigation. Their ownership/mode requires confirmation before interference.
- Back up SQLite with online backup API including WAL, verify integrity and record row counts before any migration. Test migration on an isolated copy first.
- PKCS9.0.0 enters WebApi through Microsoft.Build.Tasks.Core17.14.28 (and older MSBuild workspace floor), from EF design dependencies. Confirm complete chain before targeted fix.
- Official GHSA-2fmv-rv2c-59x7 and GHSA-846f-f764-76mx specify patched9.0.21 or10.0.13; retain security audits and warnings-as-errors.
- No commits/push without explicit request. No unrelated application refactors.

## Tasks
- [ ] T1 (in progress): Back up memory and prepare checksum-verified Engram2.2.1; test compatibility against isolated copied store.
- [ ] T2 (pending): Apply minimal patched dependency resolution and observe build RED/GREEN without security suppression.
- [ ] T3 (pending): Activate compatible Engram safely, verify native memory tools and existing-data preservation; request user action if other clients or host reload block safe activation.
- [ ] T4 (pending): Independently verify WebApi build/startup and memory health, document remaining baseline failures and recovery steps.

## Checks
- Official release archive SHA256 against published checksums; binary version/instance-id support.
- Consistent backup integrity and aggregate row counts, isolated migration preserving existing records.
- WebApi restore/build with audit on, no NU1902/NU1903, reference DLL present.
- Runtime smoke with temporary diagnostic signing key and unreachable database, no production migrations.
- Memory read and explicit config save/search roundtrip, no blind retries on ambiguous writes.
- Standard solution/tests may expose pre-existing BusinessTests.cs failures; report separately.

## Progress
- Diagnosed installed binary and package versions; verified current memory tool initialization fails.
- Confirmed current advisory fixes from official GitHub advisories.
- Engram memory mirror pending until runtime restored.

## Next step
Create consistent memory backup and test released compatible binary on isolated data.
