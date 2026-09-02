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
| Python | el que pida el release de `mlagents` (no el más nuevo) |

Instalación del lado Python en la VM (ejemplo):

```bash
python -m venv .venv && source .venv/bin/activate
pip install mlagents==<versión del release 4>   # p.ej. 1.1.0 — verificar el release
mlagents-learn --help    # comprobar que arranca
```

## 1. Construir el player headless de Linux

**No se corre el Editor en la VM** (§2.3). Se construye el player en una máquina
con licencia Unity (local o CI) y se sube el binario.

En una máquina con el Editor:

```bash
"<Unity>/Editor/Unity" -batchmode -nographics -quit \
  -projectPath unity \
  -executeMethod AgenticRacing.EditorTools.Fase2TrainingBuild.Build \
  -logFile -
# -> unity/Builds/train-linux/train.x86_64  (player Linux normal)
```

Sube `unity/Builds/train-linux/` entero a la VM. `chmod +x train.x86_64`. Es un
player normal; se corre headless con `--no-graphics` (ver abajo). No hace falta
el módulo "Dedicated Server", sólo "Linux Build Support (IL2CPP)" (§9).

La escena que construye es una rejilla de `TrainingArena` (por defecto 9), cada
una con una seed de circuito distinta (`baseSeed + índice`), separadas 4 km para
que los raycasts no vean arenas vecinas.

## 2. Lanzar el entrenamiento en la VM

```bash
source .venv/bin/activate
mlagents-learn training/config/race_ppo.yaml \
  --env=Builds/train-linux/train.x86_64 --no-graphics \
  --num-envs=4 \
  --run-id=race01
```

- `--num-envs=N` levanta N procesos del player; con 9 arenas por proceso son
  ~36 agentes en paralelo alimentando una sola política. Ajustar N al nº de
  vCPU (§2.3: VM spot ~16 vCPU → `--num-envs` 4–6).
- Spot puede desalojar la VM: **`--resume`** para continuar desde el último
  checkpoint (`checkpoint_interval` = 500k pasos en la config).
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
