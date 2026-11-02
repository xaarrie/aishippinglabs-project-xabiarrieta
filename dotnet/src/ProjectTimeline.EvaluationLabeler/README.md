# Label evaluation results locally

Read the exact question and full agent answer, then click **Good** or **Bad** to save a human decision immediately. This standalone .NET 10 Razor Pages app needs no API key, database, Python, or production WebApi startup. It never calls the agent or assigns labels automatically.

## Quick start

From the repository root, with the .NET 10 SDK installed:

```sh
dotnet restore dotnet/ProjectTimeline.sln --artifacts-path dotnet/tests/ProjectTimeline.Tests/bin/labeler-build --ignore-failed-sources -p:NuGetAudit=false
dotnet build dotnet/ProjectTimeline.sln --no-restore -c Release -p:SkipClientBuild=true --artifacts-path dotnet/tests/ProjectTimeline.Tests/bin/labeler-build
dotnet dotnet/tests/ProjectTimeline.Tests/bin/labeler-build/bin/ProjectTimeline.EvaluationLabeler/release/ProjectTimeline.EvaluationLabeler.dll --input "C:/Data/formacion/TimelineSpecifications/search-facts-evaluation-citation-allowlists-20261102T062417Z-f35d9311.jsonl"
```

Open **http://localhost:5198**. The supplied evaluation has 50 records. Starting the app and viewing records do not create any annotations. The first confirmed label creates this default CSV:

```text
C:/Data/formacion/TimelineSpecifications/search-facts-evaluation-citation-allowlists-20261102T062417Z-f35d9311.labels.csv
```

Stop with Ctrl+C. Restart with the same command to resume existing labels. Razor pages are compiled and JavaScript/CSS are embedded, so the built DLL works from the repository root without a project-directory working directory.

| CLI option | Meaning |
|---|---|
| `--input <results.jsonl>` | Required, existing UTF-8 evaluation JSONL; read-only. |
| `--labels <labels.csv>` | Optional CSV in an existing directory; defaults to `Path.ChangeExtension(input, ".labels.csv")`. Must not collide with the input or use link aliases. |
| `--port <1024-65535>` | Optional local port; default `5198`. |
| `--help` | Print usage without opening input, labels, or a listener. |

For browser testing, use a separate labels path under the ignored `dotnet/tests/ProjectTimeline.Tests/bin/labeler-browser/` directory, not the real labels CSV. Create that directory first. Do not annotate real results as a test.

## Labeling workflow

1. Read **Question** and **Full agent response**. Text is complete, selectable and scrollable, not rendered as executable HTML or Markdown. Expand the complete response JSON, saved messages, errors, diagnostics, or raw record for other saved fields. A null response explicitly says no final answer was saved; an empty answer is identified separately.
2. Click **Good** or **Bad**. There is no Save button and no automatic advance. **Saved** appears only after the server confirms durable persistence.
3. **Bad** initially saves `uncategorized` and focuses the category dropdown. Choosing a category saves again. **Good** clears any previous failure category.
4. Use **Previous**, **Next**, or **Jump to record**. All saved decisions remain editable. A fresh visit to `/` resumes at the first unlabeled record, then the first Bad still needing a category, then the first record if all are complete.

Progress shows labeled/total and how many Bad labels still need a category. Recorded outcomes and research statuses such as Refused/Partial/Complete are metadata, **not human labels**.

Canonical category codes (case-sensitive):

```text
uncategorized, hallucination, wrong-scope, incomplete, incorrect-answer,
wrong-citation, unnecessary-refusal, technical-error, other
```

**Download saved CSV** retrieves only this session's configured, unchanged labels file as `evaluation-labels.csv`; it does not choose arbitrary files or write labels. It is available after the first saved label. There is no reset/delete-labels control.

## CSV identity and encoding

Only annotated records appear in the CSV, in dataset order. Full answers are not duplicated; join labels back to the original JSONL for answers and raw details.

| Column | Version 1 contract |
|---|---|
| `version` | `1` |
| `datasetSha256` | Lowercase SHA256 of the exact JSONL bytes, including any BOM and line endings; **not** the original question CSV's `inputSha256`. |
| `runId`, `scenarioId` | Opaque, case-sensitive strings from JSONL; not restricted to GUIDs. The join key is `(datasetSha256, runId, scenarioId)`. |
| `ordinal` | Original positive scenario ordinal, for display/validation, not identity. |
| `question` | Original question metadata, with spreadsheet escaping below. Duplicate questions remain separate records. |
| `label` | Exactly `Good` or `Bad`. |
| `failureCategory` | Empty for Good; one of the canonical codes for Bad. `uncategorized` visibly needs follow-up. |
| `revision` | Positive per-record revision, incremented on every confirmed save. |
| `updatedUtc` | Round-trip ISO 8601 UTC timestamp with `+00:00` offset. |

The CSV is strict UTF-8 without BOM, with CRLF row endings. All fields are double-quoted; embedded quotes double and embedded commas/CR/LF are preserved (RFC 4180). The reader also accepts an optional UTF-8 BOM. It validates the entire file, including schema, identities, question/ordinal, labels, revisions and timestamps, before accepting any annotations.

### Reversible spreadsheet escaping

Only the three metadata columns `runId`, `scenarioId`, and `question` may receive one extra leading apostrophe:

- Ordinary metadata is unchanged, so normal IDs join directly to JSONL.
- Prefix values beginning with an apostrophe, or containing a leading control character (including after leading whitespace).
- Prefix values whose first non-whitespace character is `=`, `+`, `-`, or `@`.

For example, `opaque-run:0001` stays unchanged, `=SUM(1,2)` becomes `'=SUM(1,2)`, and an original `'literal` becomes `''literal` (before normal CSV quoting). On read, strip **exactly one** leading apostrophe if present; otherwise keep safe text unchanged. Unsafe unprefixed metadata is rejected. The same decoder accepts the earlier version-1 writer's always-prefixed metadata, including ordinary IDs, without discarding labels. A subsequent successful save uses the minimal escaping rule; no migration overwrite happens on startup. Decode these metadata cells before joining dangerous/apostrophe-shaped IDs. The JSONL remains the exact source text.

## Persistence and recovery

Each save serializes a complete next CSV into a unique temporary file in the same directory, flushes it to disk, atomically replaces the target, and only then publishes the new in-memory revision. Failed commits retain the old CSV and state. Do not treat an unconfirmed click as saved.

| Situation | Action |
|---|---|
| Another labeler owns the CSV | Stop that writer, then restart. An exclusive `<labels.csv>.lock` prevents simultaneous processes. The sidecar may remain after exit; its presence alone is not an active lock. Do not delete it to bypass a writer. |
| Stale tab / revision conflict | That click was not saved. Reload saved state, review the current decision, then label again. |
| CSV changed externally | No overwrite is attempted. Stop the app, preserve the external file, and restart to validate/reload it. A page reload alone does not import disk edits. Downloads also reject changed files. |
| Storage error | The old committed decision remains. Check directory/permissions/storage, then reload or retry as instructed. |
| Confirmation unavailable | The click may have reached disk. Controls stop further edits; reload saved state before retrying. |
| Malformed input/CSV, wrong dataset, duplicate/unknown identity, or unsafe metadata | Startup fails with a safe error code and line number where available. Fix the source or select the matching CSV; do not delete existing labels to silence validation. |

## Local safety and verification

The app binds only loopback, ignoring ambient URL configuration. It validates Host/port and loopback clients, requires same-origin antiforgery-protected POSTs, and accepts only fixed form fields. HTTP requests cannot select filesystem paths. All result text is encoded; assets are local, with a restrictive content security policy and no remote fonts, scripts or model calls. Data protection keys are ephemeral in memory, not written to a user profile. Reload tabs after restarting to obtain a fresh antiforgery token. Loopback is not authentication against other local users/processes; run only on a trusted machine and protect the CSV/dataset with filesystem permissions.

Run focused deterministic coverage after the restore above:

```sh
dotnet test dotnet/tests/ProjectTimeline.Tests/ProjectTimeline.Tests.csproj --no-restore -c Release -p:SkipClientBuild=true --artifacts-path dotnet/tests/ProjectTimeline.Tests/bin/labeler-build --filter "FullyQualifiedName~EvaluationLabeler"
```

Tests use only unique, retained synthetic fixtures under ignored test output. Coverage includes save/restart, raw-ID joins, escaping/legacy CSV, invalid datasets, failed commits, process locks, stale tabs/external edits, encoded long text, antiforgery/Host/origin/path rejection, download, and compiled-DLL serving outside the project working directory. Real-dataset browser verification is a separate step.

Workflow inspiration: [the buildcamp manual evaluation app](https://github.com/alexeygrigorev/ai-engineering-buildcamp-code/blob/main/documentation-agent/evals/manual/app.py). This is a standalone .NET adaptation, not its Python implementation, automated judge, unsafe HTML rendering, or destructive reset.
