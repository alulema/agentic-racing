# Hand-off manifest — agentic-racing

For the maintainer of the ephemeral-container infra (per `DEMO_INTEGRATION.md`
§ "Lo que entregas a la infra"). You do the registration and provisioning in the
private infra repo; this project does not touch Azure or OIDC.

| Field | Value |
|---|---|
| `projectId` (slug) | `agentic-racing` |
| Human name (EN / ES) | "Agentic Racing" / "Agentic Racing" |
| Public repo | `https://github.com/alulema/agentic-racing` |
| Image + port | `ghcr.io/alulema/agentic-racing:latest` · `8080` |
| `shareable` | `true` — the server is stateless (§2.2): no race history, no per-user session. Race logs live in the client. One environment can serve many requests. |
| Secrets to inject | **none.** The LLM is local (§2.5); there is no `ANTHROPIC_API_KEY` or equivalent. |
| Extra resources | **Required: none.** A single image runs two processes: `uvicorn` (FastAPI, the only reachable port, `8080`) and an **Ollama sidecar** (`llama3.2:3b`, weights baked in, listening on `127.0.0.1:11434` only). `docker/entrypoint.sh` starts Ollama, waits for readiness, warms the model, then `exec`s uvicorn as PID 1. **Optional**: a *second* Ollama-only container as a genuine capacity add — see "Optional Ollama sidecar" below. Only worth provisioning if this demo's pod gets extra CPU/RAM allotted for it; skip it otherwise and the app runs exactly as the row above describes. |

## Env vars

- **From the platform** (per contract): `PROJECT_ID`, `DEMO_SLOT`. The app reads
  them for logging; nothing else depends on them.
- **Pod tuning** (baked as `ENV` in the image; override only if the pod shape
  differs from the target below):
  - `OLLAMA_NUM_THREAD=3` — set to `(pod vCPU − 1)` so Ollama inference leaves a
    core for uvicorn + the OS. `3` targets a 4-vCPU pod; use `1` on a 2-vCPU pod.
  - `OLLAMA_NUM_PARALLEL=1`, `OLLAMA_MAX_LOADED_MODELS=1` — one model, one
    in-flight request. The proxy already serialises calls (§7.5); extra Ollama
    parallelism just contends for CPU.
  - `OLLAMA_KEEP_ALIVE=24h` — past the pod's max life so the model never
    reloads.
  - `STRATEGY_*` (timeout, circuit-breaker p95 / cooldown, max-concurrent,
    rate-limit) have sane defaults in `server/main.py`; documented there.
  - `OLLAMA_URLS` — comma-separated Ollama endpoints (default: the app's own
    loopback Ollama alone). Only set this if the optional sidecar below is
    provisioned; unset, nothing about the app's behavior changes.

## Pod sizing (target)

Single ephemeral pod, **4 vCPU / 8 GiB** — the upgrade request was submitted and
**confirmed approved** (2026-09-15); `OLLAMA_NUM_THREAD=3` (the image's default)
is the right value for this sizing, not the 2-vCPU fallback of `1`. CPU is biased
to inference —
Ollama gets the bulk, the FastAPI proxy is light (static file serving + one
proxied POST at a time). No GPU; the CUDA/ROCm/Vulkan runtimes are stripped from
the image (final image ~4.5 GB, of which ~2 GB is the baked model).

## Optional Ollama sidecar (extra strategy-call capacity)

**Not required to run the demo** — everything above already describes a
complete, working deployment. This is a capacity upgrade to consider only if
this demo's pod can be given a genuinely bigger CPU/RAM budget than the
target above, split across two containers instead of one.

**Why**: Fase 6.3 (`docs/Devlog.md` 2026-09-14) measured that with 6 cars and
3 of them LLM-piloted, one Ollama engine (`OLLAMA_NUM_PARALLEL=1`, by design —
§7) serves strategy calls one at a time; raising the proxy's own admission
gate (`STRATEGY_MAX_CONCURRENT`) without a second real engine just queues more
calls behind that one engine and trips the latency circuit breaker *more*
often, not less (fresh-reply rate measured at 36% → 9.5%). A second,
independent Ollama gives real additional throughput instead.

**What it is**: `ghcr.io/alulema/agentic-racing-ollama:latest` — a slightly
lighter image than the app's (`docker/Dockerfile`'s `ollama-sidecar` build
target: ~2.2 GB, just the Ollama binary + the same baked `llama3.2:3b`
weights, no Unity/Python app on top, vs. the app image's ~2.4 GB). Listens on
`11434`, **internal-only** — reachable from the app container
by service/hostname on the pod's private network, never exposed past that
(same ingress boundary the app's own port `8080` already has).

**What it needs from the infra**: a second container in the *same* ephemeral
pod/network as the app container (co-located, same lifecycle — torn down
together, per the contract's lifecycle rules), reachable from the app
container at a stable internal hostname (e.g. `ollama-2`), with its own CPU/RAM
share **on top of** — not carved out of — the app container's existing budget.
Sizing this container the same as the app container's own baked Ollama process
(`OLLAMA_NUM_THREAD` = its share of vCPU − 0, since it runs nothing else) is a
reasonable starting point.

**How to wire it**: set `OLLAMA_URLS` on the app container to
`http://127.0.0.1:11434,http://<sidecar-hostname>:11434` (see "Env vars"
above). That's the only change the app needs — `server/guardrails.py` gives
each URL its own concurrency slot automatically.

**If this isn't provisioned**: nothing to do. The app defaults to its own
loopback Ollama alone and behaves exactly as the rest of this document
describes.

## Lifecycle it tolerates (per contract)

- ~20-min sessions (hard cap), ~8-min inactivity → teardown, ~60-min max
  environment life, kill-switch any time.
- The client pings `/api/ping` on a 60 s timer while a race is active so the
  inactivity teardown doesn't fire mid-race (the whole sim runs client-side, so
  minutes can pass with no other traffic).
- Abrupt teardown is fine: no persisted state, deterministic startup (model is
  baked, no `ollama pull` at boot).
- If Ollama is slow or dies, `/api/strategy` returns a heuristic fallback
  directive (HTTP 200, never an error) and the race keeps running.

## Endpoints

- `GET /` and `/<asset>` — the Unity WebGL demo (Brotli assets served with the
  right `Content-Encoding`).
- `POST /api/strategy` — the strategist proxy. Always HTTP 200 with an envelope
  (`status: ok | fallback`).
- `POST /api/explain` — Fase 6.1: re-explain a past directive on request (the
  decision-log "ask the strategist to re-explain" button). Same guardrails as
  `/api/strategy`, always HTTP 200 with an envelope (`status: ok | fallback`).
- `GET /api/health` — LLM mode (`online` / `offline`), p95 latency, call /
  rejected / failed counts, `ollama_reachable`.
- `GET /api/ping` — heartbeat.
