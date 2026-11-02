"use strict";
(() => {
    const form = document.getElementById("label-form");
    if (!form) return;
    const category = document.getElementById("category");
    const jump = document.getElementById("jump");
    const previous = document.getElementById("previous");
    const next = document.getElementById("next");
    const good = document.getElementById("good");
    const bad = document.getElementById("bad");
    const status = document.getElementById("save-status");
    const record = Number(form.dataset.record);
    const total = Number(form.dataset.total);
    let revision = Number(form.dataset.revision);
    let label = form.dataset.label || "";
    let savedCategory = form.dataset.category || "";
    let busy = false;
    let uncertain = false;

    function controls() {
        const blocked = busy || uncertain;
        good.disabled = bad.disabled = jump.disabled = blocked;
        previous.disabled = blocked || record === 0;
        next.disabled = blocked || record === total - 1;
        category.disabled = blocked || label !== "Bad";
        good.setAttribute("aria-pressed", String(label === "Good"));
        bad.setAttribute("aria-pressed", String(label === "Bad"));
        category.value = savedCategory || "uncategorized";
    }
    function navigate(target) {
        if (!busy && !uncertain) window.location.assign("/?record=" + target);
    }
    previous.addEventListener("click", () => navigate(previous.dataset.target));
    next.addEventListener("click", () => navigate(next.dataset.target));
    jump.addEventListener("change", () => navigate(jump.value));
    document.getElementById("reload").addEventListener("click", event => { if (busy) event.preventDefault(); });
    form.addEventListener("submit", event => event.preventDefault());
    window.addEventListener("beforeunload", event => {
        if (busy) { event.preventDefault(); event.returnValue = ""; }
    });

    async function save(newLabel, newCategory) {
        if (busy || uncertain) return;
        busy = true;
        controls();
        status.textContent = "Saving… Do not close this tab.";
        status.dataset.state = "saving";
        const body = new URLSearchParams({
            record: String(record), label: newLabel, category: newCategory,
            expectedRevision: String(revision),
            __RequestVerificationToken: form.querySelector('[name="__RequestVerificationToken"]').value
        });
        try {
            const response = await fetch("/?handler=Label", {
                method: "POST", credentials: "same-origin", mode: "same-origin",
                headers: { "Content-Type": "application/x-www-form-urlencoded" }, body
            });
            const result = await response.json();
            if (!response.ok) {
                uncertain = response.status !== 503;
                status.textContent = result.message || "Save failed. Reload saved state before retrying.";
                status.dataset.state = "failed";
                return;
            }
            if (result.record !== record || !Number.isSafeInteger(result.revision) || result.revision <= revision)
                throw new Error("Unconfirmed save");
            revision = result.revision;
            label = result.label;
            savedCategory = result.failureCategory;
            form.dataset.revision = String(revision);
            form.dataset.label = label;
            form.dataset.category = savedCategory;
            document.getElementById("current-label").textContent = label + (label === "Bad" ? " — " + savedCategory : "");
            document.getElementById("progress").textContent = `${result.labeled} / ${result.total} labeled · ${result.pending} Bad needs category`;
            jump.options[record].textContent = `${record + 1} — ${label}${savedCategory === "uncategorized" ? " — needs category" : ""}`;
            status.textContent = savedCategory === "uncategorized" ? "Saved. Bad still needs a category." : "Saved.";
            status.dataset.state = "saved";
        } catch {
            uncertain = true;
            status.textContent = "Save confirmation unavailable; it may have reached disk. Reload saved state before retrying.";
            status.dataset.state = "uncertain";
        } finally {
            busy = false;
            controls();
            if (!uncertain && label === "Bad") category.focus();
        }
    }
    good.addEventListener("click", () => save("Good", ""));
    bad.addEventListener("click", () => save("Bad", "uncategorized"));
    category.addEventListener("change", () => save("Bad", category.value));
    controls();
})();
