import React, { useEffect, useState } from "react";
import { api, clearToken, setToken } from "./api.js";

const formValues = event => Object.fromEntries(new FormData(event.currentTarget));
function Form({ children, onSubmit }) { return <form onSubmit={event => { event.preventDefault(); onSubmit(formValues(event)); }}>{children}</form>; }
function Field({ name, label, type = "text", required = true, value }) { return <label>{label}<input name={name} type={type} defaultValue={value || ""} required={required} /></label>; }
function ErrorMessage({ error }) { return error ? <p className="error" role="alert">{error}</p> : null; }

function Login({ onLogin }) {
  const [error, setError] = useState("");
  const recoveryToken = new URLSearchParams(location.search).get("recoveryToken") || "";
  const submit = async values => { try { const result = await api("/api/auth/login", { method: "POST", body: JSON.stringify(values) }); setToken(result.token); onLogin(result.user); } catch (e) { setError(e.message); } };
  const recover = async values => { try { await api("/api/auth/recovery", { method: "POST", body: JSON.stringify(values) }); setError("Recovery instructions were delivered through the configured local recovery channel."); } catch (e) { setError(e.message); } };
  const reset = async values => { try { await api("/api/auth/recovery/reset", { method: "POST", body: JSON.stringify(values) }); history.replaceState({}, "", location.pathname); setError("Password reset completed. Sign in with the new password."); } catch (e) { setError(e.message); } };
  return <main className="auth"><section className="card"><h1>Project Timeline</h1><h2>Sign in</h2><Form onSubmit={submit}><Field name="email" label="Email" type="email" /><Field name="password" label="Password" type="password" /><button>Sign in</button></Form><h2>Request password recovery</h2><Form onSubmit={recover}><Field name="email" label="Email" type="email" /><button>Request recovery</button></Form><h2>Reset password</h2><Form onSubmit={reset}><Field name="token" label="Recovery token" value={recoveryToken} /><Field name="newPassword" label="New password (12+ characters)" type="password" /><button>Reset password</button></Form><ErrorMessage error={error} /></section></main>;
}

function Profile({ user, onSaved }) {
  const [error, setError] = useState("");
  return <section className="card"><h2>Profile</h2><Form onSubmit={async values => { try { await api("/api/me", { method: "PUT", body: JSON.stringify(values) }); onSaved(); } catch (e) { setError(e.message); } }}><Field name="displayName" label="Display name" value={user.displayName} /><Field name="password" label="New password" type="password" required={false} /><button>Save profile</button></Form><ErrorMessage error={error} /></section>;
}

function AdminUsers() {
  const [users, setUsers] = useState([]); const [error, setError] = useState("");
  const load = () => api("/api/admin/users").then(setUsers).catch(e => setError(e.message)); useEffect(load, []);
  const save = async (id, values) => { try { await api(id ? `/api/admin/users/${id}` : "/api/admin/users", { method: id ? "PUT" : "POST", body: JSON.stringify({ ...values, isActive: values.isActive === "on", roles: values.roles ? values.roles.split(",").map(x => x.trim()).filter(Boolean) : [] }) }); load(); } catch (e) { setError(e.message); } };
  return <section className="card"><h2>Administrator: users</h2><Form onSubmit={e => save(null, e)}><Field name="email" label="Email" type="email" /><Field name="displayName" label="Display name" /><Field name="password" label="Temporary password" type="password" /><Field name="roles" label="Roles (comma-separated)" required={false} /><label><input name="isActive" type="checkbox" defaultChecked /> Active</label><button>Create user</button></Form><ul>{users.map(user => <li key={user.id}><Form onSubmit={e => save(user.id, { ...user, ...e, roles: e.roles || user.roles.join(",") })}><strong>{user.email}</strong><Field name="displayName" label="Display name" value={user.displayName} /><Field name="password" label="Reset password" type="password" required={false} /><Field name="roles" label="Roles" value={user.roles.join(",")} /><label><input name="isActive" type="checkbox" defaultChecked={user.isActive} /> Active</label><button>Update</button></Form></li>)}</ul><ErrorMessage error={error} /></section>;
}

function Participants({ project }) {
  const [items, setItems] = useState([]); const [error, setError] = useState(""); const load = () => api(`/api/projects/${project.id}/participants`).then(setItems).catch(e => setError(e.message)); useEffect(load, [project.id]);
  const change = async (id, values, method = "PUT") => { try { await api(`/api/projects/${project.id}/participants${id ? `/${id}` : ""}`, { method, body: JSON.stringify(values) }); load(); } catch (e) { setError(e.message); } };
  return <section><h3>Participants</h3><Form onSubmit={v => change(null, v, "POST")}><Field name="email" label="User email" type="email" /><Field name="projectRoleName" label="Role (Creator, Reviewer, Contributor)" /><button>Add participant</button></Form><ul>{items.map(item => <li key={item.id}><span>{item.email}</span><Form onSubmit={v => change(item.id, v)}><Field name="projectRoleName" label="Role" value={item.projectRoleName} /><button>Update</button><button type="button" onClick={() => change(item.id, {}, "DELETE")}>Remove</button></Form></li>)}</ul><ErrorMessage error={error} /></section>;
}

function Timeline({ timeline, refresh }) {
  const [entries, setEntries] = useState([]); const [error, setError] = useState(""); const load = () => api(`/api/timelines/${timeline.id}/entries`).then(setEntries).catch(e => setError(e.message)); useEffect(load, [timeline.id]);
  const saveEntry = async (id, values) => { try { await api(id ? `/api/entries/${id}` : `/api/timelines/${timeline.id}/entries`, { method: id ? "PUT" : "POST", body: JSON.stringify({ ...values, status: values.status || "Draft" }) }); load(); refresh(); } catch (e) { setError(e.message); } };
  return <article className="timeline"><h3>{timeline.code}: {timeline.name}</h3><Form onSubmit={v => saveEntry(null, v)}><Field name="title" label="Entry title" /><label>Content<textarea name="content" required /></label><button>Add entry</button></Form>{entries.map(entry => <div className="entry" key={entry.id}><Form onSubmit={v => saveEntry(entry.id, v)}><Field name="title" label="Title" value={entry.title} /><label>Content<textarea name="content" defaultValue={entry.content} required /></label><Field name="status" label="Status" value={entry.status} /><button>Update entry</button></Form><Artifact entry={entry} /></div>)}<ErrorMessage error={error} /></article>;
}
function Artifact({ entry }) { const [items, setItems] = useState([]); const load = () => api(`/api/entries/${entry.id}/artifacts`).then(setItems); useEffect(load, [entry.id]); return <div><h4>Artifact upload</h4><form onSubmit={async e => { e.preventDefault(); const file = e.currentTarget.file.files[0]; if (file) { const body = new FormData(); body.append("file", file); await api(`/api/entries/${entry.id}/artifacts`, { method: "POST", body }); load(); } }}><input name="file" type="file" required /><button>Upload</button></form><ul>{items.map(item => <li key={item.id}><a href={`/api/artifacts/${item.id}`} download>{item.fileName}</a></li>)}</ul></div>; }

function Project({ project }) {
  const [timelines, setTimelines] = useState([]); const [error, setError] = useState(""); const load = () => api(`/api/projects/${project.id}/timelines`).then(setTimelines).catch(e => setError(e.message)); useEffect(load, [project.id]);
  const create = async values => { try { await api(`/api/projects/${project.id}/timelines`, { method: "POST", body: JSON.stringify(values) }); load(); } catch (e) { setError(e.message); } };
  return <section className="card"><h2>{project.code}: {project.name}</h2><p>{project.description}</p><Participants project={project} /><h3>Timelines</h3><Form onSubmit={create}><Field name="code" label="Timeline code" /><Field name="name" label="Timeline name" /><button>Create timeline</button></Form>{timelines.map(timeline => <Timeline key={timeline.id} timeline={timeline} refresh={load} />)}<ErrorMessage error={error} /></section>;
}

function Workspace({ user, onSignOut }) {
  const [projects, setProjects] = useState([]); const [selected, setSelected] = useState(null); const [error, setError] = useState(""); const load = () => api("/api/projects").then(setProjects).catch(e => setError(e.message)); useEffect(load, []);
  const create = async values => { try { await api("/api/projects", { method: "POST", body: JSON.stringify(values) }); load(); } catch (e) { setError(e.message); } };
  return <><header><strong>Project Timeline</strong><span>{user.email}</span><button onClick={() => { clearToken(); onSignOut(); }}>Sign out</button></header><main><Profile user={user} onSaved={() => location.reload()} />{user.roles.includes("Administrator") && <AdminUsers />}<section className="card"><h2>Projects</h2><Form onSubmit={create}><Field name="code" label="Project code" /><Field name="name" label="Project name" /><label>Description<textarea name="description" /></label><button>Create project</button></Form><ul>{projects.map(project => <li key={project.id}><button onClick={() => setSelected(project)}>{project.code}: {project.name}</button></li>)}</ul><ErrorMessage error={error} /></section>{selected && <Project project={selected} />}</main></>;
}

export default function App() { const [user, setUser] = useState(null); useEffect(() => { if (localStorage.getItem("projectTimelineToken")) api("/api/me").then(setUser).catch(clearToken); }, []); return user ? <Workspace user={user} onSignOut={() => setUser(null)} /> : <Login onLogin={setUser} />; }
