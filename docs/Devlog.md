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

---

## 2026-08-31 — Revisión de estado, sin cambios de código

Sesión de revisión: se pidió el estado actual del proyecto y luego actualizar esta
bitácora. No se escribió ni modificó código.

**Estado del repo verificado**:
- Rama `fase-0-risk-spike`, árbol de trabajo limpio, sincronizada con
  `origin/fase-0-risk-spike`. Commits: `fb9375e` (initial) + `100bdfc`
  ("Fase 0 (WIP): pista Unity+FastAPI+Docker validada, ONNX+interop en curso"). Sin PR
  abierto. Nada que commitear ni pushear en esta sesión.
- Todo lo fuera de control de versiones (`unity/Builds/`, `unity/UserSettings/`,
  `unity/.vscode/`, `.csproj` generados, `server/.venv/`) está cubierto por `.gitignore`
  a propósito.

**Hallazgos de la revisión** (contra el checklist de Fase 0 en `CLAUDE.md` §5):
- El build headless de Web de la sesión del 23-ago **sí terminó**: `unity/Builds/web-test/Build/`
  contiene los cuatro archivos esperados —`web-test.loader.js`, `web-test.data.br` (4.4 MB),
  `web-test.framework.js.br` (76 KB), `web-test.wasm.br` (8.1 MB)—, con fecha 23-ago 21:24–21:26.
  Sigue **pendiente** verificarlo corriendo en un navegador real y medir el costo por
  inferencia del ONNX de juguete; el pendiente #1 de la sesión anterior se mantiene.
- `com.unity.ai.inference` fijado en `2.6.1` en `unity/Packages/manifest.json`.
  `com.unity.ml-agents` **todavía no está** en el manifest — es dependencia de Fase 2, no
  bloquea Fase 0, pero al agregarlo hay que verificar que no entre en conflicto de versión
  con `com.unity.ai.inference` (`CLAUDE.md` §9/§11).
- `server/requirements.txt`: `fastapi==0.115.6`, `uvicorn[standard]==0.34.0`,
  `anthropic==1.0.0`.
- Unity fijado en `6000.3.22f1` (`ProjectVersion.txt`), coincide con `CLAUDE.md` §9.

**Pendientes sin cambios respecto al 23-ago**: los cuatro puntos de "Pendiente para la
próxima sesión" de la entrada anterior siguen todos abiertos.

### Validación en navegador real: ONNX + interop (checklist de Fase 0)

Retomado en la misma sesión. Se montó el build y se abrió en un navegador real
(Chrome, vía la integración Claude-in-Chrome) sirviéndolo con `server/main.py`.

**Primer intento — falló, y por qué**: el build `unity/Builds/web-test/` que había en
disco era el de la escena vacía del Punto 1 (23-ago 21:24), **anterior** a que
`SampleScene` se guardara con el GameObject `Fase0SmokeTest` (23-ago 22:16). El build que
sí lo incluía (`Fase0BatchBuild` → `Builds/fase0-onnx-interop/`) nunca llegó a
completarse: quedó a medias al pausar la sesión del 23-ago. Síntomas en el navegador:
Unity arrancaba bien (`Initialize engine version: 6000.3.22f1`, WebGL 2.0, PhysX), el
panel se quedaba en "(esperando mensaje de Unity…)", y al pulsar el botón la consola
tiraba `SendMessage: object Fase0SmokeTest not found!`. Los `ERROR: Shader Hidden/...`
de URP en consola son ruido del GPU headless, no relacionados.

**Corrección**: `Fase0BatchBuild.cs` — `OutputDir` cambiado de `Builds/fase0-onnx-interop`
a `Builds/web-test`, para que Unity nombre los archivos del player `web-test.*` y
coincidan con `BUILD_NAME = "web-test"` de `web/index.html` (Unity nombra el player según
el último segmento de `locationPathName`). El directorio anterior era descartable.

**Build headless relanzado** en esta máquina (Unity `6000.3.22f1` en
`~/Unity/Hub/Editor/`, sin Editor abierto, licencia local ya activada):

```
~/Unity/Hub/Editor/6000.3.22f1/Editor/Unity -batchmode -nographics -quit \
  -projectPath unity -executeMethod AgenticRacing.EditorTools.Fase0BatchBuild.Build -logFile -
```

Resultado: `[Fase0BatchBuild] result=Succeeded totalErrors=0 size=16860517` (~16 MB sin
comprimir). Comprimido: `web-test.wasm.br` 10.6 MB + `web-test.data.br` 6.1 MB — subió
~4 MB frente al build vacío, por incluir Inference Engine + el `.onnx`. A vigilar en
Fase 5 (peso del build, riesgo conocido de `CLAUDE.md` §11).

**Segundo intento — pasa**. Recargando la página con el build nuevo, el panel DOM muestra
(empujado desde Unity vía `CustomEvent('unity:message')`):

```
onnx_ok:backend=CPU,ms=3.40,output=[2.600,3.400]
```

- **ONNX carga y ejecuta inferencia dentro del build WebGL** (no editor). `com.unity.ai.inference`
  2.6.1 funciona en WebGL.
- **`BackendType` que funciona en WebGL: `CPU`** — como anticipaba el riesgo de `CLAUDE.md`
  §11, no se asumió `GPUCompute`.
- **Salida correcta**: `[2.600, 3.400]` es exactamente el valor calculado a mano en
  `make_toy_onnx.py` (`Gemm([1,2,3]) + B → [2.6, 3.4] → ReLU` sin cambio). La inferencia
  es numéricamente correcta, no basura.
- **Costo por inferencia**: 1ª ejecución **3.40 ms** (fría: incluye crear el `Worker` +
  warmup), ejecuciones siguientes **0.10 ms** (caliente). Extrapolado a 6 autos en
  caliente: ~0.6 ms/frame. El modelo de juguete es 3→2 trivial; el MLP real de Fase 2 con
  raycasts será bastante mayor, así que **este número es un piso**, no la estimación final.
  Cuando exista la red real hay que re-medir.
- **Interop Unity → DOM**: el panel recibió el mensaje sin tocar ningún id de elemento
  (el `.jslib` despacha un `CustomEvent` en `window`).
- **Interop DOM → Unity**: el botón "Re-ejecutar inferencia" disparó una inferencia nueva
  vía `unityInstance.SendMessage('Fase0SmokeTest', 'RunInference')` — panel actualizado en
  vivo, sin `object not found`.
- **`web/index.html`**: el `demo-theme.css` remoto no carga en local (sin red al dominio),
  pero las variables CSS de fallback aplican y el panel se ve estilado — la cadena
  `var(--color-*)` funciona.

**Nit corregido de paso**: `OnnxSmokeTest.cs` formateaba los números con la cultura del
sistema (`ms=3,40` con coma decimal en locale ES), lo que rompe un string de diagnóstico
pensado para parsearse. Ahora usa `CultureInfo.InvariantCulture` en `ToString("F3")` /
`ToString("F2")`.

**Estado del checklist de Fase 0 tras esto** (ver `CLAUDE.md` §5): pasan todos menos dos —
1. Publicar la imagen a GHCR público desde CI: workflow escrito, sin correr, bloqueado por
   el secret `UNITY_LICENSE` (humano-only, `CLAUDE.md` §8).
2. Llamada en vivo a `/api/strategy`: endpoint escrito y arranca. Ver la sección de más
   abajo — el backend del LLM cambió de Anthropic hosted a Ollama local en esta misma
   sesión, y la validación quedó a medias (plumbing OK, falta contra `llama3.2:3b`).

**Cambios de código de esta sesión**: `unity/Assets/Editor/Fase0BatchBuild.cs` (OutputDir),
`unity/Assets/Scripts/Diagnostics/OnnxSmokeTest.cs` (InvariantCulture),
`unity/Assets/Scenes/SampleScene.unity` (el batch build re-guardó la escena con el
GameObject `Fase0SmokeTest`), más este `Devlog.md` y el checklist de `CLAUDE.md`.

### Decisión revisada: el estratega LLM pasa a modelo local (Ollama), no API hosted

Al revisar el pendiente "probar `/api/strategy` con `ANTHROPIC_API_KEY`", el dueño del
proyecto planteó que **no pensaba usar modelos de Anthropic** y preguntó por un modelo
local vía Ollama en contenedor.

Se revisó el contrato (`DEMO_INTEGRATION.md`): **lo permite explícitamente**. El punto 6
nombra "Claude (no Azure OpenAI)" solo como *ejemplo* de opción aceptable, no como
requisito, y el demo de referencia del propio contrato (`rag-blogposts`) es
autohospedado con Ollama + un modelo local, con números concretos de sizing (pod
2 vCPU / 4 GiB CPU-only, Qwen 0.5B → primer token ~6-7 s). Así que la decisión de
`CLAUDE.md` §2 (`claude-haiku-4-5` hosted) era de diseño, no contractual.

**Decidido** (queda como §2.5 de `CLAUDE.md`, decisión de sección 2 cerrada en su nueva
forma):

- Estratega en **`llama3.2:3b`** (tag de Ollama; es el build instruct/q4_K_M, ~2 GB),
  servido por un **sidecar Ollama CPU-only** en la misma imagen.
- **Pesos horneados en la imagen**, no `ollama pull` al arrancar — arranque
  determinista y sin red, a cambio de +~2 GB de imagen que la infra jala en cada
  provisión (el mayor golpe al riesgo "peso de imagen" de §11).
- **Sin secretos**: se elimina `ANTHROPIC_API_KEY` de §2.2 y del hand-off manifest.
- El riesgo #1 del proyecto (§7, "tráfico no acotado" contra una API medida) desaparece
  como coste y se reconvierte en **saturación de CPU**: 6 estrategas serializados contra
  un Ollama sin GPU. Los guardrails de §7 se reescribieron en esa clave (cooldown por
  auto, límite de concurrencia, cortacircuitos a "modo offline").

Motivos que se sopesaron y se documentaron en la respuesta al dueño: 6 cerebros LLM
independientes (§6.7) contra el pod chico, JSON estricto con enums (§6.4/§6.8) que un
modelo 3B acierta menos que uno hosted, latencia CPU ~5-15 s/respuesta (aceptable porque
§6.6 dice que la carrera nunca espera al LLM), y peso de imagen. Alternativas
descartadas: API hosted no-Anthropic (Groq/Together) y Ollama externo en caja propia.

**Cambios de código por esta decisión**:

- `server/main.py`: `/api/strategy` deja la SDK `anthropic` y llama a Ollama
  (`POST /api/chat`) con **structured output** — se pasa el JSON Schema del modelo
  Pydantic como `format`, que fuerza la forma exacta (mejor adherencia a enums con un
  3B que `format: "json"` a secas). Se mantiene la validación Pydantic
  (`model_validate_json`); respuesta que no valida → 502, se descarta entera (§6.8).
  `num_predict` topado a 150 (§7). `keep_alive: 30m` para no recargar el modelo (§6.7).
  `OLLAMA_URL` / `OLLAMA_MODEL` por env.
- `server/requirements.txt`: fuera `anthropic`, dentro `httpx==0.28.1`.
- `docker/Dockerfile`: reescrito a imagen única con Ollama instalado + `llama3.2:3b`
  horneado (`ollama serve` efímero durante el build para el `pull`) + `entrypoint.sh`
  como supervisor (arranca `ollama serve`, espera readiness, calienta el modelo, hace
  `exec uvicorn`).
- `docker/Dockerfile.dev` + `compose.yaml` (nuevo, en la raíz): loop de dev que espeja
  la topología de producción sin hornear 2 GB en cada cambio — Ollama como servicio
  con volumen, `ollama-init` hace el `pull` una vez, `app` con `--reload`.
- `CLAUDE.md`: §2 (tabla), nueva §2.5, §2.2, §3, §4, §5 (Fase 0/4/5), §6.7 (de "prompt
  caching" a "reutilización de KV-cache de Ollama"), §7 (de "guardrails de costo" a
  "guardrails de carga"), §8 (fuera "tope de presupuesto en consola de Anthropic"), §11.

**Validación en esta máquina**:

- Plumbing de `/api/strategy` verificado primero contra `qwen2.5:1.5b-instruct` (ya
  presente en el Ollama local, 0.24.0): `HTTP 200` con JSON válido contra el esquema.
- Luego contra **`llama3.2:3b`** (el modelo elegido, recién descargado al Ollama local):
  - 1ª llamada (en frío): `HTTP 200`, **7.5 s**, `{"directive":"push","radio":"Gaps are
    looking good, let's push the pace on the next lap."}` — 10 palabras.
  - 2ª llamada (caliente): `HTTP 200`, **3.5 s**, `{"directive":"push","radio":"Lap 3,
    focus on closing the gap to P1, smooth acceleration out of turn 2"}` — 13 palabras.
  - Ambas validan contra el esquema (`directive` enum correcto, `radio` string, dentro
    del límite de 15 palabras sin recorte). El structured output de Ollama (`format` =
    JSON Schema) + validación Pydantic funcionan end-to-end con el modelo real.
  - Latencia CPU 3.5–7.5 s: dentro del "~5-15 s" documentado en `CLAUDE.md` §2.5 y
    aceptable por §6.6 (la carrera nunca espera al LLM).

**Checklist de Fase 0 — punto del LLM**: el plumbing y el modelo real están validados
localmente (uvicorn + Ollama del host). **Falta** validar el mismo flujo vía
`docker compose up` (app + servicio Ollama) y vía la **imagen final** (`docker build`
sobre `docker/Dockerfile` + `docker run`, que hornea el modelo).

---

## 2026-09-01 — Validación de `/api/strategy` con Ollama en las dos topologías

Se retomó desde la PARADA 2026-08-31, pasos 1 y 2 (validar el estratega LLM local
end-to-end vía Docker, no solo con uvicorn + Ollama del host).

### Topología de dev — `docker compose up --build`

`compose.yaml` levanta `ollama` (imagen oficial + volumen), `ollama-init` (one-shot,
`ollama pull llama3.2:3b` al volumen) y `app` (`Dockerfile.dev`, sin modelo horneado,
`--reload`). Primer arranque: descarga de la imagen `ollama/ollama` + 2.0 GB de pesos al
volumen (~4 min a ~8 MB/s). Tras eso:

- `GET /api/strategy` en frío (primera inferencia, carga del modelo a RAM): `HTTP 200`,
  **6.79 s**, `{"directive":"push","radio":"Gaps are getting bigger, let's push, let's
  close that gap!"}` — 10 palabras.
- En caliente: `HTTP 200`, **2.44 s**, JSON válido, 11 palabras.
- Ambas validan contra `StrategySmokeTestResponse` (enum `directive` correcto, `radio`
  string dentro de 15 palabras).

### Imagen final — `docker build -f docker/Dockerfile` + `docker run`

**Bug encontrado y corregido**: el primer `docker build` falló en el paso
`RUN curl -fsSL https://ollama.com/install.sh | sh` con
`ERROR: This version requires zstd for extraction`. El instalador de Ollama ahora
distribuye su tarball comprimido con zstd y aborta si el binario `zstd` no está. Fix:
añadir `zstd` a la línea `apt-get install` del `Dockerfile` (junto a `curl` y
`ca-certificates`). Con eso el build completa.

Build OK → `agentic-racing:fase0`. `docker run -p 8080:8080 -e PROJECT_ID=agentic-racing
-e DEMO_SLOT=demo01`:

- `entrypoint.sh` arranca `ollama serve`, espera readiness, calienta el modelo
  (`POST /api/chat` → `200` en 4.35 s) y hace `exec uvicorn`. Logs limpios.
- `GET /api/strategy`: `HTTP 200`, ~3.7 s, JSON válido contra el esquema. (Una de las
  respuestas se pasó de 15 palabras en el campo `radio` — el esquema de Fase 0 no
  fuerza el límite de palabras; el recorte a 15 es cliente-side en Fase 4, §6.8. No
  bloquea.)
- Estáticos: `GET /` y `GET /index.html` → `200 text/html`. Guard de path traversal
  (`/../etc/passwd`) → `404`. `HEAD /` → `405` (Starlette no está exponiendo HEAD en la
  ruta catch-all; irrelevante para el browser que carga el build con GET, pero anotado
  por si un proxy hace healthcheck con HEAD — los endpoints de salud reales son
  `/api/health` y `/api/ping`, aún sin implementar, Fase 4/5).
- `ollama` escucha **solo en loopback** dentro del contenedor (`curl
  127.0.0.1:11434/api/version` OK desde dentro; no publicado al host).

**Hallazgo de peso de imagen**: `agentic-racing:fase0` pesa **~8.3 GB**. Desglose por
`docker history`: capa del instalador de Ollama **2.25 GB** (trae libs de GPU
ROCm/CUDA que en este demo CPU-only no se usan), modelo horneado **2.0 GB**, base
`python:3.13-slim` + toolchain ~1.5 GB, resto. Esto contradice el "+~2 GB" que asumen
`CLAUDE.md` §2.5/§3/§11 — el runtime de Ollama por sí solo añade otros ~2.25 GB. Va a
Fase 5 como trabajo de optimización (candidato claro: borrar
`/usr/local/lib/ollama/rocm` y libs CUDA tras la instalación; el pod de la infra es
CPU-only). Riesgo §11 "peso del build + modelo horneado" confirmado y peor de lo
estimado.

### Estado del checklist de Fase 0 tras esto

Pasan todos los puntos salvo **uno**, que es humano-only: publicar la imagen a GHCR como
paquete público desde CI (workflow escrito, bloqueado por el secret `UNITY_LICENSE`,
`CLAUDE.md` §8). El punto del LLM queda `[x]` en `CLAUDE.md` §5.

**Cambios de código de esta sesión**: `docker/Dockerfile` (+`zstd`). `CLAUDE.md` §5
(checklist Fase 0, punto LLM). Este `Devlog.md`.

---

## 2026-09-01 (cont.) — Commit de la validación + bloqueo de la licencia Unity

**Commiteado**: `a41975d` en `fase-0-risk-spike` — "Fase 0: estratega LLM a Ollama local,
validado en Docker (dos topologías)". Incluye `server/main.py`, `server/requirements.txt`,
`docker/Dockerfile` (+`zstd`), `docker/Dockerfile.dev`, `compose.yaml`,
`docker/entrypoint.sh`, `CLAUDE.md` (§5 checklist Fase 0), y las dos entradas de este
Devlog. El working tree queda limpio.

### Bloqueo: la licencia Unity Personal ya no se puede activar offline

Al intentar el flujo `.alf → .ulf` (subir el `.alf` a `license.unity3d.com/manual`),
Unity respondió: *"You are not eligible to activate your license offline. Offline
activation is available only for Enterprise and Industry seats."* Unity **retiró la
activación manual/offline para seats Personal** — es un cambio suyo, conocido en la
comunidad de GameCI, no un error de configuración.

Diagnóstico de esta máquina: el Editor **sí** está activado, pero con el **Licensing
Client** de Unity 6, que guarda la entitlement como
`~/.config/unity3d/Unity/licenses/UnityEntitlementLicense.xml`. Ese formato **no** es el
`Unity_lic.ulf` portable que GameCI necesita, y **Linux + Unity 6 no genera un `.ulf`
utilizable**. Windows y macOS con Unity Hub sí lo generan
(`C:\ProgramData\Unity\Unity_lic.ulf` / `/Library/Application Support/Unity/Unity_lic.ulf`).

**Decisión tomada con el dueño**: opción A — generar el `.ulf` desde **Unity Hub en la
partición Windows de esta NUC** (arranque dual). El dueño se encarga; requiere reiniciar
a Windows. Es lo que la doc actual de GameCI (`game.ci/docs/github/activation`) recomienda
para Personal. Alternativas descartadas por ahora: el hack de `display:none` en la web de
Unity (frágil, puede que ya no exista el elemento), `game-ci/unity-license-activate`
(automatiza el login web con Playwright; hacky), self-hosted runner, y build local +
CI-solo-imagen (rompen §2).

### Resolución — `.ulf` cargado y CI verde

El dueño generó `Unity_lic.ulf` con Unity Hub en la partición Windows de la NUC y cargó
los tres secrets (`UNITY_LICENSE` / `UNITY_EMAIL` / `UNITY_PASSWORD`) vía
`gh secret set`. El `.ulf` es válido (`License id="Terms"`, `StartDate 2026-09-01`).

**PR de cierre de Fase 0**: [#1](https://github.com/alulema/agentic-racing/pull/1),
`fase-0-risk-spike` → `main`.

**Ajustes de CI necesarios para que el PR se pudiera verificar** (dos bugs, ambos
corregidos en la rama):

1. El workflow solo disparaba en `push:main` + `workflow_dispatch`, y `gh workflow run`
   falla si el archivo no está en la rama por defecto. Se añadió trigger
   `pull_request → main`; en PR construye la imagen pero **no** hace login ni push a GHCR
   (`push: false`). La publicación real sigue siendo solo en `push:main` (commit `8c5e503`).
2. `game-ci/unity-builder` escribe el player en
   `buildsPath/targetPlatform/buildName`, y `buildName` también default a `WebGL`, así que
   el player real queda **doble-anidado** en `unity-build-output/WebGL/WebGL/{Build,
   TemplateData,index.html}`. El workflow subía el nivel de arriba y `Assemble /web static
   root` fallaba con `cp: cannot stat 'unity-build-output/WebGL/Build'`. Fix: subir el dir
   interno + `if-no-files-found: error` + ensamblado con `set -euo pipefail` y check
   explícito (commit `4fa458a`).

**Runs de CI**:

- Run 1 (`8c5e503`): `build-webgl` ✅ **success** — GameCI activa la licencia Personal en
  CI y compila WebGL con `unityVersion: 6000.3.22f1` (`Build Finished, Result: Success`,
  ~16.8 MB de player). `build-and-push-image` ❌ por el bug #2 de arriba.
- Run 2 (`4fa458a`): **ambos jobs ✅**. `build-webgl` ~11 min (pegó al caché de
  `unity/Library`). `build-and-push-image` ~46 s: en el runner de GitHub el `ollama pull`
  de 2.0 GB tardó **~10 s** (~200 MB/s, vs ~8 MB/s en la máquina del dueño), la imagen de
  ~8.3 GB se exportó al store local de buildkit sin problemas de disco, sin push por ser
  PR.

Con esto, **los tres puntos de riesgo que dependían de CI quedan verificados de forma
reproducible**: (a) la licencia Unity Personal activa en CI, (b) el WebGL compila en CI,
(c) la imagen Docker arma en CI con el WebGL mergeado.

### Retomar aquí — lo único que queda de Fase 0 (humano-only, post-merge)

1. **Mergear el PR #1 a `main`.** Eso dispara el run con `push:main` → `docker login` +
   `push: true` → primera imagen a `ghcr.io/alulema/agentic-racing`.
2. **Marcar el paquete GHCR como Público** (*Packages → agentic-racing → Package settings
   → Change visibility → Public*). La infra efímera lo jala sin credenciales (contrato
   punto 7).

**Estado de Fase 0**: todo lo verificable por el agente está hecho y verde en CI. Solo
resta merge + push inicial a GHCR + visibilidad pública, que es humano-only.

**Para Fase 5 (anotado para no perderlo)**:
- Adelgazar la imagen: quitar libs GPU del runtime de Ollama (`rocm`, CUDA) — el pod es
  CPU-only. Objetivo: bajar de ~8.3 GB.
- Implementar `/api/health` y `/api/ping` (y revisar por qué `HEAD` da 405 en la ruta
  catch-all de estáticos).
