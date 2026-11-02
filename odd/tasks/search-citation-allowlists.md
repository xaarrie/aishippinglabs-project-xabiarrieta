# Operation-Scoped Search Citation Allowlists

## Objective
Constrain generated fact/context citations to the IDs actually sent in each synthesis operation, enforce empty context citations when no contexts were provided, and persist minimal ID-only diagnostics for successful and failed evaluation rows. Verify the change and capture one new full50-scenario run without overwriting earlier artifacts.

## Problem and evidence
Latest run `da0ff88c-dfef-427f-8b80-e2f176b72501` captured25Refused/23Partial/2UnknownContextCitation failures (ordinals12/14). Case14 used two EntryIds as context EvidenceIds. Case12 had at least one unknown contextual citation, but its available context set was not persisted; operation-local random EvidenceIds cannot be reconstructed from a later database read. Strict shape/GUID format alone does not enforce membership.

## Authorization and boundaries
- User explicitly said `adelante` to the proposed per-operation citation allowlists, empty-context enforcement and ID-only diagnostic logging. Implement and verify within that approved correction; one new full50 capture follows the established evaluation workflow, no extra probes/retries.
- Derive allowed fact IDs and context EvidenceIds ONLY from the exact emitted safe `matches`/`sources` payloads sent to synthesis, never database-wide facts, EntryIds, anchor IDs or omitted entries.
- Separate immutable copied catalogs per operation/request. Fresh strict schema/options and leaf validator; no global/shared mutable catalog, no leakage across reused/concurrent agents/clients.
- Nonempty catalogs use canonical GUID item enums. Empty catalog retains required array with typed items and maxItems0; never invalid enum[]. Leaf admission independently checks membership, including bypassed provider schema responses.
- Preserve final BuildResponse citation guards and order unchanged. No EntryId→EvidenceId mapping, silent dropping, retry/repair, relaxed proof or acceptance of invented citations.
- Preserve server-owned query/search/fetch lifecycle, authorization/revision/fingerprints, no-query-count-cap policy, clipping/omissions, budgets/deadlines/provider/model, safe refusal policy and public HTTP/response contracts. This feature does not tune refusal classification or solve over-refusal.
- Diagnostics are operation-local immutable snapshots: available fact/context IDs, explicit omitted EntryIds, cited valid GUID sets/occurrence counts, unknown sets, invalid-GUID counts, truncation and fixed phase/final-validation labels. No question/fact/source/model text, raw invalid strings, exception bodies/messages/stacks, headers, credentials or connection strings.
- Capture availability before synthesis, citations inside JSON admission before membership rejection, typed-result fallback for injected agents, and final revalidation/publication state where attempted. Repeated successful validation is idempotent; unavailable observation is not falsely reported as empty. Do not introduce extra revalidation or alter precedence.
- Evaluator owns a fresh capture per scenario and writes an optional additive snapshot field after operation/disposal for return/error/cancellation. Legacy sessions/rows remain compatible; no public UI change.
- Keep every previous source/evaluation artifact and unrelated dirty edit. Branch `feat/search-facts-evaluation-runner`, base HEAD `0c98e53b58d37c7b7615468e06c2937594f437a1`. No Git commit/push/PR/merge: no explicit commit request exists under active safety policy.
- RDD remains globally off. Read-only ASSESS after writer; follow independent high-risk fallback if unassessable due existing untracked declaration. Do not alter native authority or switches.

## Tasks
- [x] T1 — Implement immutable per-operation schema/leaf allowlists and safe diagnostic capture; observe regression RED→GREEN and update narrow docs. Status: completed;394focused/1580full.NET/48client tests/buildPASS. Commit pending explicit user authorization.
- [x] T2 — Independently verify wire/schema, safety/isolation and full deterministic suites/build before paid execution. Status: completed;1580.NET/48client/buildPASS,no blocking findings. Commit pending explicit user authorization.
- [x] T3 — Run exactly one new full50 evaluation with fresh configuration, ID snapshots, scope/corpus/prior-artifact integrity and no retries. Status: completed;exit0,50returned,0technicalerrors(25Refused/23Partial/2Complete). Commit pending explicit user authorization.
- [x] T4 — Independently reconcile/export snapshots and outcomes, compare recorded technical errors and refusal/partial counts, and report limits without correctness scoring. Status: completed;28independentchecks+parentrawsnapshotparserPASS. Semantic/over-refusal review remains outside scope. Commit pending explicit user authorization.

## Acceptance and checks
- Offline actual SDK wire has separate allowed fact/context enums matching exact synthesis input IDs. Unemitted known facts, EntryIds, omitted entries and cross-namespace IDs never become allowed context values.
- No-context array schema is valid strict JSON Schema and enforces[] both at provider contract and admission. No new provider/model/query-count limits or invalid empty enums.
- Preserve final GUID-based membership/case handling: uppercase valid D-format known IDs remain semantically valid to leaf/final guards. Mutable inputs cannot change an existing schema/catalog; independent/reused/concurrent operations cannot reuse IDs or observations.
- Regressions reproduce case14 wrong namespace and case12 unknown valid UUID. Keep final-guard rejection tests through a bypassing typed fake; assert native invalid output fails early and still records safe available/cited/unknown IDs.
- Under clipping/omission, projected allowed sets equal emitted/final ledger sets; omitted entries never become context IDs. Record before/after sets without source text, invalid strings or duplicate observations.
- Safe diagnostic snapshots survive errors and cancellation without replacing original exceptions; final validation not attempted is explicit on early rejection. Malformed JSON/envelopes do not masquerade as empty citation arrays.
- Baseline from prior feature:1558 deterministic .NET tests,48 client tests, solution/frontend builds and18 actual component DOM checks passed. This backend-only follow-up must run focused/full .NET suites/build and client regression tests; no new UI browser change is expected.
- Preserve CSV `C:/Data/formacion/TimelineSpecifications/search_facts_evaluation_scenarios.csv`,50rows, SHA256 `25BEC02395D327D76F8B212BF3EDF69C3E00DC59FFA45146A619FB5265C2DD0C`.
- Revalidate scope: actor `0e6047b4-385d-4d7c-bbcf-4926e7c8fc70`, timeline `76ba87c5-71a4-4bf6-a87b-d1a40a3ec86c`, project `8d3559af-77d8-476d-b763-5f085889861d`. Fresh private credentials in child env only; read-only DB transactions; external content telemetry off. No migrations, seeding, activation, web startup or paid correctness judges.
- Fresh uniquely named JSONL/manifest/context and CSV/validation/comparison derivatives beside source, CreateNew/nooverwrite; capture every response/refusal/partial/error and verify original fields, IDs, snapshots, costs, prior hashes and current corpus before/after.
- Do not promise zero future errors or valid answers. Prior25refusals include8happy_path and remain semantic-review risk outside this correction.

## Scoped research
Primary source: https://developers.openai.com/api/docs/guides/structured-outputs (read during exploration).
- Standard structured outputs support array minItems/maxItems and enum constraints; fine-tuned model restrictions are separate. Current model is base gpt-4o-mini, not a fine-tuned model.
- Up to1000 enum values across schema; >250 strings in one enum have15000-character combined bound. Existing server MaxDistinctFacts and MaxSourceContexts are <=40 each, so at most80 citation enum values, far below those bounds.
- Empty list uses maxItems0 and typed items instead of enum[]. Offline tests characterize generated request; real provider acceptance remains a functional-run observation, not assumed from docs.

## Routing and review workload
- T1 one foreground gentle-ai-worker; mandatory multi-file/preparation route. Small immutable catalog/capture helper and current option/evaluator seams, no tracing framework or SDK/UI expansion expected.
- T2 independent gentle-ai-verify under ASSESS plan. T3 existing read-only execution verifier. T4 independent offline check of runtime-derived artifacts; ignored export helper may be adapted narrowly if needed.
- Forecast approximately300–450 authored changed lines plus tests/documentation, advisory not code-golf. Keep smallest cohesive behavior and tests; no cosmetic shrinking. Delivery strategy ask-on-risk, but no delivery/commit authorized.
- No parallel source writers; no task checkoff before observed checks. Commit identities pending explicit user request.

## Progress and evidence
- Read-only scout mapped exact safe projection and leaf admission seams; no Tools/CapturingAgent/framework/UI changes are required by the recommended minimal design.
- Primary OpenAI docs confirm enum/maxItems support; parent spot-checked existing option bounds<=40. No source edits or provider calls made for this feature yet.

- T1writercompleted11narrowallowedfiles;2newfiles406linesplusexistingchanges. Actualworkloadexceeds300–450forecastbutretainscohesivetechnicalscope;noextraUI/framework/policychange,nocodegolf.Task-onlytotalchurnnotisolatablefrompredirtyfeatures.
- ObservedREDactualSDKwiremissedcitationenums(372pass/1intendedfail),thenfinalGREEN394/394focused,1580/1580full.NET,48/48client,0failed/skipped;solutionbuild0warnings/errors. Intermediateanalyzer/ImmutableArraytestassertionissuescorrected,nonewaived.
- FreshcopiedFrozenSetcatalogsfromexactsafeprojections;separatestrictGUIDenums,emptyrequiredtypedarraysmaxItems0noenum[]. Leafcapturesbotharraysbeforemembershipfailure;case-insensitiveparsedGUIDmembership,immutableoperation-localcapture,safe128-IDdiagnosticbounds/exactcounts/idempotentobservations.FinalBuildResponseguards/orderunchanged;typedbypassrejectiontestsPASS.
- Evaluatorcreatesfreshcaptureperoperationandoptionalnull-omittedcitationDiagnosticsafteroperation/disposal;earlyrejectionfinalvalidationunattempted/finalLedgernull. Parentspot-checkedcatalog/captureimplementationandnoevidencetextretention.
- NativeASSESSunassessableduetopreexistinguntrackeddeclaration;followreturnedindependenthigh-riskplan,RDDremainsoff. Noadditionalprovider/DBcallsorcommits.

- IndependentT2PASS:1580/1580.NETdeterministic,48/48client,0failed/skipped,solution0warnings/errors. Inspectedexactprojectioncatalogs/typedemptyarrays/requestlocaloptions/GUIDmembership/unchangedfinalguardorder/bypassrejection/safeidempotentcapture/disposalandlegacybehavior;noblockingfindings. Visiblegitstatusmatchedbefore/after,notfullhistoricalbyteaudit.
- UIunchangedbythisbackend-onlyfeature;prior18DOMchecksremainhistoricalevidence,notnewbrowserpass. Live/PostgreSQLoptinsfilteredout/disabled;actualprovideracceptanceofdynamicenum/zeroarraypendingT3.
- Rebuiltcurrentevaluatorreadyat`dotnet/tests/ProjectTimeline.Tests/bin/server-search-build/bin/ProjectTimeline.SearchFactsEvaluator/release/ProjectTimeline.SearchFactsEvaluator.dll`.

- T3executedONEnewfull50pass,run`5913302c-3f71-4696-b72a-d5bc7796b256`,exit0,manifestCompleted,50input/persisted/returned,0failed/cancelled/unstarted.25Refused/SafeRefusal,23Partial,2Complete,0Empty/InsufficientEvidence.153.851swall/151.541sscenariotime. No retries/probes/repairs.
- Newrawartifactsunder`C:/Data/formacion/TimelineSpecifications/`: `search-facts-evaluation-citation-allowlists-20261102T062417Z-f35d9311.jsonl`,adjacent`.jsonl.manifest.json`and`.corpus-context.json`. ContextusesrunPermissionandcount-onlysnapshotmetadata,fullIDsremaininrawrows.
- Snapshotspresent50/50;25preparedcatalogs+observations/finalledger/finalvalidationcompleted,prepared/finalsetsmatch,0unknownfact/contextIDsorinvalidGUIDs.4empty-contextcatalogsand21nonempty,nodiagnosticclipping. Actualproviderpassrecordednoschemarejection;availabilitystillpreparedpayload—notwireaudit.
- Refusal25snapshotsAvailable/Observednull,noFinalLedger/finalvalidation;publicationflagsTRUEasactualserialized(publicreturnedsaferefusal),notproof. Do notcarrypriorassumptionthatPublishedfalseforallrefusalsintovalidator.
- Cases12/14bothnowPartial/ToolOrEvidenceLimit.12:25availablefacts/0contexts,5factcites/0contextcites;14:23facts/3contexts,2factcites/2contextcites. Both0unknown/invalid,finalvalidation/publicationcompleted. DifferentoperationIDsarenew,notreconstructionofoldrandomEvidenceIds.
- KeylesspreflightPASS currentAdmin/scope/50rowhash/8entries40eligible,nofilescreated. All24priorfiles/sourceunchanged,currentcorpusbefore/after5b9d9545036c2c502f95a20cafd9a93c7592d0dc58f399567f6e7f954f727502,8entries48facts40valideligible,gaps1/3/6/entry8zeroeligible/48pendingembeddingsunchanged;binaryhashstableinrun.
-75admissions/messages,50sessions,261506input+8932output=270438tokens,0unknown/inflightusage/cost,observedestimateUSD0.04458510(notinvoice). Operationalprevious→current48→50Returned/2→0Failed/25→25Refused/23→23Partial/0→2Complete,noaccuracyclaim.

- T4helperextendedONLYignored`tests/bin/server-search-results-export/export_results.py`:strictoptionalSnapshotshape/GUID/count/flagprivacychecks,catalog-observation-finalledger/synthesis/publiccitationreconciliation,anddedicatedCSVcitation_diagnostics_json/status/final_validatedcolumns. Existingraw_record_jsonstillfull,legacyrowsnull/notcapturedexplicitly.
- HelperobservedREDnewbasenameunrecognized/missingCSVfield,then45inmemoryassertiongroupsGREENandread-only--check-inputs-onlyPASS28checks onrealdata.50snapshots/25exactcatalog+finalledger+message/citationreconciliations,0unknown/invalid/clipping;all27originalhashesunchanged/3derivativetargetsabsent. Noexternalderivativesorpaidcallscreatedbyhelper.
- NewCSVprecomputationreports2Exceloversizedcells,0truncation;readofficialrawJSONLforfullcontent. PublishedrefusaldescribespublicsafeoutcomeanddoesNOTimplycitationproof;clippeddiagnosticsetscannotprovefullsetmembershipbeyond128storedIDs.

- FinalindependentT4exactcommandsPASS28checkseach:read-onlyinputs,CreateNewexport,--verify-only. CreatedONLYcurrentbasename`.results.csv`,`.validation.json`,`.comparison.json`;all27originalhashes/sourceunchanged/24recordedpriorhashesPASS.50snapshots/25exactprepared-observed-final-model/publiccitationreconciliations,4empty/21nonemptycontextcatalogs,0unknown/invalid/diagnosticclipping.
- CSVUTF8BOM50rows/47columns/17JSONcolumns,fullrawrecord/response/diagnosticroundtripPASS,0formulaescapes/truncation.2raw_record_jsoncellsoversizedforExcel:ordinal16=33013and29=34998UTF16units;JSONLremainsauthoritative.
- Parentsecond-implementationrawsnapshotparserPASSinoneboundedcommand:all50originalorderedheader/valuefields/question/ordinals/hash/runID/uniqueIDs,fullraw_record_json+citation_diagnostics_jsonroundtrip,allReturned0errors,25Refused/23Partial/2Complete,all25available/finalsetsidenticalandobservedcitationssubsetwith0unknown/invalid,4emptycontextarrays,exactcase12/14counts. Refusalsnullavailability/observation/finalledgerbutpublicationtrueacceptedaspublicsafeoutcome,notproof.
- Freshrefusalcounts:8happy_path(ordinals1/3/5/6/11/17/18/24),4varied_phrasing,1edge_case,5out_of_scope,7breaking. No semanticverdict;over-refusalqualityriskpersists. Completeordinals23/27previouslyPartial;cases12/14nowPartialwithallowedcitations. Operational2→0errorsisnot50correctanswersoraguaranteeoffuturezeroerrors.
- Finaltests1580.NETdeterministic/48client+solutionbuildPASS;actual25synthresponsesacceptednewcatalogconstraintsand4emptycontextschemaswithoutprovider/schemafailure,notwireaudit. UIunchanged,prior18browserchecksnotrerun. Live/PostgreSQLfixturesfilteredout;theauthorizedfull50agentcaptureusedread-onlyDBandactualmodel.
- Reportretains4explicitoutofscopeitems:semanticcorrectness/over-refusal,freshDB/source-documentverificationduringofflineaudit,PIIclearance,invoice reconciliation. Actualrunpre/postDBfingerprint/coveragechecksrecorded;offlineauditdidnotrefreshDB. Estimatesnotinvoice,credentialheuristicnotprivacyclearance. RDDoff;ASSESSunassessable(untracked)coveredbyindependenthigh-riskchecks. NoGitcommit/push/PRoradditionalpaidcalls.

## Next step
Featurecomplete:delivernewCSV/JSONL/comparisonpaths,0technicalerrorsin50pass,25negative/23partial/2Completeoutcomesandcase12/14fixevidence. JSONLforcompletecells. Retainallpriorartifactsandfinalproofguards. Furthersemantic/refusalqualitytuningorpaidrunsrequireanewuserrequest;donotclaimnegativeanswersarevalidated.
