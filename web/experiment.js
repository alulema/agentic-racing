/* Fase 6.3 mixed-field data-collection harness — CLAUDE.md §6.3.
 *
 * NOT part of the shipped demo UX: opt-in only via `?experiment=1`, meant to
 * be driven locally (`docker run ...`, see README) across many unattended
 * races, one browser tab, one page load at a time. Never point this at the
 * production ephemeral pod — its ~60 min hard environment lifetime (§2.2)
 * would cut a multi-hour run off mid-experiment.
 *
 * Methodology, adapted for the fixed-circuit build (Camino A): `?seed=` no
 * longer varies the track (TrackGenerator.FixedRoundedRect ignores the RNG
 * entirely — see docs/Devlog.md), so "many trials" here means many *race
 * indices* instead of many seeds. RaceDirector's rotation —
 *   member = (slot + raceIndex) % 6
 *   useLlm = (member + raceIndex) % 6 < llmCars   (llmCars = 3)
 * already balances grid slot AND LLM/heuristic assignment across a 6-race
 * cycle: every one of the 6 population pilots spends exactly 3 races on each
 * side, and visits every grid slot exactly once per cycle. Repeating full
 * cycles on top of that (`?cycles=N`) samples the LLM's own run-to-run
 * variance (temperature 0.4, real latency) — the only remaining source of
 * variation once the track and the heuristic pilot are both deterministic.
 * This is honest about what changed vs. the original spec, not a rewrite of
 * the methodology: rotation is still the load-bearing control, seeds just
 * aren't the axis that provides it anymore.
 *
 * v2 adds decision-level logging: every `radio:msg` (one per strategy event,
 * §6.6) is recorded with its directive/aggression/risk/status, not just the
 * final per-race result. The first run (18 races, see docs/Devlog.md
 * 2026-09-13) found the `llm` group finishing ~2.8 positions worse than
 * `heuristic`, consistently across all 6 pilots — this is here to test the
 * standing hypothesis for *why*: that the model picks conservative
 * (low aggression / low risk) directives more often than the fixed
 * heuristic would in the same spot, which costs it pace directly via §6.5's
 * braking-margin/corner-exit channels. Bumped the storage key so this run
 * starts clean rather than trying to resume the older, undecorated dataset.
 *
 * Flow: read progress from localStorage. If the target hasn't been reached,
 * record every decision as it streams in and this race's per-car result on
 * `race:end`, then reload the page with the next `?race=` index after a
 * short pause — the race auto-starts on load, so this runs unattended. Once
 * the target is reached, render a summary on-page and offer the raw
 * results+decisions as a JSON download instead of reloading.
 */

const STORAGE_KEY = "agentic-racing:experiment:v2";
const RACES_PER_CYCLE = 6; // matches RaceDirective.Population size
const RELOAD_DELAY_MS = 4000; // let the "Carrera terminada" banner sit for a beat

export function initExperiment(params) {
  const cycles = Math.max(1, parseInt(params.get("cycles") || "3", 10));
  const target = cycles * RACES_PER_CYCLE;
  const reset = params.get("reset") === "1";
  // The index this page actually loaded with, not a guess reconstructed from
  // progress — if someone navigates by hand mid-run this stays correct.
  const loadedRaceIndex = Math.max(0, parseInt(params.get("race") || "0", 10)) % RACES_PER_CYCLE;

  let state = load();
  if (reset || !state || state.target !== target) {
    state = { target, racesDone: 0, results: [], decisions: [] };
    save(state);
  }
  if (!state.decisions) state.decisions = []; // upgrading a run started before v2

  let currentCars = new Map(); // carId -> "llm" | "heuristic", from this race's race:start

  // Reopening the page after the target was already hit (e.g. an accidental
  // reload) re-renders the summary immediately instead of stranding the
  // viewer on a plain progress banner — the data's already sitting in
  // localStorage, no reason to hide it.
  if (state.racesDone >= state.target) finish(state);
  else showBanner(state);

  function onStart(m) {
    currentCars = new Map();
    (m.cars || []).forEach((c) => currentCars.set(c.id, c.pilot));
  }

  // Every strategy event (§6.6), not just the final result — this is what
  // lets us see *what the model chose*, not just how the car did.
  function onRadio(m) {
    if (state.racesDone >= state.target) return;
    state.decisions.push({
      race: state.racesDone,
      cycle: Math.floor(state.racesDone / RACES_PER_CYCLE),
      raceIndex: loadedRaceIndex,
      carId: m.carId,
      name: m.name,
      engine: m.engine === "heuristic" ? "heuristic" : "llm",
      event: m.event || null,
      lap: m.lap ?? null,
      directive: m.directive || null,
      aggression: m.aggression || null,
      risk: m.risk || null,
      status: m.status === "ok" ? "ok" : "fallback",
      reason: m.reason || null,
      latencyMs: m.latencyMs || 0,
    });
    // Not saved here — `decisions` rides along with the next onEnd() save, so
    // a mid-race interruption loses that race's decisions same as it already
    // loses that race's result. Saving on every radio:msg would mean a
    // localStorage write every ~cooldown-per-car, which is unnecessary churn.
  }

  function onEnd(m) {
    if (state.racesDone >= state.target) return; // already finished; ignore a stray late event

    const cycle = Math.floor(state.racesDone / RACES_PER_CYCLE);
    for (const r of m.classification || []) {
      state.results.push({
        race: state.racesDone,
        cycle,
        raceIndex: loadedRaceIndex,
        carId: r.id,
        name: r.name,
        engine: currentCars.get(r.id) || "unknown",
        position: r.pos,
        gap: r.gap ?? 0,
        overtakes: r.overtakes ?? 0,
        incidents: r.incidents ?? 0,
      });
    }
    state.racesDone++;
    save(state);
    showBanner(state);

    if (state.racesDone >= state.target) {
      finish(state);
      return;
    }
    const nextIndex = state.racesDone % RACES_PER_CYCLE;
    const url = new URL(location.href);
    url.searchParams.set("race", String(nextIndex));
    url.searchParams.delete("reset");
    setTimeout(() => {
      location.href = url.toString();
    }, RELOAD_DELAY_MS);
  }

  return { onStart, onEnd, onRadio };
}

// --- persistence ------------------------------------------------------------

function load() {
  try {
    return JSON.parse(localStorage.getItem(STORAGE_KEY));
  } catch {
    return null;
  }
}

function save(state) {
  try {
    localStorage.setItem(STORAGE_KEY, JSON.stringify(state));
  } catch {
    // Private-browsing / quota — the run still progresses in memory for this
    // page load, it just can't survive the next reload. Not worth surfacing.
  }
}

// --- on-page status -----------------------------------------------------

let bannerEl = null;

function ensureBanner() {
  if (bannerEl) return bannerEl;
  bannerEl = document.createElement("div");
  bannerEl.id = "experiment-banner";
  bannerEl.style.cssText =
    "position:fixed;top:0.5rem;left:0.5rem;z-index:9999;max-width:min(90vw,560px);" +
    "background:rgba(10,12,16,0.85);color:#e8e9ec;padding:0.5rem 0.75rem;" +
    "border-radius:8px;font:12px/1.5 ui-monospace,SFMono-Regular,monospace;" +
    "white-space:pre-wrap;pointer-events:none;";
  document.body.appendChild(bannerEl);
  return bannerEl;
}

function showBanner(state) {
  ensureBanner().textContent =
    `Fase 6.3 experiment — race ${state.racesDone}/${state.target}  ` +
    `(${state.decisions.length} decisions logged)`;
}

function finish(state) {
  const summary = computeSummary(state.results, state.decisions);
  const el = ensureBanner();
  el.style.pointerEvents = "auto";
  const fmtResult = (g) =>
    g
      ? `n=${g.races}  pos=${g.position.mean.toFixed(2)}±${g.position.sd.toFixed(2)}  ` +
        `gap=${g.gap.mean.toFixed(1)}s  overtakes=${g.overtakes}  incidents=${g.incidents}`
      : "n=0";
  const fmtDist = (d) => (d ? Object.entries(d).map(([k, v]) => `${k}:${v}%`).join(" ") : "");
  const fmtDecisions = (g) =>
    g
      ? `n=${g.decisions} ok=${g.okRate}%  directive[ ${fmtDist(g.directiveDist)} ]  ` +
        `agg[ ${fmtDist(g.aggressionDist)} ]  risk[ ${fmtDist(g.riskDist)} ]`
      : "";
  el.textContent =
    `Fase 6.3 experiment DONE — ${state.target} races\n` +
    `llm:       ${fmtResult(summary.llm)}\n` +
    `heuristic: ${fmtResult(summary.heuristic)}\n` +
    `(mean ± population SD of finishing position; lower is better)\n\n` +
    `llm decisions:       ${fmtDecisions(summary.llm)}\n` +
    `heuristic decisions: ${fmtDecisions(summary.heuristic)}`;
  offerDownload(state, summary);
}

// --- summary stats (§6.3: primary = mean finishing position, report
// dispersion, not just the mean; secondary = total time proxy, overtakes,
// incidents, plus the decision-level directive/aggression/risk distribution
// this v2 adds, to test the "the model plays it safe" hypothesis) ----------

function computeSummary(results, decisions) {
  const groups = {};
  for (const r of results) (groups[r.engine] ||= []).push(r);

  const stat = (rows, key) => {
    const vals = rows.map((r) => r[key]);
    const n = vals.length || 1;
    const mean = vals.reduce((a, b) => a + b, 0) / n;
    const variance = vals.reduce((a, b) => a + (b - mean) ** 2, 0) / n;
    return { mean, sd: Math.sqrt(variance) };
  };

  const out = {};
  for (const [engine, rows] of Object.entries(groups)) {
    out[engine] = {
      races: rows.length,
      position: stat(rows, "position"),
      gap: stat(rows, "gap"), // seconds behind the winner — a total-time proxy
      overtakes: rows.reduce((a, r) => a + r.overtakes, 0),
      incidents: rows.reduce((a, r) => a + r.incidents, 0),
    };
  }

  const decGroups = {};
  for (const d of decisions || []) (decGroups[d.engine] ||= []).push(d);

  const distribution = (rows, key) => {
    const counts = {};
    for (const r of rows) {
      const v = r[key] || "(none)";
      counts[v] = (counts[v] || 0) + 1;
    }
    const n = rows.length || 1;
    const pct = {};
    for (const [k, c] of Object.entries(counts)) pct[k] = +((100 * c) / n).toFixed(1);
    return pct;
  };

  for (const [engine, rows] of Object.entries(decGroups)) {
    out[engine] = out[engine] || {};
    out[engine].decisions = rows.length;
    out[engine].okRate = +((100 * rows.filter((r) => r.status === "ok").length) / (rows.length || 1)).toFixed(1);
    out[engine].directiveDist = distribution(rows, "directive");
    out[engine].aggressionDist = distribution(rows, "aggression");
    out[engine].riskDist = distribution(rows, "risk");
  }
  return out;
}

function offerDownload(state, summary) {
  const payload = { results: state.results, decisions: state.decisions, summary };
  const blob = new Blob([JSON.stringify(payload, null, 2)], {
    type: "application/json",
  });
  const url = URL.createObjectURL(blob);
  const a = document.createElement("a");
  a.href = url;
  a.download = "agentic-racing-mixed-field-results.json";
  a.textContent = "⬇ Download results.json";
  // Bottom-right, not bottom-left: demo-panel.js's "About this demo" widget
  // (loaded from the site origin, outside this repo) already owns that corner.
  a.style.cssText =
    "position:fixed;bottom:1rem;right:1rem;z-index:9999;background:#1f6feb;color:#fff;" +
    "padding:0.5rem 0.9rem;border-radius:8px;font:13px/1 ui-monospace,SFMono-Regular,monospace;" +
    "text-decoration:none;box-shadow:0 4px 16px rgba(0,0,0,0.4);";
  document.body.appendChild(a);
}
