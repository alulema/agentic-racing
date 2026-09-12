"""Tests for the strategist proxy: prompt shape (§6.7), schema validation and
discard (§6.8), and the load guardrails (§7). Ollama itself is mocked — these
run without a model.
"""

from __future__ import annotations

import json
import sys
from pathlib import Path

import pytest
from fastapi.testclient import TestClient

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

import strategy as strategy_mod  # noqa: E402
from main import app  # noqa: E402
from schemas import RaceContext, StrategyResponse, Telemetry  # noqa: E402
from strategy import build_explain_messages, build_messages, clamp_radio  # noqa: E402


def _ctx(**over) -> dict:
    base = {
        "car_id": "car_03",
        "pilot_profile": "P4-Smooth: Conserve bias, brakes early, strong slow corners",
        "track_name": "Test Oval",
        "track_length_m": 1994.0,
        "total_laps": 6,
        "corners": [
            {"index": 1, "direction": "left", "severity": "medium"},
            {"index": 2, "direction": "left", "severity": "medium"},
            {"index": 3, "direction": "right", "severity": "slow"},
            {"index": 4, "direction": "right", "severity": "fast"},
        ],
    }
    base.update(over)
    return base


def _telemetry(**over) -> dict:
    base = {
        "event": "lap_completed",
        "lap": 3,
        "laps_remaining": 3,
        "me": {
            "car_id": "car_03",
            "position": 4,
            "last_lap_time": 90.2,
            "best_lap_time": 89.7,
            "gap_ahead": 1.4,
            "gap_behind": 6.8,
            "current_directive": "conserve",
            "incidents": 0,
        },
        "rivals": [
            {"car_id": "car_01", "position": 3, "gap": -1.4, "last_lap_time": 89.9, "trend": "stable"},
            {"car_id": "car_06", "position": 5, "gap": 6.8, "last_lap_time": 89.4, "trend": "closing"},
        ],
        "notes": ["L2 T3: entry too slow, lost 0.3s"],
    }
    base.update(over)
    return base


def _body(**over) -> dict:
    return {"context": _ctx(), "telemetry": _telemetry(**over)}


GOOD_REPLY = json.dumps(
    {
        "directive": "push",
        "aggression": "medium",
        "risk_tolerance": "low",
        "target_rival": "car_01",
        "focus_corners": [3, 4],
        "radio": "Gap ahead one four. Push now, clean through three and four.",
        "rationale": "Rival within a second and fading; a clean push this lap can close it before traffic.",
    }
)


@pytest.fixture
def client(monkeypatch):
    async def fake_call(*_a, **_kw):
        return GOOD_REPLY, 1200.0

    monkeypatch.setattr(strategy_mod, "call_ollama", fake_call)
    monkeypatch.setattr("main.call_ollama", fake_call)
    with TestClient(app) as c:
        yield c


# --- prompt shape (§6.7) ------------------------------------------------------


def test_system_prefix_is_stable_across_events():
    ctx = RaceContext(**_ctx())
    a = build_messages(ctx, Telemetry(**_telemetry(event="lap_completed", lap=2)))
    b = build_messages(ctx, Telemetry(**_telemetry(event="final_lap", lap=6)))
    assert a[0] == b[0], "system message must be byte-identical for KV-cache reuse"
    assert a[1] != b[1], "user message carries the per-event telemetry"
    assert "T1 medium left" in a[0]["content"]
    assert "P4-Smooth" in a[0]["content"]


def test_explain_reuses_the_same_system_prefix():
    ctx = RaceContext(**_ctx())
    a = build_messages(ctx, Telemetry(**_telemetry()))
    b = build_explain_messages(
        ctx, Telemetry(**_telemetry()), StrategyResponse(**json.loads(GOOD_REPLY))
    )
    assert a[0] == b[0], "explain must share the strategy call's exact system prefix (§6.7 KV-cache)"
    assert a[1] != b[1]


def test_clamp_radio():
    assert clamp_radio("one two three", 15) == "one two three"
    long = " ".join(["w"] * 30)
    out = clamp_radio(long, 15)
    assert len(out.split()) == 15 + 0  # 15 words + trailing ellipsis attached
    assert out.endswith("…")


# --- happy path -------------------------------------------------------------


def test_ok_returns_directive_and_clamps_radio(client):
    r = client.post("/api/strategy", json=_body())
    assert r.status_code == 200
    data = r.json()
    assert data["status"] == "ok"
    assert data["strategy"]["directive"] == "push"
    assert len(data["strategy"]["radio"].split()) <= 15
    assert data["llm"]["mode"] == "online"
    assert data["latency_ms"] == 1200


# --- schema validation / discard (§6.8) ------------------------------------


def test_bad_enum_is_rejected_whole(client, monkeypatch):
    bad = json.dumps({**json.loads(GOOD_REPLY), "aggression": "insane"})

    async def fake(*_a, **_kw):
        return bad, 900.0

    monkeypatch.setattr("main.call_ollama", fake)
    r = client.post("/api/strategy", json=_body())
    data = r.json()
    assert data["status"] == "fallback"
    assert data["reason"] == "rejected"
    assert data["strategy"] is None
    assert data["llm"]["rejected"] >= 1


def test_unknown_target_rival_is_rejected(client, monkeypatch):
    bad = json.dumps({**json.loads(GOOD_REPLY), "target_rival": "car_99"})

    async def fake(*_a, **_kw):
        return bad, 900.0

    monkeypatch.setattr("main.call_ollama", fake)
    r = client.post("/api/strategy", json=_body())
    assert r.json()["reason"] == "rejected"


def test_ollama_error_falls_back(client, monkeypatch):
    async def boom(*_a, **_kw):
        raise strategy_mod.OllamaError("connection refused")

    monkeypatch.setattr("main.call_ollama", boom)
    r = client.post("/api/strategy", json=_body())
    data = r.json()
    assert data["status"] == "fallback"
    assert data["reason"].startswith("ollama_error")
    assert data["llm"]["failed"] >= 1


# --- guardrails (§7) ------------------------------------------------------


def test_circuit_breaker_opens_after_failure_streak(monkeypatch):
    calls = {"n": 0}

    async def boom(*_a, **_kw):
        calls["n"] += 1
        raise strategy_mod.OllamaError("timeout")

    monkeypatch.setattr("main.call_ollama", boom)
    with TestClient(app) as c:
        for _ in range(3):
            assert c.post("/api/strategy", json=_body()).json()["reason"].startswith(
                "ollama_error"
            )
        # Breaker is now open: next call must not reach Ollama.
        before = calls["n"]
        out = c.post("/api/strategy", json=_body()).json()
        assert out["status"] == "fallback"
        assert out["reason"] == "offline"
        assert out["llm"]["mode"] == "offline"
        assert calls["n"] == before, "offline mode must skip the Ollama call"


def test_per_ip_rate_limit(client):
    # burst=12 then refills at ~1/s: a fast run of calls from one IP is
    # eventually rate-limited (§7.2). The first dozen get through.
    seen = [
        client.post("/api/strategy", json=_body()).json()["reason"] for _ in range(16)
    ]
    assert seen[0] is None, "first call is allowed"
    assert "rate_limited" in seen[12:], "the tail is rate-limited"


# --- health / ping -------------------------------------------------------


def test_ping():
    with TestClient(app) as c:
        out = c.get("/api/ping").json()
        assert out["ok"] is True
        assert "ts" in out


# --- Fase 6.1: /api/explain -------------------------------------------------


def _explain_body(**over) -> dict:
    base = {"context": _ctx(), "telemetry": _telemetry(), "directive": json.loads(GOOD_REPLY)}
    base.update(over)
    return base


def test_explain_returns_prose(client, monkeypatch):
    async def fake(*_a, **_kw):
        return "car_01 was fading and the gap had closed under a second, so the move was on.", 900.0

    monkeypatch.setattr("main.call_ollama", fake)
    r = client.post("/api/explain", json=_explain_body())
    assert r.status_code == 200
    data = r.json()
    assert data["status"] == "ok"
    assert "car_01" in data["explanation"]
    assert data["reason"] is None


def test_explain_uses_free_text_not_json_format(client, monkeypatch):
    captured = {}

    async def fake(*_a, **kw):
        captured.update(kw)
        return "Held the gap, no need to force it.", 500.0

    monkeypatch.setattr("main.call_ollama", fake)
    client.post("/api/explain", json=_explain_body())
    assert captured.get("json_mode") is False


def test_explain_ollama_error_falls_back(client, monkeypatch):
    async def boom(*_a, **_kw):
        raise strategy_mod.OllamaError("timeout")

    monkeypatch.setattr("main.call_ollama", boom)
    r = client.post("/api/explain", json=_explain_body())
    data = r.json()
    assert data["status"] == "fallback"
    assert data["reason"].startswith("ollama_error")


def test_explain_empty_reply_is_fallback(client, monkeypatch):
    async def fake(*_a, **_kw):
        return "   ", 300.0

    monkeypatch.setattr("main.call_ollama", fake)
    r = client.post("/api/explain", json=_explain_body())
    data = r.json()
    assert data["status"] == "fallback"
    assert data["reason"] == "empty_reply"


def test_explain_does_not_move_the_strategy_reject_rate(client, monkeypatch):
    # §6.8's "% rejected" stat is about directive calls only — a viewer
    # mashing "re-explain" must not dilute or inflate it.
    async def fake(*_a, **_kw):
        return "Because the gap was closing fast.", 400.0

    monkeypatch.setattr("main.call_ollama", fake)
    before = client.get("/api/health").json()["llm"]["calls"]
    for _ in range(3):
        client.post("/api/explain", json=_explain_body())
    after = client.get("/api/health").json()["llm"]["calls"]
    assert after == before


def test_explain_blocked_when_breaker_open(monkeypatch):
    async def boom(*_a, **_kw):
        raise strategy_mod.OllamaError("timeout")

    monkeypatch.setattr("main.call_ollama", boom)
    with TestClient(app) as c:
        for _ in range(3):
            c.post("/api/strategy", json=_body())  # trip the shared breaker
        out = c.post("/api/explain", json=_explain_body()).json()
        assert out["status"] == "fallback"
        assert out["reason"] == "offline"


def test_health_shape():
    with TestClient(app) as c:
        out = c.get("/api/health").json()
        assert out["status"] == "ok"
        assert set(out["llm"]) == {
            "mode",
            "p95_ms",
            "calls",
            "rejected",
            "failed",
            "inflight",
        }
        assert "ollama_reachable" in out
