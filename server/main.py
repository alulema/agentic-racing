"""FastAPI server: serves the Unity WebGL build as static files.

Unity WebGL builds pre-compress their heavy assets (.framework.js, .wasm, .data)
into .br (Brotli) or .gz (gzip) files and reference those exact filenames from
index.html. A generic static file server has no idea these are compressed —
it serves the raw compressed bytes without a `Content-Encoding` header, so the
browser doesn't decompress them and Unity fails to parse the file. This module
exists specifically to set those headers correctly. See CLAUDE.md section 11
("Headers de Unity WebGL") for the failure this avoids.
"""

import mimetypes
import os
from pathlib import Path
from typing import Literal

import anthropic
from fastapi import FastAPI, HTTPException
from fastapi.responses import FileResponse
from pydantic import BaseModel

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

# CLAUDE.md sección 2: decisión cerrada, no cambiar sin avisar.
ANTHROPIC_MODEL = "claude-haiku-4-5"


class StrategySmokeTestResponse(BaseModel):
    """Placeholder de Fase 0: solo valida que el proxy funciona end-to-end
    (key desde env, llamada en vivo, JSON parseado contra un esquema). El
    payload/esquema real de telemetría es Fase 4 — ver CLAUDE.md sección
    6.3/6.4."""

    directive: Literal["attack", "defend", "conserve", "push"]
    radio: str


@app.get("/api/strategy")
def strategy_smoke_test() -> StrategySmokeTestResponse:
    api_key = os.environ.get("ANTHROPIC_API_KEY")
    if not api_key:
        # Guardrail de costo (CLAUDE.md sección 7, punto 6): la key solo vive
        # en env de runtime. Sin ella, no reventamos la carrera — devolvemos
        # un error claro; la Fase 4 le agrega el fallback heurístico.
        raise HTTPException(status_code=503, detail="ANTHROPIC_API_KEY not set")

    client = anthropic.Anthropic(api_key=api_key)
    response = client.messages.parse(
        model=ANTHROPIC_MODEL,
        max_tokens=200,  # el output es el lado caro; acotado por diseño (sección 7, punto 3)
        messages=[
            {
                "role": "user",
                "content": (
                    "You are a race engineer radioing your driver over the team radio. "
                    "Car is P3, gap ahead 1.2s, gap behind 4.0s, lap 3 of 5. "
                    "Give a short radio call (max 15 words) and a directive."
                ),
            }
        ],
        output_format=StrategySmokeTestResponse,
    )
    return response.parsed_output


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
