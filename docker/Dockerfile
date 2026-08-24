# Single-stage image: FastAPI serves the pre-built Unity Web (WebGL) output as
# static files, with correct Content-Encoding for Unity's .br/.gz assets, plus
# the API endpoints. No Unity/.NET runtime inside — Unity is only a build-time
# dependency (CI produces the static output; this image just serves it).
FROM python:3.13-slim

WORKDIR /app

COPY server/requirements.txt .
RUN pip install --no-cache-dir -r requirements.txt

COPY server/ .

# The Unity Web build output. In CI this is populated by the GameCI build job
# before `docker build` runs (see .github/workflows/). Locally, point it at a
# throwaway test build with STATIC_DIR — see README for the Fase 0 spike.
COPY web/ /app/static/
ENV STATIC_DIR=/app/static

EXPOSE 8080

CMD ["uvicorn", "main:app", "--host", "0.0.0.0", "--port", "8080"]
