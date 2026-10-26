import React, { useEffect, useId, useLayoutEffect, useRef, useState } from "react";
import { api, clearToken, download, setToken } from "./api.js";
import FactReview from "./FactReview.jsx";
import EntryClarifications from "./EntryClarifications.jsx";
import TimelineSearchResults from "./TimelineSearchResults.jsx";
import { createSearchRequestFence, searchTimelineFacts } from "./timelineSearch.js";
import { hasEntryWriteAccess } from "./entryAccess.js";
import { createEntrySaveAction, createFactReviewClient, factReviewActions, savedFactSource, entryMatchesFactSource } from "./factReview.js";

const factClient = createFactReviewClient(api);

const formValues = event => Object.fromEntries(new FormData(event.currentTarget));
const currentPath = () => location.pathname.replace(/\/$/, "") || "/";
export function resolveRoute(source = location) {
  const path = (source.pathname || "/").replace(/\/$/, "") || "/";
  if (path === "/") return { path: "/projects", kind: "projects", params: {}, query: source.search || "" };
  const patterns = [
    ["recovery-request", /^\/recovery-request$/], ["reset-password", /^\/reset-password$/],
    ["profile", /^\/profile$/], ["users", /^\/users$/], ["user-create", /^\/users\/new$/],
    ["user-edit", /^\/users\/([^/]+)\/edit$/], ["projects", /^\/projects$/], ["project-create", /^\/projects\/new$/],
    ["project-edit", /^\/projects\/([^/]+)\/edit$/], ["project", /^\/projects\/([^/]+)$/],
    ["timelines", /^\/projects\/([^/]+)\/timelines$/], ["timeline-create", /^\/projects\/([^/]+)\/timelines\/new$/],
    ["timeline-edit", /^\/projects\/([^/]+)\/timelines\/([^/]+)\/edit$/],
    ["entry-create", /^\/projects\/([^/]+)\/timelines\/([^/]+)\/entries\/new$/],
    ["entry-edit", /^\/projects\/([^/]+)\/timelines\/([^/]+)\/entries\/([^/]+)\/edit$/],
    ["entry-detail", /^\/projects\/([^/]+)\/timelines\/([^/]+)\/entries\/([^/]+)$/]
  ];
  for (const [kind, pattern] of patterns) {
    const match = path.match(pattern);
    if (match) return { path, kind, params: kind === "user-edit" ? { id: match[1] } : { projectId: match[1], timelineId: match[2], entryId: match[3], id: match[1] }, query: source.search || "" };
  }
  return { path, kind: "not-found", params: {}, query: source.search || "" };
}
const SPA_NAVIGATION_EVENT = "project-timeline:navigation";
function useLocationState() { const [route, setRoute] = useState(() => resolveRoute()); useEffect(() => { const update = () => setRoute(resolveRoute()); addEventListener("popstate", update); addEventListener(SPA_NAVIGATION_EVENT, update); return () => { removeEventListener("popstate", update); removeEventListener(SPA_NAVIGATION_EVENT, update); }; }, []); return route; }
function navigate(path, { replace = false, state = null } = {}) { (replace ? history.replaceState : history.pushState).call(history, state, "", path); window.dispatchEvent(new Event(SPA_NAVIGATION_EVENT)); }
function Link({ href, children, className = "", onNavigate, onClick, ...props }) {
  const disabled = props["aria-disabled"] === true || props["aria-disabled"] === "true";
  return <a {...props} className={className} href={href} onClick={event => {
    if (disabled) { event.preventDefault(); return; }
    onClick?.(event);
    const canceled = event.defaultPrevented;
    event.preventDefault();
    if (!canceled) { navigate(href); onNavigate?.(); }
  }}>{children}</a>;
}
function Form({ children, onSubmit, ariaLabel, ...props }) { return <form {...props} aria-label={ariaLabel} onSubmit={event => { event.preventDefault(); onSubmit(formValues(event)); }}>{children}</form>; }
function FieldFeedback({ id, hint, error }) {
  return <>
    {hint && <small id={`${id}-hint`} className="field-hint muted" role="status">{hint}</small>}
    {error && <small id={`${id}-error`} className="field-error error" role="alert">{error}</small>}
  </>;
}
function fieldDescription(id, hint, error, describedBy) {
  return [describedBy, hint && `${id}-hint`, error && `${id}-error`].filter(Boolean).join(" ") || undefined;
}
function Field({ name, label, type = "text", required = true, value, hint, error, id: suppliedId, ...props }) {
  const generatedId = useId(); const id = suppliedId || generatedId;
  return <label htmlFor={id} className="field"><span id={`${id}-label`}>{label}</span>
    <input {...props} id={id} name={name} type={type} defaultValue={value ?? props.defaultValue ?? ""} required={required}
      aria-labelledby={props["aria-labelledby"] || `${id}-label`}
      aria-describedby={fieldDescription(id, hint, error, props["aria-describedby"])} aria-invalid={error ? true : props["aria-invalid"]} />
    <FieldFeedback id={id} hint={hint} error={error} />
  </label>;
}
function SelectField({ name, label, options, value, required = true, disabled = false, onChange, hint, error, id: suppliedId, ...props }) {
  const generatedId = useId(); const id = suppliedId || generatedId;
  const [selectedValue, setSelectedValue] = useState(""); const userChanged = useRef(false);
  useEffect(() => { if (userChanged.current || selectedValue !== "" || value === undefined) return; if (options.some(option => String(option.value) === String(value))) setSelectedValue(String(value)); }, [value, options, selectedValue]);
  return <label htmlFor={id} className="field"><span id={`${id}-label`}>{label}</span>
    <select {...props} id={id} name={name} value={selectedValue} required={required} disabled={disabled}
      aria-labelledby={props["aria-labelledby"] || `${id}-label`}
      aria-describedby={fieldDescription(id, hint, error, props["aria-describedby"])} aria-invalid={error ? true : props["aria-invalid"]}
      onChange={event => { userChanged.current = true; setSelectedValue(event.target.value); onChange?.(event); }}>
      {options.map(option => <option key={option.value} value={option.value}>{option.label}</option>)}
    </select>
    <FieldFeedback id={id} hint={hint} error={error} />
  </label>;
}
function RoleSelect({ value }) { const [roles, setRoles] = useState([]); const [error, setError] = useState(""); useEffect(() => { api("/api/admin/roles").then(setRoles).catch(e => setError(e.message)); }, []); return <SelectField name="IDUserRole" label="Role" value={value} error={error} hint={!error && !roles.length ? "Loading roles..." : undefined} options={[{ value: "", label: "Select a role" }, ...roles.map(role => ({ value: role.id, label: role.name }))]} />; }
function OwnerSelect({ value }) { const [users, setUsers] = useState([]); const [error, setError] = useState(""); useEffect(() => { api("/api/admin/users").then(setUsers).catch(e => setError(e.message)); }, []); return <SelectField name="OwnerId" label="Project owner" value={value} error={error} hint={!error && !users.length ? "Loading users..." : undefined} options={[{ value: "", label: "Select an owner" }, ...users.filter(user => user.isActive).map(user => ({ value: user.id, label: `${user.displayName} (${user.email})` }))]} />; }
function PrimaryTimelineSelect({ projectId, value }) { const [timelines, setTimelines] = useState([]); const [error, setError] = useState(""); useEffect(() => { api(`/api/projects/${projectId}/timelines`).then(setTimelines).catch(e => setError(e.message)); }, [projectId]); return <SelectField name="PrimaryTimelineId" label="Primary timeline" required={false} value={value || ""} error={error} hint={!error && !timelines.length ? "No timelines available." : undefined} options={[{ value: "", label: "No primary timeline" }, ...timelines.map(timeline => ({ value: timeline.id, label: `${timeline.code}: ${timeline.name}` }))]} />; }
function ParticipantRoleSelect({ name = "ProjectRoleName", value }) { return <SelectField name={name} label="Project role" value={value} options={[{ value: "Contributor", label: "Contributor" }, { value: "Reviewer", label: "Reviewer" }, { value: "Creator", label: "Creator" }]} />; }
function CollaboratorUserSelect({ projectId }) {
  const { data: users, loading, error } = useResource(`/api/projects/${projectId}/collaborator-users`, []);
  return <SelectField name="UserId" label="Collaborator" error={error} hint={loading ? "Loading collaborators..." : !users.length ? "No collaborators available." : undefined} options={[{ value: "", label: "Select a collaborator" }, ...users.map(user => ({ value: user.id, label: `${user.displayName} (${user.email})` }))]} />;
}
function ErrorMessage({ error }) { return error ? <p className="error" role="alert">{error}</p> : null; }
class RouteErrorBoundary extends React.Component {
  constructor(props) { super(props); this.state = { error: null }; }
  static getDerivedStateFromError(error) { return { error }; }
  render() { return this.state.error ? <section className="card" role="alert"><PageHeader eyebrow="Workspace error" title="This page could not be displayed" description="The rest of the application is still available." /><p className="error">{this.state.error.message || "Unexpected client error."}</p><Link href="/projects">Return to projects</Link></section> : this.props.children; }
}
function PageHeader({ eyebrow, title, description, children, headingLevel = 1, titleId }) {
  const Heading = `h${[1, 2, 3, 4, 5, 6].includes(headingLevel) ? headingLevel : 1}`;
  return <header className="page-header"><div>{eyebrow && <p className="eyebrow">{eyebrow}</p>}<Heading id={titleId}>{title}</Heading>{description && <p className="muted">{description}</p>}</div>{children && <div className="page-header-actions">{children}</div>}</header>;
}
function ActionBar({ children, label }) { return <div className="action-bar" role="toolbar" aria-label={label}>{children}</div>; }
function ConfirmButton({ label, message, onConfirm, className = "secondary", disabled = false, ariaLabel }) { return <button className={className} type="button" disabled={disabled} aria-label={ariaLabel} onClick={() => window.confirm(message) && onConfirm()}>{label}</button>; }
function RecordActions({ label, primary, children }) {
  return <div className="record-actions">{primary}<details className="record-more" onKeyDown={event => {
    if (event.key === "Escape" && event.currentTarget.open) {
      event.preventDefault(); event.stopPropagation();
      event.currentTarget.open = false;
      event.currentTarget.querySelector("summary").focus();
    }
  }}><summary aria-label={`More actions for ${label}`}>More</summary><div className="record-secondary-actions">{children}</div></details></div>;
}
function StatusBadge({ children, tone = "neutral" }) { return <span className={`status-badge status-${tone}`}><span aria-hidden="true">{tone === "positive" ? "●" : tone === "negative" ? "○" : "•"}</span> {children}</span>; }
function RoleBadge({ children }) { return <span className="role-badge">{children}</span>; }
function DataTable({ label, columns, rows, empty = "No records are available.", searchPlaceholder = "Search records", filters = [], defaultSort, loading = false, error = "", onRetry, emptyAction }) {
  const [query, setQuery] = useState("");
  const [filterValues, setFilterValues] = useState(() => Object.fromEntries(filters.map(filter => [filter.key, "all"])));
  const [sort, setSort] = useState(defaultSort || { key: columns[0]?.key, direction: "asc" });
  const [pageSize, setPageSize] = useState("10");
  const [page, setPage] = useState(1);
  const searchable = row => !query.trim() || columns.some(column => (column.search !== false) && String(column.searchValue?.(row.value) ?? row.value[column.key] ?? "").toLowerCase().includes(query.trim().toLowerCase()));
  const filtered = rows.filter(row => searchable(row) && filters.every(filter => filterValues[filter.key] === "all" || filter.match(row.value, filterValues[filter.key])));
  const sorted = [...filtered].sort((left, right) => {
    if (!sort?.direction) return String(left.key).localeCompare(String(right.key));
    const column = columns.find(item => item.key === sort.key);
    const a = String(column?.sortValue?.(left.value) ?? left.value[column?.key] ?? "").toLowerCase();
    const b = String(column?.sortValue?.(right.value) ?? right.value[column?.key] ?? "").toLowerCase();
    return (a.localeCompare(b, undefined, { numeric: true }) || String(left.key).localeCompare(String(right.key))) * (sort.direction === "desc" ? -1 : 1);
  });
  const size = Number(pageSize); const pages = Math.max(1, Math.ceil(sorted.length / size)); const visible = sorted.slice((Math.min(page, pages) - 1) * size, Math.min(page, pages) * size);
  useEffect(() => setPage(1), [query, filterValues, pageSize, sort]);
  const cycleSort = key => setSort(current => current.key !== key ? { key, direction: "asc" } : current.direction === "asc" ? { key, direction: "desc" } : current.direction === "desc" ? { key, direction: null } : { key, direction: "asc" });
  const clearFilters = () => { setQuery(""); setFilterValues(Object.fromEntries(filters.map(filter => [filter.key, "all"]))); setPage(1); };
  return <section className="grid-panel" aria-label={label} aria-busy={loading}>
    <ActionBar label={`${label} controls`}>
      <label className="compact-control">Search<input type="search" value={query} onChange={event => setQuery(event.target.value)} placeholder={searchPlaceholder} /></label>
      {filters.map(filter => <label className="compact-control" key={filter.key}>{filter.label}<select value={filterValues[filter.key]} onChange={event => setFilterValues({ ...filterValues, [filter.key]: event.target.value })}>{filter.options.map(option => <option value={option.value} key={option.value}>{option.label}</option>)}</select></label>)}
    </ActionBar>
    <LoadFeedback label={label} loading={loading} error={error} onRetry={onRetry} hasData={rows.length > 0} />
    {sorted.length ? <>
      <div className="data-grid" role="region" aria-label={label} tabIndex="0"><table><caption>{label}</caption>
        <thead><tr>{columns.map(column => <th scope="col" className={column.className} key={column.key} aria-sort={sort?.key === column.key && sort.direction ? `${sort.direction}ending` : "none"}>
          {column.sortable === false ? column.label : <button className="sort-button" type="button" onClick={() => cycleSort(column.key)}>{column.label}<span aria-hidden="true">{sort?.key === column.key ? (sort.direction === "asc" ? " ↑" : sort.direction === "desc" ? " ↓" : " ↕") : " ↕"}</span></button>}
        </th>)}</tr></thead>
        <tbody>{visible.map(row => <tr key={row.key}>{columns.map(column => <td className={column.className} key={column.key}>{column.render(row.value)}</td>)}</tr>)}</tbody>
      </table></div>
      <div className="grid-footer"><span role="status">Showing {visible.length} of {sorted.length} records</span>
        <label className="compact-control">Rows per page<select value={pageSize} onChange={event => setPageSize(event.target.value)}><option value="5">5</option><option value="10">10</option><option value="25">25</option></select></label>
        <div className="pagination" aria-label={`${label} pagination`}><button className="secondary" type="button" disabled={page <= 1} onClick={() => setPage(page - 1)}>Previous</button><span>Page {Math.min(page, pages)} of {pages}</span><button className="secondary" type="button" disabled={page >= pages} onClick={() => setPage(page + 1)}>Next</button></div>
      </div>
    </> : !loading && !error && <div className="empty-state">
      {rows.length ? <><p role="status">No matching records. Adjust your search or filters to see available records.</p><button className="secondary" type="button" onClick={clearFilters}>Clear filters</button></> : <><p role="status">{empty}</p>{emptyAction}</>}
    </div>}
  </section>;
}

function LoadFeedback({ label, loading, error, onRetry, hasData = false }) {
  return <>
    {loading && <p className="request-feedback" role="status">{hasData ? "Refreshing" : "Loading"} {label}...</p>}
    {error && <div className="request-feedback request-error" role="alert"><p>Unable to load {label}. {error}{hasData && " Available records are still shown below."}</p>{onRetry && <button className="secondary" type="button" disabled={loading} onClick={onRetry}>Retry loading</button>}</div>}
  </>;
}
function useResource(path, initial) {
  const seed = useRef(initial).current;
  const [revision, setRevision] = useState(0);
  const [state, setState] = useState({ path, data: seed, loading: true, error: "" });
  useEffect(() => {
    const controller = new AbortController();
    setState(previous => ({ path, data: previous.path === path ? previous.data : seed, loading: true, error: "" }));
    api(path, { signal: controller.signal }).then(data => {
      if (!controller.signal.aborted) setState({ path, data, loading: false, error: "" });
    }).catch(error => {
      if (!controller.signal.aborted) setState(previous => ({ ...previous, loading: false, error: error.message }));
    });
    return () => controller.abort();
  }, [path, revision, seed]);
  return { ...(state.path === path ? state : { data: seed, loading: true, error: "" }), reload: () => setRevision(value => value + 1) };
}
function useRegister(path) {
  const resource = useResource(path, []);
  const locked = useRef(false);
  const [action, setAction] = useState({ pending: false, error: "", message: "" });
  const remove = async (id, message) => {
    if (locked.current) return;
    locked.current = true; setAction({ pending: true, error: "", message: "" });
    try { await api(`${path}/${id}`, { method: "DELETE" }); setAction({ pending: false, error: "", message }); resource.reload(); }
    catch (error) { setAction({ pending: false, error: error.message, message: "" }); }
    finally { locked.current = false; }
  };
  return { ...resource, action, remove };
}
function FormPage({ eyebrow, title, description, children }) {
  return <section className="maintenance-page"><PageHeader eyebrow={eyebrow} title={title} description={description} /><div className="card form-card">{children}</div></section>;
}
function AccountPage({ titleId, eyebrow = "Account access", title, description, children }) {
  return <main className="auth" aria-labelledby={titleId}><section className="card auth-card"><PageHeader titleId={titleId} eyebrow={eyebrow} title={title} description={description} />{children}</section></main>;
}
// Explicit submission lifecycle shared by maintenance forms.
function ManagedForm({ ariaLabel, onSubmit, onSuccess, submitLabel, pendingLabel = "Saving...", successMessage, cancelHref, cancelLabel = "Cancel", children }) {
  const locked = useRef(false); const mounted = useRef(false);
  const [state, setState] = useState({ pending: false, error: "", message: "" });
  useEffect(() => { mounted.current = true; return () => { mounted.current = false; }; }, []);
  const submit = async values => {
    if (locked.current) return;
    locked.current = true; setState({ pending: true, error: "", message: "" });
    try { const result = await onSubmit(values); if (mounted.current) { setState({ pending: false, error: "", message: successMessage || "" }); onSuccess?.(result); } }
    catch (error) { if (mounted.current) setState({ pending: false, error: error.message || "The request could not be completed. Try again.", message: "" }); }
    finally { locked.current = false; }
  };
  return <>
    <Form ariaLabel={ariaLabel} className="managed-form" aria-busy={state.pending} onSubmit={submit}>
      <fieldset className="form-body" disabled={state.pending} aria-label={`${ariaLabel} fields`}>{children}</fieldset>
      <ActionBar label={`${ariaLabel} actions`}><button className="primary" type="submit" disabled={state.pending}>{state.pending ? pendingLabel : submitLabel}</button>{cancelHref && <Link className="secondary" href={cancelHref} aria-disabled={state.pending}>{cancelLabel}</Link>}</ActionBar>
    </Form>
    {state.pending && <p className="request-feedback" role="status">{pendingLabel} Please wait.</p>}
    {state.error && <p className="request-feedback request-error" role="alert">{state.error} Please try again; your entered values are retained.</p>}
    {state.message && <p className="request-feedback request-success" role="status">{state.message}</p>}
  </>;
}
function Login({ onLogin }) {
  const submit = async values => { const result = await api("/api/auth/login", { method: "POST", body: JSON.stringify(values) }); setToken(result.token); onLogin(result.user); navigate("/projects"); };
  return <AccountPage titleId="login-title" eyebrow="Project Timeline" title="Sign in" description="Access your projects, timelines, and review work.">
    <ManagedForm ariaLabel="Sign in" onSubmit={submit} submitLabel="Sign in" pendingLabel="Signing in...">
      <Field name="email" label="Email" type="email" autoComplete="username" /><Field name="password" label="Password" type="password" autoComplete="current-password" />
    </ManagedForm><div className="auth-links"><Link href="/recovery-request">Forgot your password?</Link></div>
  </AccountPage>;
}
function RecoveryRequest() {
  return <AccountPage titleId="recovery-title" title="Request password recovery" description="Request recovery instructions for your account.">
    <ManagedForm ariaLabel="Request password recovery" submitLabel="Request recovery" pendingLabel="Sending request..." cancelHref="/" cancelLabel="Back to sign in"
      successMessage="If the account exists, recovery instructions were delivered through the configured local recovery channel."
      onSubmit={values => api("/api/auth/recovery", { method: "POST", body: JSON.stringify(values) })}>
      <Field name="email" label="Email" type="email" autoComplete="username" />
    </ManagedForm>
  </AccountPage>;
}
function ResetPassword() {
  const token = new URLSearchParams(location.search).get("token") || new URLSearchParams(location.search).get("recoveryToken") || "";
  return <AccountPage titleId="reset-title" title="Reset password" description="Use your recovery token to choose a new password.">
    <ManagedForm ariaLabel="Reset password" submitLabel="Reset password" pendingLabel="Resetting password..." cancelHref="/" cancelLabel="Back to sign in"
      successMessage="Password reset completed. Sign in with the new password."
      onSubmit={values => api("/api/auth/recovery/reset", { method: "POST", body: JSON.stringify(values) })}>
      <Field name="token" label="Recovery token" value={token} /><Field name="newPassword" label="New password (12+ characters)" type="password" autoComplete="new-password" />
    </ManagedForm>
  </AccountPage>;
}
function Profile({ user, onSaved }) {
  return <FormPage eyebrow="Account" title="Profile" description="Maintain your display name and password.">
    <ManagedForm ariaLabel="Profile settings" submitLabel="Save profile" successMessage="Profile saved." onSubmit={async values => {
      const updated = await api("/api/me", { method: "PUT", body: JSON.stringify(values) }); onSaved({ ...user, ...updated });
    }}>
      <fieldset><legend>Account identity</legend><p className="context-field"><span>Email</span><strong>{user.email}</strong><small>Account identifier</small></p>
        <div className="form-grid"><Field name="displayName" label="Display name" value={user.displayName} /><Field name="password" label="New password" type="password" required={false} autoComplete="new-password" hint="Optional" /></div>
      </fieldset>
    </ManagedForm>
  </FormPage>;
}
function UserEdit({ id, onDone }) {
  const resource = useResource(`/api/admin/users/${id}`, null); const record = resource.data;
  return <FormPage eyebrow="Administration" title="Edit user" description="Update access without exposing password hashes.">
    {!record ? <LoadFeedback label="user" {...resource} onRetry={resource.reload} /> : <>
      <ManagedForm ariaLabel={`Edit ${record.email}`} submitLabel="Save user" cancelHref="/users" onSubmit={async values => {
        await api(`/api/admin/users/${id}`, { method: "PUT", body: JSON.stringify({ ...values, isActive: values.isActive === "on", IDUserRole: values.IDUserRole }) }); onDone();
      }}>
        <fieldset><legend>Account identity</legend><p className="context-field"><span>Email</span><strong>{record.email}</strong><small>Immutable account identifier</small></p>
          <div className="form-grid"><Field name="displayName" label="Display name" value={record.displayName} /><RoleSelect value={record.idUserRole} /></div>
        </fieldset>
        <fieldset><legend>Access settings</legend><div className="form-grid"><Field name="password" label="Reset password" type="password" required={false} autoComplete="new-password" hint="Optional" /><label className="check-field"><input name="isActive" type="checkbox" defaultChecked={record.isActive} /> Active account</label></div></fieldset>
      </ManagedForm><p className="muted">Delete deactivates the user; it does not erase the account.</p>
    </>}
  </FormPage>;
}
function UserCreate({ onDone }) {
  return <FormPage eyebrow="Administration" title="Create user" description="Add an account with an initial password.">
    <ManagedForm ariaLabel="Create user" submitLabel="Create user" pendingLabel="Creating user..." cancelHref="/users" onSubmit={async values => {
      await api("/api/admin/users", { method: "POST", body: JSON.stringify({ ...values, isActive: values.isActive === "on", IDUserRole: values.IDUserRole }) }); onDone();
    }}>
      <fieldset><legend>Account identity</legend><div className="form-grid"><Field name="email" label="Email" type="email" autoComplete="username" /><Field name="displayName" label="Display name" /></div></fieldset>
      <fieldset><legend>Access settings</legend><div className="form-grid"><Field name="password" label="Temporary password" type="password" autoComplete="new-password" /><RoleSelect /></div><label className="check-field"><input name="isActive" type="checkbox" defaultChecked /> Active account</label></fieldset>
    </ManagedForm>
  </FormPage>;
}
function Users({ go }) {
  const { data: items, loading, error, reload, action, remove } = useRegister("/api/admin/users");
  const create = <button className="primary" type="button" onClick={() => go("/users/new")}>Create user</button>;
  return <section className="register-page"><PageHeader eyebrow="Administration" title="Users" description="Consult and maintain user access.">{create}</PageHeader>
    <DataTable label="User register" loading={loading} error={error} onRetry={reload} empty="No users are available yet." emptyAction={create} searchPlaceholder="Search by email, name, or role"
      filters={[{ key: "status", label: "Status", options: [{ value: "all", label: "All statuses" }, { value: "active", label: "Active" }, { value: "inactive", label: "Inactive" }], match: (x, value) => value === "active" ? x.isActive : !x.isActive }]}
      defaultSort={{ key: "email", direction: "asc" }} rows={items.map(value => ({ key: value.id, value }))} columns={[
        { key: "email", label: "Email", render: x => <strong className="record-title">{x.email}</strong> },
        { key: "name", label: "Display name", sortValue: x => x.displayName, render: x => x.displayName },
        { key: "status", label: "Status", sortValue: x => x.isActive ? "Active" : "Inactive", render: x => <StatusBadge tone={x.isActive ? "positive" : "negative"}>{x.isActive ? "Active" : "Inactive"}</StatusBadge> },
        { key: "roles", label: "Roles", searchValue: x => x.roleName, sortValue: x => x.roleName, render: x => x.roleName ? <RoleBadge>{x.roleName}</RoleBadge> : <span className="muted">None</span> },
        { key: "actions", label: "Actions", className: "operation-cell", sortable: false, search: false, render: x => <RecordActions label={`user ${x.email}`} primary={<button className="secondary" aria-label={`Edit user ${x.email}`} onClick={() => go(`/users/${x.id}/edit`)}>Edit</button>}><ConfirmButton className="danger" disabled={action.pending} label="Delete" ariaLabel={`Delete user ${x.email}`} message={`Deactivate ${x.email}? The account will be soft-deactivated.`} onConfirm={() => remove(x.id, "User deactivated.")} /></RecordActions> }
      ]} />
    {action.pending && <p className="request-feedback" role="status">Deactivating user...</p>}<ErrorMessage error={action.error} />{action.message && <p className="request-feedback request-success" role="status">{action.message}</p>}
    <p className="register-note muted">Delete is a soft deactivation: the user record and history are retained.</p>
  </section>;
}
function ProjectEdit({ id, onDone, administrator = false }) {
  const resource = useResource(`/api/projects/${id}`, null); const record = resource.data;
  return <FormPage eyebrow="Workspace" title="Edit project" description="Update project details while keeping its identifier stable.">
    {!record ? <LoadFeedback label="project" {...resource} onRetry={resource.reload} /> : <ManagedForm ariaLabel={`Edit ${record.code}`} submitLabel="Save project" cancelHref="/projects" onSubmit={async values => {
      await api(`/api/projects/${id}`, { method: "PUT", body: JSON.stringify({ ...values, OwnerId: administrator ? values.OwnerId : undefined, PrimaryTimelineId: values.PrimaryTimelineId || null }) }); onDone();
    }}>
      <fieldset><legend>Project identity</legend><div className="form-grid"><p className="context-field"><span>Project code</span><strong>{record.code}</strong><small>Immutable project identifier</small></p><Field name="name" label="Project name" value={record.name} />
        {administrator ? <OwnerSelect value={record.ownerId} /> : <p className="context-field"><span>Project owner</span><strong>{record.ownerDisplayName}</strong><small>Only Administrators can reassign ownership</small></p>}<PrimaryTimelineSelect projectId={id} value={record.primaryTimelineId} />
      </div></fieldset>
      <fieldset><legend>Project description</legend><label htmlFor="project-description-edit">Description<textarea id="project-description-edit" name="description" defaultValue={record.description || ""} /></label></fieldset>
    </ManagedForm>}
  </FormPage>;
}
function ProjectCreate({ onDone }) {
  return <FormPage eyebrow="Workspace" title="Create project" description="Register a project and its workspace identity.">
    <ManagedForm ariaLabel="Create project" submitLabel="Create project" pendingLabel="Creating project..." cancelHref="/projects" onSubmit={async values => {
      const created = await api("/api/projects", { method: "POST", body: JSON.stringify(values) }); onDone(created.id);
    }}>
      <fieldset><legend>Project identity</legend><div className="form-grid"><Field name="code" label="Project code" /><Field name="name" label="Project name" /></div></fieldset>
      <fieldset><legend>Project description</legend><label htmlFor="project-description-create">Description<textarea id="project-description-create" name="description" /></label></fieldset>
    </ManagedForm>
  </FormPage>;
}
function Projects({ go }) {
  const { data: items, loading, error, reload, action, remove } = useRegister("/api/projects");
  const create = <button className="primary" type="button" onClick={() => go("/projects/new")}>Create project</button>;
  return <section className="register-page"><PageHeader eyebrow="Workspace" title="Projects" description="Consult projects and open a workspace.">{create}</PageHeader>
    <DataTable label="Project register" loading={loading} error={error} onRetry={reload} empty="No projects are available yet." emptyAction={create} searchPlaceholder="Search by code, name, or description"
      defaultSort={{ key: "code", direction: "asc" }} rows={items.map(value => ({ key: value.id, value }))} columns={[
        { key: "code", label: "Code", render: x => <strong className="record-code">{x.code}</strong> },
        { key: "name", label: "Name", render: x => <strong className="record-title">{x.name}</strong> },
        { key: "owner", label: "Owner", searchValue: x => x.ownerDisplayName, render: x => x.ownerDisplayName },
        { key: "description", label: "Description", searchValue: x => x.description || "", render: x => <div className="record-description">{x.description ? <details><summary aria-label={`${x.description} — Read full description for project ${x.code}`}><span className="truncate" title={x.description || "No description"}>{x.description}</span></summary><p>{x.description}</p></details> : <span className="muted" title="No description">—</span>}</div> },
        { key: "actions", label: "Actions", className: "operation-cell", sortable: false, search: false, render: x => <RecordActions label={`project ${x.code}`} primary={<button className="primary" aria-label={`Open workspace for project ${x.code}`} onClick={() => go(`/projects/${x.id}`)}>Open workspace</button>}><Link className="secondary" aria-label={`Timeline for project ${x.code}`} href={`/projects/${x.id}/timelines`}>Timeline</Link><button className="secondary" aria-label={`Edit project ${x.code}`} onClick={() => go(`/projects/${x.id}/edit`)}>Edit</button><ConfirmButton className="danger" disabled={action.pending} label="Delete" ariaLabel={`Delete project ${x.code}`} message={`Delete project ${x.code}? This cannot be undone.`} onConfirm={() => remove(x.id, "Project deleted.")} /></RecordActions> }
      ]} />
    {action.pending && <p className="request-feedback" role="status">Deleting project...</p>}<ErrorMessage error={action.error} />{action.message && <p className="request-feedback request-success" role="status">{action.message}</p>}
  </section>;
}

function useTimelineContext(projectId, timelineId) {
  const project = useResource(`/api/projects/${projectId}`, null);
  const timeline = useResource(`/api/timelines/${timelineId}`, null);
  const mismatch = project.data && timeline.data && timeline.data.projectId !== project.data.id;
  return { project: project.data, timeline: timeline.data, loading: project.loading || timeline.loading,
    error: project.error || timeline.error || (mismatch ? "The timeline does not belong to this project." : ""),
    reload: () => { project.reload(); timeline.reload(); } };
}
function TimelineEdit({ projectId, id, onDone }) {
  const { project, timeline, loading, error, reload } = useTimelineContext(projectId, id);
  return <FormPage eyebrow={project ? `${project.code}: ${project.name}` : "Timeline maintenance"} title="Edit timeline" description="Update the name without changing the timeline identity.">
    <LoadFeedback label="timeline" loading={loading} error={error} onRetry={reload} />
    {project && timeline && !error && <ManagedForm key={id} ariaLabel={`Edit ${timeline.code}`} submitLabel="Save timeline" cancelHref={`/projects/${projectId}/timelines`} onSuccess={onDone} onSubmit={values => api(`/api/timelines/${id}`, { method: "PUT", body: JSON.stringify(values) })}><fieldset><legend>Timeline identity</legend><p className="context-field"><span>Immutable timeline identifier</span><strong>{timeline.id}</strong><small>{timeline.code}</small></p><Field name="name" label="Timeline name" value={timeline.name} /></fieldset></ManagedForm>}
  </FormPage>;
}
function TimelineCreate({ projectId, onDone }) {
  const { data: project, loading, error, reload } = useResource(`/api/projects/${projectId}`, null);
  return <FormPage eyebrow={project ? `${project.code}: ${project.name}` : "Timeline maintenance"} title="Create timeline" description="This timeline will be owned by the current project.">
    <LoadFeedback label="project" loading={loading} error={error} onRetry={reload} />
    {project && <ManagedForm key={projectId} ariaLabel="Create timeline" submitLabel="Create timeline" pendingLabel="Creating timeline..." cancelHref={`/projects/${projectId}/timelines`} onSuccess={created => onDone(created.id)} onSubmit={values => api(`/api/projects/${projectId}/timelines`, { method: "POST", body: JSON.stringify({ code: values.code, name: values.name }) })}><fieldset><legend>Timeline identity</legend><p className="context-field"><span>Parent project</span><strong>{project.code}: {project.name}</strong></p><div className="form-grid"><Field name="code" label="Timeline code" /><Field name="name" label="Timeline name" /></div></fieldset></ManagedForm>}
  </FormPage>;
}
function Timelines({ projectId, go }) {
  const project = useResource(`/api/projects/${projectId}`, null);
  const { data: items, loading, error, reload } = useResource(`/api/projects/${projectId}/timelines`, []);
  const mutation = useWorkspaceAction();
  return <section className="register-page workspace-page"><PageHeader eyebrow={project.data ? `${project.data.code}: ${project.data.name}` : "Project workspace"} title="Project timelines" description="Consult and maintain timelines inside their parent project."><button className="primary" onClick={() => go(`/projects/${projectId}/timelines/new`)}>Create timeline</button><Link className="secondary" href={`/projects/${projectId}`}>Back to project</Link></PageHeader>
    <LoadFeedback label="project" loading={project.loading} error={project.error} onRetry={project.reload} />
    <DataTable label={`${project.data?.code || "Project"} timelines`} loading={loading} error={error} onRetry={reload} empty="No timelines are available yet." rows={items.filter(value => value.projectId === projectId).map(value => ({ key: value.id, value }))} columns={[
      { key: "code", label: "Code", render: x => <strong>{x.code}</strong> }, { key: "name", label: "Name", render: x => x.name },
      { key: "project", label: "Project", searchValue: x => x.projectName, render: x => x.projectName || project.data?.name },
      { key: "actions", label: "Actions", className: "operation-cell", sortable: false, search: false, render: x => <RecordActions label={`timeline ${x.code}`} primary={<Link className="secondary" href={`/projects/${projectId}/timelines/${x.id}/edit`}>Edit</Link>}><ConfirmButton className="danger" disabled={mutation.pending} label="Delete" message={`Delete timeline ${x.code}? This cannot be undone.`} onConfirm={() => mutation.run(async () => { await api(`/api/timelines/${x.id}`, { method: "DELETE" }); reload(); }, "Timeline deleted.")} /></RecordActions> }
    ]} /><WorkspaceFeedback action={mutation} />
  </section>;
}
function useWorkspaceAction() {
  const locked = useRef(false); const mounted = useRef(false);
  const [state, setState] = useState({ pending: false, error: "", message: "" });
  useEffect(() => { mounted.current = true; return () => { mounted.current = false; }; }, []);
  const run = async (work, message = "Saved.") => {
    if (locked.current) return;
    locked.current = true; setState({ pending: true, error: "", message: "" });
    try { await work(); if (mounted.current) setState({ pending: false, error: "", message }); }
    catch (error) { if (mounted.current) setState({ pending: false, error: error.message, message: "" }); }
    finally { locked.current = false; }
  };
  return { ...state, run };
}
function WorkspaceFeedback({ action, pendingLabel = "Saving changes..." }) {
  return <>{action.pending && <p className="request-feedback" role="status">{pendingLabel}</p>}{action.error && <p className="request-feedback request-error" role="alert">{action.error} Available content is retained; try again explicitly.</p>}{action.message && <p className="request-feedback request-success" role="status">{action.message}</p>}</>;
}

function Participants({ project }) {
  const { data: items, loading, error, reload } = useResource(`/api/projects/${project.id}/participants`, []);
  const mutation = useWorkspaceAction();
  return <section className="workspace-section collaborators register-page"><PageHeader headingLevel={2} title="Collaborators" description="Project roles and access. Changes remain subject to your existing permissions." />
    <DataTable label="Project collaborators" loading={loading} error={error} onRetry={reload} empty="No collaborators are available." rows={items.map(value => ({ key: value.id, value }))} columns={[
      { key: "email", label: "Email", render: x => x.email }, { key: "name", label: "Name", render: x => x.displayName },
      { key: "role", label: "Project role", render: x => <SelectField name={`role-${x.id}`} label={`Role for ${x.displayName}`} disabled={mutation.pending} value={x.projectRoleName} options={[{ value: "Contributor", label: "Contributor" }, { value: "Reviewer", label: "Reviewer" }, { value: "Creator", label: "Creator" }]} onChange={event => { const role = event.target.value; mutation.run(async () => { await api(`/api/projects/${project.id}/participants/${x.id}`, { method: "PUT", body: JSON.stringify({ projectRoleName: role }) }); reload(); }, "Collaborator role updated."); }} /> },
      { key: "actions", label: "Actions", sortable: false, search: false, render: x => <ConfirmButton className="secondary workspace-danger" disabled={mutation.pending} label="Remove" ariaLabel={`Remove collaborator ${x.displayName}`} message={`Remove collaborator ${x.displayName}?`} onConfirm={() => mutation.run(async () => { await api(`/api/projects/${project.id}/participants/${x.id}`, { method: "DELETE" }); reload(); }, "Collaborator removed.")} /> }
    ]} /><WorkspaceFeedback action={mutation} />
    <details className="workspace-disclosure"><summary>Add a collaborator</summary><ManagedForm ariaLabel="Add project collaborator" submitLabel="Add collaborator" successMessage="Collaborator added." onSubmit={async values => {
      await api(`/api/projects/${project.id}/participants`, { method: "POST", body: JSON.stringify({ userId: values.UserId, projectRoleName: values.ProjectRoleName }) }); reload();
    }}><div className="form-grid"><CollaboratorUserSelect projectId={project.id} /><ParticipantRoleSelect /></div></ManagedForm></details>
  </section>;
}
function UploadForm({ entryId, onUploaded }) {
  const fileInput = useRef(null); const [uploaded, setUploaded] = useState("");
  return <div className="artifact-box"><ManagedForm ariaLabel="Artifact upload" submitLabel="Upload artifact" pendingLabel="Uploading artifact..." onSuccess={name => { setUploaded(name); if (fileInput.current) fileInput.current.value = ""; onUploaded(); }} onSubmit={async values => {
    setUploaded(""); const file = values.file; if (!file?.name) throw new Error("Choose a file before uploading.");
    const body = new FormData(); body.append("file", file);
    await api(`/api/entries/${entryId}/artifacts`, { method: "POST", body });
    return file.name;
  }}><label htmlFor={`file-${entryId}`}>Choose file<input ref={fileInput} id={`file-${entryId}`} name="file" type="file" accept=".pdf,.doc,.docx,.xls,.xlsx,.md,.markdown" aria-describedby={`file-help-${entryId}`} required /></label></ManagedForm><p id={`file-help-${entryId}`} className="muted">PDF, Word, Excel or Markdown. A failed upload keeps the selected file; submit again to retry.</p>{uploaded && <p className="request-feedback request-success" role="status">Uploaded {uploaded}.</p>}</div>;
}
function ArtifactLinks({ artifacts = [] }) { return artifacts.length ? <ul aria-label="Entry artifacts">{artifacts.map(item => <li key={item.id}><button className="link-button" type="button" onClick={() => download(`/api/artifacts/${item.id}`, item.fileName)}>{item.fileName}</button></li>)}</ul> : <p className="muted">No artifacts attached.</p>; }
function EntryDetail({ id, projectId, timelineId, user }) {
  const project = useResource(`/api/projects/${projectId}`, null);
  const participants = useResource(`/api/projects/${projectId}/participants`, []);
  const canWrite = hasEntryWriteAccess(user, project.data, participants.data);
  const [facts, setFacts] = useState(null);
  const [reviewActive, setReviewActive] = useState(false);
  const [clarificationActive, setClarificationActive] = useState(false);
  const [factRefreshKey, setFactRefreshKey] = useState(0);
  const source = savedFactSource(facts, id);
  const receipt = history.state?.entrySave;
  const editable = factReviewActions(facts, { canWrite, busy: reviewActive || clarificationActive }).edit && !reviewActive && !clarificationActive;
  const { data: entry, loading, error, reload } = useResource(`/api/entries/${id}?projectId=${projectId}&timelineId=${timelineId}`, null);
  const timeline = useResource(`/api/timelines/${timelineId}`, null);
  const contextError = timeline.data && timeline.data.projectId !== projectId ? "The timeline does not belong to this project." : entry && entry.timelineId !== timelineId ? "The entry does not belong to this timeline." : "";
  const verified = entry && timeline.data && !contextError;
  return <section className="workspace-page entry-page"><PageHeader eyebrow={verified ? `Entry ${entry.entryNumber} · ${timeline.data.code}` : "Timeline conversation"} title={verified && source ? source.title : "Entry detail"}><Link className="secondary" href={`/projects/${projectId}`}>Back to project</Link></PageHeader>
    <LoadFeedback label="entry" loading={loading || timeline.loading} error={error || timeline.error || contextError} onRetry={() => { reload(); timeline.reload(); }} hasData={!!entry} />
    {receipt?.id === id && <p className={`request-feedback ${receipt.generationError ? "request-error" : "request-success"}`} role={receipt.generationError ? "alert" : "status"}>{receipt.generationError || "Entry saved. Review the generation status and pending facts below; nothing was automatically accepted."}</p>}
    {verified && <><ActionBar label="Entry source actions"><Link className="secondary" aria-disabled={!editable} href={`/projects/${projectId}/timelines/${timelineId}/entries/${id}/edit`}>Edit source</Link>{facts?.lifecycleState === "Reconciled" && <Link className="primary" href={`/projects/${projectId}/timelines/${timelineId}/entries/new`}>New correction entry</Link>}</ActionBar><section className="workspace-section" aria-label="Saved entry source">{source ? <><div className="entry-metadata"><StatusBadge>{source.status}</StatusBadge><span>By {entry.author.displayName}</span><span>Entry {entry.entryNumber}</span></div><h2>Entry content</h2><p className="entry-content">{source.content}</p><p className="context-field"><span>Entry identifier</span><strong>{entry.id}</strong></p></> : <p role="status">Loading authoritative saved source...</p>}</section>
      <EntryClarifications key={`clarifications-${id}`} entryId={id} canWrite={canWrite} blocked={reviewActive} refreshKey={`${facts?.generationId ?? ''}:${facts?.lifecycleState ?? ''}`} onResume={() => setFactRefreshKey(value => value + 1)} onActiveChange={setClarificationActive} />
      <FactReview key={id} entry={entry} canWrite={canWrite} onActiveChange={setReviewActive} onStateChange={setFacts} refreshKey={factRefreshKey} />
      <section className="workspace-section"><h2>Artifacts</h2><ArtifactLinks artifacts={entry.artifacts} />{canWrite && <UploadForm key={id} entryId={entry.id} onUploaded={reload} />}</section></>}
  </section>;
}
function EntryCreate(props) { return <EntryComposer key={`${props.projectId}-${props.timelineId}`} {...props} />; }
function EntryComposer({ timelineId, projectId, go, user }) {
  const { project, timeline, loading, error, reload } = useTimelineContext(projectId, timelineId);
  const participants = useResource(`/api/projects/${projectId}/participants`, []);
  const canWrite = hasEntryWriteAccess(user, project, participants.data);
  const [saveAction] = useState(() => createEntrySaveAction(factClient));
  const lifetime = useRef(null);
  const [saved, setSaved] = useState(null);
  useEffect(() => { const controller = new AbortController(); lifetime.current = controller; return () => controller.abort(); }, []);
  return <FormPage eyebrow={project && timeline && !error ? `${timeline.code}: ${timeline.name}` : "Timeline conversation"} title="New conversation entry" description="Save the source and automatically generate pending facts, then review and explicitly reconcile. Optional artifacts can be attached on the saved entry.">
    <LoadFeedback label="timeline" loading={loading} error={error} onRetry={reload} />
    {saved && <p role="status">Entry saved. Generating pending facts; this does not activate them. <Link href={`/projects/${projectId}/timelines/${timelineId}/entries/${saved.id}`}>Open saved entry</Link></p>}
    {project && timeline && !error && !canWrite && <p role="status">Read-only access. Entry creation is unavailable.</p>}
    {project && timeline && !error && canWrite && <ManagedForm key={`${projectId}-${timelineId}`} ariaLabel="Create timeline entry" submitLabel="Create entry" pendingLabel="Saving entry and generating pending facts..." cancelHref={`/projects/${projectId}`} onSuccess={result => {
      if (result) go(`/projects/${projectId}/timelines/${timelineId}/entries/${result.entry.id}`, { state: { entrySave: { id: result.entry.id, generationError: result.generationError } } });
    }} onSubmit={values => saveAction.run(options => api(`/api/timelines/${timelineId}/entries`, { ...options, method: "POST", body: JSON.stringify({ ...values, status: "Draft" }) }), { signal: lifetime.current.signal, onSaved: setSaved })}><fieldset><legend>Entry details</legend><p className="context-field"><span>Timeline</span><strong>{timeline.code}: {timeline.name}</strong></p><Field name="title" label="Title" /><label htmlFor="new-entry-content">Content<textarea id="new-entry-content" name="content" required /></label></fieldset><p className="muted">Saving triggers generation and may incur model billing. Candidates remain pending until explicit reconciliation and acceptance.</p></ManagedForm>}
  </FormPage>;
}
function EntryEdit({ id, projectId, timelineId, go, user }) {
  const [entry, setEntry] = useState(null);
  const [editor, setEditor] = useState({ title: "", content: "", status: "" });
  const [canWrite, setCanWrite] = useState(false);
  const [reviewActive, setReviewActive] = useState(false);
  const [factState, setFactState] = useState(null);
  const [saving, setSaving] = useState(false);
  const [saved, setSaved] = useState(false);
  const [saveAction] = useState(() => createEntrySaveAction(factClient));
  const saveLock = useRef(false); const lifetime = useRef(null);
  useEffect(() => { const controller = new AbortController(); lifetime.current = controller; return () => controller.abort(); }, []);
  const [loadRevision, setLoadRevision] = useState(0);
  const [error, setError] = useState("");
  useEffect(() => {
    const controller = new AbortController();
    const options = { signal: controller.signal };
    Promise.all([api(`/api/entries/${id}?projectId=${projectId}&timelineId=${timelineId}`, options), api(`/api/projects/${projectId}`, options), api(`/api/projects/${projectId}/participants`, options)])
      .then(([value, project, participants]) => {
        if (controller.signal.aborted) return;
        setEntry(value); setEditor({ title: value.title, content: value.content, status: value.status });
        setCanWrite(hasEntryWriteAccess(user, project, participants));
      }).catch(e => { if (!controller.signal.aborted) setError(e.message); });
    return () => controller.abort();
  }, [id, projectId, timelineId, user, loadRevision]);
  const save = async values => {
    if (saveLock.current) return;
    saveLock.current = true; setSaving(true); setError("");
    try {
      const signal = lifetime.current.signal;
      const result = await saveAction.run(async options => {
        await api(`/api/entries/${id}`, { ...options, method: "PUT", body: JSON.stringify(values) });
        return { ...entry, ...values };
      }, { signal, onSaved: value => { setSaved(true); setEntry(value); setFactState(null); } });
      if (!signal.aborted && result) {
        go(`/projects/${projectId}/timelines/${timelineId}/entries/${id}`, { state: { entrySave: { id, generationError: result.generationError } } });
      }
    } finally { saveLock.current = false; if (!lifetime.current.signal.aborted) setSaving(false); }
  };
  if (!entry) return <FormPage eyebrow="Timeline maintenance" title="Edit entry"><LoadFeedback label="entry" loading={!error} error={error} onRetry={() => { setError(""); setLoadRevision(value => value + 1); }} /></FormPage>;
  const dirty = ["title", "content", "status"].some(key => editor[key] !== entry[key]);
  const source = savedFactSource(factState, id);
  const sourceChanged = !!source && !entryMatchesFactSource(entry, source);
  const editable = factReviewActions(factState, { canWrite }).edit && !sourceChanged;
  const reviewDirty = dirty || sourceChanged;
  return <FormPage eyebrow="Timeline maintenance" title={`Edit entry ${entry.entryNumber}`} description={`By ${entry.author.displayName} · ${source?.status || "Checking saved source"}`}>
    {!canWrite && <p className="request-feedback" role="status">Read-only access. Editing and Review are unavailable.</p>}
    {!factState && <p role="status">Checking server source-edit permission...</p>}
    {sourceChanged && <div role="alert"><p>The saved source changed in another session. Your editor draft is retained; reload the saved source before saving or reconciling.</p><button type="button" className="secondary" disabled={saving || reviewActive} onClick={() => {
      const values = { title: source.title, content: source.content, status: source.status };
      setEditor(values); setEntry(previous => ({ ...previous, ...values })); setError("");
    }}>Reload saved source (discard draft)</button></div>}
    {factState?.lifecycleState === "Reconciled" && <p role="status">Accepted reconciliation locks this source. <Link href={`/projects/${projectId}/timelines/${timelineId}/entries/new`}>Create a new correction entry</Link>.</p>}
    {saved && <p role="status">Entry saved. Generating pending facts; nothing has been activated. <Link href={`/projects/${projectId}/timelines/${timelineId}/entries/${id}`}>Open saved entry</Link></p>}
    <Form className="managed-form" aria-busy={saving} ariaLabel="Edit timeline entry" onSubmit={async () => {
      if (!editable || saving || reviewActive) return;
      try { await save(editor); } catch (e) { setError(e.status === 409 ? "The source changed or was locked by accepted reconciliation. Refresh fact state; accepted-source corrections require a new entry." : e.message); }
    }}>
      <fieldset disabled={!editable || saving || reviewActive}><legend>Entry details</legend>
        <label htmlFor="edit-entry-title">Title<input id="edit-entry-title" name="title" value={editor.title} onChange={event => setEditor({ ...editor, title: event.target.value })} required /></label>
        <label htmlFor="edit-entry-content">Content<textarea id="edit-entry-content" name="content" value={editor.content} onChange={event => setEditor({ ...editor, content: event.target.value })} required /></label>
        <label htmlFor="edit-entry-status">Status<input id="edit-entry-status" name="status" value={editor.status} onChange={event => setEditor({ ...editor, status: event.target.value })} required /></label>
      </fieldset>
      <ActionBar label="Edit entry actions"><button className="primary" disabled={!editable || saving || reviewActive}>{saving ? "Saving..." : "Save entry"}</button><Link className="secondary" aria-disabled={saving || reviewActive} href={`/projects/${projectId}`}>Cancel</Link></ActionBar>
    </Form>
    {saving && <p className="request-feedback" role="status">Saving entry...</p>}
    <FactReview key={id} entry={entry} canWrite={canWrite && !saving} dirty={reviewDirty} onActiveChange={setReviewActive} onStateChange={setFactState} />
    {error && <p className="request-feedback request-error" role="alert">{error} Your editor draft is retained.</p>}
  </FormPage>;
}
function SnapshotMaintenance({ timelineId, entries, entriesLoading, entriesError }) {
  const [selected, setSelected] = useState([]); const [summary, setSummary] = useState("");
  const { data: snapshots, loading, error, reload } = useResource(`/api/timelines/${timelineId}/snapshots`, []);
  const action = useWorkspaceAction();
  return <section className="timeline-snapshots"><h4>Snapshots</h4><p className="muted">Select saved entries to capture a timeline boundary.</p>
    <form aria-label="Create timeline snapshot" aria-busy={action.pending} onSubmit={event => {
      event.preventDefault(); if (!selected.length) return;
      action.run(async () => { await api(`/api/timelines/${timelineId}/snapshots`, { method: "POST", body: JSON.stringify({ summaryContent: summary || null, entryIds: selected }) }); setSelected([]); setSummary(""); reload(); }, "Snapshot created.");
    }}><fieldset disabled={action.pending}><legend>Select entries</legend>
      {entriesLoading && <p role="status">Loading snapshot choices...</p>}{entriesError && <p className="muted">Entry choices could not be refreshed. Available choices are retained.</p>}
      {entries.map(entry => <label key={entry.id}><input type="checkbox" checked={selected.includes(entry.id)} onChange={event => setSelected(value => event.target.checked ? [...value, entry.id] : value.filter(id => id !== entry.id))} /> Entry {entry.entryNumber}: {entry.title}</label>)}
      {!entriesLoading && !entriesError && !entries.length && <p className="muted">No saved entries to select.</p>}
      </fieldset><label>Summary (optional)<textarea disabled={action.pending} value={summary} onChange={event => setSummary(event.target.value)} /></label>
      <ActionBar label="Snapshot actions"><button className="primary" disabled={!selected.length || action.pending}>{action.pending ? "Creating snapshot..." : "Create snapshot"}</button></ActionBar>
    </form><WorkspaceFeedback action={action} pendingLabel="Creating snapshot..." /><LoadFeedback label="snapshots" loading={loading} error={error} onRetry={reload} hasData={!!snapshots.length} />
    {snapshots.map(snapshot => <article className="snapshot-record" key={snapshot.id}><h5>Snapshot {snapshot.snapshotNumber}</h5><p className="entry-content">{snapshot.summaryContent}</p></article>)}
    {!loading && !error && !snapshots.length && <p className="muted">No snapshots yet.</p>}
  </section>;
}
function TimelineSearch({ timeline, projectId }) {
  const [query, setQuery] = useState(""); const [answer, setAnswer] = useState(null);
  const action = useWorkspaceAction(); const [fence] = useState(createSearchRequestFence); const hintId = useId();
  useLayoutEffect(() => { setAnswer(null); return () => fence.cancel(); }, [timeline.id, projectId, fence]);
  const visibleAnswer = answer?.timelineId === timeline.id && answer?.projectId === projectId ? answer : null;
  return <section className="timeline-search"><h4>Search accepted facts</h4><form aria-label="Search accepted facts" aria-busy={action.pending} onSubmit={event => {
    event.preventDefault(); if (action.pending) return;
    setAnswer(null);
    const question = query;
    action.run(async () => {
      const controller = fence.begin();
      try {
        const result = await searchTimelineFacts(api, timeline.id, question, controller.signal);
        if (fence.isCurrent(controller)) setAnswer({ question, result, timelineId: timeline.id, projectId });
      } catch (error) {
        if (fence.isCurrent(controller)) { setAnswer(null); throw error; }
      }
    }, "Fact search complete.");
  }}><label>Question<input type="search" maxLength={300} aria-describedby={hintId} value={query} disabled={action.pending} onChange={event => { setQuery(event.target.value); setAnswer(null); }} /></label>
    <p className="muted" id={hintId}>1–300 characters. Searches accepted valid facts, not original entry content. Matching searches may incur model billing.</p>
    <ActionBar label="Fact search actions"><button className="secondary" disabled={action.pending || !query.trim() || query.length > 300}>{action.pending ? "Searching..." : "Search facts"}</button></ActionBar></form>
    <WorkspaceFeedback action={action} pendingLabel="Searching facts..." />
    {visibleAnswer && <><p className="eyebrow">Context · {timeline.code}: {timeline.name}</p><h5>Question</h5><p className="entry-content">{visibleAnswer.question}</p>
      <TimelineSearchResults result={visibleAnswer.result} projectId={projectId} timelineId={timeline.id} Link={Link} /></>}
    {!visibleAnswer && !action.pending && !action.error && <p className="muted">Ask about accepted facts in this timeline. Search runs only when submitted; chronology is unchanged.</p>}
  </section>;
}
function TimelineEntries({ timeline, projectId }) {
  const { data: entries, loading, error, reload } = useResource(`/api/timelines/${timeline.id}/entries`, []);
  const mutation = useWorkspaceAction();
  return <div className="timeline-layout"><section className="timeline-conversation"><PageHeader headingLevel={4} title="Timeline entries"><Link className="primary" href={`/projects/${projectId}/timelines/${timeline.id}/entries/new`}>New entry</Link></PageHeader>
    <LoadFeedback label="entries" loading={loading} error={error} onRetry={reload} hasData={!!entries.length} />
    {!loading && !error && !entries.length && <p className="empty-state" role="status">No entries yet.</p>}
    <div className="conversation" aria-label={`${timeline.name} conversation`}>{entries.map(entry => <article className="conversation-entry" key={entry.id}><div className="entry-metadata"><span>Entry {entry.entryNumber}</span><StatusBadge>{entry.status}</StatusBadge><span>{entry.author.displayName}</span></div><h5>{entry.title}</h5><p className="entry-content">{entry.preview}</p><ArtifactLinks artifacts={entry.artifacts} />
      <RecordActions label={`entry ${entry.entryNumber}`} primary={<Link className="secondary" href={`/projects/${projectId}/timelines/${timeline.id}/entries/${entry.id}?projectId=${projectId}`}>Read full entry</Link>}><Link className="secondary" href={`/projects/${projectId}/timelines/${timeline.id}/entries/${entry.id}`}>Review facts / source access</Link><ConfirmButton className="danger" disabled={mutation.pending} label="Delete" message={`Delete entry ${entry.entryNumber}? This cannot be undone.`} onConfirm={() => mutation.run(async () => { await api(`/api/entries/${entry.id}`, { method: "DELETE" }); reload(); }, "Entry deleted.")} /></RecordActions>
    </article>)}</div><WorkspaceFeedback action={mutation} />
  </section><aside className="timeline-tools" aria-label={`${timeline.code} tools`}><TimelineSearch key={timeline.id} timeline={timeline} projectId={projectId} /><SnapshotMaintenance key={timeline.id} timelineId={timeline.id} entries={entries} entriesLoading={loading} entriesError={error} /></aside></div>;
}
function Workspace({ project, go }) {
  const { data: timelines, loading, error, reload } = useResource(`/api/projects/${project.id}/timelines`, []);
  return <section className="workspace-page"><PageHeader eyebrow="Project workspace" title={`${project.code}: ${project.name}`} description={project.description || "Timeline delivery workspace."}><Link className="secondary" href="/projects">Back to projects</Link><button className="secondary" onClick={() => go(`/projects/${project.id}/edit`)}>Edit project</button></PageHeader>
    <dl className="workspace-context"><div><dt>Project owner</dt><dd>{project.ownerDisplayName}</dd></div><div><dt>Primary timeline</dt><dd>{loading ? "Loading timelines..." : error ? "Timelines unavailable" : timelines.find(item => item.id === project.primaryTimelineId)?.name || "No primary timeline"}</dd></div></dl>
    <section className="workspace-section"><PageHeader headingLevel={2} title="Project timelines" description="Entries, supporting evidence and saved boundaries."><Link className="secondary" href={`/projects/${project.id}/timelines`}>Consult timelines</Link></PageHeader>
      <LoadFeedback label="timelines" loading={loading} error={error} onRetry={reload} hasData={!!timelines.length} />
      {!loading && !error && !timelines.length && <p className="empty-state" role="status">No timelines are available yet. Consult timelines to create one.</p>}
      {timelines.map(timeline => <article className="workspace-timeline" key={timeline.id}><PageHeader headingLevel={3} eyebrow={timeline.id === project.primaryTimelineId ? "Primary timeline" : "Timeline"} title={`${timeline.code}: ${timeline.name}`}><Link className="secondary" href={`/projects/${project.id}/timelines/${timeline.id}/edit`}>Edit timeline</Link></PageHeader><TimelineEntries timeline={timeline} projectId={project.id} /></article>)}
    </section><Participants key={project.id} project={project} />
  </section>;
}
function NotFound() { return <section className="card"><PageHeader eyebrow="Navigation" title="Page not found" description="The requested route does not exist." /><Link href="/projects">Return to projects</Link></section>; }

function Shell({ user, route, onUserSaved, onSignOut }) {
  const [mobile, setMobile] = useState(() => window.matchMedia("(max-width: 700px)").matches);
  const [desktopOpen, setDesktopOpen] = useState(true);
  const [mobileOpen, setMobileOpen] = useState(false);
  const sidebarOpen = mobile ? mobileOpen : desktopOpen;
  const drawerOpen = mobile && mobileOpen;
  const menuButtonRef = useRef(null);
  const navigationRef = useRef(null);
  const closeButtonRef = useRef(null);
  const topbarRef = useRef(null);
  const contentRef = useRef(null);
  const statusRef = useRef(null);
  const closeMobileNavigation = () => setMobileOpen(false);
  const closeSidebar = () => {
    if (mobile) setMobileOpen(false);
    else {
      if (navigationRef.current?.contains(document.activeElement)) menuButtonRef.current?.focus();
      setDesktopOpen(false);
    }
  };
  const go = (target, options) => { navigate(target, options); closeMobileNavigation(); };
  useLayoutEffect(() => {
    // Match the literal CSS breakpoint; crossing it never carries an open modal into desktop.
    const query = window.matchMedia("(max-width: 700px)");
    const update = () => {
      if (navigationRef.current?.contains(document.activeElement)) menuButtonRef.current?.focus();
      setMobileOpen(false);
      setMobile(query.matches);
    };
    query.addEventListener("change", update);
    return () => query.removeEventListener("change", update);
  }, []);
  useLayoutEffect(() => {
    if (!drawerOpen) return undefined;
    const navigation = navigationRef.current;
    const opener = menuButtonRef.current;
    const background = [topbarRef.current, contentRef.current, statusRef.current].map(element => ({ element, inert: element.inert }));
    // Preserve longhands too: setting the shorthand would otherwise erase an existing overflow-y rule.
    const scrollRoots = [document.documentElement, document.body].flatMap(element => ["overflow-x", "overflow-y"].map(property => ({ element, property, value: element.style.getPropertyValue(property), priority: element.style.getPropertyPriority(property) })));
    background.forEach(({ element }) => { element.inert = true; });
    scrollRoots.forEach(({ element, property }) => element.style.setProperty(property, "hidden"));
    closeButtonRef.current?.focus();
    const focusable = () => [...navigation.querySelectorAll('button:not(:disabled), a[href], [tabindex]')].filter(element => element.tabIndex >= 0 && !element.closest('[hidden], [inert]') && element.getClientRects().length);
    const onKeyDown = event => {
      if (event.key === "Escape") {
        event.preventDefault(); event.stopPropagation(); setMobileOpen(false);
      } else if (event.key === "Tab") {
        const items = focusable();
        const first = items[0], last = items[items.length - 1];
        if (!first) { event.preventDefault(); navigation.focus(); }
        else if (!navigation.contains(document.activeElement) || (event.shiftKey ? document.activeElement === first : document.activeElement === last)) {
          event.preventDefault(); (event.shiftKey ? last : first).focus();
        }
      }
    };
    const onFocusIn = event => { if (!navigation.contains(event.target)) closeButtonRef.current?.focus(); };
    document.addEventListener("keydown", onKeyDown, true);
    document.addEventListener("focusin", onFocusIn);
    return () => {
      document.removeEventListener("keydown", onKeyDown, true);
      document.removeEventListener("focusin", onFocusIn);
      background.forEach(({ element, inert }) => { element.inert = inert; });
      scrollRoots.forEach(({ element, property, value, priority }) => {
        if (value) element.style.setProperty(property, value, priority);
        else element.style.removeProperty(property);
      });
      const target = opener?.isConnected && opener.getClientRects().length && !opener.closest('[inert], [hidden]') ? opener : menuButtonRef.current;
      if (target?.isConnected) target.focus({ preventScroll: true });
    };
  }, [drawerOpen]);
  const path = route.path; const projectMatch = path.match(/^\/projects\/([^/]+)$/); const projectTimelines = path.match(/^\/projects\/([^/]+)\/timelines$/); const timelineCreate = path.match(/^\/projects\/([^/]+)\/timelines\/new$/); const timelineEdit = path.match(/^\/projects\/([^/]+)\/timelines\/([^/]+)\/edit$/); const userEdit = path.match(/^\/users\/([^/]+)\/edit$/); const projectEdit = path.match(/^\/projects\/([^/]+)\/edit$/); const administrator = (user.roleCode || user.roleName) === "Admin"; const entryNew = path.match(/^\/projects\/([^/]+)\/timelines\/([^/]+)\/entries\/new$/); const entryEdit = path.match(/^\/projects\/([^/]+)\/timelines\/([^/]+)\/entries\/([^/]+)\/edit$/); const entryDetail = path.match(/^\/projects\/([^/]+)\/timelines\/([^/]+)\/entries\/([^/]+)$/); const [projectState, setProjectState] = useState({ data: null, loading: false, error: null }); useEffect(() => { const projectId = route.params.projectId; if (!projectId) { setProjectState({ data: null, loading: false, error: null }); return undefined; } const controller = new AbortController(); setProjectState({ data: null, loading: true, error: null }); api(`/api/projects/${projectId}`, { signal: controller.signal }).then(data => { if (!controller.signal.aborted) setProjectState({ data, loading: false, error: null }); }).catch(error => { if (error.name !== "AbortError" && !controller.signal.aborted) setProjectState({ data: null, loading: false, error: error.message }); }); return () => controller.abort(); }, [route.path, route.params.projectId]); const selectedProject = projectState.data; const active = target => path === target || (target === "/projects" && path.startsWith("/projects/")); let content; if (path === "/profile") content = <Profile user={user} onSaved={onUserSaved} />; else if (path === "/users") content = (user.roleCode || user.roleName) === "Admin" ? <Users go={go} /> : <NotFound />; else if (path === "/users/new") content = (user.roleCode || user.roleName) === "Admin" ? <UserCreate onDone={() => go("/users")} /> : <NotFound />; else if (userEdit) content = (user.roleCode || user.roleName) === "Admin" ? <UserEdit id={userEdit[1]} onDone={() => go("/users")} /> : <NotFound />; else if (path === "/projects") content = <Projects go={go} />; else if (path === "/projects/new") content = <ProjectCreate onDone={id => go(`/projects/${id}`)} />; else if (projectEdit) content = <ProjectEdit id={projectEdit[1]} administrator={administrator} onDone={() => go(`/projects/${projectEdit[1]}`)} />; else if (entryNew) content = <EntryCreate projectId={entryNew[1]} timelineId={entryNew[2]} go={go} user={user} />; else if (entryEdit) content = <EntryEdit key={path} id={entryEdit[3]} projectId={entryEdit[1]} timelineId={entryEdit[2]} go={go} user={user} />; else if (entryDetail) content = <EntryDetail key={path} user={user} id={entryDetail[3]} projectId={entryDetail[1]} timelineId={entryDetail[2]} go={go} />; else if (projectTimelines) content = <Timelines projectId={projectTimelines[1]} go={go} />; else if (timelineCreate) content = <TimelineCreate projectId={timelineCreate[1]} onDone={() => go(`/projects/${timelineCreate[1]}/timelines`)} />; else if (timelineEdit) content = <TimelineEdit projectId={timelineEdit[1]} id={timelineEdit[2]} onDone={() => go(`/projects/${timelineEdit[1]}/timelines`)} />; else if (projectMatch) content = projectState.loading ? <p role="status">Loading project...</p> : selectedProject ? <Workspace project={selectedProject} go={go} /> : <section className="card"><p role="alert">Unable to load this project.</p><ErrorMessage error={projectState.error} /><Link href="/projects">Return to projects</Link></section>; else content = <NotFound />; return <div className={`app-shell ${sidebarOpen ? "sidebar-open" : "sidebar-closed"} ${drawerOpen ? "drawer-open" : ""}`}>
    <header ref={topbarRef} className="topbar" role="banner">
      <button ref={menuButtonRef} className="hamburger" type="button" aria-label={sidebarOpen ? "Hide navigation" : "Show navigation"} aria-controls="app-navigation" aria-expanded={sidebarOpen} onClick={() => sidebarOpen ? closeSidebar() : mobile ? setMobileOpen(true) : setDesktopOpen(true)}>Menu</button>
      <Link className="brand" href="/projects">Project Timeline</Link><span className="user-context">{user.email}</span>
      <button className="secondary" onClick={() => { clearToken(); onSignOut(); navigate("/"); }}>Sign out</button>
    </header>
    <div className="shell-body">
      <aside id="app-navigation" ref={navigationRef} className="sidebar" hidden={!sidebarOpen} aria-label="Application navigation" aria-hidden={!sidebarOpen} role={drawerOpen ? "dialog" : undefined} aria-modal={drawerOpen ? true : undefined} tabIndex={-1}>
        <button ref={closeButtonRef} className="sidebar-close" type="button" aria-label="Close navigation" onClick={closeSidebar}>Close</button>
        <nav aria-label="Primary">
          <Link className={active("/projects") ? "active" : ""} aria-current={active("/projects") ? "page" : undefined} href="/projects" onNavigate={closeMobileNavigation}>Projects</Link>
          <Link className={active("/profile") ? "active" : ""} aria-current={active("/profile") ? "page" : undefined} href="/profile" onNavigate={closeMobileNavigation}>Profile</Link>
          {administrator && <Link className={active("/users") ? "active" : ""} aria-current={active("/users") ? "page" : undefined} href="/users" onNavigate={closeMobileNavigation}>Users</Link>}
        </nav>
      </aside>
      {drawerOpen && <button className="overlay-dismiss" type="button" tabIndex={-1} aria-label="Dismiss navigation backdrop" onClick={closeSidebar} />}
      <main ref={contentRef} className="content" aria-label="Project Timeline workspace"><RouteErrorBoundary>{content}</RouteErrorBoundary></main>
    </div>
    <footer ref={statusRef} className="status-bar" aria-label="Application status"><span>Project Timeline</span><span>Local workspace</span><span>{user.email}</span></footer>
  </div>;
}

export default function App() { const [user, setUser] = useState(null); const [authResolved, setAuthResolved] = useState(false); const route = useLocationState(); const hasStoredToken = Boolean(localStorage.getItem("projectTimelineToken")); useEffect(() => { if (!hasStoredToken) { setAuthResolved(true); return undefined; } let active = true; api("/api/me").then(value => { if (active) setUser(value); }).catch(() => { clearToken(); }).finally(() => { if (active) setAuthResolved(true); }); return () => { active = false; }; }, [hasStoredToken]); useEffect(() => { if (authResolved && user && location.pathname === "/") navigate("/projects", { replace: true }); }, [authResolved, user]); if (route.kind === "recovery-request") return <RecoveryRequest />; if (route.kind === "reset-password") return <ResetPassword />; if (!authResolved) return <main className="auth" aria-live="polite"><section className="card auth-card"><p role="status">Restoring session...</p></section></main>; return user ? <Shell user={user} route={route} onUserSaved={setUser} onSignOut={() => setUser(null)} /> : <Login onLogin={setUser} />; }
