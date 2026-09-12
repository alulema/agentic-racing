/* DOM overlay: the lap/standings HUD, the team-radio feed, and the Fase 6.1
 * decision log (click any radio line — or the "log" link — to see exactly
 * what the strategist saw and, for a live LLM call, ask it to re-explain).
 *
 * Both are driven by messages Unity dispatches on window as
 *   new CustomEvent('unity:message', { detail: <json string> })
 * (see Assets/Scripts/Interop/WebGLBridge.jslib). This file only renders; the
 * router in app.js decides what to call.
 *
 * Message types (Unity -> DOM):
 *   race:start    { seed, laps, cars:[{id,name,color,pilot}] }
 *   race:tick     { lap, laps, meId, classification:[{pos,id,name,gap,lastLap,directive,done}] }
 *   radio:msg     { id, carId, name, color, event, lap, engine:"llm"|"heuristic",
 *                   directive, aggression, risk, targetRival, focusCorners:[int],
 *                   radio, rationale, status:"ok"|"fallback", reason, latencyMs,
 *                   request:{context,telemetry} | null }
 *   radio:outcome { carId, name, color, success, note }   -- §6.2: a tactical
 *                   bet (attack/push/defend) resolved, win or loss, shown the
 *                   same way either way.
 *   race:end      { classification:[...] }   // sent when the leader takes the flag
 *
 * `radio:msg.request` is the exact context+telemetry body the strategist was
 * given for that call (§6.1) — kept client-side only (the server is
 * stateless, §2.2) in `traceStore` below, bounded to the last TRACE_MAX
 * decisions so a long session doesn't grow this without limit.
 */

const RADIO_MAX = 6; // keep the live ticker short; it's a feed, not a log
const TRACE_MAX = 300; // the decision log can hold much more than the ticker shows

export function initOverlay() {
  const hud = document.getElementById("hud");
  const lapCard = document.getElementById("lap-card");
  const standings = document.getElementById("standings-body");
  const radio = document.getElementById("radio-feed");
  const radioAge = document.getElementById("radio-age");
  const raceOver = document.getElementById("race-over");
  const waiting = document.getElementById("waiting");
  const carColors = new Map();
  const carNames = new Map();
  let finished = false;

  // Fase 6.1: every decision the strategist made (or didn't — a kept
  // directive is logged too), newest first, keyed by the id in radio:msg.
  const traceStore = new Map();
  let traceOrder = []; // ids, newest first — Map doesn't give us unshift order cheaply

  const traceOpenBtn = document.getElementById("trace-open");
  const traceModal = document.getElementById("trace-modal");
  const traceBackdrop = document.getElementById("trace-backdrop");
  const traceClose = document.getElementById("trace-close");
  const traceList = document.getElementById("trace-list");
  const traceDetail = document.getElementById("trace-detail");
  const traceBack = document.getElementById("trace-back");
  const traceDetailBody = document.getElementById("trace-detail-body");

  // The strategist names rivals in free text as "car_02", but also "Car 2",
  // "car 04", "rival 1"… — map any of those to the pilot name the viewer sees
  // on the car and in the standings. Unknown numbers are left untouched.
  const humanize = (s) =>
    String(s == null ? "" : s).replace(
      /\b(?:car|rival)[\s_-]?0*(\d{1,2})\b/gi,
      (m, n) => carNames.get("car_" + n.padStart(2, "0")) || m
    );

  // The feed never auto-clears (it's the record of what the strategist decided),
  // but a line from 30 s ago must not look like one from 1 s ago — tick the age
  // labels once a second so silence and repetition are both legible.
  setInterval(refreshAges, 1000);

  function refreshAges() {
    let newest = 0;
    for (const el of radio.children) {
      const t = Number(el.dataset.t) || 0;
      newest = Math.max(newest, t);
      const ageEl = el.querySelector(".age");
      if (ageEl) ageEl.textContent = ago(t);
    }
    if (radioAge) {
      radioAge.textContent =
        finished ? "carrera terminada" : newest ? `última señal: ${ago(newest)}` : "";
    }
  }

  function onStart(m) {
    waiting.classList.add("hidden");
    hud.style.visibility = "visible";
    finished = false;
    if (raceOver) raceOver.classList.remove("show");
    carNames.clear();
    (m.cars || []).forEach((c) => {
      carColors.set(c.id, c.color || null);
      if (c.name) carNames.set(String(c.id).toLowerCase(), c.name);
    });
    lapCard.textContent = `LAP 1 / ${m.laps ?? "?"}`;
    standings.innerHTML = "";
    radio.innerHTML = "";
    traceStore.clear();
    traceOrder = [];
    closeTrace();
    refreshAges();
  }

  function onTick(m) {
    if (!finished && m.lap != null) lapCard.textContent = `LAP ${m.lap} / ${m.laps ?? "?"}`;
    const rows = (m.classification || [])
      .map((r) => {
        const cls = [];
        if (r.id === m.meId) cls.push("me");
        if (r.done) cls.push("done");
        const rowCls = cls.length ? ` class="${cls.join(" ")}"` : "";
        const gap = r.pos === 1 ? "leader" : r.gap != null ? `+${r.gap.toFixed(1)}` : "";
        const last = r.lastLap ? r.lastLap.toFixed(1) + "s" : "—";
        const c = carColors.get(r.id);
        const carStyle = c ? ` style="--car:${esc(c)}"` : "";
        return (
          `<tr${rowCls}><td class="pos">${r.done ? "✓" : r.pos}</td>` +
          `<td class="car"${carStyle}>${swatch(c)}${esc(r.name || r.id)}</td>` +
          `<td class="gap">${gap}</td>` +
          `<td class="gap">${last}</td>` +
          `<td class="dir">${esc(r.directive || "")}</td></tr>`
        );
      })
      .join("");
    standings.innerHTML = rows;
  }

  function onRadio(m) {
    const color = m.color || carColors.get(m.carId) || "var(--color-accent)";
    const status = m.status === "ok" ? "ok" : "fallback";
    const body = m.radio || "(no radio)";

    if (m.id) storeTrace(m, status, body);

    // Collapse a run of identical lines from the same car (the last lap fires
    // several events and the strategist re-sends the same "staying on plan" line)
    // into one row with a ×N badge and a refreshed timestamp.
    const top = radio.firstElementChild;
    if (
      top &&
      top.dataset.car === m.carId &&
      top.dataset.body === body &&
      top.dataset.status === status
    ) {
      const n = (Number(top.dataset.count) || 1) + 1;
      top.dataset.count = String(n);
      top.dataset.t = String(Date.now());
      if (m.id) top.dataset.trace = m.id; // point at the freshest decision
      top.querySelector(".count").textContent = `×${n}`;
      refreshAges();
      return;
    }

    const tagText = status === "ok" ? "LLM" : `fallback${m.reason ? " · " + m.reason : ""}`;
    const bits = [];
    if (m.directive) bits.push(m.directive.toUpperCase());
    if (m.aggression) bits.push("agg:" + m.aggression);
    if (m.risk) bits.push("risk:" + m.risk);
    if (m.targetRival) bits.push("→ " + humanize(m.targetRival));
    if (m.focusCorners && m.focusCorners.length) bits.push("T" + m.focusCorners.join(",T"));
    if (m.latencyMs) bits.push(m.latencyMs + "ms");

    const el = document.createElement("div");
    el.className = "radio-msg" + (m.id ? " clickable" : "");
    el.style.setProperty("--car-color", color);
    el.dataset.car = m.carId || "";
    el.dataset.body = body;
    el.dataset.status = status;
    el.dataset.count = "1";
    el.dataset.t = String(Date.now());
    if (m.id) el.dataset.trace = m.id;
    el.innerHTML =
      `<div class="head"><span class="who">${swatch(color)}${esc(m.name || m.carId)}` +
      `<span class="count"></span></span>` +
      `<span class="head-r"><span class="tag ${status}">${esc(tagText)}</span>` +
      `<span class="age"></span></span></div>` +
      `<div class="body">${esc(humanize(body))}</div>` +
      (bits.length ? `<div class="meta">${esc(bits.join("  ·  "))}</div>` : "");

    if (m.id) el.addEventListener("click", () => openDetail(m.id));

    radio.prepend(el);
    while (radio.children.length > RADIO_MAX) radio.lastChild.remove();
    refreshAges();
  }

  // §6.2: a tactical bet resolved. Rendered as a plain feed line — same feed,
  // same styling family as any radio call — because a miss is not dressed up
  // as a bug and a hit is not dressed up as more than it is.
  function onOutcome(m) {
    const color = m.color || carColors.get(m.carId) || "var(--color-accent)";
    const el = document.createElement("div");
    el.className = "radio-msg outcome" + (m.success ? " win" : " loss");
    el.style.setProperty("--car-color", color);
    el.dataset.t = String(Date.now());
    el.innerHTML =
      `<div class="head"><span class="who">${swatch(color)}${esc(m.name || m.carId)}</span>` +
      `<span class="head-r"><span class="tag ${m.success ? "ok" : "fallback"}">` +
      `${m.success ? "✓ result" : "✗ result"}</span><span class="age"></span></span></div>` +
      `<div class="body">${esc(humanize(m.note || ""))}</div>`;
    radio.prepend(el);
    while (radio.children.length > RADIO_MAX) radio.lastChild.remove();
    refreshAges();
  }

  function onEnd(m) {
    finished = true;
    const cls = m.classification || [];
    if (cls.length) onTick({ classification: cls, meId: m.meId });
    lapCard.textContent = "FINISHED";
    if (raceOver) {
      const winner = cls[0] ? esc(cls[0].name || cls[0].id) : "";
      raceOver.innerHTML = `🏁 Carrera terminada${winner ? `<span>${winner}</span>` : ""}`;
      raceOver.classList.add("show");
    }
    refreshAges();
  }

  // --- Fase 6.1: decision log + trace modal --------------------------------

  function storeTrace(m, status, body) {
    if (traceStore.has(m.id)) return; // shouldn't happen (ids are unique), be safe
    traceStore.set(m.id, {
      id: m.id,
      carId: m.carId,
      name: m.name || m.carId,
      color: m.color || carColors.get(m.carId) || null,
      event: m.event || null,
      lap: m.lap ?? null,
      engine: m.engine === "heuristic" ? "heuristic" : "llm",
      status,
      reason: m.reason || null,
      radio: body,
      rationale: m.rationale || null,
      directive: m.directive || null,
      aggression: m.aggression || null,
      risk: m.risk || null,
      targetRival: m.targetRival || null,
      focusCorners: m.focusCorners || [],
      latencyMs: m.latencyMs || 0,
      request: m.request || null,
      ts: Date.now(),
    });
    traceOrder.unshift(m.id);
    while (traceOrder.length > TRACE_MAX) traceStore.delete(traceOrder.pop());
  }

  if (traceOpenBtn) traceOpenBtn.addEventListener("click", openList);
  if (traceClose) traceClose.addEventListener("click", closeTrace);
  if (traceBackdrop) traceBackdrop.addEventListener("click", closeTrace);
  if (traceBack) traceBack.addEventListener("click", openList);

  function openList() {
    if (!traceModal) return;
    document.getElementById("trace-title").textContent = `Decision log (${traceOrder.length})`;
    traceDetail.hidden = true;
    traceList.hidden = false;
    if (traceOrder.length === 0) {
      traceList.innerHTML = `<div class="trace-empty">No decisions logged yet this race.</div>`;
    } else {
      traceList.innerHTML = traceOrder
        .map((id) => {
          const r = traceStore.get(id);
          const tagText = r.status === "ok" ? "LLM" : r.engine === "heuristic" ? "heuristic" : "fallback";
          return (
            `<div class="trace-row" data-id="${esc(id)}" style="--car-color:${esc(r.color || "var(--color-accent)")}">` +
            `<span class="who">${swatch(r.color)}${esc(r.name)}</span>` +
            `<span class="trace-row-mid">L${r.lap ?? "?"} · ${esc(r.event || "")}</span>` +
            `<span class="tag ${r.status}">${esc(tagText)}</span>` +
            `</div>`
          );
        })
        .join("");
      traceList.querySelectorAll(".trace-row").forEach((row) => {
        row.addEventListener("click", () => openDetail(row.dataset.id));
      });
    }
    traceModal.hidden = false;
  }

  function openDetail(id) {
    const r = traceStore.get(id);
    if (!r || !traceModal) return;
    document.getElementById("trace-title").textContent = `${r.name} — L${r.lap ?? "?"} ${r.event || ""}`;
    traceList.hidden = true;
    traceDetail.hidden = false;
    traceDetailBody.innerHTML = renderDetailBody(r);
    const btn = traceDetailBody.querySelector(".trace-explain-btn");
    if (btn) btn.addEventListener("click", () => runExplain(r, btn));
    traceModal.hidden = false;
  }

  function closeTrace() {
    if (traceModal) traceModal.hidden = true;
  }

  function renderDetailBody(r) {
    const decided = r.status === "ok";
    const parts = [];

    parts.push(`<div class="trace-meta">${ago(r.ts)} ago · ${esc(r.engine)} engine` +
      (r.latencyMs ? ` · ${r.latencyMs}ms` : "") + `</div>`);

    if (decided) {
      parts.push(
        `<dl class="trace-fields">` +
          field("directive", r.directive) +
          field("aggression", r.aggression) +
          field("risk", r.risk) +
          field("target", r.targetRival ? humanize(r.targetRival) : "—") +
          field("corners", r.focusCorners.length ? "T" + r.focusCorners.join(", T") : "—") +
          `</dl>` +
          `<div class="trace-radio">"${esc(humanize(r.radio))}"</div>` +
          (r.rationale ? `<div class="trace-rationale"><b>Rationale (not shown live):</b> ${esc(humanize(r.rationale))}</div>` : "")
      );
    } else {
      parts.push(
        `<div class="trace-note">No new directive was produced for this event` +
          (r.reason ? ` (reason: ${esc(r.reason)})` : "") +
          ` — the car kept its previous directive.</div>`
      );
    }

    if (r.request) {
      parts.push(
        `<div class="trace-label">What the strategist saw</div>` +
          `<pre class="trace-json">${esc(JSON.stringify(r.request, null, 2))}</pre>`
      );
    } else {
      parts.push(`<div class="trace-note">No context was available — this car runs without a strategist.</div>`);
    }

    if (r.engine === "llm" && decided) {
      parts.push(
        `<button type="button" class="trace-explain-btn">Ask the strategist to re-explain</button>` +
          `<div class="trace-explain-out" hidden></div>`
      );
    }

    return parts.join("");
  }

  function field(label, value) {
    return `<dt>${esc(label)}</dt><dd>${esc(value ?? "—")}</dd>`;
  }

  async function runExplain(r, btn) {
    const out = btn.nextElementSibling;
    btn.disabled = true;
    btn.textContent = "Asking…";
    out.hidden = false;
    out.className = "trace-explain-out";
    out.textContent = "";
    try {
      const resp = await fetch("api/explain", {
        method: "POST",
        headers: { "content-type": "application/json" },
        body: JSON.stringify({
          context: r.request.context,
          telemetry: r.request.telemetry,
          directive: {
            directive: r.directive,
            aggression: r.aggression,
            risk_tolerance: r.risk,
            target_rival: r.targetRival || null,
            focus_corners: r.focusCorners || [],
            radio: r.radio,
            rationale: r.rationale || "",
          },
        }),
      });
      const data = await resp.json();
      if (data.status === "ok" && data.explanation) {
        out.textContent = humanize(data.explanation);
      } else {
        out.className = "trace-explain-out fallback";
        out.textContent = `The strategist couldn't re-explain this right now (${data.reason || "unavailable"}).`;
      }
    } catch {
      out.className = "trace-explain-out fallback";
      out.textContent = "The strategist couldn't re-explain this right now (network).";
    } finally {
      btn.disabled = false;
      btn.textContent = "Ask again";
    }
  }

  return { onStart, onTick, onRadio, onOutcome, onEnd };
}

function esc(s) {
  return String(s).replace(/[&<>"]/g, (c) =>
    ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;" }[c])
  );
}

// Relative age: "ahora" under 3 s, "42s" under a minute, then "m:ss".
function ago(ms) {
  const s = Math.max(0, Math.round((Date.now() - ms) / 1000));
  if (s < 3) return "ahora";
  if (s < 60) return `${s}s`;
  return `${Math.floor(s / 60)}:${String(s % 60).padStart(2, "0")}`;
}

// A small round colour chip so a car in the 3D view can be matched to its
// standings row and its team-radio line at a glance. Colour comes from the
// race:start car list; skipped if unknown.
function swatch(color) {
  if (!color) return "";
  return `<span class="swatch" style="--sw:${esc(color)}"></span>`;
}
