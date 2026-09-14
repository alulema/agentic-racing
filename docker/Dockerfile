# Single runtime image, two processes (CLAUDE.md §2.5):
#   - uvicorn / FastAPI  -> serves the Unity Web build + /api/* on 0.0.0.0:8080
#   - ollama serve       -> local LLM for the strategist, on 127.0.0.1:11434
# A tiny entrypoint supervises both. The llama3.2:3b weights are baked in at
# build time so container startup needs no network and is deterministic.
#
# Multi-stage so the CUDA/ROCm/Vulkan runtimes the Ollama installer ships
# (~2 GB, useless on a CPU-only pod) never reach the final image. Fase 0
# measured a single-stage build at ~8.3 GB; the app image (stage 3, the
# default build target) lands around ~2.4 GB (base + python deps + ollama
# CPU libs + the ~1.9 GB model + the web build). Stage 2 is an optional
# ollama-only sidecar image, built separately with `--target ollama-sidecar`
# (Fase 6.3, see below) — same baked model, no Unity/Python app on top.
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

# ---- stage 2: ollama-only sidecar (optional extra capacity, Fase 6.3) ------
# A second, independent Ollama engine — same baked model, reused from stage 1
# so it costs no extra build time. Declared as a "recurso extra" in the
# hand-off manifest (docs/handoff.md), same pattern DEMO_INTEGRATION.md shows
# for a Postgres+pgvector sidecar. Not required to run the demo: the app
# defaults to its own loopback Ollama alone (OLLAMA_URLS unset) and works
# exactly as before. When this sidecar IS provisioned alongside the app
# container on the same internal network, the proxy load-balances strategy
# calls across both (see server/guardrails.py) — the fix for the finding in
# docs/Devlog.md 2026-09-14: raising the proxy's admission gate alone
# (STRATEGY_MAX_CONCURRENT=3) just queues more calls behind ONE
# single-threaded Ollama and makes things worse. Two real engines is the
# only way to get real additional throughput without touching the model.
#
# Built via `docker build --target ollama-sidecar` (CI does this in its own
# job, independent of the Unity build this stage doesn't need). NOT the
# default target — a plain `docker build .` still produces the app image
# (the last stage below), so nothing about the single-container path changes.
FROM debian:bookworm-slim AS ollama-sidecar

RUN apt-get update \
    && apt-get install -y --no-install-recommends curl ca-certificates \
    && rm -rf /var/lib/apt/lists/*

COPY --from=ollama-build /usr/local/bin/ollama /usr/local/bin/ollama
COPY --from=ollama-build /usr/local/lib/ollama /usr/local/lib/ollama
COPY --from=ollama-build /root/.ollama /root/.ollama

# Unlike the app container's baked-in Ollama (loopback-only, §2.5), this one
# IS the container's whole job, so it listens on all interfaces — the app
# container reaches it by service/hostname over the pod's internal network,
# never exposed past that (same "ingress interno" boundary as the app image,
# just one hop further in).
ENV OLLAMA_HOST=0.0.0.0:11434 \
    OLLAMA_KEEP_ALIVE=24h \
    OLLAMA_NUM_PARALLEL=1 \
    OLLAMA_MAX_LOADED_MODELS=1 \
    OLLAMA_NUM_THREAD=3
EXPOSE 11434
CMD ["ollama", "serve"]

# ---- stage 3: the runtime image -------------------------------------------
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
# just fights uvicorn for CPU. keep_alive 24h: past the pod's ~60 min max life
# (§2.2) so the model never evicts — a plain duration string, since the proxy
# sends the same value in the request body and Ollama 400s on the string "-1".
# OLLAMA_NUM_THREAD leaves headroom for uvicorn + the OS — set to (pod vCPU - 1):
# 3 on a 4-vCPU pod, 1 on a 2-vCPU pod. Overridable at provision time.
ENV OLLAMA_KEEP_ALIVE=24h \
    OLLAMA_NUM_PARALLEL=1 \
    OLLAMA_MAX_LOADED_MODELS=1 \
    OLLAMA_NUM_THREAD=3
EXPOSE 8080

COPY docker/entrypoint.sh /usr/local/bin/entrypoint.sh
RUN chmod +x /usr/local/bin/entrypoint.sh
CMD ["/usr/local/bin/entrypoint.sh"]
