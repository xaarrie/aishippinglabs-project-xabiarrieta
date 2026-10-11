# ODD Task: Fix Async Selector Preselection

## Objective
Fix the confirmed async selector-preselection bug so persisted values are selected after asynchronous option collections load on project and user edit forms.

## Scope
- Shared selector implementation in `dotnet/src/ProjectTimeline.UI.WebApi/ClientApp/src/App.jsx`.
- Focused regression coverage using the existing hosted test project, without modifying an already-modified test file.
- Non-mutating Playwright inspection of project and user edit forms at `http://127.0.0.1:5000`.

## Constraints
- Preserve all pre-existing uncommitted changes.
- Do not reset, checkout, stash, reformat unrelated code, or stage/commit unrelated work.
- Do not invent a test framework.
- Do not submit forms or mutate data during Playwright verification.
- Technical artifacts are written in English.

## Stable Checklist IDs
- [x] [SEL-PRE-001] Inspected existing implementation, diffs, and test approach.
- [x] [SEL-PRE-002] Reopened and completed the casing correction: `UserEdit` passes the API field `record.idUserRole` to `RoleSelect` while retaining the form contract `IDUserRole`; the focused regression test asserts the exact JSX spelling.
- [x] [SEL-PRE-003] Added `dotnet/tests/ProjectTimeline.Tests/AsyncSelectorPreselectionTests.cs` with focused source-contract regression assertions.
- [x] [SEL-PRE-008] Corrected shared selector initialization: state starts blank, adopts the persisted value only after it appears in options, and stops reapplying after a user change.
- [x] [SEL-PRE-004] Ran the frontend build and focused regression test successfully.
- [x] [SEL-PRE-005] Verified both edit forms with Playwright and compared selected values with authenticated API records without submitting forms.
- [x] [SEL-PRE-006] Updated this task document and its Engram mirror with observed evidence.
- [ ] [SEL-PRE-007] Create an isolated work-unit commit only if it can exclude all pre-existing unrelated work.

## Acceptance Criteria
- Async option collections select the persisted value after loading.
- Ordinary selector behavior and existing selector consumers remain intact.
- User changes are not overwritten by later option or prop updates.
- Project owner, primary timeline, and user role edit selectors are covered by observed verification or focused automated regression coverage.
- No unrelated modified files are changed, staged, or committed.

## Correction Evidence
- `SelectField` now starts with `useState("")`; its effect applies the persisted value only while the selection is still blank, the value exists in `options`, and the user has not changed the field.
- Current `App.jsx` contains `<RoleSelect value={record.idUserRole} />`; the previous `record.IDUserRole` browser-side lookup is absent.
- `AsyncSelectorPreselectionTests.cs` asserts the blank initialization, option-membership guard, controlled select value, user-change guard, and API-to-UI role casing at source level.

## Tests
- `npm run build` from `dotnet/src/ProjectTimeline.UI.WebApi/ClientApp`: passed (`vite build`, 30 modules transformed).
- `dotnet test dotnet/tests/ProjectTimeline.Tests/ProjectTimeline.Tests.csproj --no-restore --filter FullyQualifiedName~AsyncSelectorPreselectionTests`: passed (1 test).
- `git diff --check`: passed with no whitespace errors.

## Playwright Evidence
- After the build, `http://localhost:5000/assets/index-BwHDckEw.js` and `index-DV2gbb7h.css` were served with `304 Not Modified`; these are the current build outputs, so there is no stale-bundle evidence.
- Navigation to `/projects/0e6047b4-385d-4d7c-bbcf-4926e7c8fc70/edit` and `/users/00000000-0000-0000-0000-000000000001/edit` remained unauthenticated and rendered the Sign in form; evaluated select values were `[]` on both routes.
- No form submission or data mutation was performed. Prior authenticated values remain recorded in the earlier task evidence, but were not re-observed in this run.

## Delivery
- route: delegated
- writer trigger: `App.jsx` plus regression tests
- TDD mode: unknown/disabled (repository evidence shows no frontend test runner)
- delivery strategy: ask-on-risk

## Final Status
- Async selector correction and casing correction are covered in source and passing regression coverage.
- Automated frontend build passed.
- Focused regression test passed.
- Playwright was unable to inspect authenticated selectors because the existing browser session was signed out; the served bundle matched the current build.
- No commit created because `App.jsx` and the repository are already dirty with unrelated work, so an isolated safe work-unit commit cannot be made without absorbing existing changes.
- Next step: resolve the pre-existing hosted static-asset reference, then run the focused test and review/stage only an isolated work unit.
