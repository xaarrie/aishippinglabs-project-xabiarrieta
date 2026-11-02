# Search timeline facts with separate source context

Search Facts performs bounded, read-only research over eligible timeline facts. A tool-free model stage returns a typed query plan or a fixed application refusal. The server searches once with `search_facts_batch`, derives the complete stable distinct emitted `EntryId` set inside the admission gate, and fetches its sources once through `FetchEmittedSourcesAsync()`. A fresh, tool-free model stage synthesizes only when facts were emitted. There are no follow-up searches, fact-detail fetches or repair retries. Retrieval and citations are not a guarantee that every sentence is supported or that all relevant information was found.

## Quick path

1. Open **Search Facts** in the intended project/timeline and enter a question (1–300 characters).
2. Read **Research outcome** and **Search coverage** first. Check partial, insufficient-evidence, budget, truncation and server warnings.
3. Compare the model narrative with **Facts cited in answer**. The current UI shows cited claims and summary, not raw source bodies or provenance; verified context remains separate evidence in the API response.
4. To stop a pending search, navigate away or change timeline. There is no dedicated cancel button; question input and submission are disabled while pending. Wait for completion before submitting a replacement question. There is no streaming progress display.

## Read the evidence correctly

| Evidence or outcome | Meaning |
|---|---|
| Fact citation | References a returned effective fact with exactly one review or extraction origin. Pending proposals and invalid facts are excluded. |
| Clarification fact | Its quote/evidence is a review answer, **not original entry text**. Its entry link does not turn that answer into entry text. |
| Source-entry context | Separate entry text, explicitly **not accepted fact proof**. Server evidence IDs identify contexts; entry IDs are not context citation IDs. Each context must retain returned fact anchors with matching entry, entry number and fingerprint. |
| Context-only answer | Can cite separate context while retaining anchoring facts. Those anchors are not automatically facts cited in the answer. Merely retrieving uncited context cannot make a model narrative appear grounded. |
| Complete | The bounded operation finished, not an exhaustive search or semantic-entailment guarantee. Matching is lexical/entity based, not semantic search. A zero-hit batch waives fetching and returns insufficient evidence; no second batch is allowed. |
| Partial | Research stopped at a limit or evidence was truncated. Valid final fact/context citations can still support a displayed answer. A provider-call limit before the final answer suppresses narrative and shows available evidence only. |
| Empty / InsufficientEvidence | No grounded model answer is shown. Zero emitted facts skips fetch and synthesis after final revalidation. Coverage warnings still matter; absence of matches does not prove absence of information or contradictions. |
| Refused / SafeRefusal | Only typed `OutOfScope` or `UnsafeRequest` planning decisions qualify. Fixed server text: `This request cannot be answered by read-only timeline fact search.` Facts, citations, contexts and context citations are empty. The adapter renders a fixed outcome, never arbitrary uncited payload prose; contradictory refusal evidence is rejected. Native provider refusal remains an execution error. |

The UI rejects malformed context evidence and withholds narrative when context citation identity/provenance cannot be verified. Legacy fact-only responses remain supported; omitted context arrays become empty and research metadata may be null.

### Current entry text, not historical snapshots

The server fetches source context only through facts actually retrieved during this operation. It checks current eligibility, access, revision and source fingerprints again before responding. Changed or stale source context must be withheld rather than represented as current evidence. No immutable historical entry-body store is provided here; a source link opens the entry, not a guaranteed historical body. Re-run research after changes. A context's `Truncated` flag marks incomplete entry text. The aggregate tool response lists `OmittedEntryIds` when context cannot fit; final research status/warnings disclose partial evidence.

## Bounds, cancellation and observability

Default server limits (the response's limit metadata is authoritative):

| Resource | Default allowance |
|---|---:|
| Model/provider admission safeguard | 20; normally one planning call and, with emitted evidence, one synthesis call |
| Server retrieval invocations | One search, then one aggregate fetch with emitted facts (attempt safeguard: 20); zero AI tools registered |
| Search expressions | No count ceiling (`MaxSearchQueries: null`); actual uncached attempts reported |
| Distinct facts / hits per expression | 40 / 10 |
| Source contexts | All distinct emitted entries; at most 40 from the fact bound |
| Context text | Remaining global output budget, no separate count/per-context/aggregate-text caps |
| Total serialized tool evidence characters | 32,000 |
| Output tokens per model call | 1,500 |
| Operation deadline | 60 seconds |

Tool counters include rejected attempts and SDK argument-binding failures. Search counters count actual uncached query attempts, including invalid/read failures, not unexecuted expressions; normalized repeats within the single batch reuse validated results. Legacy context limit fields are conservative bounds derived from fact/global limits, not extra allowances. Search admission reserves a minimal explicit-omission fetch envelope; clipping alone does not cancel the model loop before fetch/final synthesis. Missing required phases or fatal validation errors reject synthesis. Counts can exceed allowances without indicating that extra evidence was admitted. Character limits are not exact input-token ceilings. Provider retries are disabled on the bounded production construction path; cancellation/deadline can end the operation without a completed answer.

Production Search now has two isolated model invocations, not SDK model-driven tool execution spans. Compatibility tool wrappers and other agents still produce native `execute_tool` spans for their SDK attempts; such spans do not imply successful retrieval or independent verification of a claim. The telemetry privacy boundary excludes question/prompt text, entry content, tool arguments and tool results from OTLP. This does not mean the model provider receives no evidence: research necessarily supplies selected evidence to the configured model. Missing model prices remain **unknown**, not zero, and bounded calls do not imply a known monetary cost.

## Offline verification and next step

`ClientApp/tests/timelineFactResearch.fixtures.json` contains one synthetic complete `mixed` response plus small `cases` overlays for `contextOnly`, `partial` and `malformed`. Apply an overlay at the top level; merge its `research` object into the base research object and replace arrays. IDs/fingerprints are illustrative; there is no real source data. Fields match the public R3 DTO; no public author ID is included.

Planning and synthesis use explicit strict wire schemas and exact-field validators (including duplicate, missing, unknown and null rejection). Query arrays have no new arbitrary count ceiling. A Search-only request-local leaf validator rejects unexpected tool calls before SDK continuations, while preserving cost accounting and other agents' normal tool envelopes. Native provider refusals, invalid finish reasons and unknown/invented citations are errors, not safe refusals. With one allowed model call, only a valid completed plan plus server retrieval can yield finally revalidated `Partial / ProviderCallLimit`; unfinished model prose is withheld.

Node tests exercise normalization and actual React SSR, including fixed safe-refusal visibility/contradictions, legacy origins, context-only/mixed answers, tampering, warnings, escaping and request fences. Independent verification must also exercise the production-built UI with fully mocked APIs and offline TestServer coverage:

- Mixed/context-only: context never becomes an accepted fact citation; the current UI keeps raw bodies, fingerprints and hidden provenance out of the result view.
- Partial: show limits, attempt counts and warnings; keep valid final narrative for tool/evidence limits or truncation. Provider-limit/empty/insufficient results show no model guess.
- Malformed: unseen/tampered context citations cannot ground narrative; malformed context evidence produces an error.
- Navigation cancellation: late responses, including from abort-ignoring transports, must not populate another project/timeline; existing feedback/action lifecycle remains intact.

Build the UI into an isolated ignored test-bin output with the task-authorized Vite `--outDir`, and verify that production bundle, not just source or SSR output. Do not overwrite unrelated dirty `wwwroot` assets during verification. Building does not restart the running application. Real models, real provider API calls, live telemetry exports and end-to-end real-data behavior have **not** been verified by these offline tests.
