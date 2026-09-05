# CLAUDE.md — Agentic Racing Demo

Guía de trabajo para Claude Code. Lee este archivo completo antes de escribir código.

---

## 1. Qué estamos construyendo

Un demo web donde varios autos mecánicamente idénticos compiten una carrera de ~10 km
sobre un **circuito cerrado** de 1.5–2.5 km generado proceduralmente, recorrido en varias
vueltas. Cada auto es un sistema de dos niveles:

- **Piloto (RL)**: red neuronal entrenada con ML-Agents que controla volante, acelerador
  y freno. Ejecuta a frecuencia de frame. No razona: mapea observaciones → acciones.
- **Jefe de equipo (LLM)**: agente que recibe telemetría, razona sobre estrategia de
  carrera y emite instrucciones de alto nivel que modulan el comportamiento del piloto.
  Ejecuta por evento, no por frame.

El objetivo del demo es **hacer visible el patrón agentic**: percepción → razonamiento →
acción delegada. El razonamiento del jefe de equipo se muestra en pantalla en tiempo real
(estilo "radio de equipo" de F1). Ese es el gancho para el espectador, no los gráficos.

**Contexto de despliegue**: se hostea como demo efímero (~20 min por sesión) en la
infraestructura de contenedores de alexisalulema.com. La imagen se publica a GHCR como paquete público
y la infraestructura del sitio la consume para levantar contenedores temporales.

---

## 2. Decisiones ya tomadas (no re-litigar)

Estas decisiones están cerradas. Si encuentras un bloqueo técnico real contra alguna,
**detente y pregunta** en vez de cambiarla por tu cuenta.

| Decisión | Elección | Razón |
|---|---|---|
| Motor | **Unity 6.3 LTS (6000.3.22f1)**, licencia **Personal** | Gratis bajo $200K de ingresos. ML-Agents es el toolkit de RL más maduro para motores de juego. LTS y no 6.5: ver sección 9. |
| Topología de pista | **Circuito cerrado 1.5–2.5 km × N vueltas** = ~10 km de carrera | Ver sección 2.1. Esta decisión es estructural: una pista punto-a-punto de 10 km rompe el entrenamiento RL, la proximidad entre autos, y la memoria del estratega. |
| Pilotos | **Políticas distintas y emparejadas en ritmo**, física idéntica | Autos justos con estilos de conducción distintos. Ver Fase 3: la selección de snapshots tiene un criterio específico, no vale tomar checkpoints espaciados. |
| Inferencia RL | **En el cliente**, vía Inference Engine / Sentis (`com.unity.ai.inference`) | El contenedor no necesita GPU, Python ni PyTorch en runtime. Es el mayor ahorro de presupuesto del proyecto. |
| Entrenamiento | **Offline, en VM cloud**; nunca en el contenedor del demo | Costo de cómputo cero en producción. Solo el `.onnx` se hornea en la imagen. Ver sección 2.3. |
| LLM | **`llama3.2:3b` local, servido por un sidecar Ollama** (CPU-only), horneado en la imagen | Decisión revisada 2026-08-31 (antes: `claude-haiku-4-5` hosted). Sin API de LLM externa: sin factura por token, sin dependencia de Anthropic. El contrato lo permite y su demo de referencia usa el mismo patrón. Coste: latencia ~5–15 s/respuesta en CPU e imagen +~2 GB. Ver sección 2.5. |
| Llamadas al LLM | **Por evento**, no por intervalo fijo; cooldown por auto para no saturar la CPU del sidecar | Reduce llamadas ~3x sin perder calidad de demo, y evita encolar 6 estrategas contra un Ollama CPU-only. |
| Backend | **FastAPI (Python 3.13)** | Sirve estáticos + proxy al LLM. **NO ASP.NET Core**: el contrato de integración prohíbe tecnología Microsoft en el stack interno. Ver sección 2.2. |
| UI de HUD y radio | **Overlay DOM fuera del canvas**, no Unity UI | La UI dentro del canvas de Unity no puede usar las variables CSS del tema del sitio. Ver sección 2.2. |
| Repo | **Público** | Minutos de GitHub Actions gratis, y permite que este agente cierre el loop de build/verify solo. El contrato también exige imagen pública. |
| Build | **CI** (GitHub Actions + GameCI) | Sin CI, el agente no puede verificar que el WebGL compile — el humano se vuelve cuello de botella. |
| Registry | **GHCR público** (`ghcr.io/alulema/agentic-racing`) | Exigido por el contrato. **NO ACR** — el contrato lo nombra explícitamente como ejemplo de lo prohibido. |
| Entrenamiento en cloud | **VM Azure spot, solo CPU, ~16 vCPU** | Con observaciones vectoriales + MLP pequeño + PPO, la GPU no aporta y a veces es más lenta. El cuello de botella es simular Unity. Ver sección 2.3. |
| Hosting | Infraestructura efímera de alexisalulema.com | Contenedor con ingress interno, sin TLS, sin auth. Ver sección 2.2. |

### 2.1 — Por qué circuito cerrado y no 10 km punto a punto

Esta decisión merece explicación porque el concepto original del proyecto era "una pista de
10 km". La distancia de carrera sigue siendo ~10 km; lo que cambia es la **topología**.
Una pista punto-a-punto de 10 km rompe tres cosas a la vez:

1. **Entrenamiento RL**: episodios larguísimos degradan la asignación de crédito en PPO.
   Con un circuito corto, un episodio es una vuelta, y puedes correr muchas arenas en
   paralelo. Esto es la diferencia entre entrenar en horas y entrenar en días.
2. **Proximidad entre autos**: con vehículos mecánicamente idénticos, en 10 km de recorrido
   único las diferencias mínimas se acumulan y el pelotón se estira hasta que nadie ve a
   nadie. **Sin proximidad no hay estrategia de carrera** — no hay a quién adelantar ni de
   quién defenderse, y toda la capa del jefe de equipo queda sin objeto.
3. **Memoria del estratega**: la bitácora lap-over-lap ("perdiste tiempo en la curva 4")
   solo tiene sentido si **vuelves a pasar por la curva 4**. En punto-a-punto cada curva se
   ve una sola vez y la memoria entre vueltas es literalmente imposible. Como la memoria es
   uno de los pilares de lo que hace "agentic" a este demo, el circuito cerrado no es una
   preferencia: es un requisito.

Corolario para la UI: numera las curvas y expón esos identificadores tanto al estratega
como al espectador. La curva 4 tiene que ser un referente estable compartido entre el LLM,
el piloto y quien está mirando.

### 2.2 — Contrato de integración (restricciones externas, no negociables)

El repo incluye `DEMO_INTEGRATION.md`: es el contrato de la infraestructura efímera que
hospeda el demo. **Léelo completo antes de la Fase 0.** No es una guía, es un contrato:
sus reglas ganan sobre cualquier preferencia técnica. Lo que más impacta el diseño:

- **Nada de tecnología Microsoft en el stack interno** (restricción dura). Por eso el
  backend es FastAPI y no ASP.NET Core, y el registry es GHCR y no ACR. Unity sí pasa:
  es motor de terceros, IL2CPP compila a WebAssembly, y no queda runtime .NET en el
  contenedor — este solo sirve estáticos y hace de proxy.
- **Imagen pública en GHCR.** Solo datos públicos horneados. Los modelos `.onnx` y los
  pesos de `llama3.2:3b` (open-weights) son datos públicos, no hay problema —
  se hornean en la imagen. No hay ninguna API key que inyectar (ver sección 2.5).
- **Sin TLS y sin auth en la app.** El gateway termina TLS y valida el JWT antes de
  enrutar. Confía en todo request que llegue. No implementes login.
- **Sirve desde la raíz `/`**, puerto fijo `8080`, ingress interno. El build de WebGL debe
  usar rutas relativas — verifica esto en Fase 0, es un fallo clásico.
- **Env vars que recibes**: `PROJECT_ID`, `DEMO_SLOT`. **No se declara ningún secreto** —
  el LLM es local (sección 2.5), así que no hay `ANTHROPIC_API_KEY` ni equivalente.
- **Stateless.** El servidor no guarda nada. Los logs de carrera (que la Fase 6.1 necesita)
  viven en memoria del cliente. Esto permite declarar `shareable: true` en el hand-off, lo
  que ahorra provisión — no lo tires por guardar historial en el servidor.

**Dos consecuencias de diseño que no son obvias:**

**a) El HUD y el panel de radio van en DOM, no en Unity UI.** El contrato pide enlazar
`https://alexisalulema.com/demo-theme.css` y estilar con sus variables CSS para que el demo
adopte el branding del sitio automáticamente. La UI dibujada por Unity vive dentro del
`<canvas>` y **no puede tocar CSS** — si construyes ahí el panel de radio, nunca adopta el
tema. Construye HUD y radio como overlay DOM sobre el canvas, comunicándose con Unity vía
interop JS (`.jslib` plugin + `SendMessage`). Unity se queda solo con pista y autos. Es
mejor diseño igual: el texto del razonamiento del LLM queda legible y seleccionable.

**b) Necesitas heartbeat o te destruyen la carrera a mitad.** La infra hace teardown tras
~8 min sin tráfico de cliente. Como toda la simulación corre en el browser, es
perfectamente posible que pasen minutos sin una sola request al contenedor (modo
heurístico, o LLM en cooldown). Implementa un ping periódico mientras haya carrera activa.
Sin esto el demo se cae solo y va a parecer un bug aleatorio imposible de reproducir.

**Ciclo de vida que la app debe tolerar**: sesión ~20 min (tope duro), ~8 min de
inactividad → teardown, vida máxima del entorno ~60 min, kill-switch en cualquier momento.
Diseña para teardown abrupto.

### 2.3 — Infraestructura de entrenamiento

**No uses GPU.** Con observaciones vectoriales (raycasts), red MLP pequeña y PPO, la GPU no
aporta y frecuentemente es más lenta que CPU: el cuello de botella es simular Unity, no
calcular gradientes. Si en algún momento consideras cambiar a observaciones visuales
(cámaras), ese cálculo cambia — pero ese cambio no está en el plan.

Setup:
- VM Azure **spot**, compute-optimized, ~16 vCPU — o la NUC del dueño del proyecto si
  alcanza para la corrida (decisión revisada 2026-09-04, ver §9 y `docs/Devlog.md`: el
  player de entrenamiento es **Windows/Mono**, no Linux/IL2CPP, así que si se usa VM cloud
  debe ser Windows). Spot da 60–90% de descuento y el entrenamiento tolera desalojos si
  haces checkpoint frecuente y usas `--resume`.
- **No corras el Editor en la VM.** Construye un player headless de Windows (§9) de la
  escena de entrenamiento, súbelo (o entrena localmente si ya estás en esa máquina), y
  corre `mlagents-learn --env=<build>`. Ese binario no requiere licencia Unity.
- Muchas arenas en paralelo (`--num-envs`), ajustado al número de núcleos.
- **Desasigna la VM cuando no entrenes.** Detenida-desasignada no cobra cómputo.
- Presupuesto estimado del proyecto completo: **~$15–40 en spot** (~80–150 horas de VM,
  incluyendo iteraciones fallidas de recompensa y las 4–6 corridas de la población de
  pilotos). Verifica tarifas actuales antes de provisionar.

### 2.4 — Duración de sesión

Una carrera de ~10 km a velocidad de competencia dura aproximadamente 5–8 minutos. La
sesión tiene tope de ~20 minutos. Diseña en consecuencia: no estires artificialmente una
carrera para llenar el tiempo. Una sesión típica es intro/selección de seed + dos o tres
carreras, o directamente el modo de campo mixto de la Fase 6.3, que son varias carreras
por diseño.

### 2.5 — LLM local, no API hosted (decisión revisada 2026-08-31)

La tabla de la sección 2 decía originalmente `claude-haiku-4-5` hosted, todo en vivo. Se
cambió a un **modelo local `llama3.2:3b` servido por un sidecar Ollama**, por
decisión del dueño del proyecto: no querer usar ni depender de modelos de Anthropic. El
contrato de integración lo permite explícitamente (`DEMO_INTEGRATION.md` punto 6 nombra
Claude solo como *ejemplo* de opción aceptable, y su demo de referencia `rag-blogposts`
es autohospedado con Ollama). Esta decisión de la sección 2 queda cerrada en su nueva
forma: no re-litigar hacia una API hosted sin hablarlo.

**Qué cambia respecto al diseño original:**

- **Modelo**: `llama3.2:3b` (open-weights, ~2 GB en q4). Elegido para caber en el
  pod efímero CPU-only (2 vCPU / 4 GiB) con margen para los hasta 6 estrategas por auto.
- **Empaquetado**: los pesos van **horneados en la imagen** (no `ollama pull` al arrancar),
  para arranque determinista y sin red. Coste: la imagen sube ~2 GB y la infra la jala
  fresca en cada provisión — es el mayor golpe al riesgo de "peso de imagen" (§11).
- **Sin secretos**: no hay `ANTHROPIC_API_KEY` ni equivalente. La sección 2.2 y el
  hand-off manifest de la Fase 5 no declaran ningún secreto.
- **Coste**: desaparece la factura por token, y con ella el riesgo #1 del proyecto
  ("tráfico no acotado" contra una API medida, §7). El riesgo se reconvierte en
  **saturación de CPU y latencia**: 6 estrategas encolados contra un Ollama CPU-only.
  Los guardrails de §7 se reinterpretan en esa clave (ver §7 y §6.7).
- **Latencia**: ~5–15 s por respuesta en CPU. Aceptable porque la carrera nunca espera al
  LLM (§6.6) y el disparo es por evento con cooldown, pero el radio irá algo más desfasado
  de lo que pasa en pista que con un modelo hosted rápido.
- **Contrato**: el demo pasa de "sin recursos extra" a declarar el sidecar Ollama en el
  hand-off manifest (§5, Fase 5).

---

## 3. Stack

**Simulación / visual**
- Unity 6 LTS (6000.x), URP
- C# para toda la lógica de juego
- Físicas: Rigidbody + torque manual. **NO usar WheelCollider** — es más difícil de
  entrenar, más pesado, y aporta realismo que este demo no necesita.

**RL**
- Paquete `com.unity.ml-agents` (Unity, C#) — release 4.x
- `mlagents` (Python) para el loop de entrenamiento — PPO, self-play
- Export a `.onnx`, inferencia dentro del build con **`com.unity.ai.inference`**

> ⚠️ **Nombre del paquete**: el producto se llamó Sentis, luego Inference Engine, y desde la
> versión 2.4 volvió al *display name* Sentis — pero el identificador de paquete real es
> `com.unity.ai.inference` y el namespace en C# es `Unity.InferenceEngine`. **No instales
> `com.unity.sentis`**: es el paquete viejo. En Package Manager puedes buscar por cualquiera
> de los dos nombres, pero el que se agrega es `com.unity.ai.inference`. ML-Agents 4.x ya
> depende de este paquete internamente, así que verifica que no haya conflicto de versión.

**Capa agentic**
- Modelo local **`llama3.2:3b`** servido por un **sidecar Ollama** (CPU-only),
  con los pesos horneados en la imagen. Sin API de LLM externa. Ver sección 2.5.
- FastAPI hace de proxy: recibe la telemetría en `/api/strategy`, llama a Ollama
  (`/api/chat` con salida JSON forzada) y valida la respuesta contra el esquema de 6.4.
- Llamadas desde el overlay DOM (JS) o desde C# vía interop, hacia `/api/strategy`

**UI**
- HUD y panel de radio en **HTML/CSS/JS sobre el canvas**, no Unity UI
- Tema del sitio vía `<link rel="stylesheet" href="https://alexisalulema.com/demo-theme.css">`
- Estilar solo con `var(--color-*)`, `var(--font-*)`, `var(--radius)` — nunca colores
  hardcodeados. Con fallback mínimo por si el tema no carga.
- Panel "Acerca de este demo" vía `window.DEMO_INFO` + `demo-panel.js` (ver contrato)

**Servidor / despliegue**
- **FastAPI (Python 3.13)**, uvicorn en `0.0.0.0:8080`
- Sirve el build de WebGL como estáticos + `/api/strategy` + `/api/health` + `/api/ping`
- **Sidecar Ollama** en la misma imagen/red, escuchando en `127.0.0.1:11434`, con
  `llama3.2:3b` horneado. Un supervisor arranca `ollama serve` y `uvicorn`.
- Docker sobre `python:3.13-slim` + binario de Ollama; el build de WebGL y los pesos del
  modelo copiados en la imagen. La imagen sube ~2 GB por el modelo — vigilar en Fase 5.
- **GHCR público** → la infra efímera lo jala sin credenciales

> ⚠️ **Headers de Unity WebGL**: los builds sirven archivos comprimidos (`.br` / `.gz`) que
> requieren `Content-Encoding` y `Content-Type` correctos. Servirlos con el StaticFiles por
> defecto de FastAPI falla silenciosamente o con errores raros de carga. Configura los
> headers explícitamente y verifícalo en Fase 0.

---

## 4. Estructura del repositorio

```
/unity/           Proyecto Unity (C#)
  Assets/
    Scripts/
      Track/        Generación procedural de circuito
      Vehicle/      Física del auto, controlador
      Agents/       ML-Agents: observaciones, acciones, recompensas
      Strategy/     Aplicación de directivas del estratega al controlador
      Interop/      Plugin .jslib: puente Unity ↔ overlay DOM
    ML-Agents/      Modelos .onnx importados
/web/             Shell HTML + overlay DOM (HUD, radio, DEMO_INFO)
/training/        Configs YAML de ML-Agents, scripts, build headless Linux, results/
/models/          .onnx entrenados, versionados con el commit que los produjo
/server/          FastAPI: estáticos + /api/strategy (proxy a Ollama) + /api/health + /api/ping
/docker/          Dockerfile (imagen final: app + sidecar Ollama, modelo horneado),
                  Dockerfile.dev (solo app, para compose), entrypoint.sh (supervisor)
/compose.yaml     Dev local: app + Ollama como servicios separados (modelo en volumen)
/.github/workflows/  Build Unity WebGL, build imagen, push a GHCR
DEMO_INTEGRATION.md  Contrato de la infra efímera — LÉELO, no lo modifiques
CLAUDE.md
README.md         Manual de réplica PÚBLICO (ver sección 7)
docs/Devlog.md    Bitácora interna cronológica (ver sección 7)
```

---

## 5. Fases de trabajo

Trabaja **una fase a la vez**. No avances a la siguiente hasta que los criterios de
aceptación de la actual pasen. Cada fase termina en un PR.

### Fase 0 — Spike de riesgo

No empieces por el juego. Valida primero lo que puede matar el proyecto.

- [x] Leer `DEMO_INTEGRATION.md` completo antes de escribir nada
- [x] Proyecto Unity vacío exporta a WebGL, con **rutas relativas**, y corre en browser
      servido desde la raíz `/` — validado en navegador real 2026-08-31
- [x] FastAPI sirve ese build con los **headers correctos para archivos `.br`/`.gz`** de
      Unity, en `0.0.0.0:8080` — `Content-Encoding` + `Content-Type` verificados por `curl`
      y en navegador
- [~] La imagen se publica a **GHCR como paquete público** desde CI — CI verde en el PR de
      Fase 0 (run 2 de #1, 2026-09-02): GameCI activa la licencia Personal, compila WebGL
      6000.3.22f1, y arma la imagen. Falta solo lo humano-only post-merge: mergear #1 →
      primer `push:main` publica a GHCR → marcar el paquete **Público**. Ver `docs/Devlog.md`
- [x] `docker run -p 8080:8080 -e PROJECT_ID=agentic-racing -e DEMO_SLOT=demo01` funciona
      localmente — es la prueba que el contrato define como suficiente
- [x] Un modelo ONNX de juguete (red densa trivial, 3 inputs → 2 outputs) carga con
      `com.unity.ai.inference` y ejecuta inferencia **dentro del build WebGL** — `onnx_ok`,
      salida `[2.600, 3.400]` correcta, en navegador real 2026-08-31
- [x] Queda documentado qué `BackendType` funciona en WebGL y cuál es el costo por
      inferencia, extrapolado a 6 autos simultáneos — `BackendType.CPU`; 3.40 ms en frío
      (crea `Worker` + warmup), 0.10 ms en caliente; ~0.6 ms/frame para 6 autos en caliente.
      Modelo de juguete 3→2: es un piso, el MLP real de Fase 2 será mayor. Ver `docs/Devlog.md`
- [x] **Interop DOM ↔ Unity funcionando en ambas direcciones**: un botón HTML fuera del
      canvas cambia algo en la escena, y un evento de la escena actualiza un elemento DOM.
      Todo el HUD depende de esto. — ambas direcciones validadas en navegador 2026-08-31
- [x] El overlay DOM enlaza `demo-theme.css` y usa sus variables CSS (con fallback si no carga)
- [x] Una llamada al LLM vía `/api/strategy` devuelve JSON válido contra el esquema,
      resuelta por el **sidecar Ollama con `llama3.2:3b`** (sin API externa, sin
      secretos) — validado 2026-09-01 en las **dos** topologías:
      `docker compose up` (app + Ollama como servicios separados, modelo en volumen) →
      `HTTP 200`, JSON válido, 6.8 s en frío / 2.4 s caliente; e **imagen final**
      (`docker build -f docker/Dockerfile` con el modelo horneado + `entrypoint.sh`
      supervisando `ollama serve` + `uvicorn`) → `docker run` arranca, `/api/strategy`
      `HTTP 200` con JSON válido (~3.7 s), sirve estáticos desde `/`, Ollama escucha
      solo en loopback. Bug encontrado y corregido: el Dockerfile no instalaba `zstd`,
      que el instalador de Ollama ahora exige. Hallazgo de peso: la imagen final pesa
      **~8.3 GB** (runtime de Ollama con libs GPU ~2.25 GB + modelo ~2.0 GB), no los
      "~2 GB extra" que asumen §2.5/§3/§11 — a optimizar en Fase 5. Ver `docs/Devlog.md`.

**Criterio de aceptación**: todo lo anterior funciona en el entorno de despliegue real, no
solo en el editor. Si la inferencia con Inference Engine falla en WebGL, toda la
arquitectura de "inferencia en cliente" se cae y hay que replantear el modelo de costos —
hay que saberlo ahora, no en la Fase 4.

### Fase 1 — Pista y física

- [ ] Generación de **circuito cerrado** desde `seed` (int) con spline Catmull-Rom cíclico,
      mesh en runtime. Longitud objetivo 1.5–2.5 km. El spline debe cerrar sobre sí mismo
      con continuidad de tangente — un circuito con una junta visible o un cambio brusco de
      curvatura en el punto de cierre arruina tanto el render como el entrenamiento.
- [ ] Validación de que el trazado no se auto-intersecte y que la curvatura máxima sea
      navegable a velocidad razonable. Si la seed genera algo imposible, re-genera con
      seed derivada de forma determinista (no aleatoria) y déjalo registrado.
- [ ] **Numeración de curvas**: detecta los sectores de curvatura significativa y asígnales
      índices estables (curva 1, 2, 3...). Estos IDs se usan después en la telemetría del
      estratega y en la UI. Deben ser deterministas para una seed dada.
- [ ] La `seed` y el número de vueltas se leen de query params (`?seed=12345&laps=5`)
- [ ] Auto con física de torque manual + fricción lateral, controlable por teclado
- [ ] Trazada ideal (racing line) calculada y expuesta como referencia
- [ ] Conteo de vueltas y detección de cruce de meta

**Criterio de aceptación**: un humano puede dar varias vueltas seguidas con teclado, la
física "se siente" controlable, y el conteo de vueltas es correcto. Si no es divertido de
manejar a mano, el RL tampoco va a aprender bien.

### Fase 2 — RL, un solo agente

- [ ] `Agent` de ML-Agents con observaciones:
      raycasts (bordes de pista), velocidad propia, ángulo respecto a trazada ideal,
      progreso normalizado en la vuelta
- [ ] **Canales de directiva en el vector de observaciones, aleatorizados por episodio.**
      Lee la sección 6.1 antes de definir el espacio de observaciones. En esta fase nada
      escribe esos canales todavía, pero tienen que existir y variar durante el
      entrenamiento para que la política aprenda a condicionarse a ellos. **Si entrenas sin
      ellos, la Fase 4 obliga a reentrenar desde cero.** Es el error más caro que puedes
      cometer en este proyecto.
- [ ] Acciones continuas: `[steering, throttle, brake]`
- [ ] Recompensa base: progreso a lo largo de la pista; penalización por salirse,
      chocar, o quedarse quieto
- [ ] **Un episodio = una vuelta**, no la carrera completa. Esto es lo que hace tratable el
      entrenamiento; ver sección 2.1.
- [ ] Entrenamiento con múltiples arenas en paralelo, **cada una con una seed de circuito
      distinta**. Entrenar sobre un solo trazado produce una política que memoriza esa
      pista y falla en cualquier otra — y como el demo genera pistas aleatorias, eso sería
      un fracaso silencioso que solo notarías en producción.
- [ ] Export a `.onnx` y validación de inferencia en WebGL

**Criterio de aceptación**: el auto completa varias vueltas consecutivas sin salirse, en
**tres seeds distintas que no vio durante entrenamiento**. Una sola seed no demuestra
generalización.

### Fase 3 — Multi-agente competitivo

- [ ] Parámetros físicos **idénticos** para todos los autos: un único ScriptableObject
      compartido, sin variación por instancia. Verifica esto explícitamente — cualquier
      asimetría invalida la premisa del demo.
- [ ] **Población de pilotos**: despliega varios snapshots de política como conductores
      distintos. Mismo auto, distinto piloto. Esto resuelve un problema real: si los 6 autos
      corren el mismo `.onnx` con física idéntica, conducen igual y la carrera es una
      procesión sin nada que mirar. Los snapshots ya existen como subproducto del
      entrenamiento, así que sale gratis, y le da al estratega algo concreto sobre qué
      razonar ("mi piloto es débil en curva lenta").

  **Cómo seleccionarlos — importa mucho.** No tomes checkpoints espaciados a lo largo del
  entrenamiento (1M, 2M, 3M... pasos). Si lo haces, el último es sencillamente mejor que
  todos, gana siempre, y vuelves a tener una procesión — pero peor: arruinas el experimento de campo mixto de la
  Fase 6.3, porque estarías midiendo **habilidad de piloto** en vez de **aporte del
  estratega**, que es justo lo que el experimento pretende aislar.

  Dos opciones válidas, en orden de preferencia:
  1. **Entrenamientos independientes con distinta semilla aleatoria** (4–6 corridas). Produce
     políticas genuinamente distintas y de nivel comparable. Más caro en tiempo de cómputo,
     pero es la opción limpia.
  2. **Snapshots de la fase tardía**, tomados solo después de que la curva de recompensa se
     aplanó. Ahí las políticas ya son comparables en ritmo y las diferencias son de estilo,
     no de nivel. Más barato, aceptable si el tiempo aprieta.

  **Verificación obligatoria antes de pasar a Fase 4**: corre a los candidatos entre sí en
  varias seeds sin capa LLM y mide tiempo de vuelta medio. Si un piloto gana
  sistemáticamente por un margen amplio, descártalo o sustitúyelo — la población tiene que
  estar emparejada en ritmo. Documenta esos tiempos: son la línea base contra la que se
  interpreta todo lo de la Fase 6.3.
- [ ] Observaciones extendidas: posición y velocidad relativa de los N rivales más cercanos
- [ ] Self-play habilitado en la config de ML-Agents
- [ ] Recompensa con componente de posición relativa (adelantar suma, ser adelantado resta),
      no solo velocidad absoluta
- [ ] Manejo de colisiones entre autos

**Criterio de aceptación**: 6 autos completan una carrera de varias vueltas sin trabarse,
el pelotón **no se estira hasta perder contacto** (mide el gap entre P1 y P6 al final —
si es enorme, hay un problema de balance o de topología de pista), y se observa al menos
un adelantamiento limpio no programado explícitamente.

### Fase 4 — Capa agentic (el diferenciador)

**La sección 6 es la especificación completa. Impleméntala desde ahí; esto es solo el
checklist de cierre.**

- [ ] Cada auto tiene su propio jefe de equipo LLM **independiente**. No un cerebro
      central: si todos comparten estratega no hay competencia real de tácticas.
- [ ] Disparo por evento con cooldown y coalescing (6.6), más límite global de llamadas
      concurrentes a Ollama (§7). La carrera **nunca espera** al LLM
- [ ] Prefijo estable + sufijo variable (6.7); modelo caliente con `keep_alive` largo
- [ ] Salida JSON forzada en la llamada a Ollama (`format` / grammar) y respuesta validada
      contra el esquema (6.3, 6.4, 6.8); medir la tasa de respuestas descartadas — con un
      modelo 3B es más alta que con uno hosted, y es dato para el post técnico
- [ ] Mapeo directiva → controlador en un único ScriptableObject (6.5), escribiendo sobre
      los canales de observación que ya existen desde Fase 2
- [ ] Bitácora lap-over-lap alimentando el campo `notes`
- [ ] **UI de radio de equipo (DOM, no Unity UI)**: panel overlay que muestra el campo
      `radio` en vivo junto a cada auto, estilado con las variables del tema del sitio.
      Esto es lo que hace que el demo comunique el concepto — no lo dejes para el final.
- [ ] **Heartbeat**: ping a `/api/ping` mientras haya carrera activa, con periodo cómodo
      bajo el umbral de inactividad de ~8 min. Sin esto la infra destruye el entorno a
      mitad de carrera cuando el LLM está en cooldown, y parece un bug aleatorio.
- [ ] Fallback: si Ollama falla, expira, o el cortacircuitos de carga (§7) se dispara, el
      auto usa una directiva heurística por defecto y el demo sigue corriendo. **Nunca**
      debe romperse la carrera por un problema del LLM.
- [ ] Manejo elegante del cierre abrupto de conexión (el gateway corta al expirar sesión)

**Criterio de aceptación**: una carrera de 20 minutos completa sin errores, con el panel
de radio mostrando razonamiento coherente con lo que pasa en pista.

### Fase 5 — Empaquetado y control de carga

- [ ] Optimización del build WebGL: code stripping, compresión Brotli, texture compression.
      Vigila el tamaño: la infra jala la imagen fresca en cada provisión y el visitante
      descarga el build completo. Ojo: el modelo `llama3.2:3b` horneado ya añade ~2 GB a
      la imagen — el margen para el resto es más estrecho que antes.
- [ ] Guardrails de carga (ver sección 7)
- [ ] Reparto de vCPU entre el sidecar Ollama y el proxy dentro del pod (2 vCPU / 4 GiB),
      y `keep_alive` del modelo ajustado a la duración de sesión
- [ ] Endpoints `/api/health` y `/api/ping`
- [ ] Indicador en la UI del estado del LLM: latencia p95, si está en "modo offline"
      (cortacircuitos disparado), y nº de respuestas descartadas por validación
- [ ] **Panel "Acerca de este demo"**: rellenar `window.DEMO_INFO` con contenido propio
      (bilingüe ES/EN), incluyendo diagrama Mermaid de la arquitectura de dos niveles, los
      componentes de infra, decisiones de diseño y limitaciones honestas. El contrato trae
      el esquema y un ejemplo ilustrativo — **no lo copies, escribe el de este demo**.
- [ ] **Hand-off manifest** para el mantenedor de la infra: `projectId` (`agentic-racing`),
      nombre legible ES/EN, URL del repo público, imagen GHCR + puerto `8080`,
      `shareable: true` (el servidor es stateless), **sin secretos** (el LLM es local),
      y **recursos extra: sidecar Ollama en la misma imagen** con `llama3.2:3b`
      horneado (levantado por el supervisor junto con uvicorn, escucha en `127.0.0.1:11434`).

### Fase 6 — Diferenciadores (lo que separa esto de un demo genérico de RL)

Esta fase existe porque "RL maneja un auto" y "LLM decide estrategia" son, por separado,
proyectos comunes. Lo que no es común es la combinación **auditable, honesta y medible**.
No agregues las cuatro piezas a la vez — cada una es independiente y se puede cortar si
el tiempo aprieta, en el orden dado (4 es la primera en sacrificarse, 1 la última).

- [ ] **6.1 — Trazabilidad de decisiones (prioridad alta, bajo esfuerzo)**
  Cada llamada al LLM ya genera telemetría + respuesta — persístela completa (no solo
  el campo `radio`) en una bitácora por auto y por carrera. UI: clic sobre cualquier
  mensaje de radio en el replay muestra el input exacto que tenía el LLM en ese momento
  y permite pedirle que re-explique su propia decisión con ese contexto. Esto es lo que
  convierte el demo de "se ve bien" a "se puede auditar" — es la pieza más alineada con
  la conversación actual sobre interpretabilidad de agentes.

- [ ] **6.2 — Errores visibles, no solo victorias (prioridad alta, bajo esfuerzo)**
  No filtres ni "arregles" las decisiones tácticas que salen mal. Cuando un adelantamiento
  arriesgado falla o una directiva de "atacar" resulta en pérdida de posición, regístralo
  explícitamente en la bitácora como una decisión fallida (no como un bug). Represéntalo
  en la UI igual que un acierto — sin dramatizarlo ni ocultarlo. El punto es demostrar que
  el agente razona con información incompleta y a veces se equivoca, en vez de vender una
  demo donde la IA siempre gana.

- [ ] **6.3 — Comparación medible: campo mixto (prioridad media, esfuerzo medio)**

  **Cómo NO hacerlo** (era el diseño original de este documento y estaba mal): correr la
  misma seed dos veces, una con todos los autos usando LLM y otra con ninguno, y comparar
  posiciones finales. Eso no mide nada. La posición es **suma cero dentro de una carrera**:
  alguien gana en ambos casos. P1 de la carrera A y P1 de la carrera B no son cantidades
  comparables. Además, con política determinista y física determinista, la carrera sin LLM
  produce un único resultado, no una distribución.

  **Cómo sí**: **campo mixto en la misma carrera**. De 6 autos, 3 con jefe de equipo LLM y
  3 con la directiva heurística fija. Ahí la posición sí significa algo, porque es un
  enfrentamiento directo bajo condiciones idénticas.

  Requisitos metodológicos:
  - **Rotar posiciones de parrilla** entre corridas para que la ventaja de salida no se
    confunda con el efecto del LLM. Sin esto, mides la parrilla, no la estrategia.
  - **Rotar qué snapshot de piloto** lleva LLM y cuál no, por la misma razón.
  - Correr **muchas seeds**, no una. Una sola seed favorable no es evidencia de nada.
  - Métrica primaria: posición media de finalización del grupo LLM vs el grupo heurístico.
    Métricas secundarias: tiempo total, adelantamientos completados, incidentes.
  - Reportar **dispersión**, no solo la media. Si la varianza entre seeds se come la
    diferencia, el resultado honesto es "no hay efecto detectable".

  El modo heurístico de fallback (ya construido en Fase 4 como red de seguridad) debe ser
  seleccionable manualmente por auto, no solo activarse ante error.

  Esta es la fase que genera los datos de los que depende la Fase 7. Si la recortas, el
  reporte técnico pierde su parte cuantitativa.

- [ ] **6.4 — Presupuesto de decisiones limitado (prioridad baja, esfuerzo medio-alto)**
  El jefe de equipo recibe un número fijo de "cambios de estrategia" disponibles por
  carrera (ej. 3 por auto) en vez de poder emitir directivas sin costo. Obliga al LLM a
  razonar sobre cuándo vale la pena gastar el recurso — más cerca de planning bajo
  restricción que de simple asesoría. Es la pieza más interesante técnicamente y también
  la que más se parece a un proyecto aparte: si el tiempo aprieta, es la primera en cortar.

**Criterio de aceptación de la fase**: al menos 6.1 y 6.2 están implementadas y son
visibles en el demo público. 6.3 y 6.4 son extensiones, no requisito de cierre.

### Fase 7 — Publicación

El contrato exige **tres capas de documentación estratificadas** (no duplicadas). Escribe la
narrativa una vez y destílala hacia las otras.

- [ ] **`README.md` — manual de réplica, público y autocontenido.** Que alguien clone el
      repo y levante el demo por su cuenta con `docker run`, sin orquestación externa.
      Estructura: qué es · arquitectura (+ diagrama) · prerrequisitos · build · run ·
      variables de entorno · uso · limitaciones.

      ⚠️ **Límite duro del contrato**: el README **no menciona** alexisalulema.com, el
      gateway, el JWT, la nube que lo hospeda ni hostnames internos. No es una omisión
      incómoda — por contrato el demo es un contenedor OSS que corre en cualquier lugar con
      Docker, así que "corre donde sea" es literalmente cierto. Si necesitas explicar por
      qué la app no lleva auth ni TLS, dilo en abstracto: *"diseñada para correr detrás de
      un reverse proxy que termina TLS y hace la autenticación"*.

- [ ] **`docs/Devlog.md` — bitácora interna cronológica.** Actividades, decisiones,
      problemas y cómo se resolvieron. Aquí SÍ puedes anotar cualquier detalle, incluida la
      plataforma. Es la memoria para reconstruir el proyecto después. Regístralo **durante**
      el trabajo, no al final.

- [ ] **`window.DEMO_INFO` — subconjunto destilado in-demo.** Ya cubierto en Fase 5.

- [ ] **Post técnico** (blog en alexisalulema.com): recorrido del diseño — por qué RL para
      control y LLM para estrategia, qué se intentó y no funcionó (recompensas que se
      degeneraron, problemas de Inference Engine/WebGL si los hubo), qué mostró el campo
      mixto. Esto es más valioso para portafolio que el código en sí: demuestra el
      razonamiento de diseño, no solo el resultado.

- [ ] **Evaluación honesta de si hay algo publicable como paper corto** (workshop, no
      venue mayor): esto solo tiene sentido si 6.3 produce señal real y repetible — una
      diferencia consistente en posición media entre el grupo LLM y el grupo heurístico,
      a través de muchas seeds y con parrilla y pilotos rotados. Si la dispersión entre
      seeds se come la diferencia, o si el efecto desaparece al rotar la parrilla, dilo así
      en el post técnico y no fuerces el marco de paper. Un hallazgo negativo bien reportado
      ("la capa LLM no mejoró el resultado de forma consistente, y esto es lo que sugiere
      sobre dónde sí y dónde no aporta un estratega LLM") es más honesto, más defendible en
      una entrevista, y a veces más interesante que uno forzado.

- [ ] Si el punto anterior resulta afirmativo, plantéalo como nota corta (formato workshop:
      motivación, método, resultado del campo mixto a través de N seeds, límites) — no como
      objetivo de entrada del proyecto. La Fase 6.3 ya genera los datos necesarios.

**Criterio de aceptación de la fase**: alguien que no participó en el proyecto puede leer
el README y el post, entender la arquitectura y correr el demo, sin necesitar explicación
adicional — y sin que el README revele nada de la infra que lo hospeda.

---

## 6. Especificación del jefe de equipo (estratega LLM)

Esta sección es el corazón conceptual del demo. Impleméntala en Fase 4, pero **léela antes
de la Fase 2**: hay una decisión aquí que hay que tomar al diseñar el espacio de
observaciones, y retrofitearla después es caro.

### 6.1 — La decisión que va antes que todo: la directiva es una observación

Para que el estratega realmente influya en cómo conduce el piloto, la directiva tiene que
ser **parte del vector de observaciones desde el entrenamiento**, con valores aleatorizados
durante las Fases 2 y 3. Así la política aprende a conducir distinto según la directiva, y
en Fase 4 el LLM simplemente decide qué valor poner ahí.

La alternativa —dejar la política entrenada tal cual y en Fase 4 post-procesar sus salidas
o recortar su envolvente— es mucho más pobre: el piloto no cambia de comportamiento, solo
se le limita. El adelantamiento agresivo no *emerge*, se *inhibe* lo contrario.

**Por lo tanto, en Fase 2 el vector de observaciones debe incluir ya los canales de
directiva, aleatorizados en cada episodio, aunque en esa fase nada los esté escribiendo.**
Entrenar sin ellos y añadirlos en Fase 4 obliga a reentrenar todo desde cero.

### 6.2 — Qué sabe el estratega y qué no

La asimetría de información es deliberada y es lo que hace demostrable la separación de
niveles. Modela el muro de boxes real: ve más *contexto* que el piloto, pero menos
*inmediatez*.

**El estratega SÍ ve:**
- Clasificación completa: posición de los 6 autos, gaps en segundos por delante y por detrás
- Tiempos de vuelta propios y de rivales, vuelta a vuelta
- Mapa completo del circuito, con las curvas numeradas (ver Fase 1)
- Su propia bitácora de vueltas anteriores
- Vueltas restantes, incidentes ocurridos
- Perfil de su piloto (qué snapshot lleva, sus tendencias observadas)

**El estratega NO ve:**
- Estado del auto frame a frame (velocidad instantánea, ángulo, raycasts)
- Nada de los próximos segundos: no puede reaccionar a un adelantamiento en curso
- Telemetría interna de los rivales (solo lo observable desde fuera: posición y tiempos)

**El piloto (RL) NO ve** la clasificación global ni tiempos de vuelta: solo su entorno local
y la directiva vigente. Esta es la razón por la que necesita al estratega.

### 6.3 — Payload de telemetría (entrada)

Compacto y estable. Todo lo que no cambie durante la carrera va en el prefijo cacheado, no
aquí.

```json
{
  "event": "lap_completed | rival_in_range | position_change | incident | final_lap",
  "lap": 3,
  "laps_remaining": 2,
  "me": {
    "car_id": "car_03",
    "position": 4,
    "last_lap_time": 84.2,
    "best_lap_time": 83.7,
    "gap_ahead": 1.4,
    "gap_behind": 6.8,
    "current_directive": "conserve",
    "incidents": 0
  },
  "rivals": [
    { "car_id": "car_01", "position": 3, "gap": -1.4, "last_lap_time": 84.0, "trend": "stable" },
    { "car_id": "car_06", "position": 5, "gap": 6.8, "last_lap_time": 83.9, "trend": "closing" }
  ],
  "notes": [
    "L2 turn 4: perdí 0.3s, entrada demasiado lenta",
    "L2: intento de adelantamiento a car_01 en turn 7 fallido, perdí posición"
  ]
}
```

`notes` es la memoria lap-over-lap. Bandéala: últimas 3–4 vueltas más un conjunto pequeño de
notas por curva. Sin límite, el costo por llamada crece con la carrera.

### 6.4 — Respuesta (salida)

```json
{
  "directive": "attack | defend | conserve | push",
  "aggression": "low | medium | high",
  "risk_tolerance": "low | medium | high",
  "target_rival": "car_01 | null",
  "focus_corners": [4, 7],
  "radio": "string, máximo 15 palabras",
  "rationale": "string, máximo 40 palabras — no se muestra en vivo, se guarda para 6.1"
}
```

**Usa niveles discretos, no flotantes 0.0–1.0.** Los LLM son inconsistentes en escalas
continuas: se agrupan alrededor de 0.7–0.8 y la misma situación produce valores distintos
sin razón. Los niveles discretos son más reproducibles, más interpretables para el
espectador, y —importante— hacen que el experimento de campo mixto de la Fase 6.3 sea
comparable entre corridas. C# los mapea a valores numéricos concretos definidos en un solo
lugar.

`radio` es lo que se muestra en vivo. `rationale` es más largo y se guarda para la
trazabilidad de la Fase 6.1.

### 6.5 — Mapeo directiva → controlador

Define este mapeo en **un solo ScriptableObject**, no disperso en el código. Los valores
concretos se calibran en Fase 4; lo que importa es qué canales existen, porque son los que
deben estar en el vector de observaciones desde Fase 2 (ver 6.1).

| Canal | Qué modula | Efecto de nivel alto |
|---|---|---|
| `aggression` | Margen de frenada y agresividad de salida de curva | Frena más tarde, acelera antes, más riesgo de bloquear o irse largo |
| `risk_tolerance` | Tolerancia a proximidad y a contacto | Acepta huecos más estrechos, se mantiene rueda a rueda |
| `directive` | Sesgo de línea y prioridad | `defend` sesga a línea interior; `attack` busca hueco; `conserve` prioriza consistencia |

`target_rival` y `focus_corners` no van al controlador RL: son contexto para la UI y para la
bitácora. Resistir la tentación de meterlos como observación — inflan el espacio de
observaciones y el beneficio es dudoso.

### 6.6 — Disparo y cadencia

Eventos que disparan llamada: fin de vuelta; rival entra en rango de ataque sostenido
(no un cruce momentáneo); cambio de posición; incidente; inicio de última vuelta.

- **Cooldown por auto** de ~10–15 s, aunque se acumulen eventos. Coalesce: si varios
  eventos ocurren durante el cooldown, manda uno solo con el más relevante.
- **Límite global** por carrera, además del cooldown por auto.
- La respuesta llega asíncrona. **La carrera nunca espera al LLM**: hasta que llegue, el
  auto sigue con la directiva vigente. Si el auto es adelantado mientras esperas la
  respuesta, la respuesta puede llegar obsoleta — es aceptable y es parte de la premisa
  (el muro también reacciona tarde). No intentes "arreglarlo" pausando la simulación.

### 6.7 — Reutilización de contexto (KV-cache de Ollama)

Con LLM local no hay "prompt caching" facturable, pero la estructura sigue importando por
**latencia**: Ollama mantiene el KV-cache del prompt mientras el modelo siga cargado
(`keep_alive`) y reutiliza el prefijo común entre llamadas consecutivas del mismo
estratega. Diséñalo igual que si fuera cache de tokens:

- **Prefijo estable** (idéntico en todas las llamadas de la carrera para un auto): rol de
  jefe de equipo, reglas, esquema de salida, mapa del circuito con curvas numeradas,
  perfil del piloto.
- **Sufijo variable**: el payload de 6.3.

Un estratega por auto, con prefijos distintos (cada uno conoce a su piloto). El mapa del
circuito es común, así que ponlo al inicio del prefijo. Mantén el modelo caliente con
`keep_alive` largo (ej. `-1` o el largo de la sesión) para no pagar recargas. Con un solo
Ollama CPU-only sirviendo a 6 estrategas, las llamadas se serializan: el cooldown por auto
(§6.6) es lo que impide que la cola crezca sin control.

### 6.8 — Validación y fallo

Todo lo que vuelve del LLM es no confiable hasta validarse:
- Valida contra el esquema. Enum desconocido, `car_id` inexistente o campo faltante →
  descarta la respuesta completa y mantén la directiva vigente. **No parchees parcialmente**.
- Recorta `radio` a 15 palabras en el cliente aunque el modelo se pase.
- Timeout corto. Vencido, la directiva vigente sigue.
- Registra siempre en la bitácora: llamadas fallidas, timeouts y respuestas rechazadas
  cuentan como datos para la Fase 6.1 y para el post técnico.
- El estratega **nunca controla directamente** el auto. Su única superficie de escritura son
  los tres canales de 6.5. Si en algún momento parece necesario darle control más directo,
  para y pregunta: eso rompería la premisa de la separación de niveles.

---

## 7. Guardrails de carga — no negociables

Con LLM local no hay factura por token, así que el riesgo deja de ser el gasto y pasa a ser
la **saturación de CPU**: 6 estrategas encolados contra un Ollama CPU-only pueden hacer que
las respuestas lleguen minutos tarde y que el pod efímero se ahogue. Implementa **todas**
estas capas:

1. **Cortacircuitos global** en el proxy: si la cola de peticiones a Ollama pasa de un
   umbral (ej. N pendientes o latencia p95 > X s), el sistema cae automáticamente a
   directivas heurísticas sin LLM y lo indica en la UI ("modo offline"). No devuelve error.
2. **Rate limit por IP/sesión** en el proxy (la simulación corre en el cliente; esto acota
   un cliente abusivo que dispare `/api/strategy` a mano).
3. **`num_predict` acotado** en cada llamada a Ollama (equivalente a `max_tokens`). El
   campo `radio` tiene límite de 15 palabras por diseño, no por casualidad; `rationale` a
   40. Un tope duro de ~150 tokens de salida mantiene la latencia bajo control.
4. **Modelo caliente** (`keep_alive` largo) y prefijo estable por auto (§6.7) para no pagar
   recargas ni recomputar el KV-cache del prefijo.
5. **Cooldown mínimo** entre llamadas del mismo auto (~10–15 s, §6.6), aunque se disparen
   varios eventos, más un **límite global de llamadas concurrentes** a Ollama (ej. 1–2):
   es la defensa principal contra la cola.
6. **Un solo turno de inferencia por request.** Nada de reintentos automáticos en cadena
   contra Ollama: si la respuesta no valida (§6.8), se descarta y sigue la directiva
   vigente — no se re-pregunta en el mismo evento.

No hay presupuesto de consola que configurar (no hay API hosted). El tope real es el
recurso del pod efímero (2 vCPU / 4 GiB): el sidecar Ollama y el proxy comparten esa CPU,
así que el reparto de vCPU entre ambos es parte del tuning de la Fase 5.

---

## 8. Qué puedes hacer tú y qué necesita al humano

Este documento describe el proyecto completo, pero no todo está a tu alcance. Cuando llegues
a uno de estos puntos, **para y pide** en vez de inventar un rodeo:

**Solo el humano puede:**
- Activar la licencia Unity (flujo `.alf` → `.ulf`) y cargarla como secret de GitHub
- Provisionar la VM de entrenamiento y desasignarla al terminar
- Entregar el hand-off manifest al mantenedor de la infra
- **Lanzar el entrenamiento**: tú escribes la config YAML, los scripts y el `Agent` en C#,
  pero correr `mlagents-learn` requiere la VM y horas de cómputo. El humano lo lanza y te
  devuelve el `.onnx` y los logs. Las Fases 2 y 3 se leen como ejecutables de corrido y no
  lo son — el ciclo real es: tú preparas → el humano entrena → tú analizas resultados y
  ajustas recompensas → repetir.

**Tú sí puedes** (y debes hacerlo sin pedir permiso): escribir todo el C#, Python, HTML/JS,
Dockerfile y workflows; abrir PRs; leer logs de CI fallidos y corregir; correr el contenedor
localmente para verificar; analizar resultados de TensorBoard que el humano te comparta.

## 9. Versiones

Fíjalas explícitamente y no las cambies sin avisar. La versión del editor debe coincidir
exactamente con la imagen de GameCI en CI.

- Unity: **6000.3.22f1 (Unity 6.3 LTS)**. Fijada — no la cambies sin avisar. La imagen de
  GameCI en CI debe coincidir exactamente con este string. Se eligió LTS sobre 6.5 porque
  ML-Agents es la dependencia más frágil del stack y es la que más probablemente fue
  validada contra LTS; además 6.3 LTS tiene soporte hasta diciembre de 2027.
- Módulos del editor requeridos: **Web Build Support** (target del demo) y **Windows Build
  Support (Mono)** (player headless de entrenamiento). Ningún otro.

  > ⚠️ **Por qué Windows/Mono y no Linux/IL2CPP para el player de entrenamiento** (decisión
  > revisada 2026-09-04, ver `docs/Devlog.md`): la intención original era un player Linux
  > para la VM de entrenamiento (§2.3), con IL2CPP porque Unity 6 **eliminó** el scripting
  > backend Mono para el target Linux Standalone — hoy Linux solo ofrece IL2CPP. El problema:
  > el comunicador gRPC que trae empaquetado ML-Agents (`Grpc.Core`, la librería vieja del
  > proyecto grpc/grpc) no funciona bajo IL2CPP — su callback de redirección de logs nativos
  > no está marcado `[MonoPInvokeCallback]`, así que el AOT de IL2CPP no puede generar el
  > trampolín y truena con `System.NotSupportedException` al arrancar, antes de completar el
  > handshake con `mlagents-learn`. Mono sí lo resuelve vía JIT. Windows Standalone sí sigue
  > ofreciendo Mono (`windows-mono` en la CLI de Unity Hub), así que el player de
  > entrenamiento se construye para Windows en su lugar — se corre en la partición Windows de
  > la NUC del dueño del proyecto, o en una VM Windows si hace falta más cómputo. El WebGL del
  > demo no se toca: sigue en IL2CPP (WebGL lo exige de todas formas) y no usa este
  > comunicador.
- `com.unity.ml-agents`: release 4.x
- `com.unity.ai.inference`: la versión que ML-Agents 4.x requiera — verifica que no haya
  conflicto antes de fijarla
- Python: 3.13 (servidor). El entorno de entrenamiento puede requerir otra versión: usa la
  que el release de ML-Agents especifique, no la más nueva.
- `mlagents` (Python): **del mismo release que el paquete de Unity**. Este es un footgun
  clásico: si el paquete de Unity y el de Python se desincronizan, falla con errores de
  protocolo gRPC difíciles de diagnosticar. Si ves algo así, revisa esto primero.

## 10. Convenciones

- **Idioma**: código, nombres y comentarios en inglés. Documentación y PRs pueden ir en español.
- **C#**: convenciones estándar de .NET. Nada de lógica de gameplay en `Update()` que
  pueda vivir en `FixedUpdate()` — la física del auto va en `FixedUpdate()`.
- **Determinismo**: dada una `seed`, la pista debe ser idéntica siempre. No uses
  `Random` sin seed en la generación.
- **Modelos**: cada `.onnx` en `/models` se commitea junto a la config YAML que lo produjo
  y una nota del commit de código con el que se entrenó. Sin eso los resultados no son
  reproducibles.
- **Secretos**: nada de keys en el repo. `.env` en `.gitignore`, secrets de GitHub para CI.

---

## 11. Riesgos conocidos

- **Entrenar sin los canales de directiva en las observaciones** (sección 6.1). Es el error
  más caro posible: no se manifiesta hasta la Fase 4, y para entonces cuesta reentrenar todo
  desde cero. Verifícalo explícitamente al cerrar la Fase 2.

- **Teardown por inactividad a mitad de carrera**: sin el heartbeat de la Fase 4, la infra
  destruye el entorno tras ~8 min sin tráfico. Como la simulación corre en el cliente, es
  fácil que no haya requests. Se manifiesta como un corte aleatorio imposible de reproducir
  localmente — si ves eso, revisa el heartbeat antes que nada.
- **Headers de Unity WebGL**: los archivos `.br`/`.gz` necesitan `Content-Encoding` y
  `Content-Type` correctos. Falla silenciosamente o con errores de carga poco descriptivos.
- **Peso del build + modelo horneado**: los builds de Unity WebGL ya son pesados y el
  visitante los descarga completos; encima el modelo `llama3.2:3b` horneado añade ~2 GB a
  la imagen, y la infra la jala fresca en cada provisión. Vigila el tamaño desde Fase 0;
  que no se convierta en sorpresa en Fase 5.

- **Saturación del sidecar Ollama (CPU-only)**: 6 estrategas contra un solo Ollama sin GPU
  en un pod de 2 vCPU. Si las llamadas se encolan, el radio llega tarde y el pod se ahoga.
  Mitigación en §7 (cooldown por auto, límite de concurrencia, cortacircuitos a modo
  offline). Si el radio se siente desconectado de la carrera, revisa la cola de Ollama
  antes que el prompt. Un modelo 3B también descarta más respuestas por JSON inválido
  (§6.8) que uno hosted — medir esa tasa en Fase 4.

- **Inference Engine + WebGL**: no todos los operadores de PyTorch tienen equivalente 1:1
  en el runtime de inferencia. Valida con un modelo de juguete en Fase 0 antes de entrenar
  nada serio. Mantén la arquitectura de red simple (MLP denso) salvo que haya razón fuerte.
- **Confusión de nombres Sentis / Inference Engine**: el paquete cambió de nombre dos veces.
  Mucha documentación, tutoriales y respuestas de foro que encuentres van a referirse a
  `com.unity.sentis` o al namespace `Unity.Sentis`, que son la versión vieja. El correcto es
  `com.unity.ai.inference` / `Unity.InferenceEngine`. Si un ejemplo no compila, revisa esto
  antes que nada — es la causa más probable.
- **Backend de inferencia en WebGL**: verifica en Fase 0 qué `BackendType` funciona
  realmente en WebGL. No asumas que el backend de GPU compute está disponible; puede que
  tengas que caer a CPU, lo cual cambia el presupuesto de rendimiento con 6 autos
  ejecutando inferencia por frame.
- **WebGL y threading**: el build de WebGL no tiene el mismo soporte de multithreading que
  standalone. Si hay lag con 6 autos, es lo primero a revisar antes de culpar al modelo.
- **Licencia Unity en CI**: la activación Personal requiere el flujo `.alf` → `.ulf`
  guardado como secret, y hay que reactivar periódicamente. Trámite de una vez, pero
  si el CI falla misteriosamente después de meses, revisa esto primero.
- **Población de pilotos desbalanceada**: si los snapshots elegidos no están emparejados en
  ritmo, el experimento de campo mixto de la Fase 6.3 queda contaminado — mides qué piloto es mejor, no si el
  estratega aporta. Es un fallo silencioso: la carrera se ve normal y los números parecen
  válidos. La verificación de tiempos de vuelta al cierre de Fase 3 existe justo para
  atrapar esto antes de que se propague.
- **Entrenamiento multi-agente**: con self-play el entorno es no-estacionario y el
  entrenamiento es inestable por naturaleza. Espera varias iteraciones de tuning de
  recompensas. Si el comportamiento se degenera (autos que se quedan quietos, o que
  se chocan a propósito), casi siempre es la función de recompensa, no el algoritmo.

---

## 12. Cómo trabajar conmigo

- Antes de cada fase, propón un plan corto y espera confirmación.
- Un PR por fase. No mezcles fases.
- Si el build de CI falla, lee el log y corrige — no pidas ayuda hasta haberlo intentado.
- Si una decisión de la sección 2 te bloquea de verdad, **pregunta antes de cambiarla**.
- Prioriza que el demo *comunique el concepto agentic* por encima de que sea un buen
  simulador de carreras. Si tienes que elegir entre física más realista y razonamiento
  más visible, elige lo segundo.
