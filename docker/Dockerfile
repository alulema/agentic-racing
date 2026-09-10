# Single runtime image, two processes (CLAUDE.md §2.5):
#   - uvicorn / FastAPI  -> serves the Unity Web build + /api/* on 0.0.0.0:8080
#   - ollama serve       -> local LLM for the strategist, on 127.0.0.1:11434
# A tiny entrypoint supervises both. The llama3.2:3b weights are baked in at
# build time so container startup needs no network and is deterministic.
#
# Two stages so the CUDA/ROCm/Vulkan runtimes the Ollama installer ships
# (~2 GB, useless on a CPU-only pod) never reach the final image. Fase 0
# measured a single-stage build at ~8.3 GB; this lands around ~2.4 GB
# (base + python deps + ollama CPU libs + the ~1.9 GB model + the web build).
#
# No Unity/.NET runtime inside: Unity is a build-time dependency only (CI
# produces the static Web output; this image just serves it).

# ---- stage 1: install Ollama, bake the model, drop the GPU runtimes ----------
FROM python:3.13-slim AS ollama-build

# zstd: the Ollama install tarball is zstd-compressed and the script aborts
# without it. curl + ca-certificates to fetch the installer.
RUN apt-get update \
    && apt-get install -y --no-install-recommends curl ca-certificates zstd \
    && rm -rf /var/lib/apt/lists/*

# The install script detects "no systemd" in a container and just drops the
# binary + runtime libs under /usr/local/{bin,lib}/ollama.
RUN curl -fsSL https://ollama.com/install.sh | sh

# Bake the model into /root/.ollama: start ollama, pull, stop.
ARG OLLAMA_MODEL=llama3.2:3b
RUN ollama serve & \
    pid=$! ; \
    until curl -sf http://127.0.0.1:11434/api/version >/dev/null 2>&1 ; do sleep 1 ; done ; \
    ollama pull "${OLLAMA_MODEL}" ; \
    kill "${pid}"

# CPU-only pod: the CUDA/ROCm/Vulkan backends are dead weight (~2 GB).
RUN rm -rf /usr/local/lib/ollama/cuda_v* \
           /usr/local/lib/ollama/rocm* \
           /usr/local/lib/ollama/vulkan \
    && du -sh /usr/local/lib/ollama /root/.ollama

# ---- stage 2: the runtime image -------------------------------------------
FROM python:3.13-slim

# curl + ca-certificates only: the entrypoint's readiness probe uses curl.
# (No zstd here — that was just for the installer in stage 1.)
RUN apt-get update \
    && apt-get install -y --no-install-recommends curl ca-certificates \
    && rm -rf /var/lib/apt/lists/*

# Ollama: the CPU binary + its runtime .so libs + the baked model.
COPY --from=ollama-build /usr/local/bin/ollama /usr/local/bin/ollama
COPY --from=ollama-build /usr/local/lib/ollama /usr/local/lib/ollama
COPY --from=ollama-build /root/.ollama /root/.ollama

WORKDIR /app

COPY server/requirements.txt .
RUN pip install --no-cache-dir -r requirements.txt

COPY server/ .

# The Unity Web build output. In CI this is populated by the GameCI build job
# before `docker build` runs (see .github/workflows/). Locally, point STATIC_DIR
# at a throwaway test build — see README / docs.
COPY web/ /app/static/
ENV STATIC_DIR=/app/static

# Sidecar binds loopback only; the gateway reaches uvicorn on 8080.
ENV OLLAMA_HOST=127.0.0.1:11434
# Pod tuning (CLAUDE.md §5, §6.7, §7). One model, one in-flight request at a
# time — the proxy already serialises calls (§7.5), so extra Ollama parallelism
# just fights uvicorn for CPU. keep_alive=-1: never evict the model within the
# pod's lifetime (~60 min max, §2.2). OLLAMA_NUM_THREAD leaves headroom for
# uvicorn + the OS — set to (pod vCPU - 1): 3 on a 4-vCPU pod, 1 on a 2-vCPU
# pod. Overridable at provision time.
ENV OLLAMA_KEEP_ALIVE=-1 \
    OLLAMA_NUM_PARALLEL=1 \
    OLLAMA_MAX_LOADED_MODELS=1 \
    OLLAMA_NUM_THREAD=3
EXPOSE 8080

COPY docker/entrypoint.sh /usr/local/bin/entrypoint.sh
RUN chmod +x /usr/local/bin/entrypoint.sh
CMD ["/usr/local/bin/entrypoint.sh"]
