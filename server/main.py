"""FastAPI server: serves the Unity WebGL build as static files, and proxies
`/api/strategy` to the local Ollama sidecar (CLAUDE.md section 2.5).

Unity WebGL builds pre-compress their heavy assets (.framework.js, .wasm, .data)
into .br (Brotli) or .gz (gzip) files and reference those exact filenames from
index.html. A generic static file server has no idea these are compressed —
it serves the raw compressed bytes without a `Content-Encoding` header, so the
browser doesn't decompress them and Unity fails to parse the file. This module
exists specifically to set those headers correctly. See CLAUDE.md section 11
("Headers de Unity WebGL") for the failure this avoids.
"""

import json
import mimetypes
import os
from pathlib import Path
from typing import Literal

import httpx
from fastapi import FastAPI, HTTPException
from fastapi.responses import FileResponse
from pydantic import BaseModel, ValidationError

# Root directory of static assets to serve (the Unity WebGL build output).
# Overridable via env var so we can point at a throwaway test build today
# and at the real build's copied-in location once Docker exists.
STATIC_DIR = Path(os.environ.get("STATIC_DIR", "../web")).resolve()

# Content-Encoding implied by these suffixes, and how to recover the real
# content type: strip the suffix and guess from what's left.
ENCODING_BY_SUFFIX = {
    ".br": "br",
    ".gz": "gzip",
}

# mimetypes doesn't reliably know about these on every platform/Python
# version, so pin the ones Unity WebGL builds actually produce.
EXTRA_CONTENT_TYPES = {
    ".wasm": "application/wasm",
    ".js": "application/javascript",
    ".data": "application/octet-stream",
    ".symbols.json": "application/octet-stream",
}

app = FastAPI()

# CLAUDE.md sección 2.5: el estratega corre en un modelo local servido por un
# sidecar Ollama. Sin API externa, sin secretos. OLLAMA_URL es overridable para
# apuntar a un Ollama del host durante el desarrollo; en la imagen el sidecar
# escucha en 127.0.0.1:11434.
OLLAMA_URL = os.environ.get("OLLAMA_URL", "http://127.0.0.1:11434")
OLLAMA_MODEL = os.environ.get("OLLAMA_MODEL", "llama3.2:3b")

# Tope duro de tokens de salida (CLAUDE.md sección 7, punto 3): acota latencia.
STRATEGY_NUM_PREDICT = 150
# Timeout corto (CLAUDE.md sección 6.8): vencido, en Fase 4 sigue la directiva
# vigente. En Fase 0 devolvemos 503 para que el smoke test lo note.
STRATEGY_TIMEOUT_S = 30.0


class StrategySmokeTestResponse(BaseModel):
    """Placeholder de Fase 0: solo valida que el proxy funciona end-to-end
    (sidecar Ollama alcanzable, respuesta JSON parseada contra un esquema). El
    payload/esquema real de telemetría es Fase 4 — ver CLAUDE.md sección
    6.3/6.4."""

    directive: Literal["attack", "defend", "conserve", "push"]
    radio: str


_SMOKE_TEST_PROMPT = (
    "You are a race engineer radioing your driver over the team radio. "
    "Car is P3, gap ahead 1.2s, gap behind 4.0s, lap 3 of 5. "
    "Give a short radio call (max 15 words) and a directive."
)


@app.get("/api/strategy")
def strategy_smoke_test() -> StrategySmokeTestResponse:
    # Ollama structured output: pasar el JSON Schema como `format` fuerza al
    # modelo a emitir exactamente esa forma (mejor adherencia a enums que
    # format="json" a secas, que importa con un modelo 3B — CLAUDE.md §6.8).
    payload = {
        "model": OLLAMA_MODEL,
        "messages": [{"role": "user", "content": _SMOKE_TEST_PROMPT}],
        "stream": False,
        "format": StrategySmokeTestResponse.model_json_schema(),
        "options": {"num_predict": STRATEGY_NUM_PREDICT, "temperature": 0.4},
        "keep_alive": "30m",  # mantener el modelo caliente entre llamadas (§6.7)
    }

    try:
        resp = httpx.post(
            f"{OLLAMA_URL}/api/chat", json=payload, timeout=STRATEGY_TIMEOUT_S
        )
        resp.raise_for_status()
    except httpx.HTTPError as exc:
        # Sidecar caído / timeout: en Fase 4 esto dispara el fallback heurístico
        # (CLAUDE.md §6.8). En Fase 0 lo reportamos.
        raise HTTPException(
            status_code=503, detail=f"ollama unreachable: {exc}"
        ) from exc

    content = resp.json().get("message", {}).get("content", "")
    try:
        return StrategySmokeTestResponse.model_validate_json(content)
    except (ValidationError, json.JSONDecodeError) as exc:
        # Respuesta que no valida contra el esquema: se descarta entera, no se
        # parchea (CLAUDE.md §6.8). En Fase 0 es un fallo del smoke test.
        raise HTTPException(
            status_code=502,
            detail=f"model output failed schema validation: {content[:200]}",
        ) from exc


def _resolve_content_type(inner_name: str) -> str:
    for ext, content_type in EXTRA_CONTENT_TYPES.items():
        if inner_name.endswith(ext):
            return content_type
    guessed, _ = mimetypes.guess_type(inner_name)
    return guessed or "application/octet-stream"


def _serve(file_path: Path) -> FileResponse:
    headers = {}
    content_type_source = file_path.name

    encoding = ENCODING_BY_SUFFIX.get(file_path.suffix)
    if encoding:
        headers["Content-Encoding"] = encoding
        # e.g. "web-test.framework.js.br" -> "web-test.framework.js"
        content_type_source = file_path.name[: -len(file_path.suffix)]

    content_type = _resolve_content_type(content_type_source)
    return FileResponse(file_path, media_type=content_type, headers=headers)


@app.get("/{path:path}")
def serve_static(path: str) -> FileResponse:
    # Default to index.html for the root and any path with no filename.
    requested = path if path else "index.html"

    file_path = (STATIC_DIR / requested).resolve()

    # Guard against path traversal outside STATIC_DIR.
    if STATIC_DIR not in file_path.parents and file_path != STATIC_DIR:
        raise HTTPException(status_code=404)

    if not file_path.is_file():
        raise HTTPException(status_code=404)

    return _serve(file_path)
