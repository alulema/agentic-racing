/* DOM overlay: the lap/standings HUD and the team-radio feed.
 *
 * Both are driven by messages Unity dispatches on window as
 *   new CustomEvent('unity:message', { detail: <json string> })
 * (see Assets/Scripts/Interop/WebGLBridge.jslib). This file only renders; the
 * router in app.js decides what to call.
 *
 * Message types (Unity -> DOM):
 *   race:start  { seed, laps, cars:[{id,name,color,pilot}] }
 *   race:tick   { lap, laps, meId, classification:[{pos,id,name,gap,lastLap,directive,done}] }
 *   radio:msg   { carId, name, color, directive, aggression, risk, targetRival,
 *                 focusCorners:[int], radio, status:"ok"|"fallback", reason, latencyMs }
 *   race:end    { classification:[...] }   // sent when the leader takes the flag
 */

const RADIO_MAX = 6; // keep the feed short; it's a live ticker, not a log

export function initOverlay() {
  const hud = document.getElementById("hud");
  const lapCard = document.getElementById("lap-card");
  const standings = document.getElementById("standings-body");
  const radio = document.getElementById("radio-feed");
  const radioAge = document.getElementById("radio-age");
  const raceOver = document.getElementById("race-over");
  const waiting = document.getElementById("waiting");
  const carColors = new Map();
  let finished = false;

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
    (m.cars || []).forEach((c) => carColors.set(c.id, c.color || null));
    lapCard.textContent = `LAP 1 / ${m.laps ?? "?"}`;
    standings.innerHTML = "";
    radio.innerHTML = "";
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
        return (
          `<tr${rowCls}><td class="pos">${r.done ? "✓" : r.pos}</td>` +
          `<td class="car">${swatch(carColors.get(r.id))}${esc(r.name || r.id)}</td>` +
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
      top.querySelector(".count").textContent = `×${n}`;
      refreshAges();
      return;
    }

    const tagText = status === "ok" ? "LLM" : `fallback${m.reason ? " · " + m.reason : ""}`;
    const bits = [];
    if (m.directive) bits.push(m.directive.toUpperCase());
    if (m.aggression) bits.push("agg:" + m.aggression);
    if (m.risk) bits.push("risk:" + m.risk);
    if (m.targetRival) bits.push("→ " + m.targetRival);
    if (m.focusCorners && m.focusCorners.length) bits.push("T" + m.focusCorners.join(",T"));
    if (m.latencyMs) bits.push(m.latencyMs + "ms");

    const el = document.createElement("div");
    el.className = "radio-msg";
    el.style.setProperty("--car-color", color);
    el.dataset.car = m.carId || "";
    el.dataset.body = body;
    el.dataset.status = status;
    el.dataset.count = "1";
    el.dataset.t = String(Date.now());
    el.innerHTML =
      `<div class="head"><span class="who">${swatch(color)}${esc(m.name || m.carId)}` +
      `<span class="count"></span></span>` +
      `<span class="head-r"><span class="tag ${status}">${esc(tagText)}</span>` +
      `<span class="age"></span></span></div>` +
      `<div class="body">${esc(body)}</div>` +
      (bits.length ? `<div class="meta">${esc(bits.join("  ·  "))}</div>` : "");

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

  return { onStart, onTick, onRadio, onEnd };
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
