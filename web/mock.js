/* `?mock=1` driver: synthesises the Unity -> DOM message stream for a fake
 * 6-car race so the HUD and radio panel can be checked in a plain browser with
 * no Unity build. Not shipped behaviour — a dev aid for the overlay.
 *
 * It also exercises the real /api/strategy endpoint when it can reach it, so
 * `docker compose up` + open `/?mock=1` is an end-to-end check of the proxy,
 * the schema and the fallback path. If the call fails it just emits a
 * fallback radio line, exactly as the C# strategist will.
 */

const CARS = [
  { id: "car_01", name: "P2-LateBrake", color: "#e0563b", pilot: "Attack bias, brakes very late, aggressive corner exit" },
  { id: "car_02", name: "P5-Aggro", color: "#e0a33b", pilot: "Attack bias, runs wheel-to-wheel, high risk tolerance" },
  { id: "car_03", name: "P1-Balanced", color: "#3bb0e0", pilot: "Neutral line, consistent, no strong tendency" },
  { id: "car_04", name: "P6-Steady", color: "#8a7be0", pilot: "Push bias but conservative braking, very repeatable" },
  { id: "car_05", name: "P3-Defensive", color: "#5ad07a", pilot: "Defend bias, holds the inside line, brakes early" },
  { id: "car_06", name: "P4-Smooth", color: "#c77bd0", pilot: "Conserve bias, smooth inputs, strong in slow corners" },
];

const CORNERS = [
  { index: 1, direction: "left", severity: "medium" },
  { index: 2, direction: "left", severity: "medium" },
  { index: 3, direction: "right", severity: "slow" },
  { index: 4, direction: "right", severity: "fast" },
];

const LAPS = 6;
const TICK_MS = 500;
const LAP_MS = 9000; // compressed lap for the mock

export function startMock(route) {
  const state = CARS.map((c, i) => ({
    ...c,
    pos: i + 1,
    lapTime: 88 + i * 0.7,
    lastLap: null,
    directive: "push",
    aggression: "medium",
    risk: "medium",
    progress: -i * 0.03,
  }));

  route({ type: "race:start", seed: 12345, laps: LAPS, cars: CARS });

  let elapsed = 0;
  let lap = 1;

  const timer = setInterval(async () => {
    elapsed += TICK_MS;

    for (const c of state) {
      const pace = paceFor(c);
      c.progress += (TICK_MS / LAP_MS) * pace;
    }
    state.sort((a, b) => b.progress - a.progress);
    state.forEach((c, i) => (c.pos = i + 1));

    const leader = state[0].progress;
    route({
      type: "race:tick",
      lap,
      laps: LAPS,
      meId: "car_03",
      classification: state.map((c) => ({
        pos: c.pos,
        id: c.id,
        name: c.name,
        gap: c.pos === 1 ? null : (leader - c.progress) * c.lapTime,
        lastLap: c.lastLap,
        directive: c.directive,
      })),
    });

    if (state[0].progress >= lap) {
      for (const c of state) c.lastLap = +(c.lapTime + (Math.random() - 0.5)).toFixed(1);
      const finishing = lap >= LAPS;
      // Ask the strategist for a couple of cars each lap (like the real
      // event-driven cadence, minus the cooldown bookkeeping).
      await Promise.all(
        [state[2], state[3]].map((c) => askStrategy(c, state, lap, finishing, route))
      );
      lap++;
      if (lap > LAPS) {
        clearInterval(timer);
        route({
          type: "race:end",
          meId: "car_03",
          classification: state.map((c) => ({ pos: c.pos, id: c.id, name: c.name, gap: c.pos === 1 ? null : 1, lastLap: c.lastLap })),
        });
      }
    }
  }, TICK_MS);
}

function paceFor(c) {
  let p = 1.0;
  if (c.directive === "push" || c.directive === "attack") p += 0.012;
  if (c.directive === "conserve") p -= 0.01;
  if (c.aggression === "high") p += 0.006;
  if (c.aggression === "low") p -= 0.006;
  return p + (Math.random() - 0.5) * 0.004;
}

async function askStrategy(car, state, lap, finishing, route) {
  const idx = state.findIndex((c) => c.id === car.id);
  const ahead = state[idx - 1];
  const behind = state[idx + 1];
  const body = {
    context: {
      car_id: car.id,
      pilot_profile: `${car.name}: ${car.pilot}`,
      track_name: "Rounded Oval (mock)",
      track_length_m: 1994,
      total_laps: LAPS,
      corners: CORNERS,
    },
    telemetry: {
      event: finishing ? "final_lap" : "lap_completed",
      lap,
      laps_remaining: LAPS - lap,
      me: {
        car_id: car.id,
        position: car.pos,
        last_lap_time: car.lastLap,
        best_lap_time: car.lapTime,
        gap_ahead: ahead ? 1.2 : null,
        gap_behind: behind ? 1.8 : null,
        current_directive: car.directive,
        incidents: 0,
      },
      rivals: state
        .filter((c) => c.id !== car.id)
        .slice(0, 3)
        .map((c) => ({
          car_id: c.id,
          position: c.pos,
          gap: (c.pos - car.pos) * 1.5,
          last_lap_time: c.lastLap,
          trend: "stable",
        })),
      notes: [`L${lap - 1} T3: entry a touch slow`],
    },
  };

  let env;
  try {
    const r = await fetch("api/strategy", {
      method: "POST",
      headers: { "content-type": "application/json" },
      body: JSON.stringify(body),
    });
    env = await r.json();
  } catch (err) {
    env = { status: "fallback", reason: "network", llm: {} };
  }

  if (env.status === "ok" && env.strategy) {
    const s = env.strategy;
    car.directive = s.directive;
    car.aggression = s.aggression;
    car.risk = s.risk_tolerance;
    route({
      type: "radio:msg",
      carId: car.id,
      name: car.name,
      color: car.color,
      directive: s.directive,
      aggression: s.aggression,
      risk: s.risk_tolerance,
      targetRival: s.target_rival,
      focusCorners: s.focus_corners,
      radio: s.radio,
      status: "ok",
      latencyMs: env.latency_ms,
    });
  } else {
    // Same path the C# strategist takes on a rejected/offline/timeout reply:
    // keep the current directive, surface it on the radio.
    route({
      type: "radio:msg",
      carId: car.id,
      name: car.name,
      color: car.color,
      directive: car.directive,
      radio: "Staying on plan — no new call from the pit wall.",
      status: "fallback",
      reason: env.reason || "unavailable",
      latencyMs: env.latency_ms,
    });
  }
}
