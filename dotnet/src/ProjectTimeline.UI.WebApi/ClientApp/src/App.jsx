import React, { useEffect, useState } from "react";
// Source for the static UI served from wwwroot. The checked-in bundle is intentionally dependency-free for dotnet run.
export default function App() { const [projects, setProjects] = useState([]); useEffect(() => fetch("/api/projects").then(r => r.json()).then(setProjects), []); return <main><h1>Projects</h1>{projects.map(p => <article key={p.id}>{p.code} — {p.name}</article>)}</main>; }
