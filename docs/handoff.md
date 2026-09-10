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
| Extra resources | **none external.** A single image runs two processes: `uvicorn` (FastAPI, the only reachable port, `8080`) and an **Ollama sidecar** (`llama3.2:3b`, weights baked in, listening on `127.0.0.1:11434` only). `docker/entrypoint.sh` starts Ollama, waits for readiness, warms the model, then `exec`s uvicorn as PID 1. |

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

## Pod sizing (target)

Single ephemeral pod, **4 vCPU / 8 GiB** (an upgrade request was submitted;
confirm it landed, otherwise the app still runs on 2 vCPU / 4 GiB with
`OLLAMA_NUM_THREAD=1` and slower strategy calls). CPU is biased to inference —
Ollama gets the bulk, the FastAPI proxy is light (static file serving + one
proxied POST at a time). No GPU; the CUDA/ROCm/Vulkan runtimes are stripped from
the image (final image ~4.5 GB, of which ~2 GB is the baked model).

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
- `GET /api/health` — LLM mode (`online` / `offline`), p95 latency, call /
  rejected / failed counts, `ollama_reachable`.
- `GET /api/ping` — heartbeat.
