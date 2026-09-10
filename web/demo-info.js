/* "About this demo" panel content (see DEMO_INTEGRATION.md). Loaded before
 * demo-panel.js; purely additive — if the widget script fails to load the demo
 * is unaffected. Bilingual: every field has an `…Es` sibling. */
window.DEMO_INFO = {
  title: "Agentic Racing",
  titleEs: "Agentic Racing",

  overview:
    "Six mechanically identical cars race a closed circuit. Each car is a two-tier agent: a frame-rate pilot that maps observations to steering/throttle/brake, and an event-driven LLM team boss that reads race telemetry and issues a high-level directive that modulates how the pilot drives. The boss's reasoning is shown live, F1 team-radio style — that's the point of the demo, not the graphics. Fully self-hosted: the strategist runs on a local llama3.2:3b via an Ollama sidecar, no external LLM API.",
  overviewEs:
    "Seis autos mecánicamente idénticos corren un circuito cerrado. Cada auto es un agente de dos niveles: un piloto a frecuencia de frame que mapea observaciones a volante/acelerador/freno, y un jefe de equipo LLM por evento que lee la telemetría y emite una directiva de alto nivel que modula cómo conduce el piloto. El razonamiento del jefe se muestra en vivo, estilo radio de equipo de F1 — ese es el punto del demo, no los gráficos. Totalmente autohospedado: el estratega corre en un llama3.2:3b local vía un sidecar Ollama, sin API de LLM externa.",

  architecture: {
    description:
      "The browser runs the whole Unity WebGL simulation — track, cars, pilots, and a DOM overlay for the HUD and team-radio feed. Each car has its own strategist (no central brain). On a race event (lap done, rival closing, position change, incident, final lap) the strategist POSTs a compact telemetry payload to /api/strategy. The FastAPI app proxies it to the local Ollama sidecar with forced-JSON output and a per-car stable prompt prefix (KV-cache reuse), validates the reply against a fixed schema, and returns one directive. Load guardrails sit in front: a global concurrency gate, a circuit breaker that falls back to a fixed heuristic strategy when the LLM is slow or failing, and a per-IP rate limit. The race never waits on the LLM — until a reply lands the car keeps its current directive.",
    descriptionEs:
      "El navegador corre toda la simulación Unity WebGL — pista, autos, pilotos, y un overlay DOM para el HUD y el panel de radio. Cada auto tiene su propio estratega (sin cerebro central). Ante un evento de carrera (fin de vuelta, rival acercándose, cambio de posición, incidente, última vuelta) el estratega hace POST de una telemetría compacta a /api/strategy. La app FastAPI la reenvía al sidecar Ollama local con salida JSON forzada y un prefijo de prompt estable por auto (reuso de KV-cache), valida la respuesta contra un esquema fijo, y devuelve una directiva. Delante hay guardrails de carga: una compuerta de concurrencia global, un cortacircuitos que cae a una estrategia heurística fija cuando el LLM va lento o falla, y un rate limit por IP. La carrera nunca espera al LLM — hasta que llega la respuesta el auto sigue con su directiva vigente.",
    diagram: `flowchart LR
  subgraph B["Browser"]
    UNITY["Unity WebGL sim<br/>6 cars: pilot + per-car strategist"]
    OVL["DOM overlay<br/>HUD + team radio"]
    UNITY <--> OVL
  end
  B -->|"POST /api/strategy<br/>(telemetry, per race event)"| APP
  B -.->|"GET /api/ping (heartbeat)"| APP
  subgraph POD["Ephemeral pod (~4 vCPU / 8 GiB)"]
    APP["FastAPI proxy<br/>guardrails: concurrency gate,<br/>circuit breaker, rate limit"]
    APP -->|"/api/chat, format=json,<br/>schema-validated reply"| OLL["Ollama<br/>llama3.2:3b (CPU)"]
  end
  APP -.->|"fallback: fixed heuristic<br/>directive when LLM slow/down"| B`,
  },

  infra: [
    {
      name: "FastAPI app",
      role: "Serves the Unity WebGL build (with the right headers for Brotli assets) and proxies /api/strategy to the LLM. Stateless.",
      roleEs: "Sirve el build Unity WebGL (con los headers correctos para los assets Brotli) y hace de proxy de /api/strategy al LLM. Sin estado.",
    },
    {
      name: "Ollama sidecar — llama3.2:3b",
      role: "Local LLM for the strategist, loopback only. Weights baked into the image, so startup needs no network. No external API.",
      roleEs: "LLM local para el estratega, solo loopback. Pesos horneados en la imagen, así el arranque no necesita red. Sin API externa.",
    },
    {
      name: "GHCR",
      role: "Public image (app + sidecar in one), pulled fresh on each provision.",
      roleEs: "Imagen pública (app + sidecar en una sola), traída fresca en cada provisión.",
    },
  ],

  sizing:
    "One ephemeral pod, CPU biased to inference: OLLAMA_NUM_THREAD ≈ (vCPU − 1), one model, one in-flight request (the proxy serialises), keep_alive 24h so the model never reloads. CPU-only tuning: 3B model, num_predict 150, format=\"json\" (not full-schema-constrained decoding) → ~7 s per strategy call warm, ~20 s on the first cold call. Image ~4.5 GB, of which ~2 GB is the baked model.",
  sizingEs:
    "Un pod efímero, CPU sesgada a la inferencia: OLLAMA_NUM_THREAD ≈ (vCPU − 1), un modelo, una petición en vuelo (el proxy serializa), keep_alive 24h para que el modelo no se recargue. Ajuste solo-CPU: modelo 3B, num_predict 150, format=\"json\" (sin decodificación restringida por el esquema completo) → ~7 s por llamada de estrategia en caliente, ~20 s en la primera llamada en frío. Imagen ~4.5 GB, de los cuales ~2 GB son el modelo horneado.",

  design: [
    "Fully self-hosted, no external LLM API — no per-token bill, no third-party dependency; showcases an OSS stack.",
    "Two tiers with deliberate information asymmetry: the pilot sees only its local surroundings and the current directive; the strategist sees the full classification, gaps and lap history but nothing frame-by-frame. That gap is why the pilot needs the strategist.",
    "The directive is an observation channel the pilot is conditioned on — so an aggressive move emerges from how the pilot drives, it isn't post-processed onto a fixed policy.",
    "The race never blocks on the LLM: calls are async with a per-car cooldown, and a fixed heuristic strategy takes over the moment the LLM is slow, fails, or the load breaker trips.",
    "Discrete directive levels (low/medium/high), not 0–1 floats — small models are inconsistent on continuous scales, and discrete levels are reproducible and readable.",
    "Honest: calls that time out, get rejected by schema validation, or fall back to the heuristic are shown in the radio feed and counted in the status chip, not hidden.",
  ],
  designEs: [
    "Totalmente autohospedado, sin API de LLM externa — sin factura por token, sin dependencia de terceros; muestra un stack OSS.",
    "Dos niveles con asimetría de información deliberada: el piloto solo ve su entorno local y la directiva vigente; el estratega ve la clasificación completa, los gaps y el historial de vueltas pero nada frame a frame. Esa brecha es por lo que el piloto necesita al estratega.",
    "La directiva es un canal de observación al que el piloto está condicionado — así un adelantamiento agresivo emerge de cómo conduce el piloto, no se post-procesa sobre una política fija.",
    "La carrera nunca se bloquea por el LLM: las llamadas son asíncronas con cooldown por auto, y una estrategia heurística fija toma el control apenas el LLM va lento, falla, o salta el cortacircuitos de carga.",
    "Niveles de directiva discretos (bajo/medio/alto), no flotantes 0–1 — los modelos chicos son inconsistentes en escalas continuas, y los niveles discretos son reproducibles y legibles.",
    "Honesto: las llamadas que expiran, que descarta la validación de esquema, o que caen a la heurística se muestran en el panel de radio y se cuentan en el chip de estado, no se ocultan.",
  ],

  limitations: [
    "CPU inference → the strategist's radio lags what's happening on track by several seconds. Acceptable by design (the race never waits), but visible.",
    "3B model → a higher rate of replies discarded for invalid JSON than a hosted model, and phrasing that is occasionally rough.",
    "In this build the shipped pilot is a scripted heuristic driving the directive channels, not a trained RL policy: RL did not converge on the procedural tracks, so the project pivoted to a heuristic on one fixed circuit. The RL observation/agent scaffolding is in place so a policy can be trained and dropped in later.",
    "Ephemeral: ~20-minute sessions, scale-to-zero, no memory across sessions.",
    "One fixed circuit in this build (a rounded-rectangle oval with four numbered corners).",
  ],
  limitationsEs: [
    "Inferencia en CPU → el radio del estratega va varios segundos por detrás de lo que pasa en pista. Aceptable por diseño (la carrera nunca espera), pero se nota.",
    "Modelo 3B → una tasa más alta de respuestas descartadas por JSON inválido que un modelo hosted, y una redacción a veces tosca.",
    "En este build el piloto que se sirve es una heurística scripted que conduce los canales de directiva, no una política RL entrenada: el RL no convergió en las pistas procedurales, así que el proyecto pivotó a una heurística sobre un circuito fijo. El andamiaje de observaciones/agente RL está puesto para entrenar una política y enchufarla después.",
    "Efímero: sesiones de ~20 minutos, scale-to-zero, sin memoria entre sesiones.",
    "Un circuito fijo en este build (un óvalo de rectángulo redondeado con cuatro curvas numeradas).",
  ],

  links: { repo: "https://github.com/alulema/agentic-racing" },
  lang: "auto",
};
