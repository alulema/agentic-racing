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
