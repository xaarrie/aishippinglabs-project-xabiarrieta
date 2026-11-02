# Entry clarification evidence: durable processing contract

Entry creation and explicit regeneration share a human-controlled extraction → clarification → verification cycle. Extractor questions pause before verification; verifier nonapproval becomes durable feedback, never an automatic paid repair. Saving an actual answer makes **zero model calls**; explicit resume creates a successor generation. Approval produces only `Generated`/`Completed`, not active facts or embeddings. Reconciliation and acceptance remain separate.

C1–C4's durable answers, frozen input, provenance, authorized APIs and client remain in place. Independent behavior verification **passed** before this documentation refresh; evidence and limitations appear below. **Repository root:** `C:/Data/formacion/aishippinglabs-project-xabiarrieta`; all `dotnet/...:line` and `odd/...` locators are plain paths relative to that root, also for external copies. This guide describes current source, including uncommitted work, not a deployed system. The feedback migration is **PREPARED, NOT APPLIED**; persistent DB use requires applying it with human authorization. No host restart or deployment was performed.

## Extraction verification feedback cycle

Extractor questions pause **before verification** and discard provisional candidates/evidence. A later explicit resume re-extracts. Question-free nonempty batches use `VerifyOutcomeAsync`: the server derives Approved, NeedsClarification or Rejected from complete indexed assessments and validated questions, never a score threshold. Zero candidates and zero questions can complete an empty outcome without invoking the verifier [G1–G3].

| Valid result | Worker effect | Next human action |
|---|---|---|
| Extractor questions | `AwaitingClarification`, no provisional candidates/support | Save actual answers, then explicitly resume |
| Verifier `NeedsClarification` | Atomically save feedback + questions + `AwaitingClarification` | Save actual answers, then explicitly resume |
| Verifier `Rejected`, no questions | Atomically save feedback + terminal `Failed`; `/facts` exposes `NeedsCorrection` | Explicitly regenerate with predecessor feedback, or edit source first |
| Question-free approval / empty extraction | `Generated` proposal + `Completed` job | Reconcile, review and explicitly accept |

Technical schema/citation/provider errors and cancellation remain technical; they are not converted into user questions. Semantic rejection neither schedules blind `Retry` nor triggers a corrective model call [G4–G7].

Verifier questions use the same key/text/reason/evidenceIds contract, with MissingInformation or AmbiguousReference and current source citations. Prompts require neutral genuine-gap questions and prohibit confirmation of hallucinated assertions. Shape/citation checks do **not** prove semantic neutrality or model reasoning.

NeedsClarification atomically stores feedback plus questions in AwaitingClarification. Rejection without questions atomically stores feedback in terminal Failed, surfaced by `/facts` as **NeedsCorrection**. Neither produces proposals/facts or automatic corrective calls. Existing explicit **Regenerate pending facts** creates a successor with predecessor feedback; saving answers invokes no agent. Approval still produces only Generated/Completed and requires separate reconciliation and acceptance.

`20261026000100_ExtractionGenerationFeedback` adds **one** nullable `ExtractionFeedbackJson` (`text`) field on `TimelineFactJobs`, with no data rewrite. It is prepared, **not applied**. Its Designer extends frozen predecessor metadata without modifying historical targets; the current snapshot registers the same addition. Offline SQL/target/snapshot assertions do not connect to PostgreSQL and are not deployment evidence [G12, G13].

Feedback output version 1 binds generation, entry, source and effective input, retaining candidate snapshots, indexed Supported/score/issues/evidenceIds and questions. Canonical serialized JSON is bounded to **512 × 1024 UTF-16 code units**, not UTF-8 bytes or raw finding text; depth is 16. It contains 1–199 findings with unique complete indices, at most 81 IDs per cited finding/question and 20 inherited answers plus new questions. Rejected findings may omit evidence; every supplied ID, **including rejected assessments**, must bind to current source. Supported findings and questions require 1–81 reconstructible IDs. Scores must be finite 0–1, issues unique and closed (`Numeric`, `Date`, `Actor`, `Negation`, `Unsupported`); no score threshold determines approval. Output is null-to-validated once under current source/revision/author/lease checks, never replaceable or permitted on embedding/completed jobs.

Only the exact current predecessor supplies feedback before atomic supersession. Frozen input optionally retains that predecessor output separately from Input.Answers. Extraction serializes it as root `extractionFeedback`, outside `source` and `sourceSpans`; repeated Freeze preserves it. It is **untrusted non-evidentiary guidance**, excluded from source, actual answers, spans and source/effective evidence fingerprint inputs. Historical feedback IDs cannot cite successor spans. Only predecessor **output** is carried, never recursive feedback from an approved predecessor's older input. No historical hash search revives feedback after source edits/restoration. Superseded outputs remain audit history [G4–G6]. The extraction prompt including guidance still has a **768 KiB UTF-8 serialized payload cap**; oversized guidance/payload fails `InputLimit`, not silent truncation. The verifier receives identical `source`/`sourceSpans`, but not the root correction guidance [G1–G4].

Both entry detail and edit mount clarification controls. Dirty/stale source, saving, review and clarification requests independently fence writes while retaining drafts. Explicit refreshes/writes invalidate the other view through GET-only callbacks, without remounting drafts or replaying paid calls. `/facts` exposes only a validated current summary (counts and closed issue codes), not raw feedback claims as evidence.

## Server calls

| Caller | Contract |
|---|---|
| Authorized entry read | `TimelineEntryClarificationService.ReadStateAsync(scope, actorId, administrator)` returns validated current-job-bound questions/carried answers separately from bounded read-only history. C1 `ReadAsync`/`AnswersAsync` remain compatibility helpers for the originating generation, not the API's carried/history view. Actor/admin context comes from authenticated server code. |
| Actual human response | `AnswerAsync(scope, generationId, clarificationId, actorId, administrator, answer)` accepts only text, not replacement evidence/readiness/author/time. Same text from the same actor is idempotent; replacement conflicts. Partial answers persist without releasing the waiting generation. |
| Explicit resume/refresh | `TimelineFactProcessing.RequestGenerationAsync(scope, actorId, administrator, regenerate: true, expectedGenerationId)` checks scope, accepted-entry locks and the observed generation. It captures actual answers **before** supersession, then saves the new input and revocation together using job/timeline CAS. All required predecessor questions must be answered. |
| Worker | `ProcessAsync` constructs `new TimelineFactExtraction(entry, persistedInput, predecessorFeedback)` and calls `ExtractOutcomeAsync`, never the legacy list API. Feedback uses `StageExtractionFeedbackForLeaseAsync`; extractor-only questions use `StageEntryQuestionsForLeaseAsync`. Each commits its complete pause/rejection atomically under the lease. |
| Durable input | `StageExtractionInputAsync(job, source, predecessorId)` is a trusted server-only staging operation accepting no replacement answer DTO. `ReadExtractionInputAsync(job, source)` validates the persisted snapshot against source, job, hashes, actual answer records, current answering-author authorization and predecessor lineage. Neither is a client binding surface. |

Use clean scoped contexts and discard them after staging/save/concurrency failures. Never automatically replay a human write. These trusted staging methods do not authenticate HTTP callers; services derive entry/project/timeline membership and enforce authorization.

## Durable schema and C3 consumption

The original three nullable, additive input/support columns on `TimelineFactJobs` preserve historical jobs; the separate feedback output column is described above:

| Column | Stored value |
|---|---|
| `ExtractionInputJson` (`text`) | Canonical `TimelineExtractionSnapshot(Source, Input, PredecessorId, ExtractionFeedback?)`; optional predecessor guidance is separate from `Input`. `Source` stores exact Id, TimelineId, EntryNumber, AuthorId, Title and Content. `Input` is C1 `TimelineEntryExtractionInput(JobId, SourceFingerprint, EffectiveFingerprint, Answers)` including every actual answer's ID, original generation, question key/text, literal answer, author and UTC time. |
| `EffectiveInputFingerprint` (`varchar(64)`) | Effective original-plus-answer identity. For zero answers it equals the unchanged original `TimelineFactEvidence.Fingerprint`. |
| `ExtractionSupportJson` (`text`) | `TimelineExtractionSupport[]`: CandidateIndex, PrimarySpanId, **all** Spans, and durable AdditionId. Each span retains Id, Source, Start, Length, Text, SourceKind and actual AnswerId. Use AdditionId to correlate persisted additions; database enumeration order is not an evidence identity. |

New jobs freeze their input at creation; legacy no-answer jobs freeze at their first acquisition. Input fields cannot subsequently change, including during retries, cancellation or supersession. Legacy completed jobs remain untouched. The original `SourceFingerprint` continues identifying the original source, not Q&A.

A successful answer-aware result is only a `Generated` proposal keyed by job ID, with immutable additions and supporting provenance on the job. It creates **no** facts or embeddings. Its `QuestionsJson` remains `[]` and review-owned `ClarificationEvidence` remains empty: entry Q&A is not forged into reviewed evidence by C2b.

Normal `TimelineFactReviewPlanner.PlanAsync` reads the complete Generated ledger through `ReadReconciliationEvidenceAsync`: job/proposal identity, full result hash, durable input/actual answer/lineage authorization and every supporting span are revalidated. Support resolves by **AdditionId**, not database enumeration. Exact unique Claim/EntityKey/EntityType/SourceKind identity may restore the canonical SourceQuote for Entry or verified Clarification candidates; ambiguity or changed identity never receives repair.

Imported `TimelineFactQuestion` values retain exact Question/Answer plus optional `ExtractionGenerationId` (the completed extraction job) and `ExtractionAnswerId` (the actual stored clarification). The latter row/snapshot retains the original question generation, key, source, author and timestamp; the former job retains all support identities without copying span arrays into each answer. These optional fields are omitted for legacy review-owned questions. Model output cannot replace actual answers or provenance, and cannot remove imported evidence. Pending QuestionsJson, answered ClarificationEvidence and accepted fact ClarificationEvidence preserve these references.

Planning rechecks proof after the model call. `PrepareAsync`, `AcceptAsync` and ORM pending/acceptance guards revalidate referenced extraction input, actual answers, full ledger and support before writes, in addition to existing source/revision/CAS checks. Generated proposals still cannot be accepted directly; original Content and existing accepted evidence remain immutable.

## HTTP contract and C4 handoff

All routes require authentication. `NameIdentifier` and the authenticated Admin role supply identity; persisted entry/timeline/project membership and active-account checks remain authoritative. Reviewers may read but not answer/resume; Admin still requires an active persisted account and valid scope.

| Method and entry-relative path | Request / response |
|---|---|
| `GET /api/entries/{entryId}/fact-clarifications` | `TimelineEntryClarificationStateDto`: entryId, generationId, lifecycleState, current[], history[], truncated, canAnswer, canResume, refreshRequired. |
| `POST /api/entries/{entryId}/fact-clarifications/{questionId}/answer` | Strict `TimelineEntryClarificationAnswerRequest`: `{ expectedGenerationId, answer }`. Returns the updated clarification state. Answer is nonblank, at most 4,000 characters; it is immutable once saved. No provider is invoked. |
| `POST /api/entries/{entryId}/fact-clarifications/resume` | Strict `TimelineEntryClarificationResumeRequest`: `{ expectedGenerationId }`. Explicit model-run action; requires all current questions answered, current proof, writer permission and no accepted-entry lock. Calls version-checked `RequestGenerationAsync(regenerate: true)` then awaits processing; returns existing `TimelineFactStateDto`. |

Rows contain id, originating generationId, questionKey, question, actual answer (nullable), answeredById/answeredAtUtc (nullable), createdAtUtc, binding and canAnswer. `current` reserves all actionable questions and validated carried answers (at most 20 combined), independent of history size. Binding is `CurrentQuestion` or `CarriedAnswer`; carried rows may originate in superseded generations but are active **only** through the validated current job snapshot/lineage. `history` excludes current IDs, contains old-source/superseded rows, is always read-only (`History`), and returns the newest 100 with `truncated=true` when more exist. Matching an old source hash never activates a historical row. Restoring source text does not restore revoked evidence. Q&A remains visible after acceptance, with writes/resume disabled.

C4 calls `ReadClarificationsAsync`, `AnswerClarificationAsync`, and `ResumeClarificationsAsync` through those routes. Render permissions/readiness from server flags, never client guesses. Refresh both facts and clarifications after resume. `/facts` reports `AwaitingClarification` with canGenerate/canRegenerate false; Generate/Plan/Accept reject waiting state. Extraction questions remain separate from proposal review questions. Proof corruption or a mixed read returns 409 (not empty success); `/facts` marks refreshRequired. On 409 refresh and retain the unsent answer for human inspection; never blindly replay writes. Successful clarification reads have refreshRequired=false.

Unknown body fields are rejected: no author/time/source/snapshot/extraction references/support/readiness can be submitted. The `/fact-` body middleware retains its 64 KiB known/unknown-length bound. Responses use existing safe codes: 401 unauthenticated, 403 unauthorized/inactive, 404 missing scoped entry/question, 409 stale/incomplete/immutable/concurrent/proof conflict, 400 malformed or invalid text, 413 oversized body, and the existing safe provider failure behavior. Every HTTP request owns a fresh scoped context; failed writes are disposed, not retried. The web host's lazy provider forwards the complete structured outcome, including questions and supporting evidence.

Normal review still accepts only persisted proposal IDs and review-question answer indices, never replacement extraction evidence. Imported answered questions cannot be replaced via review answers.

## Entry UI (C4)

Both saved-entry **detail and edit** show **Entry extraction clarifications** before FactReview, independently of proposal/reconciliation questions. Creation navigates to the saved detail after generation; regeneration uses the same controls and server contract. Original source text and existing review/acceptance behavior are unchanged. Edit fences clarification/review writes for dirty or stale source, saving, missing authoritative proof and in-flight requests; clarification busy also fences source saving and review. Detail fences review and clarification writes against one another [G9–G11].

1. Enter actual human text (maximum 4,000 characters) and choose **Save answer** for each current question. Saving only persists that answer; partial answers never start extraction.
2. After the server confirms readiness, choose **Extract using answers (model run)**. This separate action may incur billing and refreshes clarification evidence and fact state. It never accepts candidates.
3. Reconcile and explicitly accept through the existing fact review. Saved answers, originating generations, answerer IDs/timestamps, carried bindings and read-only history remain visible after generation/acceptance. Truncated history is explicitly labelled.

The client validates entry/generation identities, boolean flags, row bindings and answer provenance before enabling writes. Reviewer/no-write access never renders answer/resume controls, even if a server response advertises readiness. Answered rows are immutable. Missing/corrupt responses and source-proof conflicts fail closed, not as empty successful history. Save success additionally requires the returned current row to confirm the exact answer and expected generation.

On 409/network failure, drafts are retained and a GET refresh confirms server state without replaying a write. Writes remain paused until **Refresh clarification state** succeeds explicitly; the human must inspect and choose any subsequent save. If a question has moved out of the actionable set, its draft appears under **Retained unsaved drafts (not evidence)**. Failed/uncertain resume also refreshes facts, since processing might have completed. Abort plus request-identity fencing prevents late responses from changing another selected entry or invoking its callbacks. Effects only read; there is no automatic model run or clarification polling.

`EntryClarifications` takes `entryId`, `canWrite`, optional `blocked`, `refreshKey`, `onResume`, `onSaved`, `onRefresh` and `onActiveChange`. Explicit successful refreshes invalidate the fact view; effect-driven GET refreshes do not invoke reciprocal callbacks. FactReview's optional `refreshKey` requests a GET without remounting or clearing unchanged proposal drafts. Accessible browser targets include the section **Entry extraction clarifications**, textarea ID `clarification-answer-{questionId}`, button name `Save answer: {question}`, **Refresh clarification state**, and **Extract using answers (model run)**. Rows expose `data-clarification-id`.

The independent browser check now exercised actual production React in Chrome for **both detail and edit**, using intercepted fake APIs only: 33 assertions, 66 requests (56 GET/10 writes), zero unexpected destinations or page errors. It covered the human-loop interaction boundaries, not semantic model accuracy or an authenticated end-to-end live stack. No real backend, model or PostgreSQL was used. The isolated Vite build passed 37 modules with `envDir:false` and OS-temp output. Preserve that isolation: the default Vite build empties WebAPI `wwwroot`; it is not an authorized documentation check.

## Lifecycle and concurrency

- `AwaitingClarification` is terminal for a processing invocation: no lease, provider call, attempt increment or automatic retry, even on explicit repeated `ProcessAsync`. Existing hosted selectors include only Pending, due Retry and expired Running jobs. The opt-in retry runner selects only Retry; waiting work is excluded without changing its selector.
- Resume is a new explicitly requested generation, never a state reset. Unanswered questions or missing/mismatched provenance conflict. Captured answers survive further unchanged-source refreshes. Every carry edge records a predecessor that was current at capture; source edits supersede it. Restoring old text never revives historical answers by hash equality.
- Answer writes reserve both generation and timeline. Generation creation/supersession uses the existing version and source/timeline CAS. Lease owner, version, expiry and source/revision are checked again before pause/completion; lost ownership cannot persist questions or results. Technical retry/backoff policy is unchanged; cancellation does not enforce the noncancelled eighth-attempt terminal cutoff.
- Maximum 20 accumulated answers/questions per effective input; key/question/answer bounds remain 100/2,000/4,000 characters. A question round exceeding remaining capacity is invalid output, not an impossible-to-resume wait. Provider keys are first validated for within-round uniqueness, then stored as SHA-256 of generation ID plus key. This gives distinct bounded stable identities when later valid rounds reuse provider keys, without silently dropping earlier answers.
- Both initializer adapters (`CheckedProvider` and retry `GuardedProvider`) forward structured `ExtractOutcomeAsync` input and the complete unchanged Candidates/Questions/Evidence/Feedback envelope through their existing pre/post authorization/source/operation gates. Retry selection, lease/version CAS, invocation counts and no-embedding restrictions are unchanged. Valid waiting outcomes report `AwaitingClarification`, not `ExtractionFailed`; waiting generations remain excluded from retry selection.

## Grounding and immutable provider context

Both claim/entity extraction and semantic verification consume byte-identical serialized `source` and `sourceSpans`. Title/Content, entry/job/effective identity and actual answers with question context are quoted untrusted data, never instructions. Questions alone are not factual evidence or citation spans. Claim, EntityKey and EntityType all require support.

`TimelineFactExtraction` retains all original fingerprint metadata in private readonly value fields, including TimelineId/EntryNumber/AuthorId. Repeated `Freeze()` reconstructs and validates the complete source identity without holding mutable ORM entries or adding metadata to model JSON. Server persistence is distinct from this immutable DTO: never deserialize a client replacement into either seam.

The shared DA `TimelineExtractionEvidence` literal codec is used by both BL provider evidence and DA completion guards; DA does not reference BL. It preserves the original exact UTF-16 hashing/windows, surrogate boundaries, job/effective binding, actual AnswerId and deterministic answer-first primary selection. Windows contain at most 256 UTF-16 units; selecting support permits 1–81 spans per candidate, independently of how many windows the source and answers produce. The primary field/answer's contiguous literal envelope becomes SourceQuote; other supporting identities are retained, not discarded. Without answers, legacy single-field rules remain unchanged.

Answer-aware Generated completion revalidates every supporting span against server-reconstructed spans and each literal candidate against original source or its actual bound answer. Foreign IDs, changed text/positions/answer identity, wrong primary, omitted answer support and invalid indices fail closed. The unchanged legacy direct extraction-origin fact constraints remain Entry-only with no clarification evidence.

## Outbound extraction schema

The native OpenAI request uses `response_format.type=json_schema`, name `entry_claims`, and `strict=true`. Every object disallows additional properties and requires all its declared properties, including root `questions` and each nested question's `key`, `text`, `reason`, and `evidenceIds`. Send `questions: []` when there is no genuine gap. Extraction adds `questions` to the inferred outbound required array without making the DTO property required: legacy incoming responses omitting it still default to an empty list. Unknown fields (including model-supplied answers), malformed questions and invalid evidence remain rejected.

`TimelineFactStrictSchemaTests` captures the actual SDK HTTP body through a fake transport that returns HTTP 400 (`param=response_format`) for incomplete recursive required sets. It exercises empty questions, grounded questions using IDs from captured `sourceSpans`, and legacy omitted questions through the native pipeline without a database or live provider.

## Outcomes and failures

`TimelineFactExtractionOutcome(Candidates, Questions, Evidence, Feedback)` is validated even for custom providers. Questions are bounded, unique and nonblank; native provider questions also require a supported reason and context citations. Question/feedback outcomes cannot carry completion candidates or evidence. Verifier feedback retains candidate snapshots and findings separately, not as provisional additions.

Both agents use strict outbound JSON schemas (`entry_claims` / `fact_semantic_assessments`): every declared property, including root `questions` and nested key/text/reason/evidenceIds, is required; objects reject extra fields. Both inbound DTOs allow legacy omission of `questions` as `[]`, not null or arbitrary fields. Verifier assessments cover every index exactly once; supplied IDs are validated even when unsupported. Questions take disposition precedence; otherwise all Supported/no issues → Approved, any blocker → Rejected [G1–G3].

| Strict compatibility API | Exact fail-closed behavior |
|---|---|
| Extraction agent `ExtractAsync` | Extractor questions → `TimelineFactClarificationRequiredException` carrying outcome; this raw agent does not invoke verification |
| OpenAI provider `ExtractAsync` | Any verifier feedback, **even with questions**, → `TimelineFactProviderException("InvalidResponse", "SemanticUnsupported")`; extractor-only questions → typed clarification-required exception |
| Verifier `VerifyAsync` | Every nonapproval → `TimelineFactProviderException("InvalidResponse", "SemanticUnsupported")` |
| Default `ITimelineFactProvider.ExtractOutcomeAsync` adapter | Frozen answers or feedback it cannot consume → `InvalidResponse / ClarificationInputUnsupported`, before legacy list invocation |
| Initializer `CheckedProvider` / retry `GuardedProvider` | Forward the whole structured envelope, including feedback, through existing pre/post checks; strict list calls preserve the inner exceptions |

Only valid explicit questions cause waiting. Schema/citation/provider/SDK failures and cancellation retain technical retry/failure handling; no invented questions or automatic paid replay from UI errors. A technical worker retry can rerun the chain, unlike terminal semantic rejection. Diagnostics remain closed-vocabulary and persist no exception payloads [G4–G8].

## Verified evidence and deployment boundary

Independent combined T2 (`mvw71169-g-e85p`), recorded in `odd/tasks/extraction-verification-feedback.md`, passed **1,386 backend tests**, **38 focused / 95 full frontend tests**, zero failures/skips. Earlier independent focused backend **225** remains valid; the parent repeated frontend **38**, all green. The isolated build/browser evidence is described above. Historical writer-observed RED and focused GREEN 225 belong to implementation history, not an independently rerun RED.

The first independent full backend run was **1,385 pass / 1 fail**: `TimelineFactModelTests.Npgsql_model_has_real_vector_mapping_and_migration_snapshot_matches_without_connecting` compared a frozen historical target to the newer snapshot. The test was corrected to select the latest registered `IMigrationsAssembly` target with all assertions intact; independent **36 migration/model tests** then passed, followed by the final full **1,386** pass. Historical targets were untouched. Native ASSESS was unavailable because of undeclared untracked scope; an independent high-risk fallback verification completed. Only Node `MODULE_TYPELESS_PACKAGE_JSON` and Git LF/CRLF warnings remained.

Deterministic fake-provider/HTTP/InMemory tests cover question-first discard, strict compatibility, derived dispositions, indexed/cited feedback, atomic pause/rejection, zero-call immutable answers, explicit multi-round successor input, Freeze, exact-predecessor binding, forged/old IDs, source edit/restoration, answer-author/revision/lease/concurrency checks, technical cancellation and Generated-only completion. Existing review/acceptance evidence retains answer and generation references; imported Q&A cannot be replaced by model output. Offline migration SQL/current/frozen/historical metadata checks preserve FTS and earlier targets. Browser assertions prove interactions only; none of this proves model factual accuracy, question neutrality, PostgreSQL concurrency, live DB quality or migration application.

This passive T3 refresh has no meaningful RED/GREEN. Verification is read-only source/structure/reference checking of the five canonical documents, not another application suite. No credentials, personal seed data, live model, backend service or DB execution is involved. **Migration application and deployment remain human-authorized future actions; export follows independent documentation review by the parent.**

## Current source locators

| Ref. | Repository-relative source | Contract |
|---|---|---|
| G1 | `dotnet/src/ProjectTimeline.BusinessLogic/Agents/TimelineFactExtractionAgent.cs:15–167` | Strict schema, prompt, question-first discard |
| G2 | `dotnet/src/ProjectTimeline.BusinessLogic/Agents/TimelineFactSemanticVerifierAgent.cs:14–151` | Complete assessments, validated questions, derived disposition, strict API |
| G3 | `dotnet/src/ProjectTimeline.BusinessLogic/Agents/TimelineFactStructuredResponse.cs:13–109`; `dotnet/src/ProjectTimeline.BusinessLogic/Agents/TimelineFactClarificationQuestions.cs:6–23` | Byte caps, JSON/envelope admission and question structure |
| G4 | `dotnet/src/ProjectTimeline.BusinessLogic/TimelineFactOpenAIProvider.cs:9–82`; `dotnet/src/ProjectTimeline.BusinessLogic/TimelineFactProcessing.cs:9–90` | Provider routing, legacy differences, Freeze and adapter |
| G5 | `dotnet/src/ProjectTimeline.DataAccessLogic/TimelineFactGenerationFeedback.cs:7–163` | Codec v1, bounds and atomic immutable output |
| G6 | `dotnet/src/ProjectTimeline.DataAccessLogic/TimelineFactExtractionInputs.cs:8–295` | Evidence codec, complete ledger and predecessor input |
| G7 | `dotnet/src/ProjectTimeline.BusinessLogic/TimelineFactProcessing.cs:142–370`; `dotnet/src/ProjectTimeline.BusinessLogic/TimelineFactService.cs:63–119`; `dotnet/src/ProjectTimeline.BusinessLogic/TimelineFactService.cs:172–216` | Explicit successor, lease lifecycle, Generated versus acceptance |
| G8 | `dotnet/src/ProjectTimeline.FactsInitializer/FactInitializer.cs:163–181`; `dotnet/src/ProjectTimeline.FactsInitializer/TimelineFactRetryRunner.cs:117–179` | Pre/post adapter checks and technical Retry selection |
| G9 | `dotnet/src/ProjectTimeline.UI.WebApi/TimelineFactApplication.cs:14–94`; `dotnet/src/ProjectTimeline.UI.WebApi/TimelineFactApplication.cs:165–207`; `dotnet/src/ProjectTimeline.UI.WebApi/TimelineFactEndpoints.cs:15–92` | HTTP, current feedback summary and NeedsCorrection |
| G10 | `dotnet/src/ProjectTimeline.UI.WebApi/ClientApp/src/App.jsx:411–525`; `dotnet/src/ProjectTimeline.UI.WebApi/ClientApp/src/FactReview.jsx:6–140` | Detail/edit integration and review fences |
| G11 | `dotnet/src/ProjectTimeline.UI.WebApi/ClientApp/src/EntryClarifications.jsx:7–76`; `dotnet/src/ProjectTimeline.UI.WebApi/ClientApp/src/entryClarifications.js:8–154` | Drafts, free saves, explicit resume, GET-only invalidation |
| G12 | `dotnet/src/ProjectTimeline.Database/Migrations/20261026000100_ExtractionGenerationFeedback.cs:8–16`; `dotnet/src/ProjectTimeline.Database/Migrations/20261026000100_ExtractionGenerationFeedback.Designer.cs:6–21`; `dotnet/src/ProjectTimeline.Database/Migrations/ProjectTimelineDbContextModelSnapshot.cs:651–656` | One nullable field, frozen target and current snapshot |
| G13 | `dotnet/tests/ProjectTimeline.Tests/TimelineFactGenerationFeedbackTests.cs:294–350`; `dotnet/tests/ProjectTimeline.Tests/TimelineFactModelTests.cs:71–106` | Offline metadata invariants, not applied DB evidence |
