/* Entry point for the demo shell.
 *
 *  - loads the Unity WebGL build (relative paths only — the app is served from
 *    "/" behind a reverse proxy, CLAUDE.md §2.2),
 *  - routes Unity's `unity:message` CustomEvents to the DOM overlay,
 *  - runs the /api/ping heartbeat while a race is active so the ephemeral pod
 *    isn't torn down for inactivity mid-race (§2.2),
 *  - polls /api/health for the LLM status chip (online / offline / down).
 *
 * `?mock=1` skips Unity entirely and drives the overlay from mock.js — lets the
 * whole DOM layer be checked in a plain browser with no build.
 */

import { initOverlay } from "./overlay.js?v=6";
import { startMock } from "./mock.js?v=6";

const overlay = initOverlay();
const params = new URLSearchParams(location.search);

// --- Unity -> DOM message router -----------------------------------------

let raceActive = false;

window.addEventListener("unity:message", (e) => {
  let msg;
  try {
    msg = JSON.parse(e.detail);
  } catch {
    console.warn("[app] non-JSON unity:message:", e.detail);
    return;
  }
  route(msg);
});

function route(msg) {
  switch (msg.type) {
    case "race:start":
      raceActive = true;
      startHeartbeat();
      overlay.onStart(msg);
      break;
    case "race:tick":
      overlay.onTick(msg);
      break;
    case "radio:msg":
      overlay.onRadio(msg);
      break;
    case "race:end":
      raceActive = false;
      stopHeartbeat();
      overlay.onEnd(msg);
      break;
    default:
      console.debug("[app] unhandled message type:", msg.type);
  }
}

// --- heartbeat (§2.2) --------------------------------------------------------

let heartbeatTimer = null;
const HEARTBEAT_MS = 60_000; // well under the ~8 min inactivity teardown

function startHeartbeat() {
  if (heartbeatTimer) return;
  const beat = () => {
    fetch("api/ping", { method: "GET", cache: "no-store" }).catch(() => {});
  };
  beat();
  heartbeatTimer = setInterval(beat, HEARTBEAT_MS);
}
function stopHeartbeat() {
  clearInterval(heartbeatTimer);
  heartbeatTimer = null;
}

// --- LLM status chip ------------------------------------------------------

const chip = document.getElementById("llm-status");
async function pollHealth() {
  try {
    const r = await fetch("api/health", { cache: "no-store" });
    const h = await r.json();
    const llm = h.llm || {};
    if (!h.ollama_reachable && llm.mode !== "online") {
      chip.className = "down";
      chip.textContent = "LLM down";
    } else if (llm.mode === "offline") {
      chip.className = "offline";
      chip.textContent = `LLM offline · p95 ${fmtMs(llm.p95_ms)}`;
    } else {
      chip.className = "online";
      const rej = llm.calls ? Math.round((100 * llm.rejected) / llm.calls) : 0;
      chip.textContent = `LLM online · p95 ${fmtMs(llm.p95_ms)} · ${rej}% rejected`;
    }
  } catch {
    chip.className = "down";
    chip.textContent = "LLM ?";
  }
}
function fmtMs(ms) {
  if (!ms) return "—";
  return ms >= 1000 ? (ms / 1000).toFixed(1) + "s" : ms + "ms";
}
setInterval(pollHealth, 5000);
pollHealth();

// --- Unity loader ------------------------------------------------------------

if (params.get("mock") === "1") {
  document.getElementById("waiting").textContent = "mock mode — no Unity";
  startMock(route);
} else {
  loadUnity();
}

function loadUnity() {
  // CI rewrites BUILD_NAME when it assembles /web next to the Unity Build/
  // output. Until then this points at the Fase 0 test build name.
  const BUILD_NAME = "web-test";
  const buildUrl = "Build";
  const canvas = document.getElementById("unity-canvas");
  const bar = document.getElementById("load-bar");

  const config = {
    arguments: [],
    dataUrl: `${buildUrl}/${BUILD_NAME}.data.br`,
    frameworkUrl: `${buildUrl}/${BUILD_NAME}.framework.js.br`,
    codeUrl: `${buildUrl}/${BUILD_NAME}.wasm.br`,
    streamingAssetsUrl: "StreamingAssets",
    companyName: "DefaultCompany",
    productName: "agentic-racing",
    productVersion: "0.1.0",
  };

  const script = document.createElement("script");
  script.src = `${buildUrl}/${BUILD_NAME}.loader.js`;
  script.onload = () => {
    createUnityInstance(canvas, config, (p) => {
      bar.style.width = Math.round(p * 100) + "%";
      if (p >= 1) bar.style.opacity = "0";
    })
      .then((instance) => {
        window.unityInstance = instance;
      })
      .catch((err) => {
        document.getElementById("waiting").textContent = "Unity failed to load: " + err;
      });
  };
  script.onerror = () => {
    document.getElementById("waiting").textContent =
      "Unity build not found (expected under /Build). Try ?mock=1 for the overlay.";
  };
  document.body.appendChild(script);
}
