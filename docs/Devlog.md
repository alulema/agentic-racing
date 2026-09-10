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

### Cierre de Fase 0 — confirmado

- PR #1 **mergeado** a `main` (merge commit `f88c830`, 2026-09-02 13:01 UTC).
- Run de CI con `push:main` (`33633279640`) ✅: compiló WebGL, `docker login` a GHCR y
  `push: true`.
- **Imagen pública en GHCR** verificada con token anónimo de `ghcr.io` (sin credenciales):
  `manifests/latest` → `HTTP 200`, `tags/list` → `latest` + `<sha>`, ambos tags al mismo
  digest `sha256:a17ec970…`. Manifest single-platform `linux/amd64` (sin índice
  multi-arch). **Tamaño en registry: ~3.49 GiB comprimido / 14 capas** — los ~8.3 GB de
  antes eran sin comprimir; lo que la infra descarga por provisión es la mitad.

Todos los criterios de aceptación de Fase 0 (§5) pasan.

**Para Fase 5 (anotado para no perderlo)**:
- Adelgazar la imagen: quitar libs GPU del runtime de Ollama (`rocm`, CUDA) — el pod es
  CPU-only. Objetivo: bajar de ~8.3 GB sin comprimir.
- Implementar `/api/health` y `/api/ping` (y revisar por qué `HEAD` da 405 en la ruta
  catch-all de estáticos).

---

## 2026-09-02 — Fase 1 · Iteración 1: generación procedural del circuito cerrado

Rama `fase-1-track-fisica` desde `main` (`f88c830`). Fase 1 se hace en 3 iteraciones;
ésta cubre solo el **núcleo de generación de pista**. Numeración de curvas, racing line,
física del auto + teclado, y conteo de vueltas / cruce de meta quedan para las iteraciones
2 y 3.

### Qué se implementó — `unity/Assets/Scripts/Track/` (asmdef `AgenticRacing.Track`)

- **`CatmullRomSpline.cs`** — spline Catmull-Rom **centrípeta** (α = 0.5), cíclica y
  C1-continua incluida la junta de cierre. La variante centrípeta se eligió a propósito:
  no genera cúspides ni auto-intersecciones *dentro* de un segmento aunque los puntos de
  control estén desigualmente espaciados (importa para un circuito jitterado). Incluye
  `SampleClosed` y `ResampleByArcLength`.
- **`TrackGenerator.cs`** — `int seed` → `System.Random(seed)` (nunca `UnityEngine.Random`,
  §10) → 9–14 puntos de control en lazo con jitter radial (−25 %..+30 %) y angular
  (0.45× el paso, acotado para que el ángulo sea monótono y el lazo no se pliegue) →
  spline → centerline reesampleada a 2 m. Escala determinista a **1.5–2.5 km**. Valida
  **sin auto-intersección** (test segmento-segmento O(n²), sólo en generación) y **radio
  de curva mínimo ≥ 12 m** (curvatura de Menger sobre un **stencil de ~6 m**, no entre
  puntos adyacentes — ver bug abajo). Si falla → deriva seed con hash SplitMix32 y
  reintenta (máx. 32), registrando `EffectiveSeed`/`Attempts` en un solo `Debug.Log`.
- **`TrackMeshBuilder.cs`** — mesh de cinta plana (Y = 0) a lo largo de la centerline,
  cerrada sin costura en la junta, con UVs (v = arco / 8 m para material tileable).
- **`TrackConfig.cs`** — lee `?seed=` y `?laps=` de `Application.absoluteURL` con
  fallback serializado (parseo con `InvariantCulture`).
- **`TrackBuilder.cs`** — MonoBehaviour: en `Awake` resuelve seed, genera, asigna la mesh
  a `MeshFilter`/`MeshCollider`, dibuja centerline + línea de meta como gizmos, y expone
  `Data` (centerline, longitud, radio mínimo, pose de meta) para las fases siguientes.

### Verificación (Unity batchmode local, editor 6000.3.22f1)

- **`unity/Assets/Editor/Fase1TrackValidator.cs`** — barrido `-executeMethod` sobre seeds
  1..200. Resultado: **PASS, 200/200, 0 fallos**. Sólo **1/200 seeds** (la 117) necesita
  una seed derivada; longitudes 1898–2500 m; curva más cerrada del barrido 13.3 m.
- **`unity/Assets/Tests/EditMode/TrackGeneratorTests.cs`** (asmdef
  `AgenticRacing.Tests.EditMode`, NUnit) — **7/7 tests pasan**: determinismo
  (misma seed → vértices idénticos bit a bit), lazo cerrado + sin kink en la junta
  (< 12°), longitud en rango, sin auto-intersección, radio mínimo navegable, seeds
  distintas → pistas distintas, y fallback determinista (con umbral 22 m para forzarlo
  en parte del barrido, verifica que la cadena de seeds derivadas es reproducible y que
  el resultado sigue siendo válido).

### Bug encontrado y corregido durante la iteración

Primera pasada del validador: **181/200 seeds necesitaban seed derivada**, todas por
"radio de curva < 15 m", y el `tightest corner overall` quedaba clavado en 15.0 m exacto
(el loop de fallback forzaba hasta rozar el umbral). Causa: la curvatura de Menger se
medía entre puntos **adyacentes a 2 m** de una centerline que es interpolación lineal de
un spline muestreado grueso (`SamplesPerSegment = 24` ≈ 7.5 m entre puntos finos), así
que cada "codo" de la discretización se leía como una horquilla de ~8 m. Fixes:
`SamplesPerSegment` 24 → 160 (spline fino < 1 m), y medir la curvatura sobre un **stencil
de ~6 m** (`CurvatureStencil`), no entre vecinos. Además se bajó el jitter
(radial ±35/45 % → −25/+30 %, angular 0.55 → 0.45) y el umbral de curva a 12 m. Resultado:
de 181/200 fallbacks a 1/200. También se cambió el `Debug.LogWarning` por-reintento (588
warnings con stack trace, log de 17k líneas) por un único `Debug.Log` resumido.

### Pendiente de esta iteración (no bloquea, es housekeeping)

- El validador y los tests corren **localmente**; falta que corran en CI. Se abre un PR
  **draft** de Fase 1 para que el workflow (`pull_request → main`) valide cada push; el
  job de tests EditMode en CI se añade en la iteración 2 (ahora CI sólo compila WebGL +
  imagen, que ya cubre que el código de `Track/` compila para WebGL).
- `unity/ProjectSettings/ProjectSettings.asset` fue tocado por el Editor durante los runs
  (swap de un define symbol de WebGL, `SENTIS_ANALYTICS_ENABLED` → `APP_UI_EDITOR_ONLY`,
  sin relación con este trabajo) — revertido para no meter churn en el PR. Si reaparece de
  forma persistente, tratarlo aparte.

---

## 2026-09-02 — Fase 1 · Iteración 2: forma de circuito, numeración de curvas, racing line

Sigue en la rama `fase-1-track-fisica` / PR #2 (draft).

### Cambio de fondo: modulación radial armónica

La iteración 1 generaba puntos de control con jitter radial simple sobre un círculo, lo
que producía **casi óvalos** — sin rectas largas ni curvas diferenciadas, mal circuito de
carreras. Cambiado a **modulación radial armónica**: 2–3 sinusoides de baja frecuencia
(lóbulos 2–5) con amplitud y fase por seed, más un jitter local pequeño. Eso crea la
forma real de un trazado — rectas entre lóbulos, curvas en las transiciones. `TrackParams`
gana `MinHarmonics`/`MaxHarmonics`, `MinHarmonicFreq`/`MaxHarmonicFreq`, `HarmonicAmpMin`/
`Max`, `RadiusClampMin`/`Max`; `RadialJitter*` pasa a ser sólo el jitter local. Control
points 16–22 (antes 9–15) para resolver los armónicos.

Efecto medido (barrido validador seeds 1..200): **PASS 200/200**, sólo **6/200** necesitan
seed derivada, longitudes 1890–2500 m, **5–17 curvas por trazado** (antes 2–3 con jitter
plano), curva más cerrada del barrido 12.1 m.

### Numeración de curvas — `TrackCorner` + `TrackAnalysis.DetectCorners`

- Curvatura **con signo** por muestra (curvatura de Menger sobre stencil de 6 m, luego
  media móvil). Signo → giro a izquierda / derecha.
- Un sector es curva si `|radio| < 220 m` de forma sostenida (≥ 12 m de arco y ≥ 14° de
  cambio de rumbo); sectores separados por < 14 m se fusionan.
- **La línea de meta va sobre la recta más larga.** Se detectan curvas una vez, se busca
  el mayor hueco entre curvas, se rota la centerline para que su punto medio sea el
  índice 0, y se re-detecta: así las curvas quedan numeradas **1..N desde meta**, ninguna
  la cruza. (Un test lo pilló: con la meta en `Centerline[0]` arbitrario, una curva podía
  quedar a caballo de la línea, con arcos no monótonos.)
- Cada `TrackCorner`: índice, muestras/arcos de entrada·ápice·salida, dirección,
  cambio de rumbo en grados, radio mínimo. Deterministas por seed.

### Racing line — `TrackAnalysis.BuildRacingLine`

Referencia geométrica, **no óptimo de tiempo de vuelta** (§5 pide "expuesta como
referencia"). Offset lateral respecto a la centerline: fuera en la aproximación, dentro
en el ápice, deshaciendo a la salida; rampas que se solapan toman el sesgo más fuerte;
3 pasadas de media móvil (ventana 18 m) para que sea conducible; clamp a
`ancho/2 − 1.5 m`. Misma cantidad de puntos que la centerline, cerrada.

`TrackData` ahora expone `Corners` y `RacingLine`. `TrackBuilder` los dibuja como gizmos
(ápices magenta/rojo por dirección, número `Tn` con `Handles.Label`, racing line cian).

### Verificación

- **EditMode (`TrackGeneratorTests` + `TrackAnalysisTests`): 14/14 pasan.** Los nuevos:
  determinismo de curvas y racing line por seed, numeración 1..N en orden de arco, ≥ 2
  curvas por trazado, cada curva gira de verdad (≥ 14°) con ápice entre entrada y salida,
  racing line con misma cantidad de puntos, cerrada y **dentro de la pista** (≤ ancho/2
  del eje en todo punto).
- **`Fase1SceneRender`** (nuevo, `-executeMethod`): PNG cenital rasterizado directo a
  `Texture2D` (sin escena/cámara) — asfalto, centerline, racing line, meta y ápices
  numerados. Es el diagnóstico visual del agente. Revisados seeds 7, 12345, 314: forma de
  circuito real, meta sobre recta, racing line clavando vértices.
- **`TrackDemoBootstrap`** (nuevo): MonoBehaviour que arma la vista in-browser (cámara
  cenital + LineRenderers + `TextMesh`) en runtime. Se usará en la iteración 3 para el
  build WebGL, junto con el coche.

### CI

`.github/workflows/build-and-publish.yml`: nuevo job **`test-editmode`**
(`game-ci/unity-test-runner@v4`, mismos secrets de licencia). `build-and-push-image` ahora
`needs: [build-webgl, test-editmode]` — nada se publica a GHCR si los tests fallan.
`build-webgl` sigue en paralelo con los tests.

### Notas

- La fuente bitmap 3×5 del PNG diagnóstico se ve tosca a 1500 px (glifos algo solapados).
  Es un diagnóstico interno, no se pulió más; los puntos de color y la forma comunican lo
  esencial.
- `ProjectSettings.asset`: el define `APP_UI_EDITOR_ONLY` (de `com.unity.dt.app-ui`, vía
  `com.unity.ai.inference`) que el Editor añade al target WebGL en cada apertura — en iter
  1 se revertía, pero reaparece siempre y es correcto (App UI queda editor-only en WebGL).
  A partir de iter 2 se commitea y se deja de pelear.
- El fallback de seed derivada subió de 1/200 (iter 1, jitter suave) a 6/200 con la
  modulación armónica más agresiva. Aceptable y determinista.

---

## 2026-09-02 — Fase 1 · Iteración 3: coche, conteo de vueltas, demo WebGL

Rama `fase-1-track-fisica` / PR #2. Cierra la parte del agente de Fase 1.

### Coche — `unity/Assets/Scripts/Vehicle/` (asmdef `AgenticRacing.Vehicle`)

- **`VehicleConfig`** (ScriptableObject): masa, drag, fuerza de motor/freno/coast, tope de
  velocidad, tasa de giro (con factor a alta velocidad y fade-in a baja), agarre lateral,
  downforce. En Fase 1 hay un coche y los valores son defaults aquí; Fase 3 hará que un
  único asset sea la fuente de verdad para que todos los coches sean idénticos (§3).
- **`CarController`** (`Rigidbody`, **sin WheelCollider** — §3): todo en `FixedUpdate`.
  Empuje sobre `+Z`, freno opuesto a la velocidad, coast al soltar; giro arcade por
  `MoveRotation` con autoridad que crece de 0 (parado) a full (baja vel) y baja a
  `HighSpeedTurnFactor` en el tope; **agarre lateral** que cancela casi toda la
  componente de velocidad lateral (lo que se escapa es derrape). `Throttle`/`Brake`/
  `Steer` son públicos: el teclado los escribe ahora, el RL de Fase 2 y el estratega de
  Fase 4 escribirán los mismos campos. Teclado (flechas o WASD) leído directo de
  `Keyboard.current` — `activeInputHandler: 1` (Input System nuevo), `Input.GetAxis` no
  existe.
- **`LapDetector`** (clase pura, testeable): plano de meta por `StartPosition` +
  `StartDirection`; cuenta vuelta sólo al cruzar hacia adelante **y** dentro de
  `triggerRadius` lateral de la meta (si no, cruzar el plano infinito en otra parte del
  circuito dispararía en falso); se desarma tras contar hasta volver ~8 m por detrás de
  la línea (anti-rebote).
- **`LapTracker`** (MonoBehaviour): enchufa `LapDetector` a un `Transform` de coche y un
  `TrackData`, cuenta contra `totalLaps`, emite `LapCompleted(int)` y `RaceFinished`, y
  reporta `Progress01` (muestra más cercana de la centerline, búsqueda en ventana O(1)).
  `Tick()` es público para tests deterministas.

### Demo jugable — `unity/Assets/Scripts/Demo/` (asmdef `AgenticRacing.Demo`)

`TrackDemoBootstrap` movido aquí desde `Track/` (un asmdef propio evita el ciclo
Track↔Vehicle). En `Start` arma la escena en runtime: superficie + `MeshCollider`,
centerline y racing line como `LineRenderer`, línea de meta, ápices numerados con
`TextMesh`, **un coche cubo** en la línea, `LapTracker`, cámara ortográfica cenital que
sigue al coche, y un `OnGUI` temporal con "LAP n / N" (el HUD real es DOM en Fase 4, §2.2).
Lee `?seed=` y `?laps=`.

`unity/Assets/Editor/Fase1WebglBuild.cs` (`-executeMethod`): crea la escena de un objeto
(`TrackConfig` + `TrackDemoBootstrap`), build WebGL a `Builds/track-demo` con compresión
desactivada y rutas relativas para servir desde cualquier estático.

### Verificación

- **EditMode: 19/19** (14 previos + 5 `LapDetectorTests`): una vuelta por bucle, ignora el
  plano lejos de la meta, no re-cuenta con jitter en la línea, 5 bucles → 5 vueltas,
  `LapTracker` emite `RaceFinished` en la vuelta objetivo. El job `test-editmode` de CI
  también los corrió en verde en el push de la iteración 2.
- **Build WebGL**: `Fase1WebglBuild` tardó 3 intentos por el identificador de template.
  Unity 6 no tiene `PROJECT:Default` ni `APPLICATION:Base` (la carpeta `Base/` del editor
  es un include, no un template seleccionable); el valor bueno es `APPLICATION:Default`
  (el que ya trae `ProjectSettings`). El primer intento lo dejó persistido como
  `PROJECT:Default` y rompió los siguientes — el script ahora **guarda y restaura** los
  `PlayerSettings` de WebGL que toca (template, compresión, dataCaching, runInBackground)
  para no ensuciar `ProjectSettings.asset` (CI conserva Brotli y la ruta `.br`/`.gz` que
  validó Fase 0). Con `APPLICATION:Default` el build compila (IL2CPP → WASM, ~lento en
  local).

### Bug de render en el build WebGL (URP) — diagnóstico y fix

El primer build WebGL de la demo compiló pero **la escena crasheaba en `Start`** con
`ArgumentNullException: shader`: `Shader.Find("Universal Render Pipeline/Unlit")` devuelve
`null` en el player. El segundo build ya no crasheaba (fallback de shader) pero **toda la
geometría con shader URP salía magenta**, con estos errores en consola:

```
Hidden/CoreSRP/CoreCopy shader is not supported on this GPU (none of subshaders/fallbacks are suitable)
Hidden/Universal Render Pipeline/StencilDitherMaskSeed ... not supported
Hidden/Universal/HDRDebugView ... not supported
```

Esos tres son **ruido conocido de URP + WebGL en Unity 6** (issue de Unity, no bloquean el
render). El magenta real era otra cosa: **Unity stripea las variantes de shader que sólo se
piden por `Shader.Find` en runtime** — nada referencia `URP/Unlit` en build-time, así que
queda sin subshader válido para WebGL.

**Fix** (commit `5a7a900`):
- `Fase1WebglBuild` fuerza `Universal Render Pipeline/Unlit` + `.../Lit` en **Always
  Included Shaders** durante el build, y restaura la lista de `GraphicsSettings` en el
  `finally` (mismo patrón que ya usa con los `PlayerSettings` de WebGL — no ensucia el
  proyecto ni afecta a CI, verificado con `git status`).
- `TrackDemoBootstrap.Tint`: el coche y los puntos de curva usaban el material por defecto
  de `GameObject.CreatePrimitive` (built-in *Default-Material*, inválido bajo URP → magenta);
  pasan a `UnlitColor` como el resto.

**Verificado en navegador real** (build local servido con `python -m http.server` en `:8123`):
la escena arranca sin excepciones (`[TrackDemoBootstrap] seed 7: 2500 m, 13 corners`),
y pista (gris), centerline (blanca), racing line (cian), meta (verde) y **coche (amarillo)**
renderizan con sus colores. El HUD `OnGUI` muestra "LAP n / N". Los eventos de teclado
sintéticos del automation no los toma el Input System, así que el **manejo real queda para
la prueba del humano**.

### Notas de `Fase1WebglBuild` (identificador de template)

Costó 3 intentos: Unity 6 no acepta `PROJECT:Default` (no hay template custom en
`Assets/WebGLTemplates/`) ni `APPLICATION:Base` (la carpeta `Base/` del editor es un
*include*, no un template seleccionable). El bueno es `APPLICATION:Default`, que ya trae
`ProjectSettings`. El primer intento lo dejó persistido roto; ahora el script guarda y
restaura `template`, `compressionFormat`, `dataCaching` y `runInBackground`.

### Poner la demo jugable en el navegador — tres bugs encadenados

El build WebGL compilaba pero llegar a una demo conducible costó tres fixes, cada uno con
su ciclo de build (~15 min de `emcc` en local):

1. **El coche no arrancaba — el teclado no llegaba.** Un HUD de diagnóstico mostró que
   `Keyboard.current` NO era null pero sus teclas nunca registraban (`anyKey` siempre
   false). Es el bug conocido del **Input System en builds WebGL de Unity 6**;
   `WebGLInput.captureAllKeyboardInput = true` no bastó. Fix: `ProjectSettings`
   `activeInputHandler` 1 → **2 (Both)**, y `CarController` lee con `Input.GetKey` (Input
   Manager clásico, fiable en WebGL desde siempre); el Input System queda de fallback.

2. **El coche caía a través de la pista.** Con el input ya funcionando, `thr` subía a 1.0
   al mantener la flecha pero `speed` seguía en 0. Causa: el `MeshCollider` de la cinta no
   frenaba la caída (o el coche aparecía por debajo), y en vista cenital ortográfica un
   coche cayendo se ve quieto porque `ForwardSpeed` sólo mide la componente horizontal.
   Fix: la pista de Fase 1 es plana en Y=0 sin plano de suelo, así que
   `CarController` pasa a `useGravity = false` + `FreezePositionY`; se quita el collider
   del cubo (Fase 1 no tiene muros ni contacto entre coches) y el spawn baja a `y = 0.4`.

3. **El HUD nunca pasaba de `LAP 1`.** El conteo por cruce del plano de meta es frágil:
   depende de la orientación exacta de la recta y del radio lateral. Reescrito a
   **detección por wrap de progreso**: el índice de muestra de centerline más cercano,
   normalizado 0..1 desde meta, tiene que subir por encima de `lapArmProgress` (0.65) y
   luego saltar a `< lapWrapProgress` (0.15). Es lo que usan los juegos de carreras y es
   inmune a la geometría. `LapDetector` (el test de plano) se conserva sin cablear, para
   el timing preciso de cruce que necesitará la telemetría del estratega en Fase 4
   (gaps, tiempos de vuelta).

También en el camino: crash inicial por `Shader.Find("URP/Unlit")` → `null` → `new
Material(null)`; y todo lo URP salía magenta porque Unity stripea las variantes de shader
que sólo se piden por `Shader.Find` en runtime. Fix: `Fase1WebglBuild` fuerza `URP/Unlit`
+ `URP/Lit` en Always Included Shaders durante el build (guarda/restaura
`GraphicsSettings`), y el coche/puntos usan `UnlitColor` en vez del *Default-Material*
built-in. Los errores `Hidden/CoreSRP/CoreCopy ... not supported on this GPU` son ruido
conocido de URP+WebGL en Unity 6, no bloquean.

### Cierre de Fase 1 — hecho

El dueño condujo el demo en navegador real: el coche responde a acelerador y curvas, y el
HUD cuenta las vueltas correctamente al cruzar meta. **Criterio de aceptación de §5
cumplido.** Se limpió el HUD de debug (queda "LAP n / N" + una línea con seed, nº de
curvas, km/h, progreso y controles). EditMode 20/20.

**Estado**: PR #2 (`fase-1-track-fisica` → `main`) listo para *ready* y merge. Fase 1
completa: circuito procedural determinista + numeración de curvas + racing line de
referencia + coche de física manual conducible + conteo de vueltas, todo verificado por
tests y por una prueba de conducción humana en el navegador.

**Anotado para más adelante**:
- El HUD real de la carrera es DOM (§2.2), no `OnGUI`; el `OnGUI` actual es temporal de
  Fase 1.
- `cameraSize = 42` da una vista algo cerrada; revisar zoom/seguimiento de cámara cuando
  haya varios coches (Fase 3) o si molesta al conducir.
- Fase 2 necesita que el `Agent` de ML-Agents escriba `Throttle`/`Brake`/`Steer` de
  `CarController` (ya son públicos justo para eso) y que existan ya los canales de
  directiva en las observaciones (§6.1, §11).

### Pendiente aparte (rendimiento de CI)

El job `build-webgl` de CI del push de la iteración 2 tardó > 50 min (vs ~11 min en Fase 0)
— vigilar si es el runner o si el proyecto con URP + más código se volvió así de lento;
puede necesitar caché de `Library` más agresiva o un runner más grande.

---

## 2026-09-02 — Fase 2 · Iteración 1: agente RL, observaciones (con canales de directiva), setup de entrenamiento

Código preparado; el entrenamiento en sí lo lanza el humano en la VM (§8). Rama
`fase-2-rl-agente`, PR #3 (draft).

### Decisiones tomadas al abrir la fase (aprobadas)

- **Raycasts vía `RayPerceptionSensorComponent3D`** de ML-Agents contra muros de borde
  invisibles (`TrackEdgeColliders`), detectando por tag `TrackEdge`. No se escriben los
  hits a mano en el vector de observación: el sensor los añade aparte.
- **Codificación de la directiva** (§6.1) = `aggression` (1 float 0..1) + `risk_tolerance`
  (1 float 0..1) + `directive` one-hot de 4 (`attack/defend/conserve/push`) = **6 floats**,
  aleatorizados cada episodio con niveles discretos `{0.15, 0.5, 0.85}` para los escalares
  (§6.4: niveles discretos, no continuo). `RaceDirective.RandomEpisode`.
- **Reset de episodio** = spawn en un punto de arco aleatorio del circuito, con ruido de
  rumbo (±10°) y lateral (±2 m). Un episodio = una vuelta (§2.1): termina al completar
  `Length * 0.99` de avance, salirse, atascarse, ir al revés, o timeout (`MaxStep = 4000`).

### Qué se implementó — `unity/Assets/Scripts/Agents/` (asmdef `AgenticRacing.Agents`)

- **`RaceDirective`** — struct con los 3 canales + `ObservationSize = 6`, `Neutral`, y
  `RandomEpisode(System.Random)`. Es la única superficie que el estratega de Fase 4
  escribirá (§6.8).
- **`RaceAgent : Agent`** — `[RequireComponent]` de `CarController` + `Rigidbody`.
  Observación vectorial de **12 floats**: velocidad longitudinal y lateral (norm.),
  error de rumbo vs tangente de la racing line, offset lateral del coche y de la racing
  line respecto a centerline (clamp ±2), progreso 0..1, y los **6 canales de directiva**.
  Acciones continuas `[steer, throttle, brake]`. Recompensa: progreso por metro
  (`ConsumeForwardDelta`, wrap-aware), castigo por frame (empuja a ir rápido), castigo por
  rozar/salir del borde, por atascarse, por ir al revés, bonus al cerrar la vuelta. Todos
  los pesos serializados para tunear entre corridas sin recompilar.
- **`TrainingArena`** — arena autocontenida: genera su circuito (seed propia), construye
  los muros de borde, y crea un coche con `RaceAgent` + `BehaviorParameters`
  (`RaceAgent`, obs 12, 3 acciones continuas) + `DecisionRequester` (periodo 5) + el ray
  sensor (9 rayos, 75°, 40 m). Todo en `Awake`, sin cablear escena. El coche va a la capa
  *Ignore Raycast* para que el sensor (origen dentro del `BoxCollider`) no se detecte a sí
  mismo; la colisión física con los muros sigue por la matriz de colisión.
- **`TrainingSceneBootstrap`** — rejilla de 9 arenas, seeds `1000+i`, separadas 4 km para
  que un ray sensor no vea la arena vecina.

### Build de entrenamiento — `Fase2TrainingBuild` (editor script)

Construye `Assets/Scenes/TrainArena.unity` (un objeto: `TrainingSceneBootstrap`) a un
**player `StandaloneLinux64` normal**, no un Dedicated Server. Bug encontrado: el subtarget
`Server` necesita el módulo "Dedicated Server" que §9 no pide instalar, y el valor `Server`
quedaba persistido en `EditorUserBuildSettings`; el script ahora fuerza
`StandaloneBuildSubtarget.Player` explícitamente. Verificado en local: genera
`train.x86_64` (149 MB) que la VM corre headless con `--no-graphics`.

### Config PPO — `training/config/race_ppo.yaml`

Behavior `RaceAgent`, `batch_size` 2048 / `buffer_size` 20480, lr 3e-4 linear, red MLP
`hidden_units` 256 × 2 capas (§2.3: MLP pequeño), `normalize: true`, `gamma` 0.995,
`max_steps` 20M, `checkpoint_interval` 500k (para `--resume` en spot). `training/README.md`
tiene el procedimiento completo: construir el player, subirlo, `mlagents-learn --env=...
--num-envs=4 --run-id=race01`, TensorBoard, y qué devolver al agente.

### Verificación

- Compila con ML-Agents 4.0.3 (Sentis 2.6.1, sin conflicto — §9). EditMode 20/20.
- `Fase2TrainingBuild` produce el player Linux headless (build local, `result=Succeeded`,
  149 MB).
- CI de PR #3: `test-editmode` verde; `build-webgl` (confirma que el código que depende de
  ML-Agents compila para WebGL/IL2CPP) — en curso.

### Bloqueado en el humano (§8)

Construir el player en máquina con licencia → subir a la VM spot → `mlagents-learn` →
devolver `RaceAgent.onnx` + logs de TensorBoard + run-id + nº de pasos + commit. La
iteración 2 de Fase 2 (análisis de curvas, tuneo de recompensas, validación del `.onnx` en
WebGL) empieza cuando eso vuelva.

### Segundo bug del build de entrenamiento (2026-09-03)

Primer intento en la NUC (`build.log`): la licencia Personal activó bien y los scripts
compilaron, pero `BuildPlayer` falló con `Currently selected scripting backend (Mono) is
not installed`. El proyecto trae el backend de Standalone en Mono (default de Unity) y la
NUC solo tiene "Linux Build Support (IL2CPP)" instalado — que es exactamente lo que pide
§9. Fix: `Fase2TrainingBuild` fuerza `PlayerSettings.SetScriptingBackend(
NamedBuildTarget.Standalone, ScriptingImplementation.IL2CPP)` antes de `BuildPlayer`. Los
paquetes de toolchain de Linux (`com.unity.toolchain.linux-x86_64-linux`,
`com.unity.sysroot.base`, `com.unity.sdk.linux-x86_64`) ya estaban resueltos, así que la
cross-compilación IL2CPP Windows→Linux tiene todo lo que necesita. Commit `1f397ee`.

### Tercer bug — falta el toolchain de cross-compilación win→linux (2026-09-03)

Segundo intento en la NUC (`build.log`, 826 KB — IL2CPP sí arrancó esta vez): el player
falló en el post-proceso con `No Toolchain found for host platform. Please install package
'com.unity.toolchain.win-x86_64-linux'` / `Unable to find an Linux Sysroot` /
`Internal build system error. BuildProgram exited with code 1`. El repo se preparó en Linux,
así que el manifest traía `com.unity.toolchain.linux-x86_64-linux` (host Linux → target
Linux) pero no el equivalente para host Windows. Fix: agregar
`com.unity.toolchain.win-x86_64-linux@1.1.0` a `unity/Packages/manifest.json` (y a
`packages-lock.json`) — misma versión que el resto de la familia, marcada `unity: 6000.3`,
y trae el sysroot Linux dentro del paquete. Los dos toolchains conviven; el editor elige el
que corresponde al SO del host, así que la build sigue funcionando desde Linux (CI, mi
máquina) y ahora también desde la NUC Windows.

### Build de entrenamiento OK en la NUC (2026-09-03)

Tercer intento en la NUC a `HEAD = 21ab188`: `result=Succeeded`. El humano copió
`unity/Builds/train-linux/` de vuelta al repo (ignorado por `.gitignore`, no se commitea).
Contenido verificado: ELF x86-64 IL2CPP (`GameAssembly.so` 112 MB, `il2cpp_data/`),
`UnityPlayer.so`, y el runtime de ML-Agents horneado —
`Unity.ML-Agents.dll`, `Grpc.Core.dll`, `Plugins/AnyCPU/libgrpc_csharp_ext.x64.so`,
más `AgenticRacing.{Agents,Track,Vehicle,Demo}.dll`. Es un player headless válido para
`mlagents-learn --env=`.

Resumen de los tres bugs de este build (todos por construir desde host Windows con solo
los módulos de §9): (1) subtarget `Server` persistido → forzar `Player`; (2) backend
Standalone en Mono → forzar IL2CPP; (3) faltaba el toolchain `win-x86_64-linux` en el
manifest (el repo se preparó en Linux). Ninguno lo puede atrapar CI, que solo compila
WebGL en Linux y nunca hace un player Windows→Linux.

**Siguiente, humano (§8)**: subir `train-linux/` a la VM spot de Azure (~16 vCPU, Ubuntu),
`chmod +x train.x86_64`, venv Python con `mlagents==1.1.0`, y
`mlagents-learn training/config/race_ppo.yaml --env=Builds/train-linux/train.x86_64
--no-graphics --num-envs=4 --run-id=race01` en tmux (`--resume` tras desalojo). Devolver
`results/race01/` (con `RaceAgent.onnx` + `events.out.tfevents.*`), run-id, nº de pasos y
el commit del player (`21ab188`). Con eso arranca la iteración 2 de Fase 2.

### Provisión de la VM de entrenamiento en Azure (2026-09-03/04)

**Cuota**: una suscripción nueva trae `Total Regional Spot vCPUs` (el nombre interno que usa
la API/CLI es `lowPriorityCores`, el portal lo muestra como "Spot vCPUs") en 3 por región —
insuficiente para 16 vCPU. La extensión `az quota` dio problemas (provider `Microsoft.Quota`
sin registrar, throttling de 3600 s, nombres de subcomando que cambian entre versiones). Lo
que sí funcionó: portal → **Quotas** → Compute → filtrar por el grupo **"Spot"** → única
opción **"Spot vCPUs"** → New quota request → nuevo límite. Se resolvió solo (sin ticket de
soporte) en unos minutos. Verificar el resultado con `az vm list-usage -l <region> -o table`
(no con `az quota show`, que depende del provider problemático) — la fila se llama
`Total Regional Low-priority vCPUs`.

**Capacidad**: además de cuota, el tamaño concreto (`Standard_F16s_v2`) puede no tener
capacidad spot en una región en un momento dado (`SkuNotAvailable`) — probar otra región o
`--zone`, no es un problema de configuración.

**Bug de la Unity CLI en `az`**: los errores de `az vm create` para plantillas ARM salen con
un traceback de Python roto (`RequestThrottled`/`RuntimeError: The content for this response
was already consumed`) que oculta el mensaje real de Azure — hay que leer el bloque
`Exception Details` más arriba en el mismo output, no el traceback final.

### SIGSEGV del player headless en la VM — `GfxDevice: Null` + Xvfb (2026-09-03/04)

Con el player subido y `mlagents-learn` instalado, el entorno crasheaba con
`UnityEnvironmentException: Environment shut down with return code -11 (SIGSEGV)` en cuanto
`mlagents-learn` intentaba levantar el primer entorno — sin más detalle, porque mlagents solo
reporta el exit code, no el log de Unity. Diagnóstico: correr el binario suelto con
`./train.x86_64 -batchmode -nographics -logFile -` sí imprime el log completo de Unity, y el
crash cae justo después de `Registered Communicator in Agent.`, durante el registro del
`Agent`/comunicador de ML-Agents — no en nuestro código (`TrainingArena`/`RaceAgent` son
observación vectorial + raycasts puros, sin cámaras ni RenderTexture).

Se probó primero una pista falsa: el log también mostraba una `DllNotFoundException` de
`libAppUINativePlugin.so` por falta de `libgtk-3.so.0` (paquete `Unity.AppUI`, ligado
probablemente por `com.unity.ai.inference`, sin relación con la escena de entrenamiento).
Instalar `libgtk-3-0` quitó esa excepción pero el SIGSEGV siguió idéntico — no era la causa.

**Causa real y fix**: `-nographics` fuerza `GfxDevice: Null`, una ruta de código con historial
de segfaults en builds Linux headless de Unity 6 combinados con el registro de Agent/comunicador
de ML-Agents. El fix es darle un framebuffer real por software en vez del device Null:

```bash
sudo apt-get install -y xvfb libgl1-mesa-dri mesa-utils
xvfb-run -a mlagents-learn training/config/race_ppo.yaml \
  --env=Builds/train-linux/train.x86_64 --num-envs=4 --run-id=race01
# nota: SIN --no-graphics — xvfb-run cumple esa función
```

Un solo `xvfb-run` alcanza para todos los `--num-envs`, porque `mlagents-learn` lanza los
subprocesos del player heredando el mismo `$DISPLAY`. Aplica esta nota a cualquier VM de
entrenamiento futura (incluida una reprovisión tras desalojo spot): **siempre** envolver
`mlagents-learn` en `xvfb-run -a` y nunca pasar `--no-graphics` en este proyecto.

### `UnityTimeOutException` tras arreglar el SIGSEGV — dos bugs más, y uno es de fondo (2026-09-04)

Con el SIGSEGV resuelto, `mlagents-learn` seguía sin conectar: el player arrancaba (confirmado
por `strace -f -e trace=execve,exit_group`: el `execve` del binario ocurre y devuelve 0) pero
Python nunca recibía el handshake y terminaba en `UnityTimeOutException` tras matarlo con
`SIGKILL` — sin crash, sin nada en `/var/crash` ni en `dmesg` (Ubuntu no loguea segfaults ahí
por defecto, `kernel.print-fatal-signals=0`; esa pista llevó a un callejón sin salida).
Se descartó contaminación de intentos previos (sin procesos huérfanos, puerto 5005 libre) y se
reprodujo **idéntico en la NUC local** (Ubuntu 26.04, conda con Python 3.10.12 pinneado — 
`mlagents==1.1.0` exige `Python >=3.10.1,<=3.10.12` exacto, no sirve cualquier 3.10.x), lo que
confirmó que no era un problema de la VM de Azure sino un bug real del proyecto/build.

Sin `-logFile -` a mano, `mlagents-learn` sí pasa su propio `-logFile` apuntando a
`results/<run-id>/run_logs/Player-0.log` — ahí apareció el error real, dos capas:

1. **El plugin nativo de gRPC no está donde `Grpc.Core` lo busca.** Unity empaqueta
   `libgrpc_csharp_ext.x64.so` en `train_Data/Plugins/AnyCPU/`, pero el wrapper `Grpc.Core`
   (empaquetado dentro de ML-Agents) lo busca con rutas de paquete NuGet genérico:
   junto al ejecutable, en `runtimes/linux/native/`, o en `../Plugins/x86_64/` relativos al
   ejecutable — ninguna coincide con `AnyCPU/`. Sin la librería, cae a
   `FileNotFoundException` → "Couldn't connect to trainer ... Will perform inference instead."
   **Fix, después de cada build**: copiar el `.so` junto al ejecutable:
   ```bash
   cp train_Data/Plugins/AnyCPU/libgrpc_csharp_ext.x64.so ./libgrpc_csharp_ext.x64.so
   ```
   (mismo directorio que `train.x86_64`). Aplica sin importar el scripting backend.

2. **IL2CPP no es compatible con el comunicador gRPC de ML-Agents — bug de fondo, no de
   nuestro código.** Con el `.so` en su lugar, el error cambia a:
   ```
   System.NotSupportedException: To marshal a managed method, please add an attribute named
   'MonoPInvokeCallback' to the method definition. The method we're attempting to marshal is:
   Grpc.Core.Internal.NativeLogRedirector::HandleWrite
   ```
   Es una limitación conocida de `Grpc.Core` bajo IL2CPP: IL2CPP compila todo AOT y no puede
   generar en runtime el trampolín nativo para ese callback, cosa que Mono sí hace vía JIT.
   Unity documenta que el **player de entrenamiento de ML-Agents debe usar el scripting
   backend Mono** — IL2CPP solo está soportado para *inferencia* (`com.unity.ai.inference`,
   que es lo que corre el WebGL del demo), no para el canal de entrenamiento. Esto contradice
   directamente CLAUDE.md §9 ("Linux Build Support (IL2CPP)... Ningún otro"), así que se
   consultó al dueño del proyecto antes de tocarlo en vez de decidir unilateralmente (§12).

**Estado al cierre de la sesión**: pendiente la decisión de instalar también "Linux Build
Support (Mono)" y ajustar `Fase2TrainingBuild.cs` para que el player de entrenamiento (solo
ese — el build de WebGL del demo sigue en IL2CPP sin cambios) use Mono en vez de forzar
IL2CPP. El forzado a IL2CPP de `1f397ee` fue, en retrospectiva, el fix equivocado para el
primer bug de esta saga ("Mono no instalado") — la solución correcta era instalar el módulo
Mono en la máquina de build, no forzar IL2CPP.

### Giro final: el player de entrenamiento pasa a Windows/Mono, no Linux (2026-09-04)

Al revisar qué módulo instalar, la CLI de Unity Hub (`unityhub --headless install-modules
--version 6000.3.22f1`) reveló que **"Linux Build Support (Mono)" ya no existe** — Unity 6
eliminó el scripting backend Mono para el target Linux Standalone; hoy Linux solo ofrece
IL2CPP. Como el bug de la entrada anterior (`Grpc.Core` + IL2CPP) es insalvable, un player
de entrenamiento **Linux** queda descartado por completo con esta versión de Unity, sin
importar el backend. La CLI sí lista `windows-mono` ("Windows Build Support (Mono)"), así
que — consultado y confirmado con el dueño del proyecto — el player de entrenamiento pasa a
**Windows Standalone + Mono**, corrido en la partición Windows de la NUC (o una VM Windows si
hiciera falta más cómputo más adelante). El WebGL del demo no cambia: sigue en IL2CPP porque
WebGL lo exige de todas formas, y no usa el comunicador de ML-Agents.

Cambios:
- `Fase2TrainingBuild.cs`: target `StandaloneWindows64`, `ScriptingImplementation.Mono2x`
  (ya no `IL2CPP`), salida `Builds/train-windows/train.exe`. Además, copia automáticamente
  `grpc_csharp_ext.x64.dll` junto al `.exe` tras el build — mismo bug de ubicación del
  plugin nativo que en Linux (ver entrada anterior), confirmado también en la variante
  Windows del paquete (`Library/PackageCache/com.unity.ml-agents@.../Plugins/ProtoBuffer/
  runtimes/win/native/grpc_csharp_ext.x64.dll`).
- CLAUDE.md §9: el módulo requerido pasa de "Linux Build Support (IL2CPP)" a "Windows Build
  Support (Mono)", con la explicación completa inline. §2.3 actualizada para no asumir Linux.
- `training/README.md`: instrucciones reescritas para Windows — ya no hace falta `Xvfb`
  (un `.exe` de Windows no necesita framebuffer virtual para correr headless, basta
  `-batchmode`), y se documenta el rango exacto de Python que exige `mlagents==1.1.0`
  (`>=3.10.1,<=3.10.12` — un `conda create python=3.10` sin fijar el patch puede darte una
  versión fuera de rango y fallar la instalación, como pasó esta noche).

Pendiente para la próxima sesión: instalar `windows-mono` en el Editor de la partición
Windows de la NUC (`unityhub --headless install-modules --version 6000.3.22f1 -m
windows-mono` — la sintaxis exacta de invocación de Hub varía por SO, en Windows lleva
`-- --headless` por ser una app Electron), reconstruir con el script actualizado, y
verificar que `mlagents-learn --env=Builds/train-windows/train.exe --num-envs=1` conecta
sin el `UnityTimeOutException` que bloqueó toda la sesión de hoy.

Pendiente de limpieza no urgente: `unity/Packages/manifest.json` y `packages-lock.json`
todavía traen los tres paquetes de toolchain de cross-compilación IL2CPP Linux
(`com.unity.sdk.linux-x86_64`, `com.unity.toolchain.linux-x86_64-linux`,
`com.unity.toolchain.win-x86_64-linux`) agregados en `21ab188`/commits previos — ya no hacen
falta porque el player de entrenamiento no se construye más para Linux. No estorban (UPM
simplemente no los usa), así que se puede posponer su remoción hasta que se abra el Editor
y se pueda dejar que resuelva el manifest de nuevo sin arriesgar un lock file inconsistente
editado a mano.

### El player Windows/Mono SÍ conecta — ahora falla por mismatch de observaciones (2026-09-05)

En la partición Windows de la NUC: `windows-mono` instalado, `git pull`, rebuild con
`Fase2TrainingBuild.Build` → `result=Succeeded`, `unity/Builds/train-windows/train.exe`
generado. Venv con `mlagents==1.1.0` (Python 3.10.12, `venv` no conda). El
`UnityTimeOutException` que bloqueó la sesión anterior **desapareció**: el player Mono
completa el handshake gRPC (`Connected to Unity environment with package version 4.0.3 and
communication version 1.5.0`, `Connected new brain: RaceAgent?team=0`, hiperparámetros
impresos). El giro a Windows/Mono resolvió el bug de fondo `Grpc.Core` + IL2CPP.

**Dos fallos nuevos, uno de entorno y uno de código:**

1. **Entorno Python — versiones fuera del pin de mlagents 1.1.0.** El `pip install` dejó
   `protobuf 7.36.1`, `numpy 2.2.6`, `onnx 1.22.0`, `torch 2.14.0`, cuando `mlagents==1.1.0`
   exige `protobuf<3.21`, `numpy<1.24`, `onnx==1.15.0` y `torch>=2.1.1` (sin tope → pip
   agarró la última). `torch 2.14` arrastró `numpy 2.x`; instalar `onnxscript` a mano
   (para un `ModuleNotFoundError` durante el export ONNX en el crash) subió `protobuf` a
   7.x. Síntoma final: `TypeError: Descriptors cannot be created directly` al importar
   mlagents. Fix: recrear el venv instalando **`torch==2.2.2` (CPU) ANTES** que
   `mlagents==1.1.0`, así pip no sube torch y resuelve el resto en rango. Con `torch 2.2.2`
   el export ONNX usa el exportador clásico y **no** necesita `onnxscript`. `training/README.md`
   actualizado con el orden de instalación y los pines exactos.

2. **Código — la escena de entrenamiento arma el agente sobre un GameObject ya activo.**
   `TrainingArena.BuildAgentCar()` hacía `GameObject.CreatePrimitive` (activo) →
   `AddComponent` de Rigidbody/CarController/BehaviorParameters/DecisionRequester/
   RayPerceptionSensorComponent3D/RaceAgent uno por uno. `Agent.OnEnable` corre
   `InitializeSensors()` en el instante en que el componente cae sobre un objeto activo, así
   que se ejecutaba contra un set de componentes a medio construir. Resultado: el
   ObservationSpec negociado en el handshake quedaba con **solo el vector sensor (12
   floats)**, pero en runtime el stream trae además el `RayPerceptionSensor` (9 rays × 3 =
   **27 floats**). `mlagents-learn` revienta en el primer `step()` con
   `UnityObservationException: Observation at index=0 ... Expected shape (12,) but got (27,)`
   y `Player-0.log` se llena de `Fewer observations (0) made than vector observation size
   (12). The observations will be padded.` (7200 veces en el smoke test).

   **Fix**: en `BuildAgentCar`, `body.SetActive(false)` justo tras crear el cubo, añadir y
   configurar **todos** los componentes en frío, y `body.SetActive(true)` una sola vez al
   final → `InitializeSensors()` corre una vez sobre el set completo. `PlaceAt` se mueve
   después de la activación porque necesita el `Rigidbody` que `CarController.Awake` cachea.
   Cambio acotado a `TrainingArena.cs`, sin tocar observaciones ni recompensas.

**Siguiente**: `git pull` en la NUC → rebuild `train.exe` con el mismo comando → reintentar
`mlagents-learn ... --run-id=win-test1 --force`. Si `Player-0.log` ya no trae el "Fewer
observations" y empieza a imprimir Step/Mean Reward, el smoke test pasó y se puede lanzar
`--run-id=race01` con `--num-envs` ajustado a los núcleos.

### Post-fix: entrena pero el env se cuelga a los ~63k steps — `runInBackground` (2026-09-05, cont.)

Rebuild con el fix de ensamblado en frío + venv sano (`torch 2.2.2`). El
`UnityObservationException` (12 vs 27) **desapareció**: `mlagents-learn` conecta, imprime
hiperparámetros y entrena de verdad — `Step: 50000` en 62 s, ONNX exportado a
`RaceAgent-63000.onnx`. Pero:

- A los ~63k steps: `[WARNING] Restarting worker[0] after 'The Unity environment took too
  long to respond...'` → reconecta una vez → `Environment timed out shutting down. Killing`
  → `TimeoutError: Workers {0} stuck in waiting state`. `timers.json` confirma el patrón:
  `env_step` 264.9 s con solo ~83 s contabilizados en hijos → ~180 s de Python **bloqueado
  esperando** al worker de Unity.
- `[INFO] RaceAgent. Step: 50000. ... No episode was completed since last summary` — en
  ~50k experiencias × 9 arenas (con `MaxStep = 4000`) no cerró **ni un** episodio.
- `Player-0.log` sigue con `Fewer observations (0) made than vector observation size (12)`
  ×7659 — el vector sensor se escribe vacío. (El ray sensor sí manda sus 27; por eso el
  trainer ya no crashea por shape, solo entrena con el canal vectorial en ceros.)

**Causa del cuelgue (alta confianza)**: `ProjectSettings/ProjectSettings.asset` tiene
`runInBackground: 0` y ML-Agents 4.0.3 **ya no** fuerza `Application.runInBackground = true`
(sí lo hacían versiones viejas). `mlagents-learn` lanza `train.exe` sin foco; con
`runInBackground` apagado, Unity estrangula el `FixedUpdate` en cuanto la ventana pierde
foco → el canal gRPC del lado Unity se atasca → `--timeout-wait` salta. Encaja con "corre
62 s y luego se cuelga" y con "ningún episodio completado" (la sim va a cámara lenta, tarda
una eternidad de wall-clock en llegar a `MaxStep`).

**Fix aplicado (sin tocar recompensas ni observaciones):**
- `TrainingSceneBootstrap.Awake` y `TrainingArena.Awake`: `Application.runInBackground = true`.
- `Fase2TrainingBuild.cs`: `PlayerSettings.runInBackground = true` antes de `BuildPlayer`
  (horneado en el player, además del runtime).
- `RaceAgent.cs`: tres `Debug.Log` one-shot (`Initialize`, `CollectObservations`,
  `OnEpisodeBegin`) para que el próximo `Player-0.log` diga de una vez si el override de
  `CollectObservations` corre, si `_track` está seteado, y con qué `centerline.Count` —
  el "Fewer observations (0)" sigue sin explicación estática y hay que verlo en runtime.

**Siguiente**: rebuild `train.exe` → `mlagents-learn ... --run-id=win-test1 --force`. Si el
env ya no se cuelga y empieza a acumular episodios, el cuelgue era `runInBackground`.
Pasar el nuevo `Player-0.log` (las líneas `[RaceAgent] ...`) para cerrar lo del vector
sensor en ceros.

### `runInBackground` resolvió el cuelgue; ahora: 0 episodios en 2M steps (2026-09-05, cont.)

Con `runInBackground` horneado, el env **ya no se cuelga**: `mlagents-learn` entrenó estable
a ~1000 steps/s hasta 2M+ steps sin timeout. Pero **ni un episodio completado en toda la
corrida**, y `Player-0.log` sigue lleno de `Fewer observations (0) ... size (12)` (~1 por
agente-step, ≈2.09M líneas).

Los logs one-shot que se agregaron dicen:
- `[RaceAgent] Initialize ok: car=True rb=True centerline=1098 racingLine=1098 width=12 maxStep=4000`
  — `Initialize` corre bien, la pista es válida, `MaxStep` seteado.
- `[RaceAgent] OnEpisodeBegin (first): track=True stepCount=0` — corre el primer episodio.
- `[RaceAgent] CollectObservations (first): ...` aparece **~7200 líneas después** de que
  empiezan los warnings de "Fewer observations". O sea: durante miles de steps el vector
  sensor (size 12) se escribe **vacío** antes de que el override de `CollectObservations`
  llegue a ejecutarse siquiera, y los warnings **continúan** después. Aritmética: ≈1 warning
  por agente-step ⇒ cada agente tiene un segundo VectorSensor de size 12 que nadie llena
  (el que `CollectObservations` llena es el otro) — apunta a **sensores duplicados** en el
  agente armado por código, o a que el override no despacha para todos los agentes.

No se pudo cerrar la causa por lectura estática (el ensamblado en frío debería haber
evitado la doble init; `InitializeSensors` solo se llama desde `LazyInitialize`, que
rehace la lista). Segundo build instrumentado a fondo en `RaceAgent.cs` (solo diagnóstico,
sin tocar recompensas/observaciones):
- primer `CollectObservations`: **dump por reflexión de `Agent.sensors`** (nombre + shape
  de cada sensor) — confirma o descarta duplicados.
- `OnActionReceived`: contador + log del primero y cada 20000, con `StepCount` y `fwdSpeed`
  — confirma si corre y a qué ritmo, y si el auto se mueve.
- `OnEpisodeBegin`: cuenta episodios, loguea los primeros 15 y cada 200, con
  `stepCount` y `prevEpisodeSteps` — dice si los episodios se repiten y de qué largo.
- `EndDiag`: loguea la primera vez que se dispara cada razón de fin (`offTrack`, `stuck`,
  `wrongWay`, `lap`) — dice si alguna termina el episodio o ninguna.

**Siguiente**: matar la corrida actual (entrena sobre basura), rebuild, correr 1-2 min,
pasar las líneas `[RaceAgent] ...` del `Player-0.log`.

### Diagnóstico 2: `OnActionReceived` nunca se llama; DecisionRequester agregado antes del Agent (2026-09-05, cont.)

El build instrumentado descartó la hipótesis de sensores duplicados: el dump por reflexión
dio `sensors=[TrackRays(27), VectorSensor_size12(12)]` — limpio, 2 sensores. Lo que sí
mostró:

- `[RaceAgent] OnActionReceived #1` **nunca aparece** — `OnActionReceived` no se invoca ni
  una vez en toda la corrida.
- `OnEpisodeBegin` se dispara para los 9 agentes al arranque y después **cicla ~cada 4000
  academy-steps** (= `MaxStep`), siempre con `prevEpisodeSteps=0`: el episodio termina por
  timeout de `MaxStep` sin que el auto haya movido un dedo.
- `CollectObservations` (el override) empieza a correr recién en la línea ~7252 del log
  (tras el primer ciclo completo de `MaxStep`); antes corre el `CollectObservations` base
  vacío (de ahí los `Fewer observations (0)`).

O sea: el pipeline de **decisión** funciona (llega `CollectObservations`), el de **acción**
no (nunca `OnActionReceived`), y el episodio solo avanza por el contador de `MaxStep` del
`Agent` base. El auto no se mueve → nunca sale de pista ni completa vuelta → "No episode
completed" para siempre.

**Causa probable**: `AddBehaviour` agregaba el `DecisionRequester` **antes** que el
`RaceAgent`. `DecisionRequester` tiene `[RequireComponent(typeof(Agent))]` y
`[DefaultExecutionOrder(-10)]`; agregarlo sin un `Agent` concreto presente hace que Unity
intente satisfacer el require con el tipo abstracto `Agent`, y su `Awake` a -10 (que engancha
`Academy.AgentPreStep += MakeRequests` y cachea `m_Agent = GetComponent<Agent>()`) corre
antes de que el `Agent` real se inicialice. Si `m_Agent` queda mal cacheado,
`MakeRequests` llama `m_Agent?.RequestAction()` sobre null cada step → `m_RequestAction`
nunca se pone en true → `OnActionReceived` no se ejecuta jamás.

**Fix aplicado** en `TrainingArena.cs`: `BehaviorParameters` + `RayPerceptionSensorComponent3D`
primero, después `RaceAgent`, y el `DecisionRequester` **al final** (`AddDecisionRequester`),
todo con el `body` inactivo y una sola activación. Diagnóstico ampliado en `RaceAgent.cs`:
el primer `CollectObservations` ahora loguea `behaviorType`, `actionSpec` (C/D), y si el
`DecisionRequester` tiene su `Agent` cacheado.

**Siguiente**: rebuild → correr 1-2 min → pasar las líneas `[RaceAgent] ...`. Si aparece
`OnActionReceived #1` y los episodios empiezan a cerrar por `stuck`/`offTrack`, el orden de
componentes era la causa.

### Smoke test OK — el reorden de componentes lo resolvió (2026-09-06)

Rebuild con `DecisionRequester` agregado al final. Los logs `[RaceAgent] ...` confirman todo
sano:
- `CollectObservations (first): behaviorType=Default actionSpec=C3/D0 decisionRequester=present
  drAgent=True sensors=[TrackRays(27), VectorSensor_size12(12)]`.
- `OnActionReceived #1` aparece (antes nunca) — pipeline de acción vivo.
- `first EndEpisode via 'stuck' ... at stepCount=151` y luego `via 'offTrack'` — los
  episodios cierran.
- Trainer: `Mean Reward: -1.11 / -1.22 / -1.32 / -1.08` a 50k–200k steps (antes: "No episode
  was completed"). `fwdSpeed` en los logs sube de ~0 a ~7–10 y `prevEpisodeSteps` de 151 a
  2000+ — la política aprende a avanzar. El reward negativo es esperado tan temprano
  (dominan `offTrackPenalty`/`stuckPenalty`); se evalúa la forma de recompensa con la
  corrida larga.
- `Fewer observations (0)` **desapareció** del `Player-0.log`.

Confirmado el diagnóstico anterior: `DecisionRequester` (con `[RequireComponent(typeof(Agent))]`
+ `[DefaultExecutionOrder(-10)]`) agregado antes de que exista un `Agent` concreto rompía el
cacheo de `m_Agent` y `RequestAction()` nunca se llamaba.

**Resumen de la sesión** — tres causas raíz encadenadas, todas ahora resueltas:
1. venv de Python con versiones fuera del pin de `mlagents 1.1.0` (`torch 2.14` arrastró
   `numpy 2.x`/`protobuf 7.x`) → recrear instalando `torch==2.2.2` (CPU) **antes** que mlagents.
2. `ProjectSettings.runInBackground = 0` + ML-Agents 4.x ya no lo fuerza → el player se
   estrangulaba sin foco y `mlagents-learn` lo mataba por timeout → `runInBackground = true`
   en la escena (runtime) y en `Fase2TrainingBuild` (horneado).
3. `TrainingArena` armaba el agente por código en un orden que ML-Agents no tolera
   (`InitializeSensors` contra un set a medio construir; `DecisionRequester` antes del
   `Agent`) → ensamblado en frío (`SetActive(false)` → componentes → `SetActive(true)`) y
   `DecisionRequester` al final.

**Siguiente**: lanzar la corrida real — `--run-id=race01`, `--num-envs` ajustado a los
núcleos físicos de la NUC, `max_steps: 20000000` (ya en el YAML). Dejar los `Debug.Log` de
diagnóstico puestos para revisar la salud de `race01` en su `Player-0.log`; quitarlos antes
de las corridas de población de Fase 3. Devolver `results/race01/` (con `RaceAgent.onnx` +
`events.out.tfevents.*`), run-id, nº de pasos y el commit del player. Con eso arranca la
iteración 2 de Fase 2 (análisis de curvas de TensorBoard, tuneo de recompensas, validación
del `.onnx` en WebGL).

### Análisis de `race01` — corrida completa de Fase 2 iter 1 (2026-09-06)

20M steps, `num_envs=4`, ~2.5–3 h de wall-clock en la NUC. `results/race01/RaceAgent.onnx`
= checkpoint del step 20000027, reward de ventana 3.89. **Health perfecta**: `Fewer
observations` = 0 en los 4 `Player-*.log`, sin excepciones, y las tres razones de fin
(`lap`, `offTrack`, `stuck`) se disparan — o sea, los autos **sí completan vueltas** a veces.

**Curvas (de `events.out.tfevents`):**

| Métrica | 50k | ~1.85M | 20M |
|---|---|---|---|
| `Environment/Cumulative Reward` | −1.11 | **3.35** | 3.74 |
| `Environment/Episode Length` | 42 | **179** | 143 |
| `Policy/Entropy` | 1.42 | 1.23 | 0.82 |
| `Losses/Value Loss` | 0.12 | 0.14 | 0.27 |

**Lectura:**
- **Convergió a ~1.85M steps** y de ahí quedó plano/ruidoso entre 3.3 y 3.7 (con caídas
  puntuales de ventana a 0.6–1.8) durante los **18M steps restantes**. ~85% del cómputo de
  esta corrida no aportó nada. Para iterar, `max_steps ≈ 4M` sobra.
- `Episode Length` tocó techo (~179) a 1.85M y después **bajó despacio** a ~143 mientras el
  reward seguía plano, y `Value Loss` **subió** (0.12→0.27): la política cayó temprano en un
  óptimo local y el resto de la corrida solo osciló. Señal de que la función de recompensa
  no tiene suficiente estructura para empujar más allá de "conduce más o menos".
- Estimado ~10–16% de episodios "buenos" (largos / vuelta completa), el resto `stuck`/`offTrack`.
- **Lejos del criterio de aceptación de Fase 2** ("varias vueltas consecutivas sin salirse
  en 3 seeds nuevas"). Es una línea base válida de iter 1, no un piloto entrenable.

**Nota importante para el tuneo**: los parámetros de recompensa de `RaceAgent`
(`progressRewardPerMetre`, `lapBonus`, etc.) son `[SerializeField]` pero **la escena los
arma por código sin overrides** (`TrainingArena.BuildAgentCar` hace `AddComponent<RaceAgent>()`
y nada más), así que en la práctica corren con los **defaults del C#**. Tunearlos = editar
`RaceAgent.cs` y reconstruir el player. La nota de `training/README.md` que dice
"serializado, sin recompilar" es incorrecta para este setup — corregir.

**Siguiente (iter 2 de Fase 2)**: bajar `max_steps` a ~4M; reforzar la recompensa para
premiar velocidad / seguir la trazada (candidatos: `progressRewardPerMetre` ponderado por
velocidad, `lapBonus` mayor, shaping denso con el error de rumbo y offset lateral que ya
están en las observaciones); quizá `beta` 0.005→0.01 por exploración. Después: validar el
`.onnx` en WebGL sobre 3 seeds no vistas (criterio de aceptación + riesgo de Fase 0).

### Fase 2 iter 2 — reward shaping aplicado (2026-09-06)

Cambios sobre `RaceAgent.cs` (defaults del C#, ver nota de la entrada anterior):
- `speedRewardPerSec = 0.03` — premio por segundo escalado por la fracción de velocidad
  hacia adelante (`ForwardSpeed / MaxSpeed`). Ataca directo el óptimo local de race01
  ("avanzar despacio"): ahora ir rápido paga aparte del progreso.
- `lineFollowRewardPerSec = 0.02` — shaping denso: premia ir alineado con la tangente de
  la trazada ideal **y** cerca de ella. **Gateado por velocidad** (`fwdFrac > 0.05` y el
  término se multiplica por `fwdFrac`) para que no se pueda farmear quieto y alineado.
- `lapBonus` 5 → 12; nuevo `fastLapBonus = 8` escalado por `1 - episodeSteps/MaxStep`
  (presupuesto de tiempo sin gastar al cerrar la vuelta) → premia cerrar rápido.

`training/config/race_ppo.yaml`:
- `max_steps` 20M → **4M** (race01 convergió a 1.85M).
- `beta` 5e-3 → **1e-2** (más exploración, para no recaer en el óptimo local).
- `checkpoint_interval` 500k → 250k, `keep_checkpoints` 10 → 20 (más resolución para
  elegir snapshot en una corrida corta).

`training/README.md`: corregida la nota que decía "serializado, sin recompilar" — hay que
recompilar; tabla de campos de recompensa actualizada con los nuevos.

Diagnósticos `[RaceAgent] ...` en `RaceAgent.cs` se dejan puestos para revisar la salud de
race02; se quitan antes de las corridas de población de Fase 3.

**Siguiente**: rebuild `train.exe` → `mlagents-learn ... --run-id=race02 --num-envs=4`.
Qué mirar en TensorBoard: que `Cumulative Reward` **no** se aplane a 1.85M como race01, que
`Episode Length` deje de decaer, y que el % de fines por `lap` suba. Pendiente aparte:
validar un `.onnx` en WebGL sobre 3 seeds no vistas (criterio de aceptación de Fase 2).

### race02 — el shaping ayudó poco; el cuello es la supervivencia del episodio (2026-09-06)

`race02` (4M steps, ~44 min, `num_envs=4`). Curvas:

| | 50k | ~1.25M | 4M |
|---|---|---|---|
| Cumulative Reward | −1.10 | **4.12** | 4.10 |
| Episode Length | 40 | 180 (pico 220 @350k) | 149 |
| Entropy | 1.42 | 1.34 | 1.27 |
| Value Loss | 0.04 | 0.20 | 0.25 |

Misma forma que race01: **converge a ~1.25M steps y se aplana** (plateau ~4.0 vs ~3.4 de
race01 — el shaping subió el techo ~0.6 y adelantó la convergencia, nada más). `beta=1e-2`
mantuvo la entropía alta (1.27 vs 0.82 de race01) pero no mejoró la política final.
`Std of Reward` ~9.8 (más alto que race01) y `Value Loss` sigue subiendo — más ruido, no
más competencia.

**Diagnóstico**: el problema no es el peso de las recompensas, es que **los episodios
duran ~150 steps (~3 s)** y casi nunca se completa una vuelta, así que todo el reward de
vuelta (`lapBonus`/`fastLapBonus`) es peso muerto y el shaping por-step que agregué era
además ~10–30× más chico que el reward de progreso y ~1000× más chico que
`offTrackPenalty=1.0`.

**Hipótesis fuerte**: cada episodio spawnea el auto **parado** (`OnEpisodeBegin` pone
velocidad 0), y el check de `stuck` (`< 1 m/s` por 3 s continuos) lo mata a los ~150 steps
antes de que arranque. El RL nunca llega lo bastante lejos para que el `lapBonus` reciba
crédito.

**Cambios iter 3** (`RaceAgent.cs`):
- **Rolling start**: `_rb.linearVelocity = trackDir * launchSpeed` (8 m/s) al spawnear.
- **`stuck` armado**: solo cuenta después de que el auto superó `stuckSpeed` al menos una
  vez en el episodio (`_stuckArmed`) — un arranque fallido ya no mata, un stall a mitad de
  pista sí. `stuckSpeed` 1.0 → 0.5.
- Shaping con magnitud útil: `speedRewardPerSec` 0.03 → 0.15, `lineFollowRewardPerSec`
  0.02 → 0.10.
- Instrumentación: `EndDiag` ahora lleva un tally acumulado (stuck / offTrack / wrongWay /
  lap) y `mean lapArc`, logueado cada 500 fines — para ver la distribución real de por qué
  mueren los episodios.

**Siguiente**: rebuild → `--run-id=race03 --num-envs=4`. Confirmar en el `Player-0.log` el
split de `end reasons` y si `Episode Length` sube. Si sigue plano y corto, toca ver un
checkpoint corriendo en el Editor (Behavior Type = Inference Only) antes de seguir tuneando.

### race03 — el rolling start rompió el plateau (2026-09-06)

`race03` (4M steps, iter 3). **Cambio cualitativo**, no incremental:

| | 50k | ~2M | 4M |
|---|---|---|---|
| Cumulative Reward | −1.71 | ~9 | **~10** (checkpoint window hasta 31 @2.5M, 12.6 final) |
| Episode Length | 242 | 737 | **759** (subiendo) |
| Entropy | 1.42 | 1.35 | 1.32 |
| Value Loss | 0.04 | 0.15 | 0.16 (estable) |

- race01/race02 se aplanaban en reward ~4 con episodios de ~150 steps **decreciendo**.
  race03: episodios **5× más largos (~750) y creciendo**, reward de −2 a ~10 **sin
  aplanarse a 4M**, `Value Loss` estable (no la deriva de race01/02).
- El tally `end reasons @ 500` (temprano, política aún mala) daba stuck 80–85%, lap 6–12%.
  Hacia el final del run muchos episodios llegan a `MaxStep=4000` (`prevEpisodeSteps=4000`
  en los logs) — sobreviven los 80 s pero todavía no cierran la vuelta consistentemente
  (`mean lapArc` ~240 m temprano). El `stuck` temprano era el spawn parado, confirmado.
- Sin `Fewer observations`, sin excepciones, log limpio (325 líneas).

**Conclusión**: el rolling start + `stuck` armado eran el bloqueo real; el shaping de
velocidad/trazada ahora sí tracciona. La política aún es inconsistente (`fwdSpeed` salta
entre 12 y −2 m/s dentro de un episodio) y no llega al criterio de Fase 2, pero la curva
apunta en la dirección correcta y no había convergido.

**Cambios iter 4**:
- `race_ppo.yaml`: `max_steps` 4M → **10M** (seguía subiendo), `checkpoint_interval` de
  vuelta a 500k.
- `RaceAgent.cs`: el tally de `end reasons` ahora es ventana móvil de los últimos 400
  (no promedio de vida dominado por los episodios malos del principio) y **cuenta también
  los fines por `MaxStep`** (`_diagCounted` / rama `maxStep` en `OnEpisodeBegin`).

**Siguiente**: rebuild → `--run-id=race04 --num-envs=4` (~1.8 h). Mirar si el reward sigue
subiendo más allá de ~10 y si el % de `lap` en la ventana móvil crece. Si se aplana,
comparar con ver un checkpoint en el Editor.

### race04 regresó — race03 fue varianza, no breakthrough + harness de eval offline (2026-09-06)

`race04` (10M steps, iter 4). **Volvió al pozo de race01/race02**:

| | race01 | race02 | race03 | race04 |
|---|---|---|---|---|
| steps | 20M | 4M | 4M | 10M |
| Cumulative Reward (plateau) | ~3.4 | ~4.0 | ~10 ↑ | ~4.8 |
| Episode Length final | 143 ↓ | 149 ↓ | 759 ↑ | 155 ↓ |
| `stuck` al final del run | — | — | bajando | **96%** |

El tally de ventana móvil de race04 da **`stuck` 96–97% constante** durante los 10M steps,
`lap` 3–4%, `mean lapArc` clavado en ~245 m (10% de vuelta). race03 (que había roto el
plateau) fue **varianza favorable de una corrida**, no un cambio real — con más entrenamiento
race04 volvió al mismo óptimo local.

**El exploit, con números**: episodio típico de race04 → progreso `245 m × 0.02 = 4.9`,
menos `timePenalty` (~0.08), menos `stuckPenalty` (−1) ≈ **+3.8** = el plateau exacto. El
agente aprendió la política óptima *dado este reward*: usar el rolling start, rodar ~245 m,
**parar y cobrar el −1 de `stuck`, que además termina el episodio rápido**. Empujar más
arriesga `offTrack` o el castigo de tiempo hasta `MaxStep`. Morir rápido es el mejor retorno.
El `stuck` que *termina* el episodio es una vía de escape.

**Harness de eval offline** (para ver qué hace la política sin abrir el Editor, que es el
lado flaco del dueño del proyecto):
- `EvalRunner.cs` — corre la grilla de arenas en `BehaviorType.InferenceOnly` con un `.onnx`
  baked (`Resources/Eval/RaceAgent`), 120 s, y loguea un reporte agregado: nº de episodios,
  pasos medios, % de progreso de vuelta, split de fines, y velocidad/throttle/steer medios.
  Se apoya en el evento `RaceAgent.AnyEpisodeEnded`.
- `Fase2EvalBuild.cs` — copia el `.onnx` (de `-evalModel <path>`, o `AGENTIC_EVAL_MODEL`, o
  `results/race04/RaceAgent.onnx`) a `Assets/Resources/Eval/`, arma la escena `EvalArena`
  con `EvalRunner`, y buildea `Builds/eval-windows/eval.exe` (Windows/Mono).
- `AgenticRacing.Agents.asmdef` ahora referencia `Unity.InferenceEngine` (para `ModelAsset`).
- `.gitignore`: `Assets/Resources/Eval/`, `EvalArena.unity`, `unity/Builds/`.

**Siguiente**: evaluar race03 y race04 con el harness para confirmar el patrón "lanza y
para", y después reestructurar el reward — `stuck` deja de terminar el episodio (pasa a
penalización por segundo, terminación solo como red de seguridad a los ~8 s) y bajar
`timePenaltyPerStep`.

### Eval de race03 vs race04 — la política no frena; reestructura de reward iter 5 (2026-09-06)

`eval.exe` (120 s, InferenceOnly) sobre los dos modelos:

| | race03 | race04 |
|---|---|---|
| meanForwardSpeed | 6.7 m/s | 15.8 m/s |
| meanThrottle | 0.18 | 0.64 |
| **meanBrake** | **0.02** | **0.02** |
| meanAbsSteer | 0.43 | 0.36 |
| meanEpisodeSteps | 1768 (~35 s) | 673 (~13 s) |
| meanLapProgress | 8% | 9% |
| fin dominante | maxStep 76%* | stuck 86% |

\* el bucket `maxStep` de race03 está inflado por ~9 resets espurios del harness al
cargar el modelo — corregido para el próximo eval (`OnEpisodeBegin` ahora solo cuenta
`maxStep` si `_episodeSteps >= MaxStep-5`).

**Hallazgo central**: `meanBrake ≈ 0.02` en **ambos** — la política **nunca frena**. Sin
frenar no se puede tomar una curva a velocidad en esta física, así que solo le quedan dos
opciones y aprendió una u otra según la corrida: reptar a ~7 m/s para poder doblar sin
frenar (race03) o acelerar a fondo y salirse / pararse (race04). Las dos cubren ~9% de la
vuelta: **se atascan en la primera curva de verdad** (~245 m ≈ donde está esa curva).

**Reestructura de reward (iter 5, `RaceAgent.cs`)** — quitar las dos formas de "ganar" sin
manejar bien:
- **`stuck` ya no termina el episodio**. Era la vía de escape (reptar un poco, parar,
  cobrar el −1 y resetear). Ahora es `stuckPenaltyPerSec = 0.3` mientras está parado, con
  corte duro solo a los 8 s (auto genuinamente muerto).
- **El time-penalty plano (`0.0005`/step) se reemplaza por una penalización por ir lento**:
  `slowPenaltyPerSec = 0.25`, rampa desde 0 en `targetSpeedFrac = 0.30` de `MaxSpeed`
  (~16 m/s) hasta el máximo cerca de la parada. El plano castigaba por igual episodios
  largos → premiaba morir rápido. Economía nueva: reptar a ≤7 m/s queda ≈0 o negativo,
  manejar a 12+ paga, a 16+ sin penalización.
- `speedRewardPerSec` 0.15 → 0.30.
- `stuckSeconds` 3 → 8 (solo red de seguridad).

`race_ppo.yaml`: `max_steps` 10M → 6M (el reward cambió, es un problema nuevo; extender si
promete). run-id race05.

**Siguiente**: rebuild `train.exe` → `--run-id=race05 --num-envs=4` (~1 h). Mirar en el
`Player-0.log` el split de `end reasons` (ahora `stuck` no debería dominar) y `mean lapArc`.
Después evaluar el `.onnx` con el harness: lo que quiero ver es `meanBrake` > 0 y
`meanLapProgress` subiendo por encima del 9%.

### race05 — falla en la misma curva, ahora por `offTrack`; ¿es la pista manejable? (2026-09-07)

`race05` (6M steps, iter 5). El exploit de `stuck` **desapareció** (2-3%) pero:

| | 50k | ~2.7M | 6M |
|---|---|---|---|
| Cumulative Reward | −34 | ~4.9 | **~4.8** (plateau) |
| Episode Length | 694 | ~117 | ~113 (~2.3 s) |
| Value Loss | 0.15 | 0.55 | 0.52 (alto) |

**`end reasons`: `offTrack` 95-97%**, `lap` 2%, `stuck` 2%. `mean lapArc` **bajó a ~180 m**
(era 245). O sea: el auto ahora va más rápido pero se sale de pista a los ~180 m, ~2.3 s.
Es el modo "acelera y se estrella" de race04 pero más marcado — con reptar penalizado, la
única estrategia que encontró es ir rápido, y **no puede tomar la primera curva**.

Cinco corridas, cinco variantes de recompensa, **el mismo fallo**: el auto no pasa la
primera curva de verdad (~180-245 m). Ya no parece un problema de pesos de recompensa. La
`slowPenalty` de iter 5 puede incluso estar impidiendo aprender a frenar (frenar para una
curva baja la velocidad → dispara la penalización justo cuando hace falta).

**Experimento decisivo**: un **controlador scripted** (racing-line pure-pursuit + control de
velocidad que frena según la curvatura próxima) — si *él* da vueltas, el problema es de
aprendizaje/recompensa; si *él tampoco*, es la física del auto o el trazado (curvas
demasiado cerradas), y ninguna recompensa lo arregla.

Implementado:
- `RaceAgent.Heuristic()` reescrito: seguidor autónomo de `RacingLine` (antes era solo
  teclado, tras `#if ENABLE_LEGACY_INPUT_MANAGER`). Es también la semilla de la heurística
  fija de Fase 6.3.
- `EvalRunner` + `eval.exe -heuristic`: corre `BehaviorType.HeuristicOnly` en vez de cargar
  un modelo. `Fase2EvalBuild` ya no exige que exista el `.onnx`.

**Siguiente**: rebuild el eval player → `eval.exe -heuristic` y `eval.exe` (modelo race05).
Comparar `meanLapProgress` y `meanBrake`. Si la heurística da vueltas y el modelo no →
problema de RL (subir exploración, quitar/suavizar `slowPenalty`, reward de trazada más
fuerte). Si la heurística tampoco → revisar `CarController` (grip lateral, `HighSpeedTurnFactor`)
y la validación de curvatura de Fase 1.

### Eval -heuristic v2: muere en cualquier lado, siempre pegado a un muro (2026-09-07)

Los 24 `end #N` del segundo `-heuristic`: **todos `stuck`, `speed=0.0`**, `lap%` disperso
(8, 17, 27, 31, 44, 45, 55, 62, 72, 85, 89, 95...) y `lapArc` de 0 a 471 m. Varios en
`lapArc≈0` con `steps≈400` = spawneó, se frenó casi al instante, y quedó 8 s clavado. El
`lateral` al morir: muchos en 4–7 m de 6 → **contra el muro o pasado el borde**.

**Diagnóstico**: no hay una curva asesina; el patrón es que el control (heurística *y* RL)
tarde o temprano roza un muro, y **cualquier contacto que le baje la velocidad es
terminal**: con `SteerFadeInSpeed = 1.5`, por debajo de 1.5 m/s el `authority` de dirección
es **0** — un auto pegado al muro no puede girar la trompa para salir, y encima acelera
contra él. Además la línea ideal (`BuildRacingLine`) **pega a los bordes** (swing de
+maxOffset a −maxOffset por curva, con `maxOffset ≈ halfWidth − margin`), así que premiar
"seguir la línea" empujaba el auto a los muros. Y `wrongWay` (2.5 s en reversa → fin de
episodio) prohíbe la única maniobra de escape que usaría un humano.

**Cambios (física + recompensa + heurística):**
- `VehicleConfig.SteerFadeInSpeed` 1.5 → **0.4**, nuevo `MinSteerAuthority = 0.25` — hay
  algo de dirección aún parado, un auto clavado puede zafar. `CarController.ApplySteering`
  usa `Lerp(MinSteerAuthority, 1, speed/SteerFadeInSpeed)`.
- `RaceAgent`: penalización de borde **suave**, rampa desde `edgeSafeFrac = 0.55` del
  half-width hacia afuera (antes: nada hasta el borde, después salto). `lineFollowReward`
  0.10 → 0.05 (deja de empujar a los muros). `wrongWay` 2.5 → 4.5 s y **solo arma tras
  15 m de progreso** (una reversa corta para despegarse no mata).
- `Heuristic`: apunta a un blend 50/50 centerline+racing-line (no a la línea que pega al
  muro) y **reversa de escape** cuando está lento y contra un borde.

**Siguiente**: rebuild eval → `eval.exe -heuristic`. Si ahora la heurística da vueltas →
la física era el bloqueo y toca reentrenar con estos cambios (nuevo run-id). Si sigue
muriendo → mirar `TrackAnalysis` (curvatura máxima que valida Fase 1 / si el generador
hace curvas imposibles).

### Gate resuelto: la pista ES manejable — a corrida limpia (race06) (2026-09-07)

Tercer `eval -heuristic`, con los arreglos de física/recompensa de `e718c7b`. Sigue
imperfecto (18/24 `stuck`) pero apareció la señal que importa:

    end #20  maxStep  lapArc=1691m  steps=4000  speed=21.0  lateral=-0.8

**Un auto manejó 1691 m — el 82% de la vuelta — a 21 m/s** y todavía iba al acabarse el
tiempo. Con la física anterior eso era imposible. Otros llegaron a 383/418/506/509 m. Los
`lateral` de muerte bajaron de 4–7 (contra el muro) a ~3 (media pista): ya no es
"clavado al muro", es "perdió velocidad y no re-aceleró".

**Conclusión**: el entorno es aprendible; el bloqueo era la física de muros no
recuperables, ya arreglada. Los fallos que quedan en la heurística son de la heurística
(herramienta de diagnóstico), no del entorno — un agente RL aprende su propia recuperación.

**Decisión (acordada con el dueño del proyecto)**: no seguir con micro-iteraciones de
recompensa. Camino (a): una corrida limpia y larga con los arreglos actuales, y recién
si *esa* se estanca, tuning de RL deliberado.

Cambios:
- `RaceAgent.Heuristic`: la reversa de escape ahora está fuertemente acotada
  (`_wallJamTimer` > 0.5 s genuinamente clavado → un burst de 0.8 s vía `_escapeUntil`),
  para que no sea un ciclo límite de baja velocidad (v1 reversaba en cualquier momento
  lento cerca del borde y no re-aceleraba nunca; `#14`: 48 s parado).
- `race_ppo.yaml`: `max_steps` 6M → 10M, run-id race06.

Pendiente para *después* de race06 (no ahora): sumar curvatura hacia adelante al vector de
observaciones (cambio de rumbo de la centerline a ~15/30/50 m) — el agente hoy solo "ve"
con raycasts a 40 m y no anticipa curvas. Se difiere para aislar el efecto de los arreglos
de física/recompensa primero.

**Siguiente**: rebuild `train.exe` (trae física + recompensa nuevas, ningún build de
entrenamiento las tiene aún) → `--run-id=race06 --num-envs=4` (~1.7 h). No tocar nada
hasta que termine. Mirar: `end reasons` (¿baja `stuck`/`offTrack`, sube `lap`?),
`Cumulative Reward` (¿pasa el plateau de ~4-5?), `Episode Length` (¿sube sostenido?).

### race06 = plateau otra vez → confirmado: brecha de RL, no del entorno. iter 7 (2026-09-07)

`race06` (10M steps, con los arreglos de física/recompensa de `e718c7b`): **mismo plateau**.
Cumulative Reward −37 → ~4.8 y plano desde ~2.7M. Episode Length ~100 (peor que race05).
`end reasons`: **`offTrack` 98%**, `lap` 2%, `mean lapArc` ~207 m (~10% de vuelta). Entropía
1.42→1.23, Value Loss 0.29→0.52 (subiendo).

Contradicción que lo decide: **la heurística, con este mismo build, llevó un auto a 1691 m
(82%) a 21 m/s. El RL se estanca en 207 m.** Mismo entorno → **es problema de aprendizaje
del RL, no del entorno**. Los arreglos de física/recompensa solos no alcanzan.

Qué tiene la heurística que el RL no:
1. **Anticipación**: escanea 55 m de curvatura hacia adelante para fijar velocidad de
   entrada. El RL solo "veía" con 9 raycasts a 40 m (2.7 s a 15 m/s). No ve venir la curva.
2. Frena para las curvas explícitamente. El RL nunca aprendió (eval: `meanBrake` ~0.02) —
   y la `slowPenalty` de iter 5 castigaba justo el frenado que hace falta.

**iter 7 — darle al RL la percepción + la estructura de recompensa que funciona en la
heurística** (obliga a reentrenar de cero, cambia el vector de observación):
- **+3 observaciones de curvatura hacia adelante**: cambio de rumbo con signo de la
  centerline en tramos ~0-22 / 18-45 / 40-75 m. `VectorObservationSize` 12 → 15
  (`RaceAgent.ObsSize`, referenciado desde `TrainingArena`).
- **Raycasts 40 → 70 m** (`RayPerceptionSensorComponent3D.RayLength`).
- **Recompensa de velocidad con pico en una velocidad OBJETIVO por curvatura**
  (`TargetSpeed()`, misma lógica que la heurística: `Lerp(straightSpeedFrac 0.42,
  cornerSpeedFrac 0.12)` de `MaxSpeed` según el giro próximo). El reward cae a ambos lados
  del objetivo → frenar en curva paga, pasarse de largo no. Reemplaza el `speedReward`
  proporcional a la velocidad + la `slowPenalty` absoluta de iter 5.
- Heurística: la reversa de escape ahora está acotada (`_wallJamTimer`/`_escapeUntil`).

`race_ppo.yaml`: run-id race07, `max_steps` 10M.

**Siguiente**: rebuild `train.exe` → `--run-id=race07 --num-envs=4` (~1.7 h). Si sigue
plano tras esto → toca curriculum (empezar en seeds de baja curvatura) y/o red más grande
(hoy 256×2). Si mejora → eval con el harness (`meanBrake` > 0, `meanLapProgress` alto) y a
cerrar Fase 2.

### race07 = ganancia chica; camino A: imitación desde la heurística (2026-09-07)

`race07` (10M, +obs de anticipación + recompensa de velocidad objetivo): Cumulative Reward
plateau **~6.5** (era ~4.8 en race06), **Value Loss 0.22 estable** (era 0.52 subiendo),
`lap` en `end reasons` 2% → **3%**. Las observaciones de curvatura sí ayudaron (el crítico
entiende el mundo, el reward subió ~40%), pero el fallo central no se movió: `offTrack` 97%,
`mean lapArc` ~213 m.

Diagnóstico firme: **hard-exploration**. La recompensa ya es correcta (race07 lo probó); el
agente no *descubre* la maniobra de curva (frenar→girar→acelerar, ~1-2 s coordinados)
porque el 97% de los intentos mueren en <2 s. No se arregla con más shaping.

**Camino A (elegido): imitación desde la heurística.** Ya tenemos un "profesor" que hace
el 82% de la vuelta.

- `RaceAgent.Heuristic` endurecido: recovery más amplio — lento en cualquier lado por
  >1.2 s (no solo contra un muro) → burst de reversa alineándose con la dirección local de
  pista; lookahead corto (9 m) cuando va lento para no perseguir un punto detrás de un muro
  o cruzado. Ataca los dos modos de fallo del eval (clavado al muro / círculo mediopista).
- `EvalRunner` gana `-record`: implica `-heuristic`, corre 300 s, adjunta un
  `DemonstrationRecorder` a cada agente → `Builds/eval-windows/demos/RaceHeuristic_*.demo`.
  `Fase2EvalBuild` no cambia (el flag es de runtime).
- `race_ppo.yaml`: bloques `behavioral_cloning` (`demo_path: training/demos`, `strength 0.5`,
  `steps 2M`) + `gail` (`strength 0.15`, `use_actions: true`). run-id race08.
- `training/demos/` (con `.gitkeep`); `*.demo` gitignoreado (datos).
- `training/README.md` §6: workflow de grabación + entrenamiento con imitación.

**Siguiente**:
1. rebuild eval → `eval.exe -record` → copiar `demos\*.demo` a `training\demos\`.
2. (opcional pero recomendado) `eval.exe -heuristic` para ver si el recovery endurecido
   subió el % de vuelta de la heurística — mejor profesor = mejores demos.
3. rebuild `train.exe` (trae el Heuristic nuevo, aunque no se use en training) →
   `--run-id=race08 --num-envs=4`.

Mirar en race08: ¿el agente aprende a frenar y pasar curvas (baja `offTrack`, sube `lap`)?
Si BC+GAIL tampoco lo saca → curriculum + red más grande.

### El log [Heur] resolvió el misterio: la heurística SÍ maneja (2026-09-07)

`eval.exe -heuristic` con el log `[Heur]` (1x/s): los `[Heur]` muestran autos a **18-22 m/s
sostenido, `slip` 0-2°** (sin derrape), frenando suave para curvas, `lapArc` subiendo
parejo. `end #1 'maxStep': lapArc=1595m speed=19.2` — un auto hizo **1595 m a ~20 m/s los
80 s completos**. La heurística maneja bien.

El `meanForwardSpeed=5.3` del REPORT era un promedio engañoso: **solo ~2-3 de los 9 autos
manejan; los otros 6-7 quedan en diente de sierra cerca del spawn** (`end #2`: 154 m y
parado 70 s). Pegan una curva que no pasan → pared → se frenan → el recovery reversa 0.7 s,
los saca apenas, vuelven a frenarse — y como el chequeo de `stuck` usa `Abs(velocidad)`,
los bursts de reversa (-3 m/s) le resetean el timer: nunca terminan el episodio, sobreviven
80 s sin avanzar, y arrastran el promedio (y llenarían las demos de basura).

**Fix — terminación por estancamiento de progreso** (`RaceAgent.OnActionReceived`):
`stallSeconds = 5`, `stallMinMetres = 8` — si el auto cubrió < 8 m de pista en los últimos
5 s (y ya arrancó, `_episodeSteps > 60`), `EndEpisode` con penalización. Independiente de
la velocidad instantánea, así que el sawtooth no lo esquiva. Los autos malos mueren rápido
→ **respawnean** → la mayoría de spawns dan buen manejo → demos mayormente de 20 m/s. Sirve
también al RL (mata la degeneración sawtooth si el agente la descubriera).

**Siguiente**: rebuild eval → `eval.exe -heuristic` para confirmar que ahora el REPORT
tiene `meanForwardSpeed` ~15-20 y `meanLapProgress` alto → `eval.exe -record` → race08.

### Causa raíz: curvas de 12 m no navegables → subir MinCornerRadius (2026-09-07)

El log `[Heur]` mostró que la heurística maneja bien (18-22 m/s, sin derrape) en los tramos
suaves, pero con el stall check activo el 98% de los episodios terminan por `stall` a ~180 m:
**los autos pegan una curva que no pueden tomar.**

Cuenta: `TrackGenerator.TrackParams.Default.MinCornerRadius = 12f`. Con la física del auto,
el radio de giro a full lock es ~11.5 m a 20 m/s y ~16 m a 25 m/s → una curva de 12 m es
tomable solo al límite a 20 m/s y **imposible a 25**. Ni la heurística ni el RL frenan lo
suficiente. El auto de 1595 m fue un seed con todas las curvas suaves.

Cambios en `TrackParams.Default` (parámetro de Fase 1; afecta también el WebGL — pistas
menos retorcidas, consultado con el dueño del proyecto):
- `MinCornerRadius` 12 → **20 m** (tomable a ~25 m/s con margen).
- `HarmonicAmpMax` 0.30 → 0.24, `MaxHarmonicFreq` 5 → 4 (pistas inherentemente más suaves,
  menos rechazos del generador).
- `MaxAttempts` 40 → 80 (headroom; si un seed no genera pista válida, `Generate` *lanza* y
  rompería el arranque de la escena).

**Siguiente**: rebuild eval → `eval.exe -heuristic`. Si el `REPORT` ahora tiene
`meanLapProgress` alto y `stall`/`offTrack` bajos → la heurística es buen profesor sobre
las pistas nuevas → `-record` → race08. Si el eval crashea al arrancar = algún seed
1000-1008 no genera pista válida en 80 intentos → bajar `MinCornerRadius` a 16-18.

### CAUSA RAÍZ (por fin): los muros de borde eran mallas de espesor cero (2026-09-07)

El volcado de trayectoria en cada muerte lo dejó claro: en TODAS, `spd=0` fijo durante 3 s,
`turn` bajo (0-8°, sin curva), `steer` chico constante, `brk=0`, `lateral` derivando
despacio hacia afuera. Los autos **no chocan a velocidad ni derrapan** — se **frenan en
seco cerca del borde y no re-arrancan** aunque estén a full throttle y a media pista. El
auto que anda bien nunca toca el borde; en cuanto uno se corre, se traba, arrastra a
~0.4 m/s → stall → respawn → repite. 8 de 9 spawns llevan a una deriva al borde.

`TrackEdgeColliders` construía cada muro como un **`MeshCollider` de una cinta vertical sin
espesor, con triángulos en ambas caras**. PhysX no puede resolver el contacto contra eso:
la normal queda indefinida y la caja del auto **se clava en la geometría** en vez de
rebotar. CLAUDE.md §5 asumía "el auto rebota en los muros" — no rebotaba.

**Esto explica ~15 iteraciones de esta sesión**: no era la recompensa, ni la anticipación,
ni la heurística, ni el radio de curva. Era que **cualquier roce con el borde clava el auto
y mata el episodio**, y ningún controlador (heurístico o RL) maneja perfecto siempre.

**Fix**: `TrackEdgeColliders` reescrito — cadena de `BoxCollider` solapados a lo largo de
cada borde (uno cada ~12 m, 0.8 m de espesor, empujados apenas hacia afuera para no comer
pista). Convexos → contacto robusto y barato; el ray sensor los detecta igual por tag.

**Siguiente**: rebuild eval → `eval.exe -heuristic`. Espero un cambio cualitativo: la
heurística debería dar vueltas (rebota o raspa el muro y sigue). Si es así → grabar demos
→ race08. Y probablemente convenga re-evaluar si con muros que funcionan hace falta todo
el andamiaje de imitación, o si un RL "limpio" ya pasa el criterio de Fase 2.

### Muros como sólido extruido + fix del trompo por spawn (2026-09-07)

Los dos intentos con `BoxCollider` dieron `meanLapProgress` 2% (peor que la malla, 11%):
cajas rectas de ~15/10 m que aproximan el arco del borde con una cuerda → los extremos de
la caja sobresalen en la pista en curva. Y al mirar el volcado de trayectoria de nuevo:
`steer` **exactamente constante** por 3.2 s con `spd≈0` y `lat` derivando de costado = el
auto **trompea y desliza de lado**, cerca del spawn.

Dos causas, no una:
1. **Trompo por spawn**: `CleanSpawn` ponía el auto sobre la **racing line** (~5 m del
   centro en curva), pero la heurística ahora tira fuerte a la **centerline**
   (`crossCorr = carOffLeft*3`, ±25°). A 14 m/s → giro a tope → oscila → trompo. El único
   auto que sobrevivía spawneaba en una recta (racing line ≈ centerline). Fix: `CleanSpawn`
   spawnea en la centerline; `crossCorr` gain 3→1.4, clamp ±25→±13.
2. **Muros**: reescritos como **sólido extruido cerrado** (caras interna + externa + techo
   + piso, 1 m de espesor) — un `MeshCollider` por muro, sigue la curva exacta (sin
   aproximación de cuerda), con normal de contacto bien definida. La malla vieja era una
   cinta de espesor cero doble cara (normal indefinida → el auto se clavaba); las cajas
   sobresalían en curva.

**Siguiente**: rebuild eval → `eval.exe -heuristic`. Si `meanLapProgress` da un salto
cualitativo → los dos bugs eran esto, y pivoteamos a reentrenar limpio (race08 sin BC/GAIL,
con la config de recompensa actual) para ver si pasa Fase 2.

### CAUSA RAÍZ (de verdad esta vez): el grip lateral frena en seco al doblar (2026-09-07)

Malla / cajas / sólido extruido para los muros: los tres dan `meanLapProgress` ~11-12% con
la MISMA firma de muerte (`spd=0` fijo 3 s, `steer` chico constante, `lat` derivando de
lado, sin curva, mitad de pista). **Los muros no eran la causa.**

El auto termina en una **pirueta lenta** (círculo de ~1 m a ~0.4 m/s). Cómo entra:
`ApplyLateralGrip` **borra** la velocidad lateral, `Δv = -vRight × LateralGrip × dt =
-vRight × 0.18` por step. Al doblar fuerte a 13 m/s, la velocidad queda "de costado"
respecto a la trompa nueva → `vRight` ~5 → grip = **−45 m/s²** → el auto **frena de 13 a
2 m/s en una fracción de segundo** → lento + girando → pirueta → stall.

Consecuencia: cualquier giro agresivo a velocidad = frenazo. El RL y la heurística
**aprenden a no doblar fuerte** = nunca tomar curvas = ~10% de vuelta. Esto explica los
race01-07 y todas las iteraciones de heurística de esta sesión.

**Fix** (`CarController` / `VehicleConfig`): cap del grip lateral a `MaxGripAccel = 16 m/s²`
— con slip chico el grip funciona igual (el auto va "sobre rieles"), con slip grande
(doblada agresiva) el auto **desliza ancho** (pierde la línea) en vez de frenar en seco, y
el controlador tiene tiempo de recuperar. `LateralGrip` 9 → 7.

**Siguiente**: rebuild eval → `eval.exe -heuristic`. Espero por fin un salto: la heurística
debería dar vueltas (dobla, desliza un poco, sigue). Si es así → reentrenar limpio.

### Fix del grip (redirigir, no borrar) + pistas deliberadamente suaves (2026-09-07)

Tras ~20 iteraciones sin mover el resultado (~10% de vuelta), acordado con el dueño del
proyecto: el demo es sobre el loop agentic piloto<->estratega, no un sim de carreras
(CLAUDE.md §12). Dos cambios, una prueba, y si no da señal → descope (óvalo simple).

1. **`CarController.ApplyLateralGrip` redirige en vez de borrar**: quita una fracción
   `LateralGrip*dt` de la velocidad lateral y devuelve `GripRedirect` (0.85) de esa
   magnitud hacia +forward. El modelo viejo la eliminaba → cada giro frenaba el auto →
   RL/heurística aprendían a no doblar. `MaxGripAccel` (del intento anterior) eliminado.
2. **`TrackParams.Default` mucho más suave**: `MaxControlPoints` 22→18, `MaxHarmonics`
   3→2, `MaxHarmonicFreq` 4→3, `HarmonicAmp` 0.12-0.24 → 0.08-0.16, `AngularJitter`
   0.35→0.2, `RadiusClamp` 0.45-1.75 → 0.6-1.5, `MinCornerRadius` 20→30. Circuitos con
   unas pocas curvas numeradas navegables a velocidad, no horquillas.

**Siguiente**: rebuild eval → `eval.exe -heuristic`. Si la heurística da vueltas → señal
real, reentrenar y AVANZAR a Fase 3/4 (el estratega, que es el punto). Si no → descope.

### race08 = mismo plateau → decisión: descope del piloto RL (camino A) (2026-09-07, cierre de sesión)

`race08` (10M steps, PPO limpio, con grip que redirige + pistas suaves + recompensa
velocidad-objetivo): `Cumulative Reward` ~6 plano desde 2.7M, `Episode Length` ~123,
`end reasons` **`offTrack` 91% / `lap` 4% / `stall` 5%**, `mean lapArc` ~258 m (~13% de
vuelta). Los tres cambios movieron el número apenas (`lap` 2%→4%). **Sin breakthrough.**

**Balance de la sesión**: ~22 iteraciones sobre Fase 2 (piloto RL) — recompensa (7
variantes), heurística (8), radio de curva, muros (3 versiones: malla cero / cajas /
sólido extruido), spawn, grip (cap y luego redirigir), pistas suaves. El número de vuelta
no se movió de ~10-13% en ninguna. El pipeline de entrenamiento y el eval harness quedaron
sólidos; el modelo de física mejoró (grip ya no frena al doblar); pero **PPO no converge a
dar vueltas** en este entorno con el esfuerzo invertido.

**Decisión (dueño del proyecto)**: camino A. El demo es sobre el loop piloto↔estratega
(CLAUDE.md §12), y para eso no hace falta un piloto RL excelente — hace falta un piloto
cuyo comportamiento **cambie según los canales de directiva**. Plan para la próxima sesión:

1. **Pista fija simple** para Fase 2+: un `TrackParams` (o un generador dedicado) que
   produzca un óvalo / rectángulo redondeado con ~4 curvas numeradas navegables. Mantiene
   la numeración de curvas y la memoria lap-over-lap que necesita el estratega (§2.1). La
   generación procedural desde seed queda como feature opcional / Fase 1, no bloqueante.
2. **Piloto = heurística scripted** (`RaceAgent.Heuristic`, ya escrita) con un ajuste de
   suavizado de dirección (re-agregar el low-pass `_steerSmooth`, gain `/11` → `/16`) para
   que no oscile. Verificar que da vueltas completas sobre la pista fija con `eval -heuristic`.
3. **Cablear los canales de directiva** (`_directive.Aggression` / `RiskTolerance` /
   `Kind`, ya en las observaciones desde §6.1) a la heurística: modular `targetSpeed`
   (agresión → frena más tarde / entra más rápido), margen lateral / línea (`directive`),
   tolerancia a proximidad (`risk`). Eso da la superficie de escritura del estratega (§6.5)
   y, con 6 semillas de parámetros distintas, la población de pilotos de Fase 3.
4. El intento de RL (race01-08) queda documentado acá y para el post técnico (CLAUDE.md
   §6.2 valora mostrar lo que no funcionó). El `.onnx` de race08 se puede conservar en
   `models/` como referencia del intento, no como piloto de producción.
5. Limpiar los `Debug.Log` de diagnóstico de `RaceAgent.cs` (`[Heur]`, volcado de
   trayectoria, `[RaceAgent] ...` one-shots) una vez estabilizado.

Ramas/artefactos: todo commiteado hasta `e2dae1c` en `fase-2-rl-agente`. `results/race01..08`
en disco (gitignored). Los `.onnx` de race08 en `results/race08/`.

### BREAKTHROUGH: circuito fijo → la heurística da vueltas limpias (2026-09-08)

`eval -heuristic` sobre el óvalo redondeado fijo (`TrackParams.FixedRoundedRect`, ~2 km,
4 curvas de r=120 m):

    episodes=9  meanEpisodeSteps=4000 (~80s)  meanLapProgress=86% of a lap
    end reasons: maxStep=100%
    meanForwardSpeed=21.4 m/s  meanThrottle=0.43  meanBrake=0.07  meanAbsSteer=0.04

**Los 9 autos corrieron los 80 s completos sin salirse, sin trabarse, sin pararse.** Crucero
a 21 m/s, control modulado. La "parálisis" (`spd 18→0`, crawl a 3-13 m/s) que dominó
race01-08 y toda la caza de esta sesión **era de las pistas procedurales** — segmentos
degenerados del centerline, geometría de muros mal formada en curva, y curvas que ni la
heurística ni el RL manejaban. **No era un bug de fondo del `CarController`.** En un
circuito limpio y simple, la física y la heurística funcionan.

`MaxStep` 4000 → 6000 (una vuelta al óvalo son ~95 s a ritmo; con 80 s no cerraba).

**Estado del camino A**:
- [x] Paso 1 — circuito fijo. Hecho, funciona.
- [x] Paso 2 — heurística con dirección suavizada. Da vueltas limpias.
- [ ] Paso 3 — cablear los canales de directiva (`_directive.Aggression/RiskTolerance/Kind`,
  ya en las observaciones) a la heurística: `Aggression` → `targetSpeed` / margen de
  frenada; `directive` (attack/defend/conserve/push) → sesgo de línea; `RiskTolerance` →
  tolerancia a proximidad (Fase 4). Con 6 juegos de parámetros distintos → población de
  pilotos de Fase 3.
- [ ] Paso 4 — documentar el intento RL (race01-08) para el post técnico.
- [ ] Paso 5 — limpiar los Debug.Log de diagnóstico.

Nota sobre el RL: con el circuito fijo, un PPO limpio probablemente SÍ aprenda a dar
vueltas (el entorno ahora es tratable). Queda como opción para después de tener el piloto
heurístico+directivas funcionando y la Fase 4 encaminada — no bloquea.

### Camino A completo: piloto heurístico + directivas, Fase 2 cerrada (2026-09-08)

`eval -heuristic` sobre el óvalo fijo, forzando la directiva:

| directiva | fin | m/s | throttle | brake | vuelta |
|---|---|---|---|---|---|
| Conserve, agg 0.15 | lap 100% | 17.6 | 0.38 | 0.07 | 112 s |
| random | lap 100% | 20.1 | 0.45 | 0.10 | 100 s |
| Attack, agg 0.85 | lap 100% | 24.9 | 0.63 | 0.18 | 79 s |

**Los 9 autos completan vuelta entera en los tres casos.** La directiva da ~40% de spread
en tiempo de vuelta cambiando solo `Aggression`/`Kind`. El estratega (Fase 4) tiene una
superficie de escritura real y observable.

- [x] Paso 1 — circuito fijo (`FixedRoundedRect`).
- [x] Paso 2 — heurística con dirección suavizada, da vueltas limpias.
- [x] Paso 3 — canales de directiva cableados (`Aggression`/`RiskTolerance`/`Kind` →
  pace, margen de frenada, sesgo de línea). `RaceAgent.ForcedDirective` + flags de eval.
- [ ] Paso 4 — documentar el intento RL race01-08 para el post técnico.
- [ ] Paso 5 — limpiar los `Debug.Log` de diagnóstico (`[Heur]`, volcado de trayectoria,
  one-shots `[RaceAgent] ...`).

**Fase 2 (piloto) — cerrada por camino A.** El piloto es `RaceAgent.Heuristic()` en
`HeuristicOnly`; no necesita `.onnx` ni Sentis en runtime → simplifica el build WebGL
(el piloto es C# puro en el cliente). El RL queda documentado como intento; con el
circuito fijo un PPO limpio probablemente aprenda, pero no bloquea y se puede retomar
después.

**Población de pilotos (Fase 3)**: 6 `RaceDirective` base fijas (ej. combinaciones de
`Kind` × `Aggression`) en vez de snapshots RL. Emparejadas en ritmo por diseño (mismo
controlador, distintos parámetros) — más limpio que elegir checkpoints (§5, §11). Falta:
correrlas entre sí en el óvalo y registrar tiempos de vuelta medios (la línea base de §5).

**Pendiente de diseño para Fase 3/4**: en la carrera real el episodio es UNA carrera
larga (varias vueltas), no muchos episodios cortos — hay que desactivar/relajar las
terminaciones `stall`/`stuck`/`offTrack` para el modo demo (que un roce no "reinicie" el
auto a mitad de carrera; en su lugar, respawn suave en la pista o penalización de tiempo).

### Paso 5 (limpieza) + Fase 3 Lite: población de pilotos y su línea base (2026-09-08)

**Paso 5 — Debug.Log de diagnóstico fuera (`0eedda1`).** `RaceAgent.cs` baja de ~505 a
~340 líneas: se quitó el volcado de trayectoria de muerte (`_trajLat/Steer/Speed/Brake/
Turn` + `TrajStr`), el log rodante de razones de fin (`_endRecent`/`_endTotal`/
`_lapArcSum`/`EndWindow`), los one-shots `[RaceAgent]` de `Initialize`/`CollectObservations`,
`DumpSensors` por reflexión y el `[Heur]` por segundo. `ReportEpisodeEnd` queda mínimo
(marca `_endReported`, dispara `AnyEpisodeEnded`). Recompensa, límites de episodio y el
piloto heurístico intactos. Único `Debug` que sobrevive: el `LogError` de "no
TrainingArena".

**Fase 3 Lite — la población son 6 presets de directiva, no snapshots RL.** Camino A: el
piloto es un solo controlador scripted, así que la "población" de la Fase 3 son 6
`RaceDirective.PopulationMember` fijas (`RaceDirective.Population`), emparejadas en ritmo
*por construcción* (mismo `Heuristic()`, distintos `Kind`/`Aggression`/`RiskTolerance`) en
vez de elegir checkpoints —que §5/§11 desaconsejan porque el último gana siempre—. Presets
deliberadamente mid-range y juntos (agg 0.45–0.62), no el rango 0.15–0.85 del eval de
directivas, para que ninguno saque segundos al resto y no contamine la Fase 6.3:

| miembro | Kind | agg | risk |
|---|---|---|---|
| P1-Balanced  | Push     | 0.50 | 0.50 |
| P2-LateBrake | Attack   | 0.62 | 0.55 |
| P3-Defensive | Defend   | 0.45 | 0.40 |
| P4-Smooth    | Conserve | 0.52 | 0.45 |
| P5-Aggro     | Attack   | 0.58 | 0.62 |
| P6-Steady    | Push     | 0.46 | 0.48 |

**Cómo se corren entre sí**: flag `-population` nuevo en `EvalRunner`. Implica
`-heuristic`, ventana 360 s, reparte los 6 miembros en las **12 arenas** del eval
(`Fase2EvalBuild` ahora pide `SetArenaCount(12)` → 2 arenas por miembro) vía
`RaceAgent.InstanceDirective` (override por-agente, gana sobre `ForcedDirective`).
`AnyEpisodeEnded` ahora pasa el `RaceAgent` para atribuir cada vuelta a su miembro. Al
cierre loguea `[Eval] POPULATION baseline`: `mean`/`min`/`max` de tiempo de vuelta por
miembro y el spread fastest↔slowest.

    unity\Builds\eval-windows\eval.exe -population -logFile eval-population.log

**Pendiente (humano)**: rebuild del eval player en la NUC y correr `-population`; pegar la
tabla `[Eval] POPULATION baseline` acá. Es la línea base de §5 contra la que se lee la
Fase 6.3. Si el spread es amplio (un miembro gana sistemáticamente), acercar su preset al
pelotón y repetir antes de pasar a Fase 4.

- [x] Paso 5 — limpieza de `Debug.Log`.
- [ ] Paso 4 — documentar el intento RL race01-08 para el post técnico (no bloquea Fase 4).
- [x] Fase 3 Lite — corrida `-population` hecha (ver tabla abajo); P4 re-tuneada, falta 1 re-run de verificación.

**Línea base `-population` (12 arenas, 2 por miembro, 360 s) — 2026-09-08**

    [Eval] REPORT policy=HEURISTIC
      episodes=45  meanEpisodeSteps=4451 (~89.0s)  meanLapProgress=99% of a lap
      end reasons: lap=100%
      meanForwardSpeed=22.0 m/s  meanThrottle=0.51  meanBrake=0.13  meanAbsSteer=0.06

    | miembro       | Kind     | agg  | risk | laps | mean   | min   | max   |
    |---------------|----------|------|------|------|--------|-------|-------|
    | P1-Balanced   | Push     | 0.50 | 0.50 |  8   |  88.4s | 87.5  | 89.0  |
    | P2-LateBrake  | Attack   | 0.62 | 0.55 |  8   |  85.8s | 84.8  | 87.4  |
    | P3-Defensive  | Defend   | 0.45 | 0.40 |  7   |  89.5s | 88.0  | 90.1  |
    | P4-Smooth     | Conserve | 0.52 | 0.45 |  6   |  96.9s | 96.7  | 97.1  |
    | P5-Aggro      | Attack   | 0.58 | 0.62 |  8   |  86.4s | 85.9  | 87.4  |
    | P6-Steady     | Push     | 0.46 | 0.48 |  8   |  89.2s | 88.2  | 89.9  |
    spread: 85.8s .. 96.9s  (+11.1s, 13% del más rápido)

**Lectura**: 100% de vueltas completadas (45/45), varianza intra-miembro <1.5 s — la
población es viable y determinista. **Cinco de los seis caen en una banda de 85.8–89.5 s
(3.7 s, ~4%)**: eso está emparejado. El único outlier es **P4-Smooth (Conserve), 8 s más
lento** que el siguiente. Causa: la rama `Conserve` de `RaceAgent.Heuristic` aplica
`aggSpeed *= 0.9` y el `centreBlend` más alto (0.92), así que con `agg` mid-pack queda muy
por detrás. Es a la vez el más consistente (min/max 96.7–97.1, 0.4 s) — que es justo lo que
`Conserve` significa (§6.5) — pero un piloto 8 s/vuelta más lento se va al fondo y se queda
ahí en la Fase 6.3, confundiendo posición de parrilla con efecto del estratega.

**Ajuste**: P4 sube a `Conserve, agg 0.72, risk 0.50` para compensar el recorte de la rama
`Conserve` sin tocar la semántica de la directiva (sigue con el sesgo de línea al centro).

**Re-run de verificación (mismo setup, 2026-09-08)** — P4 baja de 96.9 s a 90.5 s:

    | miembro       | Kind     | agg  | risk | laps | mean   | min–max     |
    |---------------|----------|------|------|------|--------|-------------|
    | P2-LateBrake  | Attack   | 0.62 | 0.55 |  8   |  85.8s | 84.8–87.5   |
    | P5-Aggro      | Attack   | 0.58 | 0.62 |  8   |  86.3s | 85.9–87.4   |
    | P1-Balanced   | Push     | 0.50 | 0.50 |  8   |  88.3s | 88.0–88.4   |
    | P6-Steady     | Push     | 0.46 | 0.48 |  8   |  89.2s | 88.2–89.9   |
    | P3-Defensive  | Defend   | 0.45 | 0.40 |  7   |  89.6s | 88.1–90.1   |
    | P4-Smooth     | Conserve | 0.72 | 0.50 |  6   |  90.5s | 90.0–90.7   |
    spread: 85.8s .. 90.5s  (+4.7s, 5% del más rápido)

**Fase 3 Lite — CERRADA.** Los seis miembros en una banda de 4.7 s (5%), cada uno con
min/max < 2 s. Los dos `Attack` (P2, P5) quedan ~2 s por delante del resto: es un orden
*de estilo* (agresivo = más rápido), no una diferencia de habilidad — y la rotación de
parrilla de la Fase 6.3 absorbe ese residuo. Ningún piloto "gana por un margen amplio"
(criterio de §5). Esta tabla es la **línea base** contra la que se lee todo resultado de
la Fase 6.3. La población vive en `RaceDirective.Population`; se reproduce con
`eval.exe -population`.

**Siguiente: Fase 4 — la capa agentic (el estratega LLM).** Es el punto del demo
(CLAUDE.md §12, §6). El piloto (heurístico + canales de directiva) y su población ya son
la superficie de escritura que el estratega necesita.

### Fase 4 (parte 1/2): servidor + web + capa C# del estratega (2026-09-08)

Acordado con el dueno: hacer primero lo verificable sin Unity (servidor end-to-end,
overlay web, capa C# del estratega) y dejar la **escena de carrera multi-auto**
(parrilla, colisiones, clasificacion/gaps/tiempos en vivo, fuente de la telemetria
§6.3) como paso 2, porque necesita validarse en el Editor. Esa escena es en rigor el
cuerpo de la Fase 3 que el Camino A / Fase 3 Lite no llego a construir.

**Servidor (`server/`, commit `8cbb0f5`) - hecho y probado, 10 tests verdes.**
- `schemas.py`: `StrategyRequest` = `context` (prefijo estable §6.7: perfil de piloto +
  mapa de curvas + vueltas) + `telemetry` (parte variable §6.3). `StrategyResponse` con
  niveles discretos (§6.4). `StrategyEnvelope`: siempre HTTP 200, `status` `ok|fallback`,
  `reason` explica el fallback - un solo camino para el cliente.
- `strategy.py`: `build_messages` (system message byte-identico por auto -> reuso de
  KV-cache). `call_ollama` = un turno, `format` = JSON Schema, `num_predict` 150,
  `keep_alive` 25m. `parse_response` descarta la respuesta entera si algo no valida
  (§6.8). `clamp_radio` a 15 palabras server-side.
- `guardrails.py` (§7): compuerta de concurrencia global (semaforo 1 -> `busy`),
  cortacircuitos (p95 > 20s o racha de 3 fallos -> `offline` 60s sin tocar Ollama, no
  error), rate limit por IP (token bucket burst 12 / 1 s). Contadores para `/api/health`.
- `main.py`: async con `httpx.AsyncClient` + `lifespan`. `/api/health` (modo LLM, p95,
  rejected, failed, inflight, `ollama_reachable`). `/api/ping` para el heartbeat.
- Probado con `curl` contra Ollama caido: fallback correcto; tras 3 fallos el
  cortacircuitos abre (`mode: offline`, `calls` deja de subir).

**Web (`web/`, commit `1e90376`) - hecho; render en browser sin capturar.**
- Overlay 100% DOM sobre el canvas, solo `var(--color-*)` + fallback `:root`.
- `app.js`: carga Unity (rutas relativas), router de `unity:message`, heartbeat a
  `/api/ping` cada 60s mientras hay carrera activa (§2.2), poll de `/api/health` cada 5s
  -> chip online/offline/down con p95 y % descartadas.
- `overlay.js`: HUD de vuelta/clasificacion arriba, feed 'Team radio' abajo-derecha
  (ultimas 6). Marca las llamadas fallback sin ocultarlas (§6.2). Protocolo Unity->DOM
  (`race:start`/`race:tick`/`radio:msg`/`race:end`) documentado en el archivo.
- `mock.js` (`?mock=1`): sintetiza una carrera de 6 autos y llama al `/api/strategy` real
  - revisa el overlay sin build de Unity y prueba el proxy end-to-end.
- El Chrome de automatizacion no pudo abrir `localhost` (bloqueo de red del entorno;
  `curl` da 200). PENDIENTE: abrir `/?mock=1` a mano y confirmar el render.

**Capa C# del estratega (`Assets/Scripts/Strategy/` + `Vehicle/StrategyDirectiveMap.cs`)
- escrita; COMPILA (verificado con el `csc` de Unity 6000.3.22f1 contra los modulos de
engine + ML-Agents + InferenceEngine, 28 archivos, 0 errores). Falta probar en el Editor.**
- `StrategyDirectiveMap.cs` (en asmdef Vehicle): el UNICO mapeo directiva->controlador
  (§6.5), ahora un `ScriptableObject` con constantes serializadas (calibrables en Fase 4).
  `RaceAgent.Heuristic` deja el bloque de numeros inline y llama a `Resolve(_directive)`.
  **Los valores por defecto son identicos a los de antes** -> la linea base de Fase 3
  Lite (P1..P6) no cambia. `ToDirective(kind, aggLevel, riskLevel)` convierte la salida
  discreta del LLM al `RaceDirective` que el piloto ya consume. `.Default` = instancia en
  codigo para las arenas de train/eval.
- `RaceStrategist.cs`: un componente por auto (independiente, sin cerebro central).
  `Notify(evt, snapshot)` con cooldown ~12s + coalescing por relevancia (§6.6). Llama a
  `/api/strategy` en una corrutina con `UnityWebRequest` - la carrera nunca espera.
  Valida el sobre (§6.8): cualquier problema -> conserva la directiva vigente + nota.
  Emite la linea de radio al overlay via `JsBridge`. `UseLlm=false` -> corre siempre con
  `HeuristicFallback` (grupo de control §6.3). Backstop cliente §7.5: max 2 llamadas en
  vuelo en toda la grilla. Evento `DecisionMade(StrategyRecord)` con el registro completo
  para Fase 6.1.
- `HeuristicFallback.cs`: la estrategia heuristica fija (fallback §7 Y control §6.3).
- `StrategyModels.cs` (DTOs para `JsonUtility.FromJson`), `JsonBuilder.cs` (escritor JSON
  minimo - `JsonUtility` no emite `null`), `RaceTelemetry.cs` (structs `SelfSnapshot`/
  `RivalSnapshot`/`TelemetrySnapshot` que la escena llenara), `StrategyApi.cs` (endpoint:
  WebGL -> origen de la pagina; Editor -> `localhost:8080` / `AGENTIC_API_BASE`),
  `PilotProfiles.cs` (perfil de una linea por miembro, para el prefijo §6.2/§6.7).
- asmdefs nuevos: `AgenticRacing.Interop` (Interop pasa a asmdef propio para que Strategy
  lo referencie) y `AgenticRacing.Strategy` (refs: Track, Vehicle, Interop).

**Pendiente para el humano (Editor):**
1. Abrir en Unity 6000.3.22f1 -> genera los `.meta` de `Strategy/`, del asmdef de Interop
   y de `StrategyDirectiveMap.cs`, y **commitearlos** (sin ellos GameCI genera GUIDs no
   deterministas).
2. Confirmar que compila en el Editor y que los tests EditMode siguen verdes.
3. Abrir `web/index.html?mock=1` contra `docker compose up` y confirmar overlay + radio +
   chip de estado del LLM.
4. Paso 2/2 de Fase 4: la escena de carrera multi-auto que emite `race:*` y construye los
   `TelemetrySnapshot` - 6 autos de `RaceDirective.Population` en parrilla, colisiones,
   `RaceDirector` que calcula clasificacion/gaps/tiempos y llama a cada `RaceStrategist`,
   3 con `UseLlm=true` y 3 con `false` (campo mixto §6.3). Ademas: en la carrera real el
   episodio es UNA carrera larga -> relajar las terminaciones `stall`/`stuck`/`offTrack`
   del `RaceAgent` para el modo demo.

### Fase 4 (parte 2/2) paso 1: RaceDirector + telemetria §6.3 (2026-09-08, sesion Ubuntu)

Retomada en la particion **Ubuntu** de la NUC (Docker + Editor Linux `6000.3.22f1`
instalados: cubre todo lo que queda de Fase 4/5; solo se vuelve a Windows si se
retoma RL real con `mlagents-learn`). El `?mock=1` contra `docker compose up`
quedo confirmado por el dueno (pendiente 3 de la parte 1/2 -> hecho).

**Seam en `RaceAgent` para la escena de carrera (`Agents/RaceAgent.cs`):**
- `internal TrackData ExternalTrack` — el `RaceDirector` inyecta UN circuito
  compartido para toda la grilla; `Initialize()` lo usa y solo cae al
  `GetComponentInParent<TrainingArena>()` si es null (train/eval intactos).
- `internal bool RaceMode` — corre como piloto puro para UNA carrera larga:
  `OnEpisodeBegin` no hace respawn aleatorio (el auto se queda en el slot de
  parrilla), no toca recompensa ni el chequeo de timeout de `MaxStep`;
  `OnActionReceived` mapea controles + `_progress.Update` y **retorna antes** de
  cualquier `AddReward`/`EndEpisode`. `Heuristic()` (el piloto Camino A) intacto.
- `internal void SetRaceDirective(RaceDirective)` — el estratega empuja su
  directiva vigente cada tick para que el piloto Y los canales de directiva de
  su vector de observaciones (§6.1) sigan al muro de boxes en vivo.

**`RaceDirector.cs` nuevo (`Assets/Scripts/Agents/`, asmdef `AgenticRacing.Agents`
ahora referencia `AgenticRacing.Strategy` + `AgenticRacing.Interop` — sin ciclo:
Strategy no referencia Agents).** MonoBehaviour que:
- `StartRace()` (auto en `Awake` salvo que un bootstrap lo apague): genera el
  ovalo fijo (`TrackGenerator.Generate`), construye el mapa de curvas numeradas
  (`CornerInfo[]` desde `TrackData.Corners`, severidad por `MinRadius`), levanta
  los muros (`TrackEdgeColliders.Build`) y arma la parrilla de 6 autos de
  `RaceDirective.Population` (2 columnas, filas hacia atras de la meta).
- Cada auto: cold-build ML-Agents (BehaviorParameters HeuristicOnly ->
  `RaceAgent.ObsSize` = 15, sin ray sensor) + `RaceAgent` en `RaceMode` con
  `ExternalTrack`/`InstanceDirective`/`MaxStep=0` + `DecisionRequester`(5) +
  `RaceStrategist` con `UseLlm = slot < llmCars` (campo mixto §6.3, 3 y 3),
  `Context` (perfil de piloto §6.2/§6.7 + mapa de curvas + vueltas) y `Map`.
  Pose de parrilla ANTES de activar (para que el reset de `_progress` de
  `OnEpisodeBegin` en RaceMode vea el slot real, no el origen) y `PlaceAt`
  despues.
- `FixedUpdate`: `TrackProgress` por auto, EMA de velocidad, deteccion de cruce
  de meta por wrap de `Distance01` (con histeresis `CrossArmed`), `Crossings` ->
  `LapsCompleted`, `TotalArc = Crossings*len + arc` (monotonico), clasificacion
  por `TotalArc`, gaps en segundos (`ΔTotalArc / max(8, EMA velocidad)`),
  tiempos de vuelta (best/last), y el mirror de `CurrentDirective` -> piloto.
- Dispara `RaceStrategist.Notify` por evento (§6.6): `LapCompleted` / `FinalLap`
  (al empezar la ultima vuelta, una vez) en el cruce de meta; `PositionChange`
  al cambiar de posicion; `RivalInRange` cuando el gap al de adelante < 1.5 s
  **sostenido** 2 s (con rearmado por histeresis, no un cruce momentaneo);
  `Incident` via `ReportIncident(slotA, slotB)` (hook publico, lo llamara la
  deteccion de colisiones del paso 2). El cooldown/coalescing por auto ya vive
  dentro de `RaceStrategist`.
- `BuildSnapshot(car, evt)` arma el `TelemetrySnapshot` §6.3 completo:
  `SelfSnapshot` (pos, last/best lap, gap_ahead/gap_behind con centinela -1 =
  lider/ultimo, directiva vigente, incidentes) + `RivalSnapshot[]` de los otros
  5 (gap firmado <0 = adelante, `trend` closing/stable/dropping por delta del
  gap). Las notas lap-over-lap las agrega el director via
  `RaceStrategist.AddNote` (que es lo que va en el prefijo `_notes` enviado).
- Emite `race:start` / `race:tick` (cada 0.2 s) / `race:end` al overlay via
  `JsBridge` con los nombres de campo exactos que consumen `web/app.js` +
  `web/overlay.js` (`type`, `classification:[{pos,id,name,gap,lastLap,directive}]`,
  `meId`, etc.). El `radio:msg` lo sigue emitiendo `RaceStrategist`.

**Typecheck (sin Editor):** `csc` de Roslyn de Unity 6000.3.22f1 (`-nostdlib`,
`-langversion:9.0`) sobre las 5 asmdef (Track, Vehicle, Interop, Strategy,
Agents) aplanadas + engine + ML-Agents + InferenceEngine -> **0 errores**. No
verifica el grafo de referencias entre asmdef (eso es el Editor).

**`.meta`:** creado `Agents/RaceDirector.cs.meta` a mano con GUID estable (mismo
formato minimo que los `.meta` que dejo la sesion de Windows). El editar
`RaceAgent.cs` / el `.asmdef` no cambia sus `.meta`.

**Pendiente:**
- Paso 2 — escena `Race.unity` + bootstrap; colisiones auto-auto que llamen a
  `RaceDirector.ReportIncident`; rotacion de parrilla y de que slots llevan LLM
  (§6.3). El `RaceDirector` ya no necesita `TrainingArena`.
- Paso 3 — politica de respawn suave / penalizacion de tiempo para un auto que
  se sale o se enreda (hoy `RaceMode` simplemente no termina nunca; el auto
  sigue con la recuperacion de muro de `Heuristic()`).
- Paso 4 — abrir `web/index.html` (sin `?mock=1`) contra la escena real y
  confirmar el flujo `race:*` + `radio:msg` completo.
- Paso 5 — compilar en el Editor Linux (asmdef graph) + tests EditMode verdes;
  correr la escena. Generar/commitear cualquier `.meta` que falte.
- Sigue pendiente de la parte 1/2: `.meta` de `Strategy/` etc. los genera el
  Editor al abrir (item 1 de la lista anterior).

### Fase 4 (parte 2/2) paso 2: escena Race + colisiones + rotacion (2026-09-08, sesion Ubuntu)

**Escena `Race.unity` (via `Assets/Editor/Fase4RaceScene.cs`).** Mismo patron
que Fase 1: un solo objeto (`TrackConfig` + `RaceSceneBootstrap`), todo lo demas
se construye en runtime.
- `RaceSceneBootstrap.cs` (asmdef `AgenticRacing.Agents`): resuelve seed/laps de
  la URL (`TrackConfig`) y `?race=N` (parser propio — el de `TrackConfig` es
  `internal`); genera el ovalo, dibuja superficie + racing line + linea de meta +
  numeros de curva (T1..Tn), crea el `RaceDirector` (inactivo -> `Configure` ->
  activar -> `StartRace`), y una camara ortografica top-down que **encuadra el
  peloton** (centroide + tamano ajustado al spread, clamp [38, 240], suavizado).
  El HUD/radio siguen 100% en el overlay DOM (§2.2); en pantalla solo pista y
  autos.
- `Fase4RaceScene.Setup()` materializa `Assets/Scenes/Race.unity`;
  `.BuildWebGL()` es un build local sin comprimir para verificar (el pipeline de
  CI mantiene Brotli + .br/.gz de Fase 0).

**Colisiones auto-auto -> `RaceDirector.ReportIncident` (`RaceCarContact`).**
Componente nuevo (en `RaceDirector.cs`), uno por auto, anadido en el cold-build.
`OnCollisionEnter` busca un `RaceCarContact` en el otro collider/rigidbody; si es
otro auto y no esta en cooldown (1.5 s, compartido entre los dos) llama a
`ReportIncident(slotA, slotB)` una sola vez por golpe. Los contactos con muro los
sigue manejando `RaceAgent` (no cuentan como incidente de carrera).

**Rotacion de parrilla y de LLM (§6.3).** `RaceDirector.raceIndex` (settable por
`Configure` / `?race=N`). En la carrera k: el slot s arranca al miembro
`(s+k) mod N` de `RaceDirective.Population`, y un miembro m lleva LLM sii
`(m+k) mod N < llmCars`. Ejes distintos (slots vs miembros) -> la ventaja de
salida no se confunde con el efecto del estratega; en N carreras cada miembro
visita cada slot y lleva LLM en exactamente `llmCars` de ellas. La **identidad**
(`car_0N`, color, perfil de piloto) sigue al **miembro**, no al slot, para que la
bitacora §6.1 y la comparacion §6.3 sigan a un piloto estable y no a un asiento.
`RaceDirector.Configure(track, seed, laps, race)` permite que el bootstrap le pase
el `TrackData` ya generado (visuales y sim comparten uno solo).

**Typecheck (sin Editor):** `csc` de Roslyn de Unity 6000.3.22f1 sobre runtime
(Track/Vehicle/Interop/Strategy/Agents) + Demo + `Fase4RaceScene.cs` + UnityEditor
-> **0 errores**. (Los `Fase0*.cs` fallan en este check ad-hoc por falta de
`System.Diagnostics.Process` en el set de refs improvisado, no por el cambio.)

**`.meta`:** creados a mano (GUID estable, formato minimo) para
`RaceSceneBootstrap.cs` y `Fase4RaceScene.cs`.

**Pendiente:**
- Paso 3 — respawn suave / penalizacion de tiempo para un auto que se sale o se
  enreda (hoy `RaceMode` nunca termina; el auto sigue con la recuperacion de muro
  de `Heuristic()`, y un choque fuerte lo puede dejar mal encarado sin correccion).
- Paso 4 — abrir `web/index.html` (sin `?mock=1`) contra un build de la escena y
  confirmar el flujo `race:*` + `radio:msg` end-to-end contra `docker compose up`.
- Paso 5 (Editor Linux) — `Fase4RaceScene.Setup` para generar/commitear
  `Race.unity` + su `.meta`; confirmar compila + tests EditMode verdes; abrir la
  escena en Play y ver la parrilla, las vueltas, el encuadre de camara y (con el
  server arriba) las lineas de radio. Generar los `.meta` que falten de la parte
  1/2 (`Strategy/`, etc.).

### Fase 4 (parte 2/2) paso 3: respawn suave + penalizacion de tiempo (2026-09-08, sesion Ubuntu)

En modo carrera el episodio es UNA carrera larga, asi que `RaceAgent.RaceMode`
nunca llama `EndEpisode()` (el reset de entrenamiento reposiciona en un punto
aleatorio — catastrofico a mitad de carrera). En su lugar, un **respawn suave con
penalizacion de tiempo** (CLAUDE.md §6.3, nota de diseno):

- `RaceAgent.RaceSoftRespawn()` (nuevo, `internal`): coloca el auto en la
  centerline en su `NearestSample` actual (conserva el arco de vuelta), mirando
  la direccion de carrera, con velocidad de rodaje (`launchSpeed`), y limpia el
  estado de recuperacion de la heuristica (`_wallJamTimer`/`_escapeUntil`/
  `_steerSmooth`). No termina el episodio.
- `RaceDirector.MaybeSoftRecover(car, dt)` (cada tick, por auto): acumula tres
  temporizadores y dispara `SoftRecover` cuando uno pasa su umbral —
  **fuera de pista** (`|LateralOffset| > halfWidth + 2 m` por >1.5 s),
  **parado** (empezo a correr y `|ForwardSpeed| < 0.6 m/s` por >4 s), o
  **al reves** (`dot(forward, tangent) < -0.3` a >4 m/s por >3 s).
- `RaceDirector.SoftRecover(car, reason)`: llama a `RaceSoftRespawn`, re-sincroniza
  el `TrackProgress` del director (y `CrossArmed` para no contar un cruce
  fantasma), suma la penalizacion a `car.ArcPenalty` (= `penaltySeconds *
  max(8, EMA velocidad)` metros, default 4 s) y la resta de `TotalArc` en todos
  lados — asi el auto reaparece en la posicion que le toca, no adelante. Cuenta
  como incidente (§6.2), agrega nota lap-over-lap y dispara un `Incident` al
  estratega.

`TotalArc = Crossings*len + arc - ArcPenalty` en `FixedUpdate` y en
`HandleCrossing`; el conteo de vueltas (por wrap de `Distance01`) es independiente
y no se ve afectado por la penalizacion.

Typecheck Roslyn Unity 6000.3.22f1 (runtime + Demo + `Fase4RaceScene`): 0 errores.

**Pendiente:**
- Paso 4 — flujo `race:*` + `radio:msg` end-to-end sin `?mock=1`, contra
  `docker compose up`.
- Paso 5 (Editor Linux) — `Fase4RaceScene.Setup` -> commitear `Race.unity` +
  `.meta`; compila + EditMode verdes; abrir en Play y observar parrilla, vueltas,
  encuadre de camara, respawn suave y (con server) lineas de radio. Generar los
  `.meta` que falten de la parte 1/2.
- Tuning de fisica de contacto auto-auto (cubos ligeros con Y congelada): un
  choque fuerte puede lanzar a un auto; los umbrales de `MaybeSoftRecover` son un
  primer valor, ajustar tras ver la escena en el Editor.

### Fase 4 (parte 2/2) paso 5 (parcial): Editor Linux batchmode — escena + tests (2026-09-08)

El Editor Linux `6000.3.22f1` de la NUC esta activado y corre en batchmode
(entitlements OK en `Editor.log`). Desde la particion Ubuntu:

- **`Fase4RaceScene.Setup`** (`-batchmode -nographics -quit -executeMethod`):
  genero `Assets/Scenes/Race.unity` (+ `.meta`). El `-executeMethod` solo corre
  si el proyecto compila entero -> **el grafo de asmdef quedo validado**
  (`AgenticRacing.Agents` -> `Strategy` + `Interop` resuelve; 0 errores de
  compilacion, 0 warnings de asmdef). La escena: objeto `Race` con `TrackConfig`
  + `RaceSceneBootstrap`.
- **EditMode: 20/20 verdes** tras arreglar 2 tests que ya estaban rojos desde
  antes de esta sesion (deuda de Camino A, no de los pasos 1-4):
  - `TrackGeneratorTests.DistinctSeeds_ProduceDistinctTracks` — asertaba variedad
    por seed; con `TrackParams.Default.FixedRoundedRect = true` (commit `3b35176`)
    todos los seeds dan el mismo ovalo. Fix: el test pide la ruta procedural
    (`FixedRoundedRect = false`).
  - `TrackGeneratorTests.Fallback_WhenTriggered_IsDeterministicAndStillValid` —
    esperaba que un piso de 22 m disparara la re-derivacion determinista; el
    commit `8308531` ("pistas deliberadamente suaves", sesion anterior) ablando
    los armonicos del Default por debajo de ese punto, asi que `fellBack == 0`.
    Fix: el test restaura localmente los armonicos agresivos del Default de Fase 1
    (`5d2ef7a`) para volver a estresar el camino de fallback.
  Los otros 18 (cierre de bucle, longitud en rango, sin auto-interseccion,
  determinismo, curva mas cerrada navegable) pasan con el ovalo fijo tal cual.
- Revertido el churn que el Editor regenera al abrir: reordenamiento de
  `unity.slnx` y un flip de `scriptingDefineSymbols` (Standalone) en
  `ProjectSettings.asset` — estado derivado de paquetes, no un cambio intencional.

**Pendiente de paso 5 (necesita GUI o build):**
- Abrir `Race.unity` en Play y observar parrilla, cuenta de vueltas, encuadre de
  camara, respawn suave, y —con `docker compose up`— las lineas de radio del
  estratega.
- Paso 4 — `Fase4RaceScene.BuildWebGL` y servir contra el proxy para el flujo
  `race:*` + `radio:msg` end-to-end sin `?mock=1`.
- Generar los `.meta` que Unity no haya materializado aun de la parte 1/2
  (`Strategy/`, etc. — al abrir el proyecto en GUI).

### Fase 4 paso 4: build WebGL local BLOQUEADO (modulo incompleto) — 2026-09-08

Tres intentos de `Fase4RaceScene.BuildWebGL` en el Editor Linux de la NUC:
1. Fallo antes de compilar: el backend de build (Bee/ILPP) no encontraba un
   .NET 8 SDK (solo habia .NET 10). El dueno instalo `dotnet-sdk-8.0`.
2. Fallo mio: `BuildWebGL()` llamaba a `Setup()`, y `Setup()` hace
   `EditorApplication.Exit(0)` en batchmode -> el proceso moria antes de
   `BuildPipeline.BuildPlayer`. Corregido: `Setup()` y `BuildWebGL()` ahora
   comparten un `WriteScene()` interno sin el `Exit`.
3. `BuildPlayer` corrio y fallo con `errors=2`:
   `CarController.cs(51,13): error CS0103: The name 'WebGLInput' does not exist`.
   Causa: el modulo WebGL de este Editor Linux esta **incompleto** —
   `PlaybackEngines/WebGLSupport/il2cpp/` no existe y el log dice "Native
   extension for WebGL target not found", asi que la superficie de API
   especifica de WebGL (`UnityEngine.WebGLInput`, usado en un `#if UNITY_WEBGL`
   preexistente de Fase 0) no entra en las referencias del player. **No es un
   problema del codigo** — ese `#if` compila bien en el WebGL de CI (Fase 0/1
   ya publicaron WebGL via GameCI).

Conclusion: el build WebGL local no es viable en la NUC Linux (modulo
incompleto), que es justo por lo que CLAUDE.md §2 delega los builds WebGL a
**CI/GameCI**. Opciones para cerrar el paso 4:
- Cablear `Race.unity` en `.github/workflows/build-and-publish.yml` (empaquetado
  de Fase 5) y dejar que GameCI lo compile.
- Compilar en la particion Windows (`Fase4RaceScene.BuildWebGL` funciona alli).
- Reinstalar el modulo WebGL en el Editor Linux via Unity Hub y reintentar.
- Smoke test headless en Play mode (verifica sim + telemetria + el JSON exacto
  de `race:*`/`radio:msg` sin build WebGL).

### Fase 4 paso 4: `BuildWebGL` ahora ensambla `/web` (build en Windows) — 2026-09-08

Decidido: cerrar el paso 4 con un build WebGL en la **particion Windows** (el
modulo WebGL de la NUC Linux esta incompleto). Para que ese build se sirva
directo desde `/web`:

- `Fase4RaceScene.BuildWebGL` ahora: (a) compila con **Brotli** (como CI, y
  `server/main.py` ya pone `Content-Encoding: br`); (b) tras un build OK, copia
  `Builds/race-demo/Build/*` -> `web/Build/` renombrando el prefijo
  `race-demo.*` -> `web-test.*` (lo que espera `web/app.js` `BUILD_NAME`), y
  `StreamingAssets/` si existe. Es el equivalente local del paso "Assemble /web"
  de `build-and-publish.yml`; conserva `web/index.html` (el shell del overlay).
- `.gitignore`: `/web/Build/`, `/web/TemplateData/`, `/web/StreamingAssets/`
  (artefacto generado, no fuente).

**Pasos para el humano (Windows -> Ubuntu):**
1. Windows: `git pull`; luego
   `"C:\Program Files\Unity\Hub\Editor\6000.3.22f1\Editor\Unity.exe" -batchmode -quit -projectPath "C:\Users\alexi\source\repos\agentic-racing\unity" -executeMethod AgenticRacing.EditorTools.Fase4RaceScene.BuildWebGL -logFile build-race.log`
   -> revisar `[Fase4RaceScene] BuildWebGL result=Succeeded` y `merged N player files into ...\web\Build`.
2. Copiar `web\Build\` (y `web\StreamingAssets\` si aparece) del checkout Windows
   al checkout Ubuntu (`web/Build/`), por scp / carpeta compartida / USB.
3. Ubuntu: `STATIC_DIR=<repo>/web docker compose up` (o exportar `STATIC_DIR` para
   el server). Abrir `http://localhost:8080/` **sin** `?mock=1`.
4. Exito = carga el player Unity (parrilla de 6 autos en el ovalo), HUD arriba-izq
   con clasificacion que se reordena, chip LLM online, panel "Team radio"
   abajo-der con lineas nuevas por evento (verde = LLM, ambar = fallback), y
   `GET /api/ping` cada ~60 s en los logs. Si el overlay carga pero el player no:
   F12 -> consola + `docker compose logs app`.

### Fase 4: colores consistentes auto / HUD / radio (2026-09-09)

Feedback del dueno tras verlo correr en la particion Windows: cuesta seguir que
auto es cual. Cambios (esteticos):
- `RaceDirector.TintCar` — el cuerpo de cada auto toma su color de paleta
  (`Palette[memberIndex]`, el mismo `st.Color` que ya iba al overlay). Necesita
  rebuild del player (Windows).
- `web/overlay.js` `swatch()` — circulito redondo del color del auto, delante del
  nombre en la clasificacion del HUD y en la cabecera de cada linea del Team
  Radio. Color desde `race:start` (`carColors`); sin cambio de protocolo.
  Servido desde `/web` -> basta `git pull` en Ubuntu + refrescar.
- `web/style.css` `.swatch`.

### Fase 4: bandera a cuadros — los autos paran, y panel de radio legible (2026-09-09)

Feedback del dueno tras la corrida en Windows: (a) los autos seguian corriendo
tras la vuelta final; (b) en el panel de radio no se distinguia una linea nueva
de una repetida ni se sabia si el estratega seguia hablando.

**Fin de carrera (`RaceDirector` + `RaceAgent`).**
- `RaceAgent.RaceStop()` / `_raceStopped`: al cruzar la meta en su ultima vuelta
  el auto frena y pasa a ignorar al piloto (`OnActionReceived` fuerza
  throttle 0 / brake 1). No termina el episodio.
- `RaceDirector.FinishCar`: cuando un auto completa `totalLaps` (o ya salio la
  bandera y cruza meta) queda `Finished` con `FinishOrder`, se le llama
  `RaceStop()`, y su fila de clasificacion se congela en ese orden
  (`UpdateClassification` pone a los terminados al frente por `FinishOrder`, el
  resto por `TotalArc`). El primero en terminar dispara `_chequered` +
  `race:end` (HUD -> FINISHED + banner). El director sigue tickeando hasta que
  **todos** terminan (`_finished`), para que se vea a los rezagados cruzar y
  parar. Tras la bandera no se disparan mas llamadas al estratega.
- `race:tick`/`race:end` ahora mandan `done:true` y, para terminados, `gap` =
  segundos tras el ganador (`FinishGap`).

**Panel Team Radio (`web/overlay.js` + `style.css` + `index.html`).** El feed no
se auto-borra (es el registro de lo que decidio el estratega, §6.1/§6.2), pero:
- **Marca de tiempo por linea** (`ahora` / `42s` / `1:05`), refrescada cada
  segundo por un `setInterval`.
- **Cabecera** `#radio-age`: `ultima senal: Xs` (o `carrera terminada`) — el
  silencio se ve de un vistazo.
- **Colapso de repetidas consecutivas** del mismo auto (mismo texto + estado):
  no agrega fila, pone un badge `xN` y refresca la hora. Una repetida real se
  nota; el spam de "staying on plan" de la ultima vuelta no ensucia.
- `RADIO_MAX` sigue en 6.
- Banner `#race-over` ("Carrera terminada" + ganador) en `race:end`; `onTick`
  deja de tocar el lap card tras el fin (no pisa "FINISHED"). Filas terminadas
  en la tabla: `tr.done` (atenuadas, `pos` -> check).

Typecheck Roslyn Unity 6000.3.22f1: 0 errores. `node --check` en overlay/app/mock.
El C# necesita rebuild del player (Windows); overlay/css/html se sirven de `/web`
(`git pull` + refrescar).

### Fase 4: cache del navegador + pulido de overlay (2026-09-09)

**Causa raiz de "no veo mis cambios de overlay":** el navegador servia un
`overlay.js` / `style.css` viejos cacheados (anteriores a todo el trabajo de
swatches/timestamps/banner). Verificado con Chrome automatizado: `transferSize=0`
(cache), `encodedBodySize` del tamano antiguo. Arreglos:
- `server/main.py`: `Cache-Control: no-cache` para `.html`/`.js`/`.css` (revalida
  siempre; los `.br` grandes siguen cacheando).
- `web/index.html` + `app.js`: `?v=6` en el `<script>` y en los `import` de
  `overlay.js`/`mock.js`/`style.css`. Salta la copia vieja una vez; el header
  mantiene fresco de aqui en mas (no hace falta volver a subir el numero).
Confirmado en navegador tras el bust: timestamps en TODAS las lineas (LLM y
fallback), cabecera `ultima senal: Xs`, swatch + nombre coloreado en clasificacion
y radio, `.hud-card` a 0.78 / `.radio-msg` a 0.82 de opacidad, `#race-over`
`display:none` -> `flex` con `.show`.

**Pulido de overlay (servido de `/web`):**
- Nombres en la clasificacion con el color del auto (`var(--car)`); "me" pasa a
  fondo tenue en vez de recolorear.
- Paneles -10% opacos.
- `humanize()`: el estratega cita rivales por id de protocolo (`car_02`); el
  overlay lo cambia a nombre de piloto (`P2-LateBrake`) en `target_rival` y en el
  texto libre del radio, usando `carNames` de `race:start`.

**Paleta (C#, `RaceDirector.Palette`, necesita rebuild del player):**
`#86efac` (P6, segundo verde que se confundia con el mint de P1) -> `#fb923c`
(naranja). Sincroniza cuerpo del auto + swatch + nombre + linea de radio.

### Fase 4: flecha de rumbo sobre cada auto (2026-09-09)

Feedback: al volver de mirar la pantalla unos segundos no se sabe hacia donde
apunta cada auto (el cuerpo es un rectangulo simetrico visto desde arriba). Un
F1 desde arriba tampoco ayuda (es casi una linea). Solucion top-down clasica:
un **triangulo plano** sobre cada auto apuntando hacia adelante.
- `RaceDirector.BuildHeadingArrow` + `ArrowMesh` (3 verts, doble cara). Vive en
  espacio-mundo (hijo del `RaceDirector`, no del cubo con escala no uniforme) y
  se mueve a seguir al auto cada tick (`SetPositionAndRotation`, +0.95 m en Y).
- Color = `Lerp(colorAuto, blanco, 0.55)` — mas claro que el cuerpo, refuerza la
  identidad.
- Refactor: `CarMaterial(Color)` como unico punto que crea el material unlit;
  `TintCar` ahora toma `Color` (parseado una vez en `BuildCar`).
Necesita rebuild del player (Windows) — junto con la paleta P6 naranja de
`9cf0e60`.

### Fase 4: el LLM se quedaba "offline" — breaker + format + parsing (2026-09-09)

Sintoma (dueno): el chip de estado marcaba "LLM offline" casi todo el tiempo.
Causa: el cortacircuitos (§7.1) abre con p95 > 20 s y `llama3.2:3b` en CPU tardaba
**~27 s** por llamada (decode restringido por el JSON Schema completo pasado como
`format`). Entra 1-2 llamadas, p95 > 20 s, offline 60 s, reintenta, sigue lento,
reabre. Estado estable = casi siempre offline.

Cambios (todo servidor + overlay, sin rebuild del player). Probado en Chrome:
carrera de 3+ vueltas, chip **online** estable, `rejected 0 / failed 0`,
p95 ~22 s, lineas reales del estratega con tag verde.

- `main.py`: `STRATEGY_TIMEOUT_S` 30 -> 45; nuevos env
  `STRATEGY_BREAKER_P95_MS` (35000) y `STRATEGY_BREAKER_COOLDOWN_S` (60),
  pasados a `GuardrailState`. El umbral tiene que estar por encima de la latencia
  real del modelo en la caja objetivo o un LLM lento-pero-vivo queda atrapado
  offline (§2.5 acepta que el radio vaya desfasado).
- `strategy.py`: `format` pasa del JSON Schema completo a `"json"` a secas
  (decode sin gramatica ~3x mas rapido en CPU: ~27 s -> ~7-9 s). Con eso solo, el
  3B se saltaba `risk_tolerance` y ponia `focus_corners: ["T3"]` -> 100%
  descartado, asi que ademas:
  - `_SYSTEM_RULES`: un **ejemplo explicito** del objeto JSON (los modelos chicos
    copian ejemplos) + "nombra a los otros autos por su car_id exacto".
  - `parse_response`: `json.loads` primero + `_normalise()` que convierte
    `focus_corners` `["T3","turn 7"]` -> `[3, 7]` (tolerancia de formato, no
    parcheo semantico §6.8; lo que sigue sin encajar se descarta entero).
- `web/overlay.js`: `humanize()` ahora caza `car_02`, `Car 2`, `car 04`,
  `rival 1`… (antes solo `car_NN` exacto) -> nombre de piloto. El `format:"json"`
  suelto deja al modelo parafrasear los ids en el texto libre.

### Fase 4 — CIERRE (2026-09-10)

Los 10 items del checklist de §5 estan implementados y marcados en CLAUDE.md con
su evidencia. Resumen de lo que quedo montado (camino A, sin RL):

- **Escena** `Race.unity` (`RaceSceneBootstrap`): construye todo en runtime
  (pista fija, curvas numeradas, camara top-down que encuadra el peloton) y
  levanta un `RaceDirector` con 6 autos de `RaceDirective.Population`.
- **`RaceDirector`**: parrilla + rotacion (§6.3: slot `(s+k)%N`, LLM sii
  `(m+k)%N<llmCars`), clasificacion / gaps en segundos / tiempos de vuelta,
  `TelemetrySnapshot` §6.3, disparo por evento a cada `RaceStrategist`
  (LapCompleted/FinalLap/PositionChange/RivalInRange sostenido/Incident),
  respawn suave con penalizacion de tiempo, bandera a cuadros (autos paran,
  orden de llegada congelado, `race:end`).
- **`RaceStrategist`** (uno por auto): cooldown+coalescing, corrutina a
  `/api/strategy`, valida el sobre, escribe SOLO `CurrentDirective`, emite la
  linea de radio; `UseLlm=false` -> control heuristico permanente (§6.3).
- **Servidor**: `format:"json"` + validacion + `_normalise`; breaker/timeout
  configurables por env (subidos para que `llama3.2:3b` en CPU no quede atrapado
  "offline"); guardrails §7 (semaforo, rate-limit, cortacircuitos).
- **Overlay** (`web/`): HUD con clasificacion coloreada + swatch, panel Team
  Radio (timestamps, colapso de repetidas, `humanize()` de ids -> nombre de
  piloto), chip de estado del LLM, heartbeat, banner de fin de carrera.
- **Pilotos**: `RaceAgent` en `RaceMode` (heuristica pura, sin `.onnx`), con
  flecha de rumbo (triangulo) sobre el cuerpo. Paleta de 6 colores separados.

**Verificacion**: build WebGL de Windows servido por el proxy FastAPI
(`STATIC_DIR=<repo>/web`, `docker compose`), abierto sin `?mock=1`. 0 errores
JS/consola, LLM "online" estable (~4% descartes, ~7-9 s/llamada), radio coherente
con la pista, adelantamientos reales, y la secuencia de meta completa. El soak
formal de 20 min queda para dejar la pestana en primer plano (el throttling de
pestana en segundo plano impide automatizarlo).

**CI**: `build-webgl` verde; `test-editmode` pasaba 20/20 pero el job fallaba por
falta de `checks:write` en el token — arreglado en `51413f0`.

**Pendiente (NO bloquea el cierre — es Fase 5):**
- Cablear `Race.unity` en `build-and-publish.yml` (el build de CI aun apunta a la
  escena de Fase 1) y la imagen GHCR.
- Opcional: atenuar/espaciar las lineas de radio de los 3 autos de control para
  que el razonamiento del LLM se lea mejor.
- Ganchos ya listos para Fase 6: `RaceStrategist.DecisionMade(StrategyRecord)`
  (6.1 trazabilidad) y el campo mixto 3+3 con rotacion (6.3).

**PR**: la rama `fase-2-rl-agente` (PR #3) cargo Fase 2 (camino A), Fase 3 Lite y
Fase 4 en un hilo — el camino A fusiono 2+3. Se reescribe el cuerpo del PR para
reflejarlo y se saca de draft para merge a `main`.
