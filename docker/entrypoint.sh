#!/bin/sh
# Supervise the two processes of the demo container (CLAUDE.md section 2.5):
#   1. ollama serve  -> local LLM, loopback only (127.0.0.1:11434)
#   2. uvicorn        -> FastAPI, the only externally-reachable port (8080)
#
# The environment is torn down abruptly (CLAUDE.md section 2.2), so this stays
# deliberately simple: start ollama in the background, wait until it answers,
# warm the model, then exec uvicorn as PID 1 so it receives signals directly.
# If ollama dies later, /api/strategy falls back to a heuristic directive
# (Fase 4) — the race never breaks on an LLM problem.
set -e

OLLAMA_MODEL="${OLLAMA_MODEL:-llama3.2:3b}"

echo "[entrypoint] starting ollama serve..."
ollama serve &

# Readiness probe: Ollama is up when /api/version responds.
i=0
until curl -sf http://127.0.0.1:11434/api/version >/dev/null 2>&1; do
    i=$((i + 1))
    if [ "$i" -gt 60 ]; then
        echo "[entrypoint] ollama did not become ready in 60s" >&2
        exit 1
    fi
    sleep 1
done
echo "[entrypoint] ollama ready."

# Warm the model so the first real request isn't the one that pays the load
# cost. keep_alive matches what /api/strategy sends.
curl -sf http://127.0.0.1:11434/api/chat \
    -d "{\"model\":\"${OLLAMA_MODEL}\",\"messages\":[{\"role\":\"user\",\"content\":\"ok\"}],\"stream\":false,\"keep_alive\":\"30m\"}" \
    >/dev/null 2>&1 || echo "[entrypoint] model warm-up call failed (non-fatal)" >&2

echo "[entrypoint] starting uvicorn on 0.0.0.0:8080..."
exec uvicorn main:app --host 0.0.0.0 --port 8080
