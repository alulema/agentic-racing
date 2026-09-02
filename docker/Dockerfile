# Single image, two processes (CLAUDE.md section 2.5):
#   - uvicorn / FastAPI  -> serves the Unity Web build + /api/* on 0.0.0.0:8080
#   - ollama serve       -> local LLM for the strategist, on 127.0.0.1:11434
# A tiny entrypoint supervises both. The llama3.2:3b weights are baked in at
# build time so container startup needs no network and is deterministic.
# Measured cost (Fase 0, 2026-09-01): final image ~8.3 GB — ~2.0 GB model
# + ~2.25 GB for the Ollama install (bundles ROCm/CUDA libs unused on a CPU-only
# pod). Slimming that is Fase 5 work (CLAUDE.md section 11).
#
# No Unity/.NET runtime inside: Unity is a build-time dependency only (CI produces
# the static Web output; this image just serves it).
FROM python:3.13-slim

# curl + ca-certificates: needed to install Ollama and for the entrypoint's
# readiness probe (kept in the final image — entrypoint uses curl).
# zstd: the Ollama install script ships its tarball zstd-compressed and aborts
# with "This version requires zstd for extraction" if the binary is missing.
RUN apt-get update \
    && apt-get install -y --no-install-recommends curl ca-certificates zstd \
    && rm -rf /var/lib/apt/lists/*

# Ollama: the install script detects "no systemd" in a container and just drops
# the binary in /usr/local/bin.
RUN curl -fsSL https://ollama.com/install.sh | sh

WORKDIR /app

COPY server/requirements.txt .
RUN pip install --no-cache-dir -r requirements.txt

COPY server/ .

# Bake the model: start ollama, pull, stop. The weights land in /root/.ollama
# and stay in this layer.
ARG OLLAMA_MODEL=llama3.2:3b
ENV OLLAMA_MODEL=${OLLAMA_MODEL}
RUN ollama serve & \
    pid=$! ; \
    until curl -sf http://127.0.0.1:11434/api/version >/dev/null 2>&1 ; do sleep 1 ; done ; \
    ollama pull "${OLLAMA_MODEL}" ; \
    kill "${pid}"

# The Unity Web build output. In CI this is populated by the GameCI build job
# before `docker build` runs (see .github/workflows/). Locally, point STATIC_DIR
# at a throwaway test build — see README / docs for the Fase 0 spike.
COPY web/ /app/static/
ENV STATIC_DIR=/app/static

# Sidecar binds loopback only; the gateway reaches uvicorn on 8080.
ENV OLLAMA_HOST=127.0.0.1:11434
EXPOSE 8080

COPY docker/entrypoint.sh /usr/local/bin/entrypoint.sh
RUN chmod +x /usr/local/bin/entrypoint.sh
CMD ["/usr/local/bin/entrypoint.sh"]
