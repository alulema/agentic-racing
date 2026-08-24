# Devlog — agentic-racing

Bitácora interna cronológica. Ver `CLAUDE.md` sección 7 para el rol de este documento
frente a `README.md` (manual de réplica público) y `window.DEMO_INFO` (panel in-demo).

---

## 2026-08-21 — Kickoff, lectura de contrato, plan de Fase 0

**Estado del repo**: solo `CLAUDE.md` y `DEMO_INTEGRATION.md`. Ningún código escrito
todavía — Fase 0 no ha empezado.

**Actividad**:
- Lectura completa de `CLAUDE.md` y `DEMO_INTEGRATION.md` (contrato de integración con
  la infra efímera de alexisalulema.com).
- No se encontraron contradicciones duras entre ambos documentos. Puntos aclarados y
  dados por cerrados:
  - La excepción a "sin tecnología Microsoft" para Unity (motor de terceros, IL2CPP →
    WASM, sin runtime .NET en el contenedor) se acepta tal como está justificada en
    `CLAUDE.md` §2.2.
  - `/api/strategy` no necesita usar streaming (WS/SSE) del gateway — el diseño de
    §6.6 es petición/respuesta JSON simple por evento con timeout corto.
  - Posible fricción a vigilar en Fase 0 / CI: el paquete en GHCR puede nacer privado
    en el primer push y requerir un paso manual (o `gh api`) para marcarlo público.
- Confirmado el entendimiento de la sección 8: entrenamiento (`mlagents-learn`),
  provisión/desasignación de la VM, activación de licencia Unity para CI (`.alf`→`.ulf`
  como secret), tope de presupuesto en Anthropic, y el hand-off manifest final a la
  infra son tareas del humano. El agente prepara todo lo demás (código, configs,
  workflows, PRs) sin pedir permiso paso a paso.

**Plan de Fase 0 propuesto** (pendiente de confirmación antes de escribir código):
1. Unity vacío → build WebGL con rutas relativas, sanity check con servidor estático
   local simple.
2. Modelo ONNX de juguete (3→2) cargado con `com.unity.ai.inference` corriendo
   inferencia dentro de ese build WebGL — se prioriza temprano por ser el mayor riesgo
   arquitectónico (si falla, cae la premisa de "inferencia en cliente").
3. Interop DOM ↔ Unity en ambas direcciones (`.jslib`) + overlay enlazando
   `demo-theme.css`.
4. FastAPI sirviendo el build con headers correctos `.br`/`.gz`, local con uvicorn.
5. Dockerizar y validar `docker run -p 8080:8080 -e PROJECT_ID=... -e DEMO_SLOT=...`.
6. `/api/strategy` con Anthropic (`claude-haiku-4-5`), key leída de env, JSON validado.
7. CI (GitHub Actions + GameCI) → build WebGL → imagen → push a GHCR público. Al final
   porque depende de que el humano active la licencia Unity para CI; si el secret no
   está listo, este ítem queda documentado como bloqueado en vez de improvisar un
   rodeo.

**Próximo paso**: esperando confirmación del plan de Fase 0 para empezar por el punto 1.
