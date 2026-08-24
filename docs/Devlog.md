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

---

## 2026-08-23 — Fase 0: pista 1 (Unity+FastAPI+Docker) y arranque de pista 2 (ONNX+interop)

Sesión retomada tras reinicio de máquina; el proyecto Unity ya existía (URP 3D, plantilla
por defecto) del trabajo previo. Plan de Fase 0 confirmado implícitamente al pedir avanzar
"con todos los puntos".

**Confusión de UI aclarada**: en Unity 6, Build Profiles (que reemplazó a la vieja ventana
Build Settings) ya no lista "WebGL" como nombre de plataforma — la renombraron a **"Web"**.
Es la misma plataforma (mismo módulo `webgl` internamente, mismo Player Settings). No hubo
que instalar nada adicional; el módulo "Web Build Support" ya estaba presente en la
instalación de Unity 6.3 LTS (`6000.3.22f1`) de esta máquina.

**Punto 1 — Unity vacío → WebGL con rutas relativas + FastAPI con headers correctos**:
- Confirmado: el `index.html` que genera Unity 6 ya no tiene un `<script src="...">`
  estático — carga el loader dinámicamente por JS, pero las rutas siguen siendo relativas
  (`buildUrl = "Build"`, sin dominio ni slash inicial). Checklist de Fase 0 validado.
- `python -m http.server` para probarlo localmente reprodujo exactamente el riesgo descrito
  en `CLAUDE.md` §11: sin `Content-Encoding: br`, el navegador no descomprime
  `web-test.framework.js.br` y Unity falla al parsearlo. Confirma que ese punto del
  checklist es real, no teórico.
- Se escribió `server/main.py` (FastAPI): sirve estáticos desde `STATIC_DIR` (env var) con
  un handler que detecta sufijo `.br`/`.gz`, setea `Content-Encoding` y recalcula el
  `Content-Type` real a partir del nombre sin ese sufijo (`.wasm.br` → `application/wasm`,
  etc.). Verificado con `curl` sirviendo el build de prueba: headers correctos, la escena
  vacía carga en el navegador.
- `server/.venv` creado como venv del proyecto (ya cubierto por `.gitignore`).

**Punto 5 — Dockerizar y validar `docker run`**:
- `docker/Dockerfile`: single-stage `python:3.13-slim`, copia `server/` + `web/` (este
  último como el static root final — ver nota de arquitectura abajo), expone `8080`.
- Prueba end-to-end con la prueba que el contrato define como suficiente: `docker build` +
  `docker run -p 8080:8080 -e PROJECT_ID=agentic-racing -e DEMO_SLOT=demo01` + `curl`.
  Headers `.br` correctos también dentro del contenedor. Para la prueba se copió
  temporalmente el build de `unity/Builds/web-test/` a `web/` y se restauró después
  (`web/` en el repo se queda solo con `.gitkeep` — el build real lo ensambla CI).

**Nota de arquitectura fijada**: `/web` es el static root final que sirve el contenedor.
Unity genera su propio `index.html` en cada build, pero nuestro `web/index.html` (shell
custom con overlay DOM, ver abajo) lo reemplaza — CI copia solo `Build/`, `TemplateData/`
y `StreamingAssets/` del output de Unity dentro de `web/`, sin tocar nuestro `index.html`.

**Punto 2 — ONNX de juguete + `com.unity.ai.inference` en WebGL** (el riesgo arquitectónico
más caro de Fase 0, según `CLAUDE.md` §0):
- Modelo de juguete generado a mano con el paquete Python `onnx` (sin depender de
  `torch`): `Gemm(3,2) + ReLU`, pesos fijos y verificables a mano, guardado en
  `unity/Assets/Resources/Diagnostics/OnnxSmokeTest.onnx` (carpeta `Resources/` — no
  `Assets/ML-Agents/` — a propósito: permite `Resources.Load<ModelAsset>` en runtime sin
  tener que asignar la referencia a mano en el Inspector, automatizable sin abrir la GUI).
- `com.unity.ai.inference` agregado a `Packages/manifest.json`. Versión `2.6.1` confirmada
  vía búsqueda web (releases de `needle-mirror/com.unity.ai.inference` en GitHub).
- **Trampa de nombres reconfirmada** (`CLAUDE.md` §9/§11): la página de manual resumida por
  WebFetch sugirió erróneamente `using Unity.Sentis;`. La página de **API reference** del
  mismo release (`.../api/Unity.InferenceEngine.Worker.html`) mostró el namespace real:
  `Unity.InferenceEngine`. Para este paquete específico, confiar en las páginas de API
  reference (más literales) antes que en las de manual (resumidas por un modelo chico) — y
  contrastar contra lo que ya dice `CLAUDE.md` cuando hay conflicto.
- `OnnxSmokeTest.cs` escrito contra esa API. Un batchmode headless (`Unity -batchmode
  -nographics -quit`) detectó en el primer intento un error de compilación real:
  `Worker` no tiene `WaitForCompletion()` en esta versión del paquete. Se confirmó
  contra el código fuente ya resuelto en `Library/PackageCache/com.unity.ai.inference@.../
  Runtime/Core/Backends/Worker.cs` (no existe ese método) y se quitó la llamada —
  `Tensor<float>.DownloadToArray()` ya bloquea hasta tener el resultado, así que no hacía
  falta. Recompiló limpio.
- Backend elegido: `BackendType.CPU` (no `GPUCompute`), siguiendo el riesgo conocido de
  `CLAUDE.md` §11 sobre no asumir compute shaders disponibles en WebGL. Pendiente:
  confirmar en el navegador real que corre y medir el costo por inferencia.

**Punto 3 — Interop DOM ↔ Unity**:
- `Assets/Scripts/Interop/WebGLBridge.jslib`: dirección Unity → DOM, despacha un
  `CustomEvent('unity:message', ...)` en `window` en vez de acoplarse a un id de elemento
  específico — así el HUD/radio de Fase 4 puede escuchar sin tocar este plugin.
- `Assets/Scripts/Interop/JsBridge.cs`: wrapper C#, no-op fuera de WebGL (permite correr en
  Editor/standalone sin `DllImport` fallando).
- `OnnxSmokeTest` reporta su resultado por este puente, combinando en una sola escena la
  validación de ONNX y de interop (dirección Unity→DOM). Dirección DOM→Unity: botón en
  `web/index.html` que llama `unityInstance.SendMessage('Fase0SmokeTest', 'RunInference')`.
- `web/index.html`: shell propio con `demo-theme.css` enlazado (+ variables de fallback si
  no carga), panel de debug, y el loader de Unity adaptado del template pero apuntando a un
  nombre de build parametrizado (`BUILD_NAME`, hoy fijo a `"web-test"` — lo reemplaza CI).

**Automatización sin GUI**: como no hay forma de hacer clic dentro del Editor por este
canal, se automatizó todo lo posible por línea de comandos:
- `Assets/Editor/Fase0SmokeTestSetup.cs`: menu item que crea el GameObject de prueba en la
  escena activa y la guarda (reproducible con un clic si hace falta hacerlo a mano).
- `Assets/Editor/Fase0BatchBuild.cs`: entry point para `-executeMethod`, pensado para
  correr headless (`-batchmode -nographics -quit`) — abre `SampleScene`, hace el wiring, y
  llama `BuildPipeline.BuildPlayer` a Web. La primera corrida chocó con "another Unity
  instance is running" porque el Editor estaba abierto en GUI; una vez que se cerró, el
  batchmode resolvió el paquete nuevo y compiló sin problema. El build headless a Web
  quedó corriendo en background al pausar la sesión (primer build IL2CPP, tarda varios
  minutos) — pendiente de confirmar resultado y probarlo en navegador real la próxima
  sesión.

**Punto 6 — `/api/strategy`**:
- Endpoint mínimo de Fase 0 (no el esquema completo de telemetría de §6.3/6.4, eso es
  Fase 4): llama a `claude-haiku-4-5` (decisión cerrada de `CLAUDE.md` §2) vía el SDK
  `anthropic` (Python, pinneado a `1.0.0`), usando `client.messages.parse(...,
  output_format=<PydanticModel>)` para que el SDK valide el JSON contra el esquema en vez
  de parsear texto a mano. `max_tokens` acotado a 200 (guardrail de costo §7). Si
  `ANTHROPIC_API_KEY` no está en el entorno, responde 503 en vez de romper — nunca se
  hornea la key en la imagen.
- **Pendiente**: no se pudo probar la llamada real (esta máquina no tiene
  `ANTHROPIC_API_KEY` en el entorno ni un perfil de `ant auth login` activo). El import y
  el arranque del servidor sí se verificaron sin errores.

**Punto 7 — CI/GHCR**: `.github/workflows/build-and-publish.yml` escrito (no probado —
no se hizo push): job `build-webgl` con `game-ci/unity-builder@v4` (requiere secret
`UNITY_LICENSE`, humano-only por `CLAUDE.md` §8) + job `build-and-push-image` que ensambla
`web/` con el artifact de Unity y publica a `ghcr.io/alulema/agentic-racing` usando el
`GITHUB_TOKEN` automático (no hace falta secret extra para el push en sí). Nota dejada en
el propio workflow: el paquete probablemente nace privado en el primer push y va a
necesitar un cambio manual de visibilidad a Public en GitHub (contrato, punto 7).

**Pendiente para la próxima sesión**:
1. Confirmar resultado del build headless de Web (ONNX + interop) y probarlo en un
   navegador real sirviéndolo con `server/main.py`.
2. Probar `/api/strategy` con una `ANTHROPIC_API_KEY` real.
3. Decidir si se abre PR de Fase 0 ahora o se sigue acumulando checklist.
4. Los puntos humano-only siguen pendientes de que el humano los haga: activar licencia
   Unity para CI (`.alf`→`.ulf` como secret `UNITY_LICENSE`), y — cuando se corra el
   workflow por primera vez — marcar el paquete GHCR como público.
