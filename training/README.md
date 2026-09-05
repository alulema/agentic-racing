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
| Python | `mlagents==1.1.0` exige **exactamente** `>=3.10.1,<=3.10.12` — no cualquier 3.10.x. Un `conda create -n ... python=3.10` puede darte un patch fuera de rango (p.ej. 3.10.21) y `pip install mlagents` falla con "no matching distribution". Fija el patch: `python=3.10.12`. |

Instalación del lado Python (ejemplo con conda, funciona igual en la VM que en la
máquina de entrenamiento local):

```bash
conda create -n agentic-racing-train python=3.10.12 -y
conda activate agentic-racing-train
pip install "setuptools<81"   # mlagents usa pkg_resources, retirado de setuptools 81+
pip install mlagents==1.1.0
mlagents-learn --help    # comprobar que arranca
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

```bash
conda activate agentic-racing-train
mlagents-learn training/config/race_ppo.yaml \
  --env=Builds/train-windows/train.exe \
  --num-envs=4 \
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

## Notas de recompensa (para ajustar entre corridas)

En `RaceAgent` (serializado, sin recompilar):

| Campo | Efecto |
|---|---|
| `progressRewardPerMetre` | premio por avanzar por la centerline |
| `timePenaltyPerStep` | castigo por frame → empuja a ir rápido |
| `edgeCreepPenaltyPerSec` | castigo por rozar el borde |
| `offTrackPenalty` | castigo grande + fin de episodio al salirse |
| `wallHitPenalty` | castigo por tocar el muro de borde (no termina) |
| `stuckPenalty` / `stuckSeconds` | fin de episodio si se queda parado |
| `lapBonus` | premio al completar la vuelta (episodio = una vuelta) |

Si el comportamiento se degenera (coche parado, o girando en círculos), casi
siempre es la función de recompensa, no el algoritmo (§11).
