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
 * Flow: read progress from localStorage. If the target hasn't been reached,
 * record this race's per-car result on `race:end`, then reload the page with
 * the next `?race=` index after a short pause — the race auto-starts on load,
 * so this runs unattended. Once the target is reached, render a summary
 * on-page and offer the raw results as a JSON download instead of reloading.
 */

const STORAGE_KEY = "agentic-racing:experiment:v1";
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
    state = { target, racesDone: 0, results: [] };
    save(state);
  }

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

  return { onStart, onEnd };
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
    "position:fixed;top:0.5rem;left:0.5rem;z-index:9999;max-width:min(90vw,480px);" +
    "background:rgba(10,12,16,0.85);color:#e8e9ec;padding:0.5rem 0.75rem;" +
    "border-radius:8px;font:12px/1.5 ui-monospace,SFMono-Regular,monospace;" +
    "white-space:pre-wrap;pointer-events:none;";
  document.body.appendChild(bannerEl);
  return bannerEl;
}

function showBanner(state) {
  ensureBanner().textContent = `Fase 6.3 experiment — race ${state.racesDone}/${state.target}`;
}

function finish(state) {
  const summary = computeSummary(state.results);
  const el = ensureBanner();
  el.style.pointerEvents = "auto";
  const fmt = (g) =>
    g
      ? `n=${g.races}  pos=${g.position.mean.toFixed(2)}±${g.position.sd.toFixed(2)}  ` +
        `gap=${g.gap.mean.toFixed(1)}s  overtakes=${g.overtakes}  incidents=${g.incidents}`
      : "n=0";
  el.textContent =
    `Fase 6.3 experiment DONE — ${state.target} races\n` +
    `llm:       ${fmt(summary.llm)}\n` +
    `heuristic: ${fmt(summary.heuristic)}\n` +
    `(mean ± population SD of finishing position; lower is better)`;
  offerDownload(state);
}

// --- summary stats (§6.3: primary = mean finishing position, report
// dispersion, not just the mean; secondary = total time proxy, overtakes,
// incidents) --------------------------------------------------------------

function computeSummary(results) {
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
  return out;
}

function offerDownload(state) {
  const blob = new Blob([JSON.stringify(state.results, null, 2)], {
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
