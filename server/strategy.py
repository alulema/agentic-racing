"""The strategist call itself: build the prompt, ask Ollama once, validate.

Shape of the prompt (CLAUDE.md §6.7):

* a **stable system message** — role, rules, the output-schema reminder, the
  numbered corner map and the pilot profile. Identical on every call for a
  given car, so Ollama reuses the KV-cache of that prefix while the model
  stays loaded (``keep_alive``).
* a **variable user message** — the §6.3 telemetry for this event.

One inference turn only. If the reply does not validate against
:class:`StrategyResponse`, it is discarded whole (never patched field by
field, §6.8) and the caller falls back to the current directive.
"""

from __future__ import annotations

import json
import re
import time

import httpx
from pydantic import ValidationError

from schemas import RaceContext, StrategyResponse, Telemetry

# Hard cap on output tokens (§7.3): radio is 15 words, rationale 40, so ~150
# tokens is plenty and keeps latency bounded.
NUM_PREDICT = 150
# Low but non-zero: some variety in the radio wording, stable directives.
TEMPERATURE = 0.4


_SYSTEM_RULES = """\
You are the race engineer (pit wall) for one car in a closed-circuit race. You \
see the full race picture — classification, gaps, lap times, the circuit map, \
your own notes from earlier laps — but nothing frame-by-frame and nothing about \
the next few seconds. You cannot drive the car. Your only output is a strategy \
directive that biases how your driver drives.

Reply with ONE JSON object and nothing else. It MUST have ALL SEVEN keys:
  directive, aggression, risk_tolerance, target_rival, focus_corners, radio, rationale

- directive: one of attack (find a way past), defend (protect position), \
conserve (consistency, tyre/energy), push (maximum clean pace).
- aggression: one of low, medium, high.
- risk_tolerance: one of low, medium, high. REQUIRED — never omit it.
- target_rival: a car_id string from the telemetry (e.g. "car_03"), or null.
- focus_corners: a JSON array of plain integers, e.g. [3, 7] — NOT ["T3","T7"]. \
Use [] if none. Max 4.
- radio: max 15 words, plain, like a real team-radio call.
- rationale: max 40 words, why this call now. Not shown live; logged.
- In radio and rationale, name other cars by their exact car_id from the \
telemetry (e.g. car_05) — not "Car 5", "the rival", or "Rival 1".

Example of the exact shape (values are illustrative):
{"directive":"attack","aggression":"high","risk_tolerance":"medium",\
"target_rival":"car_03","focus_corners":[4,7],\
"radio":"Car ahead is slow in 4 — have a look on the exit.",\
"rationale":"Held within a second for two laps and quicker on the straight; a move at turn 4 is on."}

Base the call on the situation. Early laps with a big gap behind: usually \
conserve or push. A rival within ~1s for more than a lap: attack or defend. \
Last lap: commit."""


def _corner_map(context: RaceContext) -> str:
    if not context.corners:
        return "Circuit map: not provided."
    parts = [
        f"T{c.index} {c.severity} {c.direction}" for c in context.corners
    ]
    return "Circuit map (numbered corners, in lap order): " + ", ".join(parts)


def build_messages(context: RaceContext, telemetry: Telemetry) -> list[dict]:
    """Stable system message + variable user message (§6.7)."""

    system = "\n\n".join(
        [
            _SYSTEM_RULES,
            f"Circuit: {context.track_name}, {context.track_length_m:.0f} m, "
            f"{context.total_laps} laps.",
            _corner_map(context),
            f"Your car: {context.car_id}. Pilot profile: {context.pilot_profile}",
        ]
    )
    user = (
        "Telemetry for this event — decide the directive:\n"
        + telemetry.model_dump_json(indent=None)
    )
    return [
        {"role": "system", "content": system},
        {"role": "user", "content": user},
    ]


class OllamaError(RuntimeError):
    """Transport-level failure talking to Ollama (unreachable, timeout, 5xx)."""


async def call_ollama(
    client: httpx.AsyncClient,
    *,
    base_url: str,
    model: str,
    keep_alive: str,
    messages: list[dict],
    timeout_s: float,
) -> tuple[str, float]:
    """One ``/api/chat`` call with forced-JSON output. Returns
    ``(content, latency_ms)``. Raises :class:`OllamaError` on transport failure."""

    payload = {
        "model": model,
        "messages": messages,
        "stream": False,
        # format="json" only (not the full JSON Schema): schema-constrained
        # decoding roughly doubles latency on a CPU-only 3B. The prompt already
        # spells out every field and enum, and parse_response validates the
        # reply against the schema — a bad one is discarded whole (§6.8). Expect
        # a somewhat higher discard rate; that is the trade §2.5/§6.8 anticipate.
        "format": "json",
        "options": {"num_predict": NUM_PREDICT, "temperature": TEMPERATURE},
        "keep_alive": keep_alive,
    }
    started = time.perf_counter()
    try:
        resp = await client.post(
            f"{base_url}/api/chat", json=payload, timeout=timeout_s
        )
        resp.raise_for_status()
    except httpx.HTTPError as exc:
        raise OllamaError(str(exc)) from exc
    latency_ms = (time.perf_counter() - started) * 1000.0
    content = resp.json().get("message", {}).get("content", "")
    return content, latency_ms


_CORNER_INT = re.compile(r"-?\d+")


def _normalise(raw: dict) -> dict:
    """Tolerate cosmetic noise a small model adds without changing meaning:
    ``focus_corners`` given as ["T3", "turn 7"] instead of [3, 7]. Anything that
    still doesn't fit the schema (missing key, bad enum) is left to fail
    validation and be discarded whole (§6.8)."""

    fc = raw.get("focus_corners")
    if isinstance(fc, list):
        out: list[int] = []
        for item in fc:
            m = _CORNER_INT.search(str(item))
            if m:
                out.append(int(m.group()))
        raw["focus_corners"] = out
    return raw


def parse_response(content: str, known_car_ids: set[str]) -> StrategyResponse:
    """Validate a model reply against the schema. Raises
    :class:`pydantic.ValidationError` on any problem — unknown enum, missing
    field, or a ``target_rival`` that is not a car in this race (§6.8)."""

    try:
        raw = json.loads(content)
    except json.JSONDecodeError as exc:
        raise ValidationError.from_exception_data(
            "StrategyResponse",
            [{"type": "value_error", "loc": ("__root__",), "input": content[:200],
              "ctx": {"error": f"not JSON: {exc}"}}],
        ) from exc
    if not isinstance(raw, dict):
        raise ValidationError.from_exception_data(
            "StrategyResponse",
            [{"type": "value_error", "loc": ("__root__",), "input": str(raw)[:200],
              "ctx": {"error": "not a JSON object"}}],
        )
    obj = StrategyResponse.model_validate(_normalise(raw))
    if obj.target_rival is not None and obj.target_rival not in known_car_ids:
        raise ValidationError.from_exception_data(
            "StrategyResponse",
            [
                {
                    "type": "value_error",
                    "loc": ("target_rival",),
                    "input": obj.target_rival,
                    "ctx": {"error": "unknown car_id"},
                }
            ],
        )
    return obj


def clamp_radio(text: str, max_words: int = 15) -> str:
    """Hard word clamp applied server-side even though the prompt asks for it
    (§6.8: "Recorta radio a 15 palabras aunque el modelo se pase")."""

    words = text.split()
    if len(words) <= max_words:
        return text.strip()
    return " ".join(words[:max_words]).rstrip(",.;:") + "…"
