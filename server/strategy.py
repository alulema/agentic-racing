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
# Fase 6.1 re-explain: free text, a bit more room than the directive call, but
# still bounded (§7.3) — this is a manual, out-of-band ask, not a race event.
EXPLAIN_NUM_PREDICT = 200
# Low but non-zero: some variety in the radio wording, stable directives.
TEMPERATURE = 0.4


_SYSTEM_RULES = """\
You are the race engineer (pit wall) for one car in a closed-circuit race. You \
see the full race picture — classification, gaps, lap times, the circuit map, \
your own notes from earlier laps — but nothing frame-by-frame and nothing about \
the next few seconds. You cannot drive the car. Your only output is a strategy \
directive that biases how your driver drives.

Important calibration: this car has no tyre wear and no fuel model — there is \
no mechanical cost to pushing hard, lap after lap. Choosing low aggression or \
low risk_tolerance has a direct, real cost (it slows the car down) and there is \
usually nothing to show for it. Do not default to "safe" out of general \
caution — reserve low aggression/low risk_tolerance for when the telemetry \
gives a concrete reason (see the rules below), not as your typical answer.

Second calibration, about the directive itself, not just aggression/risk: do \
not default to defend. "Defend" only makes sense when a rival behind you is \
genuinely the closer threat right now — not because defending feels like the \
safe choice, and not because a rival is described as "closing" in general \
terms. A big, stable gap_behind is not a threat no matter how it reads. \
Treat gap_ahead and gap_behind as two numbers to compare directly, not as two \
independent moods.

Reply with ONE JSON object and nothing else. It MUST have ALL SEVEN keys:
  directive, aggression, risk_tolerance, target_rival, focus_corners, radio, rationale

- directive: one of attack (find a way past), defend (protect position), \
conserve (hold a clean, steady pace — only once truly clear of traffic), \
push (maximum pace).
- aggression: one of low, medium, high. Default medium or high.
- risk_tolerance: one of low, medium, high. REQUIRED — never omit it. Default medium.
- target_rival: the car_id this call is actually about — for attack/push, \
the car you're trying to pass (the one at gap_ahead); for defend, the car \
threatening you (the one at gap_behind). Never the car ahead when the \
directive is defend. null if directive is conserve or nothing specific applies.
- focus_corners: a JSON array of plain integers, e.g. [3, 7] — NOT ["T3","T7"]. \
Use [] if none. Max 4.
- radio: max 15 words, plain, like a real team-radio call.
- rationale: max 40 words, why this call now. Not shown live; logged.
- In radio and rationale, name other cars by their exact car_id from the \
telemetry (e.g. car_05) — not "Car 5", "the rival", or "Rival 1".

Pick the directive by comparing gap_ahead and gap_behind as numbers — act on \
whichever one is smaller, not on instinct:
- gap_ahead is the smaller number AND under ~1.5s (a car ahead is \
catchable): attack that car_id. aggression high, risk_tolerance medium or high.
- gap_behind is the smaller number AND under ~1.5s (a rival behind is the \
closer threat): defend against that car_id. aggression medium or high, \
risk_tolerance medium — a passive, low/low defend usually just gets you \
passed anyway, it does not protect the position.
- Neither gap is under ~1.5s (both clear, nobody within ~3s either way): \
push. aggression medium, risk_tolerance medium — clear track is not a \
reason to lift, there is no tyre or fuel saving to bank.
- Final lap: commit regardless of the above — attack or push, aggression \
high, risk_tolerance high.
- conserve with low aggression/low risk_tolerance is the rare exception, not \
the default: only when clear both ways AND there is a concrete reason not \
to change anything (e.g. a large, unthreatened gap already).

Example of the exact shape (values are illustrative):
{"directive":"attack","aggression":"high","risk_tolerance":"medium",\
"target_rival":"car_03","focus_corners":[4,7],\
"radio":"Car ahead is slow in 4 — have a look on the exit.",\
"rationale":"Held within a second for two laps and quicker on the straight; a move at turn 4 is on."}"""


def _corner_map(context: RaceContext) -> str:
    if not context.corners:
        return "Circuit map: not provided."
    parts = [
        f"T{c.index} {c.severity} {c.direction}" for c in context.corners
    ]
    return "Circuit map (numbered corners, in lap order): " + ", ".join(parts)


def _system_message(context: RaceContext) -> str:
    """The stable prefix (§6.7): identical for every call this car makes,
    whether it's asked to decide (build_messages) or to explain a past
    decision (build_explain_messages) — same bytes, so Ollama's KV-cache for
    this car's prefix is shared across both call kinds."""

    return "\n\n".join(
        [
            _SYSTEM_RULES,
            f"Circuit: {context.track_name}, {context.track_length_m:.0f} m, "
            f"{context.total_laps} laps.",
            _corner_map(context),
            f"Your car: {context.car_id}. Pilot profile: {context.pilot_profile}",
        ]
    )


def build_messages(context: RaceContext, telemetry: Telemetry) -> list[dict]:
    """Stable system message + variable user message (§6.7)."""

    user = (
        "Telemetry for this event — decide the directive:\n"
        + telemetry.model_dump_json(indent=None)
    )
    return [
        {"role": "system", "content": _system_message(context)},
        {"role": "user", "content": user},
    ]


def build_explain_messages(
    context: RaceContext, telemetry: Telemetry, directive: StrategyResponse
) -> list[dict]:
    """Fase 6.1: ask the same strategist to elaborate on a call it already
    made, given the same telemetry it had at the time. Free text, not the
    directive schema — this never drives the car, it only feeds the decision
    log's "re-explain" button."""

    user = (
        "Earlier, given the telemetry below, you made this call:\n"
        + directive.model_dump_json(indent=None)
        + "\n\nTelemetry at the time:\n"
        + telemetry.model_dump_json(indent=None)
        + "\n\nExplain, in 2-3 short sentences (max 60 words), why that was the "
        "right call. Be concrete: reference the actual gaps, positions, "
        "corners or rivals in the telemetry above — do not restate the "
        "directive fields, and do not repeat the radio line verbatim. Plain "
        "prose only: no JSON, no bullet points, no preamble."
    )
    return [
        {"role": "system", "content": _system_message(context)},
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
    json_mode: bool = True,
    num_predict: int = NUM_PREDICT,
) -> tuple[str, float]:
    """One ``/api/chat`` call. Returns ``(content, latency_ms)``. Raises
    :class:`OllamaError` on transport failure.

    ``json_mode`` forces JSON output for the directive call (§6.4/§6.8); the
    Fase 6.1 explain call passes ``json_mode=False`` — it wants a couple of
    prose sentences, not a schema, so there is nothing to force."""

    payload = {
        "model": model,
        "messages": messages,
        "stream": False,
        "options": {"num_predict": num_predict, "temperature": TEMPERATURE},
        "keep_alive": keep_alive,
    }
    if json_mode:
        # format="json" only (not the full JSON Schema): schema-constrained
        # decoding roughly doubles latency on a CPU-only 3B. The prompt already
        # spells out every field and enum, and parse_response validates the
        # reply against the schema — a bad one is discarded whole (§6.8). Expect
        # a somewhat higher discard rate; that is the trade §2.5/§6.8 anticipate.
        payload["format"] = "json"
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
