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
# the KV-cache of each car's prefix survives between events (§6.7, §7.4).
OLLAMA_KEEP_ALIVE = os.environ.get("OLLAMA_KEEP_ALIVE", "25m")
# Short timeout (§6.8): on expiry the client keeps its current directive.
STRATEGY_TIMEOUT_S = float(os.environ.get("STRATEGY_TIMEOUT_S", "30"))
# §7.5: at most this many calls reach Ollama at once (1-2 on a CPU-only pod).
MAX_CONCURRENT = int(os.environ.get("STRATEGY_MAX_CONCURRENT", "1"))


@asynccontextmanager
async def lifespan(app: FastAPI):
    app.state.http = httpx.AsyncClient()
    app.state.guard = GuardrailState(max_concurrent=MAX_CONCURRENT)
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
