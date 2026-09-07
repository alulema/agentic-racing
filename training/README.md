# Entrenamiento — Fase 2 (piloto RL, un solo agente)

Este directorio tiene la config de ML-Agents y las instrucciones para lanzar el
entrenamiento. **El entrenamiento lo lanza el humano en una VM cloud** (CLAUDE.md
§8): el agente prepara el código y la escena, el humano corre `mlagents-learn` y
devuelve el `.onnx` + los logs.

## Versiones (fijas — CLAUDE.md §9)

| Componente | Versión |
|---|---|
| Editor Unity | `6000.3.22f1` |
| `com.unity.ml-agents` (paquete Unity) | `4.0.3` |
| `mlagents` (Python) | **del mismo release** que el paquete Unity (release 4). Si Unity y Python se desincronizan, falla con errores raros de gRPC. |
| Python | `mlagents==1.1.0` exige **exactamente** `>=3.10.1,<=3.10.12` — no cualquier 3.10.x (un gestor que solo deja elegir `3.10` sin fijar el patch puede darte, por ejemplo, `3.10.21`, fuera de rango, y `pip install mlagents` falla con "no matching distribution"). |
| `torch` (Python) | **`2.2.2` (CPU)**. `mlagents==1.1.0` pide `torch>=2.1.1` **sin tope**, así que un `pip install mlagents` sin más deja que pip agarre la última (`2.14`), que arrastra `numpy 2.x` y rompe el pin `numpy<1.24` de mlagents → `TypeError: Descriptors cannot be created directly` y demás. Instala `torch==2.2.2` **antes** que mlagents (ver abajo). Con `2.2.2` el export a ONNX usa el exportador clásico y **no** necesita `onnxscript`. |

**Preferir `venv` sobre `conda`** para el entorno de Python de entrenamiento — instala un
Python **3.10.12** exacto (desde [python.org](https://www.python.org/downloads/release/python-31012/)
en Windows, o vía `pyenv`/deadsnakes en Linux) y arma el venv directo con ese intérprete:

**El orden importa**: `torch` fijado **antes** que `mlagents`, para que pip no lo suba a la
última y arrastre `numpy 2.x` / `protobuf` incompatibles (ver tabla de versiones y
`docs/Devlog.md` 2026-09-05). **No instales `onnxscript`** — con `torch==2.2.2` no hace falta.

```powershell
# Windows, con el 3.10.12 de python.org instalado (o `py -3.10-64` si el launcher lo resuelve así)
py -3.10 -m venv .venv
.venv\Scripts\activate
python -m pip install "setuptools<81" wheel   # mlagents usa pkg_resources, retirado de setuptools 81+
pip install "torch==2.2.2" --index-url https://download.pytorch.org/whl/cpu
pip install mlagents==1.1.0
python -c "from mlagents.trainers.learn import main; import mlagents_envs; print('import ok')"
mlagents-learn --help    # comprobar que arranca
```

```bash
# Linux/macOS, con python3.10 (3.10.12) ya instalado
python3.10 -m venv .venv && source .venv/bin/activate
python -m pip install "setuptools<81" wheel
pip install "torch==2.2.2" --index-url https://download.pytorch.org/whl/cpu
pip install mlagents==1.1.0
mlagents-learn --help
```

Verifica tras instalar: `pip list` debe mostrar `protobuf` 3.20.x, `numpy` 1.23.5,
`onnx` 1.15.0, `torch` 2.2.2. Si alguno está fuera de rango, el venv quedó envenenado
(típicamente por un `pip install` posterior) — recréalo desde cero.

Solo si el sistema no trae ningún Python 3.10.x instalable fácilmente (pasó en la NUC:
Ubuntu 26.04 solo trae 3.13/3.14 por `apt`, sin `python3.10` disponible ni en universe), usar
`conda` como fallback, fijando el patch exacto:

```bash
conda create -n agentic-racing-train python=3.10.12 -y && conda activate agentic-racing-train
```

## 1. Construir el player headless de Windows

**No se corre el Editor en la VM/máquina de entrenamiento** (§2.3). Se construye el
player en una máquina con licencia Unity (local o CI) y se sube el binario — o, si
se entrena en la misma máquina donde está el Editor, no hace falta subir nada.

⚠️ **Windows, no Linux.** El player de entrenamiento se construye para
`StandaloneWindows64` con el scripting backend **Mono**, no para Linux/IL2CPP.
Unity 6 eliminó el backend Mono para el target Linux Standalone, y el comunicador
gRPC que trae ML-Agents (`Grpc.Core`) no funciona bajo IL2CPP (`System.
NotSupportedException` por un callback nativo sin `[MonoPInvokeCallback]` — AOT no
puede generar el trampolín, JIT sí). Ver CLAUDE.md §9 y `docs/Devlog.md`
(2026-09-04) para el diagnóstico completo. Esto **no** afecta el WebGL del demo,
que sigue en IL2CPP.

En una máquina Windows con el Editor y el módulo "Windows Build Support (Mono)"
instalado (`unityhub --headless install-modules --version 6000.3.22f1 -m windows-mono`):

```powershell
"<Unity>\Editor\Unity.exe" -batchmode -quit `
  -projectPath unity `
  -executeMethod AgenticRacing.EditorTools.Fase2TrainingBuild.Build `
  -logFile -
# -> unity/Builds/train-windows/train.exe  (player Windows normal, Mono)
```

El build script copia automáticamente `grpc_csharp_ext.x64.dll` junto al `.exe`
(Grpc.Core lo busca ahí, no donde Unity lo empaqueta por defecto — mismo bug que
en Linux, ver Devlog). Si sube a otra máquina, copia `unity/Builds/train-windows/`
entera. No hace falta el módulo "Dedicated Server".

La escena que construye es una rejilla de `TrainingArena` (por defecto 9), cada
una con una seed de circuito distinta (`baseSeed + índice`), separadas 4 km para
que los raycasts no vean arenas vecinas.

## 2. Lanzar el entrenamiento

A diferencia de Linux, un player Windows normal no necesita `Xvfb` ni ningún
framebuffer virtual para correr headless — `-batchmode` (que ya trae por defecto la
`UnityEnvironment` de Python) es suficiente.

> ⚠️ **`runInBackground`**: `mlagents-learn` lanza el player sin foco, y Unity
> estrangula el `FixedUpdate` cuando la ventana pierde foco si `Run In Background`
> está apagado (lo está en `ProjectSettings`, y ML-Agents 4.x ya no lo fuerza) →
> `The Unity environment took too long to respond` / `Workers {0} stuck in waiting
> state`. `Fase2TrainingBuild` ahora hornea `PlayerSettings.runInBackground = true`
> y la escena lo re-fuerza en runtime. Si reconstruyes el player por otra vía,
> asegúrate de que quede activado.

```powershell
.venv\Scripts\activate      # o `conda activate agentic-racing-train` si usaste el fallback
mlagents-learn training/config/race_ppo.yaml `
  --env=Builds/train-windows/train.exe `
  --num-envs=4 `
  --run-id=race01
```

- `--num-envs=N` levanta N procesos del player; con 9 arenas por proceso son
  ~36 agentes en paralelo alimentando una sola política. Ajustar N al nº de
  núcleos de la máquina (§2.3).
- Si entrenas en una VM spot: puede desalojarla — **`--resume`** para continuar
  desde el último checkpoint (`checkpoint_interval` = 500k pasos en la config).
- `--force` sólo para empezar de cero pisando un `run-id` anterior.

## 3. Seguir el entrenamiento

```bash
tensorboard --logdir results --host 0.0.0.0
```

Curvas a mirar: `Environment/Cumulative Reward` (debe subir y aplanarse),
`Environment/Episode Length` (sube a medida que el coche sobrevive más),
`Losses/Policy Loss`, `Policy/Entropy` (baja despacio).

## 4. Qué devolver al agente

- `results/race01/RaceAgent.onnx` (el modelo)
- `results/race01/` completo (o al menos los `events.out.tfevents.*` y
  `configuration.yaml`)
- El `run-id`, nº de pasos alcanzado, y el commit de código con el que se
  construyó el player.

El agente lo mete en `models/` versionado junto a este YAML y el commit
(§10), analiza las curvas y ajusta recompensas para la siguiente corrida.

## 5. Ver qué hace una política entrenada (eval offline, sin Editor)

`Fase2EvalBuild` arma un player que corre la grilla de arenas en `InferenceOnly`
con un `.onnx` horneado durante 120 s y loguea un reporte agregado (split de por
qué terminan los episodios, % de vuelta recorrido, velocidad/throttle/steer
medios). Sirve para diagnosticar sin abrir el Editor.

```powershell
# 1. buildear el eval player con el modelo elegido
"<Unity>\Editor\Unity.exe" -batchmode -quit -projectPath unity `
  -executeMethod AgenticRacing.EditorTools.Fase2EvalBuild.Build `
  -logFile eval-build.log -evalModel results\race04\RaceAgent.onnx

# 2. correrlo y leer la línea [Eval] REPORT
unity\Builds\eval-windows\eval.exe -logFile eval.log
```

Sin `-evalModel` usa `AGENTIC_EVAL_MODEL` o, por defecto,
`results/race04/RaceAgent.onnx`. Para comparar dos modelos, repetir con otro
`-evalModel` (cada build hornea uno).

Flags del `eval.exe`:
- `-heuristic` — ignora el modelo y corre `RaceAgent.Heuristic` (el seguidor
  scripted): la referencia "¿esta pista se puede manejar?".
- `-record` — implica `-heuristic`, corre 300 s y adjunta un `DemonstrationRecorder`
  a cada agente. Escribe `.demo` en `unity\Builds\eval-windows\demos\`.

## 6. Imitación desde la heurística (BC + GAIL)

Cuando el RL no despega solo (race01-07: se estancaba en ~10% de vuelta por
hard-exploration), se arranca la política desde la heurística, que sí maneja
la mayor parte de la vuelta.

```powershell
# 1. grabar demostraciones (misma build que la §5)
unity\Builds\eval-windows\eval.exe -record -logFile eval-record.log
# -> unity\Builds\eval-windows\demos\RaceHeuristic_*.demo  (uno por agente)

# 2. copiarlas a donde el YAML las busca
Copy-Item unity\Builds\eval-windows\demos\*.demo training\demos\

# 3. entrenar — race_ppo.yaml ya trae los bloques behavioral_cloning + gail
mlagents-learn training\config\race_ppo.yaml `
  --env=unity\Builds\train-windows\train.exe --num-envs=4 --run-id=race08
```

`behavioral_cloning.steps` (2M) es cuánto dura el empuje de BC antes de que
domine el RL. `gail.strength` (0.15) es el peso de la recompensa de imitación
durante toda la corrida. Los `.demo` son datos (gitignoreados), no fuente.

## Notas de recompensa (para ajustar entre corridas)

⚠️ Los campos son `[SerializeField]` en `RaceAgent`, **pero `TrainingArena` arma el
agente por código sin overrides**, así que corren con los **defaults del C#**.
Ajustarlos = editar `RaceAgent.cs` y **reconstruir** el player de entrenamiento
(`Fase2TrainingBuild.Build`). No hay instancia serializada que tocar.

| Campo | Efecto |
|---|---|
| `progressRewardPerMetre` | premio por avanzar por la centerline |
| `speedRewardPerSec` | premio por segundo, escalado por fracción de velocidad hacia adelante |
| `lineFollowRewardPerSec` | premio denso por ir alineado y cerca de la trazada ideal (solo con velocidad > 0) |
| `timePenaltyPerStep` | castigo por frame → empuja a ir rápido |
| `edgeCreepPenaltyPerSec` | castigo por rozar el borde |
| `offTrackPenalty` | castigo grande + fin de episodio al salirse |
| `wallHitPenalty` | castigo por tocar el muro de borde (no termina) |
| `stuckPenalty` / `stuckSeconds` | fin de episodio si se queda parado |
| `lapBonus` | premio al completar la vuelta (episodio = una vuelta) |
| `fastLapBonus` | extra al completar vuelta, escalado por el presupuesto de `MaxStep` sin gastar |

Si el comportamiento se degenera (coche parado, o girando en círculos), casi
siempre es la función de recompensa, no el algoritmo (§11).
