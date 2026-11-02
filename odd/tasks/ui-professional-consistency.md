# Professional UI consistency

## Objective
Redesign the complete web frontend into a coherent, professional and responsive workspace using the current local Ibernova-inspired token foundation. Remove systemic misalignment and visual inconsistencies without changing backend contracts or existing workflows.

## Authorization and design direction
- User requested full UI/style review and explicitly selected `full_current_foundation`: integral redesign on current local token base, no external assets or backend changes.
- This is not certified official Ibernova DS integration: no package, callable catalogue or verified official documentation is available. Prior user constraint prohibits Entra-gated assets. Do not download fonts/icons/logos, probe credentials or invent official provenance.
- Direction: restrained technical workspace; violet primary actions, navy inverse chrome, mint sparing accents, neutral surfaces, system typography, consistent 4px-based spacing, clear hierarchy and controlled density. Innovation through composition and progressive information, not ornamental effects.
- Use shared semantic tokens and primitives, including inverse/success/warning/error/focus roles. Separate CSS grammar roles (flex factors versus grid tracks); compile/use valid literal breakpoints, never CSS vars in media conditions.
- Fluid main content with min-width:0, predictable chrome, local table scrolling, wrapping action bars and deliberate narrow-screen layouts. One page h1, section h2/h3, consistent card/header/form/table rhythms.
- Normalize text/select/textarea/button/link controls, accessible disabled/danger states and inline checkbox rows. Maintain adequate touch targets, focus visibility and contrast; mobile drawer closed means no hidden focus stops.
- Preserve all current routes, authentication, permissions, CRUD/status semantics, review source/proposal/audit safety and explicit Save/Discard. No new review modes/eligibility work in this slice; those remain in optimize-entry-review.md.
- Preserve extensive unrelated dirty user edits, especially provider/agent/backend changes, user task documents and hosted assets. Never read launchSettings, keys or agent-file-memory. No live backend/model/database calls during browser checks.
- One writer at a time. No staging, commits, push, reset, clean or user-process restarts without explicit request. Work-unit evidence will be recorded here without inventing commit identities.

## Read-only baseline evidence
- Source/UI audit plus 38 mocked Edge screenshots from current Vite source; no mutations. External evidence: ibernova-ui-audit-MVl6VM in session temp directory.
- At390px projects document497px wide (107px overflow); invalid media queries leave sidebar in-flow. Closed nav links still focus at x=-224 under aria-hidden.
- Logout foreground/background contrast1.035:1. Invalid unitless grid track expands topbar/footer depending on content instead of intended52/28px.
- Owner select567x19px vs adjacent input508x56.5px; mobile form scrollWidth643px. Snapshot checkbox width998px desktop; links with primary class unstyled; Review Discard offscreen at360px.
- Workspace has multiple32px h1s beneath smaller headings. Empty validation nodes disrupt grid. Loading and failed lists show false empty state. Review palette/focus differs from main semantic tokens.
- Login/recovery sampled390px fit; desktop table filters already align44px; Review own literal700px comparison stacking works. Preserve these working behaviors.

## Tasks and acceptance
- [x] U1 (completed): Valid shell/responsive navigation, contrast and focus lifecycle verified by113 fresh independent browser assertions and21 Node tests. Test-double-only maintenance unblocked focused HostedUiTests2/2 and full Debug157/157; parent repeated157/157. No backend behavior/hosted assets changed and no commits requested.
- [x] U2 (writer checks passed; final independent integration U6): Unified shared semantic tokens, typography, native field wrappers,44px controls/anchor variants,20px checkbox/radio glyphs, accessible states and wrapping actions.143 browser/113 shell checks,21Node,3HostedUi/full158 .NET passed; parent Node21 and desktop/mobile screenshot spot check passed.
- [x] U3 (writer checks passed; final independent integration U6): Account/admin/forms/lists share composition and explicit request states; confirmed user-route lookup fixed. One bounded density refinement yields69px normal/85px long-name desktop rows with44px targets and accessible native secondary-action disclosures. FullDebug160/Node21/U1–U3 browser regressions passed; parent Hosted5/screenshot check passed.
- [x] U4 (writer checks passed; final independent integration U6): Recomposed workspace/timelines/entries/artifacts/snapshots/collaborators/search using existing primitives and request guards. One workspace h1 instead of3, complete multiline content, aligned/local responsive controls and retained drafts/files/answers;118 specific browser checks/fullDebug161 and U1–U3 regressions passed.
- [x] U5 (writer checks passed; independent integration U6): Unified Review/comparison/audit semantic tokens, hierarchy, labels, keyboard-scrollable exact-text panes and wrapping actions.65 presentation checks/33 behavioral groups,Node21,Hosted7/fullDebug162 and U1–U4 regressions passed; request/state/Save-Discard controller bytes unchanged.
- [x] U6 (completed; independent proof accepted): Fresh browser/functional sampling and normal full build/post-build tests verified. Final normal Debug build exit0,zero warnings/errors after recorded static-asset failures/retry-procedure exception; post-build162/162 and parent162/162 passed,Node21.72screen/viewport samples589assertions plus full U1–U5 suites and33dev/33productionReviewgroups passed. Scoped source/hosted integrity preserved; unrelated whitespace/cache churn reported separately.

## Verification strategy
- Applicable behavior changes use deterministic browser test-first: baseline failures already observed; each worker adds/runs bounded assertions against current source before implementation and after. Source-string tests are supplemental, not layout proof.
- Use owned isolated Vite/Edge with external temp cache and synthetic API fixtures. Block non-owned network/service workers and all real backend/model writes/calls. Preserve source and original hosted-file manifests before/after.
- Existing Node review tests must continue passing. Update HostedUiTests only where layout implementation invalidates obsolete source checks or useful structural guards are needed. Full .NET/native builds run via verifier, not silently in every task.
- Normal npm/.NET builds can empty wwwroot and invalidate evaluated static-assets inventories. Before any such build, verify complete external asset backup; restore exact original paths/bytes in finally. Do not claim an incremental/retried failure as an initial success.
- Global whitespace failures previously existed in untouched CustomOpenAIClient.cs; report separately, never clean unrelated user code.
- RDD switch is user-owned; assess delegated output and follow returned verification plan. Review authority never authorizes delivery.

## Progress and evidence
- Audit completed read-only; parent inspected contact sheet. No implementation performed before this document and mirror.
- Engram mirror created under odd/ui-professional-consistency/tasks (observation900); readback verified by parent.
- Commit identities: none; no commits requested.

### U1 writer evidence
- Changed styles.css, App.jsx shell/navigation and HostedUiTests.cs only. Valid grid tracks/literal700px breakpoints, fixed52px/28px chrome, min-width0 main, overlay drawer, hidden closed nav, modal focus cycle/dismissal/restoration, inert background and reversible scroll locking.
- Behavioral RED: original browser assertions failed overflow/grid/focus/contrast. GREEN:113/113 browser checks at360/390/768/1440, short/long content, keyboard/dismiss/resize/cleanup; zero browser errors or unmocked/domain-write requests.
- Logout contrast18.17:1 normal and13.94:1 hover/focus. Intermediate overflow-longhand cleanup and brand alignment failures corrected; resize assertion synchronized to avoid harness race.
- Node review tests21/21 and in-memory Vite build passed. Scoped whitespace passed. Manifest confirmed only3 authorized files changed; hosted assets/unrelated frontend/task files unchanged by writer.
- `dotnet test ... --no-restore -c Release -p:BuildProjectReferences=false --filter FullyQualifiedName~HostedUiTests` failed compilation on missing current agent/review types in cached references; no tests executed and no backend rebuild attempted. Treat as a separate reference/tooling diagnosis, not passing proof or confirmed candidate source defect.
- Known long project form still scrolls627px inside390px; U2–U5 own those controls. No whole-application overflow-free claim.
- External browser/build/integrity evidence: ibernova-u1-shell-20260729 under session temp directory. No live backend/provider calls, restarts or commits.
- Native assess unassessable due unrelated untracked declaration; independent verifier required, RDD remains off.

### U1 independent diagnosis and bounded verification repair
- Debug focused HostedUiTests command exited1 with zero tests executed: CS0067 in EntryReviewTests.cs320/390 and EntryReviewDetailTests.cs193; CS0414 in OptimizationAgentToolsTests.cs163 and EntryReviewAuditTests.cs545, all unused FunctionCalled event implementations in test doubles. Current ICustomAgent declares the event (user's pre-existing backend change).
- Debug references contain current review/audit/event types; Release references dated2026-10-02 lack them while Debug dated2026-10-17 contains them. No U1-induced missing-type defect found. Do not rebuild/modify backend to repair test doubles or suppress warnings-as-errors.
- Verifier inspected stored113 passing browser evidence and current hashes but did NOT independently rerun browser/Node after its compile blocker. This is corroboration, not fresh independent proof. Scoped whitespace passed;13 file hashes/status unchanged.
- Parent derives narrow test-only repair surfaces: EntryReviewTests.cs, EntryReviewDetailTests.cs, OptimizationAgentToolsTests.cs, EntryReviewAuditTests.cs, plus HostedUiTests.cs only if an obsolete structural assertion needs correction after tests actually execute. Preserve real event behavior outside fakes; use explicit no-op accessors for fake agents that intentionally emit no events, rather than invented invocations/warning suppression. No production/backend source edits.

### U1 fresh independent browser verification
- Fresh independent copied/hash-verified harness exited0:113/113 browser assertions; Node21/21, zero failures/skips; scoped whitespace clean. Historical RED not replayed, current GREEN freshly observed.
-360/390/768/1440px: document matches viewport, header52px/footer28px, main fluid/local scroll.700mobile/701desktop boundary passed. Logout18.17:1normal/13.94:1hover-focus; drawer close5.90:1; solid3px mint keyboard focus.
- Closed drawer no hidden tab stops; modal semantics, initial/loop/programmatic focus containment, Escape/backdrop/close/navigation restore, resize/unmount, overflow/inert cleanup passed.
- Known U2 form overflow627px inside390px content reproduced; document itself remains390px. No confirmed U1 regression. All13 checked source/hosted/task hashes unchanged; no real services/writes/restarts.
- Independent external evidence: ibernova-u1-independent-Aqc2FY under session temp, fresh results/metrics/screenshots/traffic/manifests; original evidence retained.

### U1 verification repair and closure
- Five fake agents now implement non-emitting FunctionCalled with explicit no-op add/remove accessors, removing unused backing fields/assignments. No production interface/behavior/warning policy changed. HostedUiTests declaration scanner now excludes media headers but continues checking declarations inside valid media rules.
- Observed compiler RED5 errors; intermediate focused1/2 failed obsolete media-condition source assertion; final focused2/2 and full Debug157/157,0 skipped. No backend/frontend rebuild. Scoped whitespace clean; four untracked test files normalized toLF by edit tool, recorded without unrelated cleanup.
- Writer preserved backend41/frontend8/hosted5 manifest entries; task aggregate changed only through parent bookkeeping. No commits/live calls/restarts. Parent repeated normal no-build Debug solution tests:157 passed,0 failed/skipped,exit0.
- Native assessment still unassessable due unrelated untracked scope; independent browser verification already passed, full independent combined checks remain U6.

### U2 shared primitive evidence
- Changed styles.css/App.jsx/HostedUiTests.cs only: semantic type/status/focus/border tokens,44px input/select/button/link controls, system14px/20px control type,20px native checkbox/radio, growing textarea, danger/disabled/forced-colors states and wrapping actions.
- Field/SelectField now native single-child label wrappers with associated hint/error IDs, no empty siblings; async preselection/props/callback semantics preserved. PageHeader optional headingLevel defaults h1. Disabled links prevent navigation.
- Fresh behavioral RED54 intended failures/49 passes; final GREEN143/143 browser at360/390/768/1440. Inputs/selects44px, border-box and common font; sampled forms fit without horizontal overflow. Checkboxalignment0.5px; textarea grows338px. Text contrast>=5.81:1 and boundaries>=3:1.
- Mocked auth/project CRUD/filter/sort/snapshots/controlled editing/Review save-discard/read-only/keyboard/forced-colors checks passed. U1 shell113 regression,Node21,focusedHosted3/fullDebug158 passed; in-memoryVite passed. Initial source-scanner/native-select/harness issues corrected, not counted as final passes.
- Scoped whitespace clean;46-file manifest changed only3 allowed files; hosted/unrelated/provider/testfake state unchanged. Parent repeatedNode21 and inspected desktop1440/mobile390 project-form screenshots. Module-typewarning remains nonfatal.
- External evidence: ibernova-u2-primitives-c9ARY5; no external assets, backend/live-model calls, normal hosted builds, user restarts or commits. Native assess unassessable due untracked scope requires separate finalU6 integration verification.

### U3 initial functional evidence and visual review
- Account/auth/profile/users/projects composition, ManagedForm synchronous submit lock/feedback/draft retention, DataTable explicit loading/error/retry/true-empty/filtered-empty and danger actions implemented within3 allowed files,277 additions/21 deletions. User route/project lookup defect confirmed and corrected client-side; no backend contracts changed.
- Browser RED88 intended failures/101 passes; extra filter-wrap RED8; current GREEN259 assertions at360/390/768/1440. U2 controls143/U1 shell113 regression,Node21,Hosted4/fullDebug159,write:falseVite/scopedwhitespace/integrity passed. External evidence: ibernova-u3-screens-5mb88O. No source outside3surfaces or hosted assets changed.
- Parent focusedHosted4 passed and inspected mobile login/desktop project-list captures. Login/form composition is coherent, but repeated full-width primary row actions plus prominent red deletes produce about120px rows and dominate the list visually. This violates the intended restrained hierarchy/controlled density despite functional passes.
- U3 remains open for one bounded visual refinement: content-sized primary row action and compact secondary actions/progressive disclosure where needed, keeping44px touch targets, all permissions/callbacks/confirmations/keyboard behavior and accessible full details. Do not broaden into workspace or invent domain behavior. Functional tests must be rerun against actual interaction changes.
- Native assessment unassessable due unrelated untracked scope; final independent U6 still required.

### U3 density refinement and closure
-47 additions/10 deletions across App.jsx/styles.css/HostedUiTests: naturally sized primary row actions, record-specific accessible names, in-flow native More disclosure with Escape/focus restore, full-description preview inside disclosure and subdued destructive actions. Same callbacks/confirmations/permissions.
- Behavioral RED29 failures/23 passes:121px rows,96px action stacks,264px primary width. GREEN125 refinement assertions: normal rows69px,long-name85px,Open workspace142.8px,Edit59.5px,closedactionband44px. At768 long text may legitimately yield105–125px; no shrinking/truncating data for a pixel target.
- Enter/Space/Escape, hidden child focus stops, unclipped disclosure/full descriptions, callbacks and native confirmation cancellation passed. Initial hidden-details rectangle assertions corrected to checkVisibility, not counted as final proof.
- Full U3 screens259/U2 controls143/U1 shell113,Node21,Hosted5/fullDebug160 and in-memorybuild/scopedwhitespace/integrity passed. Only3allowedfiles changed; workspace/shell/native fields/table-form lifecycle bytes unchanged. Parent repeatedHosted5 and inspected final desktop project-list capture, accepted controlled density.
- External evidence: ibernova-u3-density-TkXrr2. No unrelated/hosted/user provider changes, live calls, restarts or commits. Native assess unassessable requires final independent U6.

### U4 workspace evidence
- Changed App.jsx/styles.css/HostedUiTests only: coherent project/timeline/entry hierarchy, maintenance feedback, artifact/snapshot/collaborator controls and explicit search/conversation. Mounted success/navigation/context guards preserve drafts/files/answers and Review callbacks/permissions.
- Behavioral RED22 intended failures/17 passes; finalGREEN118/118. Separate stale-save/context failures, upload feedback/alignment/overflow and harness selectors corrected before final proof. No remaining in-scope failing check.
-360/390/768/1440: workspace h1 count3->1; document/main contained; multiline content retained. New entry/snapshot actions~103/140pxwide and44pxhigh; checkbox20px; desktop upload alignment within1px.
- Exact CRUD/status payloads,403/read-only, duplicate protection/failure-retained data, native confirmations,snapshot ID order,query/answer binding and Review Discard/explicitSave passed. U1shell113/U2controls143/U3screens259/density125 regressions,Node21,Hosted6/fullDebug161 passed;write:falseVite/scopedwhitespace/integrity passed.
-56-filemanifest changed only3allowedfiles; hosted assets, shell/account/admin/nativefields/Reviewcallbackcontroller bytes unchanged. No live services/prohibited reads/restarts/commits. Parent Node21 repeated and inspected1440workspace capture; accepted visual hierarchy.
- External evidence: ibernova-u4-workspace-x2hwL1. Normal hosted/backend builds deferred; native assess unassessable requires independent U6.

### U5 Review integration evidence
- Changed EntryReview.jsx/EntryReview.css/styles.css/HostedUiTests only: shared typography/spacing/surfaces/focus and diff/evidence role aliases; hierarchy/explicit question-answer label IDs, complete suggested options, labelled keyboard-scrollable comparison, secondary audit/discard and primarySave. Hooks/request/state/Save-Discard logic bytes unchanged.
- Presentation RED32 intended failures/25 passes; GREEN65; behavioral33 groups passed exact content/payloads,coverage0/1/null andissuesat1,failures/stale/races/StrictMode/permissions/questions/explicitSaveDiscard. Previous-answer label lookup exposed accessibility defect corrected with explicit IDs; false border-measurement assertions corrected.
-360/390/768/1440 no document/main overflow; comparison stacks correctly. Deletioncontrast6.10/insertion5.81/warnings6.01; shared3pxvioletfocus. Underline/strike-through/whitespace/complete evidence preserved, no score guarantee/automaticSave.
- U1shell113/U2controls143/U3screens259/density125/U4workspace118,Node21,Hosted7/fullDebug162,write:falseVite/token scan/scopedwhitespace/integrity passed.59-filemanifest changed only4allowedfiles; hosted/controller/unrelatedsharedcomponentrules unchanged.
- External evidence: ibernova-u5-review-31qnou, verification-receipt.json and delivery viewport captures. Parent Node21 repeat and1440 comparison screenshot inspected/accepted. Module-type warning and unrelatedCustomOpenAIClient whitespace unchanged. No livecalls/normalbuilds/userrestarts/commits. Native assess unassessable requires U6 independent verification.

### U6 independent visual pass and unresolved build procedure
- Fresh verified Playwright1.60/Edge source suites passed:shell113,controls143,admin259,density125,workspace118,Review65; behavior33development+33productiongroups. Independent sampling589assertions across72screen/viewport combinations at360/390/768/1440. Parent inspected contact-sheet and accepted cross-screen visual consistency; no concrete redesign-caused defect reproduced.
- Computed measures: document/main fit, localtable scroll,52/28chrome,700/701breakpoint,drawerfocus/inertcleanup,44targets/native labels/preselection/pendinglocks,69/85pxrows and natural142.8/59.5actionwidths,logout18.17/13.94contrast,Review6.10/5.81/6.01. Exactsource/evidence/races/permissions/explicitSaveDiscard passed. Synthetic-only, no blanket accessibility/officialDScertification.
- Restore exited0; normal Debug solutionbuild exited1 after Vite generation because static-assets evaluation referenced missing index-3xHtmoe4.js. Verifier retry detector missed phrase 'No file exists', then finally restored originalassets before an unchanged retry. No retry ran; build is NOT verified. This is a verification-procedure failure, not proof of a new source defect.
- No-build Debug tests162/162 andNode21 passed, vulnerabilityscan clean. Existingbinaries are not current-fullbuild proof. Scopedwhitespace passed; globalexit2 for unchanged prior whitespace in CustomOpenAIClient45/48 and OptimizeEntryAgent24/30. Module-typewarning remains. Do not clean out-of-scope source.
-64 scoped source/config/task hashes unchanged; originalwwwroot3files263597B restored exactly. Three .vs FileContentIndex paths disappeared andthree appeared; actorunknown, no cleanup/revert. Treat as separate cache/status incident, distinguish from source/asset integrity and do not claim globally clean repo.
- External evidence: ibernova-u6-independent-awptAm, independent-summary.json/contact-sheet/logs/manifests. No realdata/modelcalls or user-process interference.

### U6 normal build/post-build proof and closure
- Follow-up normal Debug solution build with OPENAI_API_KEY unset: attempt1 exit1 known missing evaluated hashedasset; matcher Windows-separator bug again restored prematurely. Corrected external matcher, then attempts2(exit1 same 'No file exists') and3(exit0,zero warnings/errors) ran in one verified protected window without intermediate restoration. Three follow-up attempts plus earlier initial failure; no source/config fixes, suppressions or build bypass. Procedure exceptions accepted as disclosed, not erased.
- Post-build normal solution tests on attempt3 outputs:162 passed,0failed/skipped,exit0. Parent repeated same no-build Debug solution test command with keyunset:162/162,exit0. Previous fresh Node21/vulnerabilityscan/browserproof retained because64scoped baseline/frontend hashes unchanged.
- Fresh full original hosted backup3files263597B verified/restored exactly. Status outside .vs unchanged. Further3gone/3new .vs FileContentIndex paths remain cache-like activity with unknown actor, no contents inspected/cleanup/attribution. This is not scoped source/asset damage or a claim of globally clean repository.
- Scopedwhitespace clean; globalexit2 remains prior out-of-scope whitespace CustomOpenAIClient45/48 and OptimizeEntryAgent24/30. Existing Node module-typewarning remains; no warningpolicy changes. Live provider/database/actual502 and separate latest/newReviewmodes untested/outofscope, not silently completed.
- Follow-up external evidence: ibernova-u6-followup-fuqdeo__ (diagnosis/attempt1) and ibernova-u6-followup-final-20jrdrlv (attempts2–3/tests/manifests); initial fresh visualproof/screenshots at ibernova-u6-independent-awptAm. Originalhostedassets/64scopedsource bytes preserved, no source edits/restarts/commits during independent verification.

## Next step
All six UI redesign units are complete and verified on sampled desktop/mobile views with mocked data. To serve the new source without the old evaluated hashed-asset mismatch, regenerate frontend before .NET project evaluation: `npm --prefix dotnet/src/ProjectTimeline.UI.WebApi/ClientApp run build`, then `dotnet build dotnet/ProjectTimeline.sln`, then restart the API under user control. Existing hosted outputs were deliberately restored to preserve prior dirty artifacts; no serving-process restart or publishing performed. This is the accepted local Ibernova-inspired foundation, not official DS/accessibility certification. Other review-feature work remains in its separate task document.
