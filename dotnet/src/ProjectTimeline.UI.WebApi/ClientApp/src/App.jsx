import React, { useEffect, useState } from "react";
import { api, clearToken, download, setToken } from "./api.js";

const formValues = event => Object.fromEntries(new FormData(event.currentTarget));
const currentPath = () => location.pathname.replace(/\/$/, "") || "/";
function navigate(path) { history.pushState({}, "", path); window.dispatchEvent(new PopStateEvent("popstate")); }
function Form({ children, onSubmit }) { return <form onSubmit={event => { event.preventDefault(); onSubmit(formValues(event)); }}>{children}</form>; }
function Field({ name, label, type = "text", required = true, value }) { return <label>{label}<input name={name} type={type} defaultValue={value || ""} required={required} /></label>; }
function ErrorMessage({ error }) { return error ? <p className="error" role="alert">{error}</p> : null; }

function Login({ onLogin }) {
  const [error, setError] = useState("");
  const submit = async values => { try { const result = await api("/api/auth/login", { method: "POST", body: JSON.stringify(values) }); setToken(result.token); onLogin(result.user); navigate("/projects"); } catch (e) { setError(e.message); } };
  return <main className="auth"><section className="card"><h1>Project Timeline</h1><h2>Sign in</h2><Form onSubmit={submit}><Field name="email" label="Email" type="email" /><Field name="password" label="Password" type="password" /><button>Sign in</button></Form><a href="/recovery-request">Forgot your password?</a><ErrorMessage error={error} /></section></main>;
}

function RecoveryRequest() {
  const [message, setMessage] = useState(""); const [error, setError] = useState("");
  const submit = async values => { try { await api("/api/auth/recovery", { method: "POST", body: JSON.stringify(values) }); setMessage("If the account exists, recovery instructions were delivered through the configured local recovery channel."); setError(""); } catch (e) { setError(e.message); setMessage(""); } };
  return <main className="auth"><section className="card"><h1>Request password recovery</h1><p>Enter your account email to request recovery instructions.</p><Form onSubmit={submit}><Field name="email" label="Email" type="email" /><button>Request recovery</button></Form><p className="success" role="status">{message}</p><ErrorMessage error={error} /><a href="/">Back to sign in</a></section></main>;
}

function ResetPassword() {
  const [message, setMessage] = useState(""); const [error, setError] = useState("");
  const token = new URLSearchParams(location.search).get("token") || new URLSearchParams(location.search).get("recoveryToken") || "";
  const submit = async values => { try { await api("/api/auth/recovery/reset", { method: "POST", body: JSON.stringify(values) }); navigate("/"); setMessage("Password reset completed. Sign in with the new password."); setError(""); } catch (e) { setError(e.message); setMessage(""); } };
  return <main className="auth"><section className="card"><h1>Reset password</h1><Form onSubmit={submit}><Field name="token" label="Recovery token" value={token} /><Field name="newPassword" label="New password (12+ characters)" type="password" /><button>Reset password</button></Form><p className="success" role="status">{message}</p><ErrorMessage error={error} /><a href="/">Back to sign in</a></section></main>;
}

function Profile({ user, onSaved }) {
  const [error, setError] = useState("");
  return <section className="card"><h2>Profile</h2><Form onSubmit={async values => { try { await api("/api/me", { method: "PUT", body: JSON.stringify(values) }); onSaved(); } catch (e) { setError(e.message); } }}><Field name="displayName" label="Display name" value={user.displayName} /><Field name="password" label="New password" type="password" required={false} /><button>Save profile</button></Form><ErrorMessage error={error} /></section>;
}

function AdminUsers() {
  const [users, setUsers] = useState([]); const [error, setError] = useState(""); const load = () => api("/api/admin/users").then(setUsers).catch(e => setError(e.message)); useEffect(load, []);
  const save = async (id, values) => { try { await api(id ? `/api/admin/users/${id}` : "/api/admin/users", { method: id ? "PUT" : "POST", body: JSON.stringify({ ...values, isActive: values.isActive === "on", roles: values.roles ? values.roles.split(",").map(x => x.trim()).filter(Boolean) : [] }) }); load(); } catch (e) { setError(e.message); } };
  return <section className="card"><h2>Users</h2><Form onSubmit={e => save(null, e)}><Field name="email" label="Email" type="email" /><Field name="displayName" label="Display name" /><Field name="password" label="Temporary password" type="password" /><Field name="roles" label="Roles (comma-separated)" required={false} /><label><input name="isActive" type="checkbox" defaultChecked /> Active</label><button>Create user</button></Form><ul>{users.map(user => <li key={user.id}><Form onSubmit={e => save(user.id, { ...user, ...e, roles: e.roles || user.roles.join(",") })}><strong>{user.email}</strong><Field name="displayName" label="Display name" value={user.displayName} /><Field name="password" label="Reset password" type="password" required={false} /><Field name="roles" label="Roles" value={user.roles.join(",")} /><label><input name="isActive" type="checkbox" defaultChecked={user.isActive} /> Active</label><button>Update</button></Form></li>)}</ul><ErrorMessage error={error} /></section>;
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
function Artifact({ entry }) { const [items, setItems] = useState([]); const [error, setError] = useState(""); const load = () => api(`/api/entries/${entry.id}/artifacts`).then(setItems); useEffect(load, [entry.id]); return <div><h4>Artifact upload</h4><form onSubmit={async e => { e.preventDefault(); const file = e.currentTarget.file.files[0]; if (file) { try { const body = new FormData(); body.append("file", file); await api(`/api/entries/${entry.id}/artifacts`, { method: "POST", body }); load(); } catch (err) { setError(err.message); } } }}><input name="file" type="file" required /><button>Upload</button></form><ul>{items.map(item => <li key={item.id}><button type="button" onClick={() => download(`/api/artifacts/${item.id}`, item.fileName)}>{item.fileName}</button></li>)}</ul><ErrorMessage error={error} /></div>; }

function Project({ project }) {
  const [timelines, setTimelines] = useState([]); const [error, setError] = useState(""); const load = () => api(`/api/projects/${project.id}/timelines`).then(setTimelines).catch(e => setError(e.message)); useEffect(load, [project.id]);
  const create = async values => { try { await api(`/api/projects/${project.id}/timelines`, { method: "POST", body: JSON.stringify(values) }); load(); } catch (e) { setError(e.message); } };
  return <section className="card"><a href="/projects">Back to projects</a><h2>{project.code}: {project.name}</h2><p>{project.description}</p><Participants project={project} /><h3>Timelines</h3><Form onSubmit={create}><Field name="code" label="Timeline code" /><Field name="name" label="Timeline name" /><button>Create timeline</button></Form>{timelines.map(timeline => <Timeline key={timeline.id} timeline={timeline} refresh={load} />)}<ErrorMessage error={error} /></section>;
}

function Projects({ onOpen }) {
  const [projects, setProjects] = useState([]); const [error, setError] = useState(""); const load = () => api("/api/projects").then(setProjects).catch(e => setError(e.message)); useEffect(load, []);
  const create = async values => { try { await api("/api/projects", { method: "POST", body: JSON.stringify(values) }); load(); } catch (e) { setError(e.message); } };
  return <section className="card"><h2>Projects</h2><Form onSubmit={create}><Field name="code" label="Project code" /><Field name="name" label="Project name" /><label>Description<textarea name="description" /></label><button>Create project</button></Form><ul>{projects.map(project => <li key={project.id}><button onClick={() => onOpen(project)}>{project.code}: {project.name}</button></li>)}</ul><ErrorMessage error={error} /></section>;
}

function Shell({ user, onSignOut }) {
  const [sidebarOpen, setSidebarOpen] = useState(true); const [path, setPath] = useState(currentPath());
  useEffect(() => { const update = () => setPath(currentPath()); addEventListener("popstate", update); return () => removeEventListener("popstate", update); }, []);
  const go = target => { navigate(target); setSidebarOpen(false); };
  const projectMatch = path.match(/^\/projects\/([^/]+)/);
  const [selectedProject, setSelectedProject] = useState(null);
  useEffect(() => { if (projectMatch) api("/api/projects").then(items => setSelectedProject(items.find(item => item.id === projectMatch[1]) || null)).catch(() => setSelectedProject(null)); }, [path]);
  let content = path === "/profile" ? <Profile user={user} onSaved={() => location.reload()} /> : path === "/users" && user.roles.includes("Administrator") ? <AdminUsers /> : projectMatch && selectedProject ? <Project project={selectedProject} /> : <Projects onOpen={project => go(`/projects/${project.id}`)} />;
  return <div className={`app-shell ${sidebarOpen ? "sidebar-open" : "sidebar-closed"}`}><header className="topbar"><button className="hamburger" type="button" aria-label={sidebarOpen ? "Hide navigation" : "Show navigation"} aria-controls="app-navigation" aria-expanded={sidebarOpen} onClick={() => setSidebarOpen(open => !open)}><span aria-hidden="true">☰</span></button><strong>Project Timeline</strong><span>{user.email}</span><button onClick={() => { clearToken(); onSignOut(); navigate("/"); }}>Sign out</button></header><div className="shell-body"><aside id="app-navigation" className="sidebar" aria-label="Application navigation" aria-hidden={!sidebarOpen}><nav><a className={path.startsWith("/projects") ? "active" : ""} href="/projects" onClick={event => { event.preventDefault(); go("/projects"); }}>Projects</a><a className={path === "/profile" ? "active" : ""} href="/profile" onClick={event => { event.preventDefault(); go("/profile"); }}>Profile</a>{user.roles.includes("Administrator") && <a className={path === "/users" ? "active" : ""} href="/users" onClick={event => { event.preventDefault(); go("/users"); }}>Users</a>}</nav></aside><main className="content">{content}</main></div></div>;
}

export default function App() { const [user, setUser] = useState(null); const path = currentPath(); useEffect(() => { if (localStorage.getItem("projectTimelineToken")) api("/api/me").then(setUser).catch(clearToken); }, []); if (path === "/recovery-request") return <RecoveryRequest />; if (path === "/reset-password") return <ResetPassword />; return user ? <Shell user={user} onSignOut={() => setUser(null)} /> : <Login onLogin={setUser} />; }
