# Timeline facts recovery and completeness

## Objective
Make the seven failed extraction entries explicitly runnable again without deleting durable history, improve detailed faithful extraction, and include the entry with three accepted facts via additive enrichment.

## Authorization and non-goals
User authorizes implementation and inclusion of accepted entry. Prepare safe rerun capability; do not launch new paid calls, write application DB, reset/delete jobs or migrate during development. No commits without explicit request. Preserve unrelated dirty/untracked work. Existing connection/key must never appear in logs/docs/memory. Existing accepted facts and proposals remain intact.

## Decisions
Use explicit operator console modes requiring actor and one timeline, dry-run default and --apply only for future operator execution. Retry selects existing due Retry extraction job IDs with source/auth/lease checks, never global queue or embeddings. Enrichment creates new additive proposals, not implicit acceptance, invalidation or replacing accepted evidence. Normalize entity/type/claim whitespace/case for exact duplicate filtering, never dedupe by entity alone; semantic equivalence/contradiction remains review responsibility. Detailed extraction covers all supported independent claims, quantities, dates, conditions and relationships without invented details; no arbitrary fact-count target. Fake tests prove contracts not live semantic completeness.

## Tasks
- [ ] T1 (paused): Add bounded explicit existing-job retry mode, preserving stable job IDs and attempt history, timeline/actor safeguards and deterministic tests/docs.
- [ ] T2 (pending): Strengthen complete detailed extraction instructions, payload-free diagnostic error codes and additive enrichment proposals with duplicate protection, focused tests/docs.
- [ ] T3 (pending): Assess, independently verify offline build/tests and structural CLI safeguards; report deployment limitations.

## Acceptance and checks
T1: new mode cannot silently change legacy defaults; default dry-run no writes/provider; only explicit timeline due retry jobs without accepted evidence; preserve active leases, future-due/terminal/changed-source/unrelated jobs; test observed RED/GREEN.
T2: additions only, existing facts/evidence preserved; replay-safe pending proposal handling; exact duplicate normalization does not remove distinct same-entity claims; auth/source/revision checks; explicit detailed prompt fixtures and safe granular rejection codes. New live completeness unproven until reviewed actual proposals.
T3: offline focused initializer/provider/processing/service tests and isolated console build, no DB/paid/live tests. Use existing offline cached dependencies, all generated artifacts in initializer artifacts directory. Assessment failure treated high-risk independent verification. No native review when RDD off.

## Evidence and progress
Previous run: eight attempted, one accepted with 3 facts, seven Retry/InvalidResponse after one attempt. No specific InvalidResponse cause proven. Existing initializer skips any job; rerun alone cannot recover them. Current feature branch main-to-facts-interaction has extensive pre-existing changes, preserve them. Work-unit commits pending explicit user authorization.

## Next step
PAUSED: user corrected architectural intent. No apply or further writes authorized until extraction-only vs review mutation semantics are reconciled. Writer cancelled; inspect partial edits read-only. Prior automatic-acceptance design is superseded/pending clarification, not current authority.

## Scope correction
User states extractor should only extract facts supported by the entry, not perform review additions/invalidations; reviews should update TimelineEntryFacts with a fact-state field. User questions need for additions/invalidations tables. Stop implementation and launch, explain current schema/proposal staging, clarify baseline persistence before redesign. Prior source tasks require reconciliation and must not be resumed as written.
