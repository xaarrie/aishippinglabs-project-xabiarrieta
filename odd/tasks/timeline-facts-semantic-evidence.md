# Semantic evidence for fact extraction

## User decision
Replace extraction's requirement that model-generated citation be an exact title/content substring with independent semantic verification. User selected separate verifier over self-scoring after cost disclosure. Score0..1 model assessment, not calibrated probability; no arbitrary numeric threshold. Keep review literal checks and immutable job/source provenance.

## Design and boundaries
Every nonempty extraction batch gets a separate typed verifier call against original source and proposed candidates. Require exact index coverage, explicit Supported=true, no closed contradiction/unsupported flags and finite0..1SupportScore. All-or-nothing batch; any failed/malformed/refused/truncated/timeout assessment rejects without partial facts. Score transient in typed verifier result, no DB schema/API score promise.
Retain database literal-evidence guards without accepting an untrusted 'verified' flag: verifier selects deterministic bounded source-span IDs, code reconstructs SourceQuote from the ORIGINAL source (same Title or Content), never LLM-generated text. Initial paraphrased quote is assessed semantically, not rejected for string mismatch; final stored quote is source-derived supporting evidence. IDs/ranges/source/candidate association and score validated. Keep source fingerprints/candidate hashes, revision/CAS/lease and review exactness. Avoid capability-certificate framework or weakening guards.

## Authorization and non-goals
User additionally authorized generation of missing entries AFTER implementation and independent verification. During S1/S2 no DB/paid calls. S3 may run actual extraction+semantic verification on rechecked pending scope, preserving stable jobs and accepted/direct facts; embeddings QUEUE ONLY. No migrations, deletes/resets, commits or blind retries. Five previousRetry jobs remain untouched until S3.

## Tasks
- [x] S1 (done): Implement typed semantic-verifier agent/DTO, deterministic source evidence selection, provider orchestration and validation with focused RED/GREEN fixtures and docs.
- [x] S2 (done): Assess and independently verify full offline fact/client regression, strict schema/closed diagnostics, rejection cases, guard/review compatibility and isolated console build/help.
- [ ] S3 (blocked: partial semantic rejection): Recheck actual missing entry/job scope read-only, dry-run explicit targeted recovery then run approved extraction+semantic verification once, observe counts/diagnostics and queued embeddings.

- [ ] S4 (pending): Diagnose three rejected semantic batches before any separately authorized further retry; do not weaken checks or infer exact reasons from coarse code.

## Acceptance and checks
Supported paraphrase succeeds without exact input quote match, outputs original-backed source quote. Similarity-only contradictions in number/date/actor/negation or unsupported detail reject even high score. Missing/duplicate/out-of-range indexes or evidence IDs, cross-source evidence misuse, scoresNaN/inf/outside0..1, unknown fields/flags, wrong count, schema/refusal/finish/cancel fail safely. No partial result or bypass of database guard; review exactness unchanged. Source segment decomposition lossless/bounded/deterministic with stable IDs, full original source available to verifier; no invented content or arbitrary score threshold. Avoid duplicate strict response transport machinery where reusable bounded helper makes sense, don't rebuild generic engine. Fake results test contract, not real entailment accuracy; no assurance of exhaustive extraction.

## Existing evidence
Five previous batches UnsupportedQuote exactness check; raw failed responses unavailable, so paraphrase vs false assertion unknown. Earlier current runtime3entries saved,5Retry pending. Framework integration independent298 offlinepasses/build0errors; migration20261018000200 already applied. SourceQuotes currently validated in processing, service and extraction guard; semantic admission must maintain final actual source evidence. No new migration needed with transient assessment.

## Next step
S1 writer already active. Parent maintains document/mirror/todo. After verified S2, execute user-authorized S3 on actual missing scope; no blind replay after uncertain/failed outcome. No commits requested.

## S1 evidence / S2 start
Implemented extraction(shape only)->independent verifier->deterministic source-derived final quote. Typed transient score/support/closedissues, complete candidate index coverage, source-ID validation, final literal guards unchanged. Shared bounded structured response helper, deferred composition, no migration or persistence score fields. RED supportedparaphrase failed original exact check then GREEN; wrongcase field RED fixed with strict case-sensitive deserialization; schema inference serializer mutation diagnosed/resolved with independentoptions copy. Final361offlinepasses0fail0skip;focused93pass;consolebuild0warnings/errors/help0. No DB/providers/paidcalls/retries/migrations/embeddings. Raw candidates<200,output12000tokens/call,title200/content20000,response512KiB,input768KiB,source<=81spans<=256UTF16. Two near90s calls may exhaust existing3minute lease; independent verifier must evaluate fail-closed safety/runtime risk. Native ASSESS unavailable due pre-existing undeclared untracked scope;RDDoff,conservative independent verification required. S2 underway;S3 authorized but not launched.

## S2 independent result / S3 start
Independent PASS361tests0fail0skip,consolebuild0warnings/errors/help0. Confirmed initial paraphrase admitted only after separate complete typed assessment and deterministic sourceevidence, finalguards/reviewsunchanged,no persistence score or migration. No blocking source defect. Medium runtime limitation:two90s calls plusoverhead may exceed existing3min lease; expires failclosed,mayleave Running/LostLease and paid repeat via separatepoller. No exactly-once billing guarantee; no concurrent drain/blind retry permitted. Verifier considers bounded5existingRetry safe after recheck. Parenthelp spotcheck0. S3 now starts read-only actualscope/dryrun then userauthorized singleapply; pending actual results.

## S3 launch evidence
Parent read-only preflight confirmed8existing extractionjobs=3Completed5dueRetry,no active leases/no otherstates; currentconfigbackgroundprocessing disabled. Scoped retrydryrun exit0 WouldRetry5 ExistingWork3. Launched --retry-existing --timeline-id76ba87c5-71a4-4bf6-a87b-d1a40a3ec86c --apply ONCE withapprovedactor and privateenvconfig. Supervisor32176 console26132,observedrunning, status/log/beforejobs snapshot C:/Users/biarunxa/AppData/Local/Temp/facts-semantic-retry-_46yiyrh. Observermve3l0lh-10-yqqx waitsONCE then reads logs/readonlyDBcounts. No blanketqueue drain,reset,recreate or embeddingexecution. Finalresultpending;neverblindrerun.

## S3 observed final result / remaining blocker
Supervisor completed,consoleexit2:Saved2(entries4=8facts,8=1fact),ExtractionFailed3(SemanticUnsupported),ExistingWork3. Read-onlyDBexit0/read-onlyconfirmed:entries1,3,5 stillRetry2attempts(+1);entries2=6facts,4=8,6=3,7=4,8=1 completed. Total22validdirectfacts,0revieworigin;22PendingembeddingjobsAttempts0 and0embeddedfacts. All8original extractionjobIDs preserved;only5selected jobsattempts+1,otherattemptsunchanged,noextraextractionjobs,0Running/activeleases. Proposalscurrently0,unchangedcount notprovenbecausebeforejobs snapshothasnoproposalbaseline.
Status/log C:/Users/biarunxa/AppData/Local/Temp/facts-semantic-retry-_46yiyrh. No ongoingprocess. SemanticUnsupported means separateverifierdidnotadmitatleastonecandidate;fine-grainedreasons/scoresnotretained,notproofentireentryfalse. Wholebatchrejectedbydesign. No blindnewretry/sourcechange. S3fullgenerationincomplete:threeentriesremain;S4requiresfurtherdiagnosis/decision. Scores/transientsemanticaccuracy/exhaustivecoverageunverified. Userauthorizedsingleboundedrunexecuted,noadditionalpaidcallsafterresult.
