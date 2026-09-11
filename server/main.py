"""FastAPI server for the agentic-racing demo.

Two jobs:

1. Serve the Unity WebGL build as static files with the right headers for the
   pre-compressed ``.br`` / ``.gz`` assets Unity emits (a generic static server
   gets ``Content-Encoding`` wrong and the browser fails to parse them — see
   CLAUDE.md §11).
2. Proxy ``POST /api/strategy`` to the local Ollama sidecar: build the prompt
   (stable prefix + variable telemetry, §6.7), force JSON-schema output, one
   inference turn, validate the reply (§6.8), and return a directive — behind
   the load guardrails of §7 (global concurrency gate, circuit breaker to an
   "offline" fallback, per-IP rate limit). Plus ``/api/health`` and
   ``/api/ping`` (the client heartbeat that keeps the ephemeral pod alive,
   §2.2).

The server is stateless (§2.2): no race history, no per-car session. The client
sends the stable ``context`` on every call so Ollama can still reuse the KV
prefix.
"""

from __future__ import annotations

import logging
import mimetypes
import os
import time
from contextlib import asynccontextmanager
from pathlib import Path

import httpx
from fastapi import FastAPI, HTTPException, Request
from fastapi.responses import FileResponse, JSONResponse
from pydantic import ValidationError

from guardrails import GuardrailState
from schemas import LlmStatus, StrategyEnvelope, StrategyRequest, StrategyResponse
from strategy import OllamaError, build_messages, call_ollama, clamp_radio, parse_response

logging.basicConfig(level=logging.INFO, format="%(asctime)s %(levelname)s %(name)s: %(message)s")
logger = logging.getLogger("agentic_racing")

# --- hand-off identity (DEMO_INTEGRATION.md) --------------------------------
# The ephemeral infra injects these two env vars per §2.2 of CLAUDE.md and
# docs/handoff.md. The app declares no secrets and is stateless, so it does
# nothing else with them — they exist purely so a log line can be correlated
# back to the provisioning event that created this pod.
PROJECT_ID = os.environ.get("PROJECT_ID", "unknown")
DEMO_SLOT = os.environ.get("DEMO_SLOT", "unknown")

# --- static serving (unchanged from Fase 0) --------------------------------

STATIC_DIR = Path(os.environ.get("STATIC_DIR", "../web")).resolve()

ENCODING_BY_SUFFIX = {".br": "br", ".gz": "gzip"}
EXTRA_CONTENT_TYPES = {
    ".wasm": "application/wasm",
    ".js": "application/javascript",
    ".data": "application/octet-stream",
    ".symbols.json": "application/octet-stream",
}

# --- strategist config ----------------------------------------------------

OLLAMA_URL = os.environ.get("OLLAMA_URL", "http://127.0.0.1:11434")
OLLAMA_MODEL = os.environ.get("OLLAMA_MODEL", "llama3.2:3b")
# Keep the model resident for the whole session so no call pays a reload and
# the KV-cache of each car's prefix survives between events (§6.7, §7.4). "24h"
# is well past the pod's ~60 min max life (§2.2) — NOT "-1": call_ollama sends
# this in the request body as a JSON string, and Ollama 400s on the string
# "-1" (it only accepts -1 as a bare number, or a duration like "5m").
OLLAMA_KEEP_ALIVE = os.environ.get("OLLAMA_KEEP_ALIVE", "24h")
# Timeout (§6.8): on expiry the client keeps its current directive. Sized for a
# CPU-only 3B — a schema-free JSON generation still runs ~20-30 s on a small box.
STRATEGY_TIMEOUT_S = float(os.environ.get("STRATEGY_TIMEOUT_S", "45"))
# §7.5: at most this many calls reach Ollama at once (1-2 on a CPU-only pod).
MAX_CONCURRENT = int(os.environ.get("STRATEGY_MAX_CONCURRENT", "1"))
# Circuit breaker (§7.1). The p95 threshold has to sit above the model's real
# latency on the target box, or a working-but-slow LLM is stuck "offline"; the
# radio just lags the race by that much (§2.5 accepts this).
BREAKER_P95_MS = int(os.environ.get("STRATEGY_BREAKER_P95_MS", "35000"))
BREAKER_COOLDOWN_S = float(os.environ.get("STRATEGY_BREAKER_COOLDOWN_S", "60"))


@asynccontextmanager
async def lifespan(app: FastAPI):
    logger.info(
        "agentic-racing starting: project_id=%s demo_slot=%s static_dir=%s",
        PROJECT_ID, DEMO_SLOT, STATIC_DIR,
    )
    app.state.http = httpx.AsyncClient()
    app.state.guard = GuardrailState(
        max_concurrent=MAX_CONCURRENT,
        breaker_p95_ms=BREAKER_P95_MS,
        breaker_cooldown_s=BREAKER_COOLDOWN_S,
    )
    app.state.ollama_probe = (0.0, False)  # (checked_at, reachable) — cached
    try:
        yield
    finally:
        await app.state.http.aclose()


app = FastAPI(lifespan=lifespan)


# --- strategist endpoint ------------------------------------------------------


def _envelope(
    guard: GuardrailState,
    *,
    status: str,
    strategy: StrategyResponse | None = None,
    reason: str | None = None,
    latency_ms: int = 0,
) -> JSONResponse:
    env = StrategyEnvelope(
        status=status,  # type: ignore[arg-type]
        strategy=strategy,
        reason=reason,
        latency_ms=latency_ms,
        llm=LlmStatus(**guard.snapshot()),
    )
    return JSONResponse(env.model_dump())


@app.post("/api/strategy")
async def strategy(req: StrategyRequest, request: Request) -> JSONResponse:
    guard: GuardrailState = request.app.state.guard

    client_ip = request.client.host if request.client else "unknown"
    if not guard.allow_ip(client_ip):
        return _envelope(guard, status="fallback", reason="rate_limited")

    # Circuit breaker open: don't even touch Ollama (§7.1). Not an error.
    if guard.offline:
        return _envelope(guard, status="fallback", reason="offline")

    slot = await guard.acquire_slot()
    if slot is None:
        return _envelope(guard, status="fallback", reason="busy")

    known_ids = (
        {req.context.car_id, req.telemetry.me.car_id}
        | {r.car_id for r in req.telemetry.rivals}
    )
    messages = build_messages(req.context, req.telemetry)

    async with slot:
        guard.calls += 1
        try:
            content, latency_ms = await call_ollama(
                request.app.state.http,
                base_url=OLLAMA_URL,
                model=OLLAMA_MODEL,
                keep_alive=OLLAMA_KEEP_ALIVE,
                messages=messages,
                timeout_s=STRATEGY_TIMEOUT_S,
            )
        except OllamaError as exc:
            guard.record_failure()
            return _envelope(guard, status="fallback", reason=f"ollama_error: {exc}"[:200])

        guard.record_success(latency_ms)

        try:
            parsed = parse_response(content, known_ids)
        except (ValidationError, ValueError) as exc:
            # Discard the whole reply, keep the current directive (§6.8).
            guard.rejected += 1
            return _envelope(
                guard,
                status="fallback",
                reason="rejected",
                latency_ms=int(latency_ms),
            )

        parsed.radio = clamp_radio(parsed.radio)
        return _envelope(
            guard, status="ok", strategy=parsed, latency_ms=int(latency_ms)
        )


# --- health / heartbeat ----------------------------------------------------


async def _ollama_reachable(request: Request) -> bool:
    """Cheap cached liveness probe of the sidecar (re-checked every ~5 s)."""

    checked_at, ok = request.app.state.ollama_probe
    now = time.monotonic()
    if now - checked_at < 5.0:
        return ok
    try:
        resp = await request.app.state.http.get(
            f"{OLLAMA_URL}/api/version", timeout=2.0
        )
        ok = resp.status_code == 200
    except httpx.HTTPError:
        ok = False
    request.app.state.ollama_probe = (now, ok)
    return ok


@app.get("/api/health")
async def health(request: Request) -> JSONResponse:
    guard: GuardrailState = request.app.state.guard
    return JSONResponse(
        {
            "status": "ok",
            "llm": guard.snapshot(),
            "ollama_reachable": await _ollama_reachable(request),
            "static_dir": str(STATIC_DIR),
            "project_id": PROJECT_ID,
            "demo_slot": DEMO_SLOT,
        }
    )


@app.get("/api/ping")
async def ping() -> JSONResponse:
    # The client hits this on a timer while a race is running so the ephemeral
    # infra doesn't tear the pod down for inactivity (~8 min) mid-race (§2.2).
    return JSONResponse({"ok": True, "ts": time.time()})


# --- static files (must be registered last: it owns "/{path:path}") ---------


def _resolve_content_type(inner_name: str) -> str:
    for ext, content_type in EXTRA_CONTENT_TYPES.items():
        if inner_name.endswith(ext):
            return content_type
    guessed, _ = mimetypes.guess_type(inner_name)
    return guessed or "application/octet-stream"


# The shell (index.html, app.js, overlay.js, style.css) is tiny and changes
# often; the browser must not serve a stale ES module after a redeploy. The big
# Unity Build/*.br assets are effectively immutable per build, so let them cache.
_NO_CACHE_SUFFIXES = {".html", ".js", ".css"}


def _serve(file_path: Path) -> FileResponse:
    headers: dict[str, str] = {}
    content_type_source = file_path.name

    encoding = ENCODING_BY_SUFFIX.get(file_path.suffix)
    if encoding:
        headers["Content-Encoding"] = encoding
        content_type_source = file_path.name[: -len(file_path.suffix)]

    if file_path.suffix in _NO_CACHE_SUFFIXES:
        headers["Cache-Control"] = "no-cache"

    content_type = _resolve_content_type(content_type_source)
    return FileResponse(file_path, media_type=content_type, headers=headers)


@app.get("/{path:path}")
def serve_static(path: str) -> FileResponse:
    requested = path if path else "index.html"
    file_path = (STATIC_DIR / requested).resolve()

    # Guard against path traversal outside STATIC_DIR.
    if STATIC_DIR not in file_path.parents and file_path != STATIC_DIR:
        raise HTTPException(status_code=404)
    if not file_path.is_file():
        raise HTTPException(status_code=404)

    return _serve(file_path)
