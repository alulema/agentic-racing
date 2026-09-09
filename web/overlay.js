/* DOM overlay: the lap/standings HUD and the team-radio feed.
 *
 * Both are driven by messages Unity dispatches on window as
 *   new CustomEvent('unity:message', { detail: <json string> })
 * (see Assets/Scripts/Interop/WebGLBridge.jslib). This file only renders; the
 * router in app.js decides what to call.
 *
 * Message types (Unity -> DOM):
 *   race:start  { seed, laps, cars:[{id,name,color,pilot}] }
 *   race:tick   { lap, laps, meId, classification:[{pos,id,name,gap,lastLap,directive}] }
 *   radio:msg   { carId, name, color, directive, aggression, risk, targetRival,
 *                 focusCorners:[int], radio, status:"ok"|"fallback", reason, latencyMs }
 *   race:end    { classification:[...] }
 */

const RADIO_MAX = 6; // keep the feed short; it's a live ticker, not a log

export function initOverlay() {
  const hud = document.getElementById("hud");
  const lapCard = document.getElementById("lap-card");
  const standings = document.getElementById("standings-body");
  const radio = document.getElementById("radio-feed");
  const waiting = document.getElementById("waiting");
  const carColors = new Map();

  function onStart(m) {
    waiting.classList.add("hidden");
    hud.style.visibility = "visible";
    (m.cars || []).forEach((c) => carColors.set(c.id, c.color || null));
    lapCard.textContent = `LAP 1 / ${m.laps ?? "?"}`;
    standings.innerHTML = "";
    radio.innerHTML = "";
  }

  function onTick(m) {
    if (m.lap != null) lapCard.textContent = `LAP ${m.lap} / ${m.laps ?? "?"}`;
    const rows = (m.classification || [])
      .map((r) => {
        const me = r.id === m.meId ? ' class="me"' : "";
        const gap = r.pos === 1 ? "leader" : r.gap != null ? `+${r.gap.toFixed(1)}` : "";
        const last = r.lastLap ? r.lastLap.toFixed(1) + "s" : "—";
        return (
          `<tr${me}><td class="pos">${r.pos}</td>` +
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
    const el = document.createElement("div");
    el.className = "radio-msg";
    el.style.setProperty("--car-color", color);

    const tagClass = m.status === "ok" ? "ok" : "fallback";
    const tagText = m.status === "ok" ? "LLM" : `fallback${m.reason ? " · " + m.reason : ""}`;

    const bits = [];
    if (m.directive) bits.push(m.directive.toUpperCase());
    if (m.aggression) bits.push("agg:" + m.aggression);
    if (m.risk) bits.push("risk:" + m.risk);
    if (m.targetRival) bits.push("→ " + m.targetRival);
    if (m.focusCorners && m.focusCorners.length) bits.push("T" + m.focusCorners.join(",T"));
    if (m.latencyMs) bits.push(m.latencyMs + "ms");

    el.innerHTML =
      `<div class="head"><span class="who">${swatch(color)}${esc(m.name || m.carId)}</span>` +
      `<span class="tag ${tagClass}">${esc(tagText)}</span></div>` +
      `<div class="body">${esc(m.radio || "(no radio)")}</div>` +
      (bits.length ? `<div class="meta">${esc(bits.join("  ·  "))}</div>` : "");

    radio.prepend(el);
    while (radio.children.length > RADIO_MAX) radio.lastChild.remove();
  }

  function onEnd(m) {
    if (m.classification) onTick({ classification: m.classification, meId: m.meId });
    lapCard.textContent = "FINISHED";
  }

  return { onStart, onTick, onRadio, onEnd };
}

function esc(s) {
  return String(s).replace(/[&<>"]/g, (c) =>
    ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;" }[c])
  );
}

// A small round colour chip so a car in the 3D view can be matched to its
// standings row and its team-radio line at a glance. Colour comes from the
// race:start car list; skipped if unknown.
function swatch(color) {
  if (!color) return "";
  return `<span class="swatch" style="--sw:${esc(color)}"></span>`;
}
