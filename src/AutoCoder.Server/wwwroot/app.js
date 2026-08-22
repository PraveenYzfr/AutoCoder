async function api(path) {
  const res = await fetch(path, { headers: { Accept: "application/json" } });
  if (res.status === 401) {
    document.body.innerHTML = `<div class="locked"><h1>Dashboard locked</h1>
      <p>This UI is not public. Open with <code>?token=…</code> after Claude sets
      <code>AUTOCODER_UI_TOKEN</code>, or put Cloudflare Access on this hostname.</p>
      <p><a href="/health">/health</a> stays open for probes. Jira still posts to <code>/webhook/jira</code>.</p></div>`;
    throw new Error("unauthorized");
  }
  if (!res.ok) throw new Error(await res.text());
  return res.json();
}

function badge(status) {
  return `<span class="badge ${status || ""}">${status || ""}</span>`;
}

function money(n) {
  return n == null ? "—" : `$${(+n).toFixed(4)}`;
}

function ago(iso) {
  if (!iso) return "";
  const ms = Date.now() - new Date(iso).getTime();
  if (ms < 60000) return "just now";
  if (ms < 3600000) return `${Math.round(ms / 60000)}m ago`;
  if (ms < 86400000) return `${Math.round(ms / 3600000)}h ago`;
  return new Date(iso).toLocaleString();
}

async function listPage() {
  const routing = await api("/api/ui/routing");
  document.getElementById("routing").textContent =
    `Scout/summarize: ${routing.cheap} · Plan: ${routing.costly} · Code: ${routing.coding}`;

  const ticket = document.getElementById("ticket");
  const status = document.getElementById("status");
  const rows = document.getElementById("rows");

  async function draw() {
    const data = await api("/api/runs");
    const q = ticket.value.trim().toLowerCase();
    const st = status.value;
    rows.innerHTML = data
      .filter(r => !q || (r.ticket || r.runId).toLowerCase().includes(q))
      .filter(r => !st || r.status === st)
      .map(r => {
        const pr = r.prUrl
          ? `<a class="pr-link" href="${r.prUrl}" target="_blank" rel="noopener">Open PR</a>`
          : `<span class="sub">${r.status === "failed" ? "No PR — failed" : "PR not opened yet"}</span>`;
        const err = r.error
          ? `<div class="sub" title="${escapeHtml(r.error)}">${escapeHtml(r.error.length > 140 ? r.error.slice(0, 140) + "…" : r.error)}</div>`
          : "";
        const dots = (r.journey || []).map(j =>
          `<span class="dot ${j.state}" title="${j.label}: ${j.state}"></span>`).join("");
        return `<tr>
          <td data-label="Ticket"><a href="/runs/${encodeURIComponent(r.runId)}">${r.ticket || r.runId}</a><div class="sub">${ago(r.startedUtc)}</div></td>
          <td data-label="Now">${badge(r.status)} <div class="sub">${r.nowLabel || r.lastStep || "—"}</div>${err}</td>
          <td data-label="Progress"><div class="mini-journey">${dots}</div></td>
          <td data-label="Pull request">${pr}</td>
        </tr>`;
      }).join("") || `<tr><td colspan="4">No runs yet.</td></tr>`;
  }

  ticket.addEventListener("input", draw);
  status.addEventListener("change", draw);
  await draw();
  setInterval(draw, 4000);
}

async function detailPage() {
  const id = decodeURIComponent(location.pathname.replace(/^\/runs\//, ""));
  async function draw() {
    const r = await api(`/api/runs/${encodeURIComponent(id)}`);
    document.getElementById("title").textContent = r.ticket || r.runId;
    document.getElementById("meta").textContent =
      `${r.pipeline} · ${ago(r.startedUtc)} · ${r.tokens} tok · ${money(r.usd)}`;
    document.getElementById("status").innerHTML = badge(r.status);
    const banner = document.getElementById("prBanner");
    banner.innerHTML = r.prUrl
      ? `<strong>Pull request</strong><br><a href="${r.prUrl}" target="_blank" rel="noopener">${r.prUrl}</a>`
      : `<strong>Pull request</strong><br><span class="sub">${r.status === "running" ? "Not opened yet — run is still in progress." : "No PR on this run."}</span>`;
    document.getElementById("journey").innerHTML = (r.journey || []).map(j =>
      `<span class="pill ${j.state}">${j.label}</span>`).join("");
    document.getElementById("stages").innerHTML = (r.stages || []).map(s =>
      `<div class="stage"><span>${s.name}</span><span>${badge(s.state)}${s.durationMs ? ` ${(s.durationMs / 1000).toFixed(1)}s` : ""}</span></div>`
    ).join("");

    const c = r.coding || {};
    const files = (c.files || []).map(f => `<li><code>${f}</code></li>`).join("");
    document.getElementById("coding").innerHTML = `
      <p>${c.turns || 0} / ${c.maxTurns || "?"} turns · ${c.toolCalls || 0} tool calls ·
      ${c.filesWritten || 0} files written · ${c.finished ? "finish called" : "not finished"}</p>
      <p class="sub">Coding model: ${c.provider || "?"} / ${c.model || "?"}</p>
      <ul>${files || "<li class='sub'>No write_file yet</li>"}</ul>`;

    document.getElementById("models").innerHTML = (r.models || []).map(m =>
      `<p><strong>${m.role}</strong> (${m.tier}) → ${m.provider}/${m.model}
      · ${m.calls} calls · ${m.promptTokens + m.completionTokens} tok · ${money(m.usd)}</p>`
    ).join("") || "<p class='sub'>No llm.call events on this run (older logs).</p>";

    document.getElementById("outcome").innerHTML = `
      ${r.prUrl ? `<p>PR: <a href="${r.prUrl}">${r.prUrl}</a></p>` : "<p>No PR yet.</p>"}
      ${r.failedStep ? `<p>Failed at <strong>${r.failedStep}</strong></p>` : ""}
      ${r.error ? `<pre class="md">${escapeHtml(r.error)}</pre>` : ""}
      ${r.result ? `<pre class="md">${escapeHtml(r.result)}</pre>` : ""}`;
    document.getElementById("brief").textContent = r.ticketBrief || "(none yet)";
    document.getElementById("scout").textContent = r.scout || "(none yet)";
    document.getElementById("plan").textContent = r.plan || "(none yet)";

    const log = await api(`/api/runs/${encodeURIComponent(id)}/log`);
    document.getElementById("log").textContent = log.map(e =>
      `${e.ts || ""}  ${e.event || ""}  ${e.step || e.role || e.tool || ""}`
    ).join("\n");
    if (r.status === "running") setTimeout(draw, 2500);
  }
  await draw();
}

function escapeHtml(s) {
  return String(s).replace(/[&<>]/g, ch => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;" }[ch]));
}
