"""Tests for the manage_editor_lock MCP wrapper."""

import asyncio
from types import SimpleNamespace
from unittest.mock import AsyncMock

from services.registry import get_registered_tools
from services.tools.manage_editor_lock import manage_editor_lock


def test_manage_editor_lock_registered_as_core_unity_tool():
    tool_info = next(
        item for item in get_registered_tools()
        if item["name"] == "manage_editor_lock"
    )

    assert tool_info["group"] == "core"
    assert tool_info["unity_target"] == "manage_editor_lock"
    assert "group:core" in tool_info["kwargs"]["tags"]


def test_acquire_forwards_params(monkeypatch):
    captured: dict[str, object] = {}

    async def fake_send(send_fn, unity_instance, tool_name, params):
        captured["unity_instance"] = unity_instance
        captured["tool_name"] = tool_name
        captured["params"] = params
        return {
            "success": True,
            "message": "Lock acquired.",
            "data": {"token": "lock-token"},
        }

    monkeypatch.setattr(
        "services.tools.manage_editor_lock.get_unity_instance_from_context",
        AsyncMock(return_value="unity-instance-1"),
    )
    monkeypatch.setattr(
        "services.tools.manage_editor_lock.send_with_unity_instance",
        fake_send,
    )

    result = asyncio.run(
        manage_editor_lock(
            SimpleNamespace(),
            action="acquire",
            holder_hint="codex",
            reason="live validation",
            ttl_seconds=30,
        )
    )

    assert result["success"] is True
    assert captured["unity_instance"] == "unity-instance-1"
    assert captured["tool_name"] == "manage_editor_lock"
    assert captured["params"] == {
        "action": "acquire",
        "holder_hint": "codex",
        "reason": "live validation",
        "ttl_seconds": 30,
    }


def test_get_state_omits_none_params(monkeypatch):
    captured: dict[str, object] = {}

    async def fake_send(send_fn, unity_instance, tool_name, params):
        captured["tool_name"] = tool_name
        captured["params"] = params
        return {
            "success": True,
            "message": "Lock state retrieved.",
            "data": {"locked": False},
        }

    monkeypatch.setattr(
        "services.tools.manage_editor_lock.get_unity_instance_from_context",
        AsyncMock(return_value="unity-instance-1"),
    )
    monkeypatch.setattr(
        "services.tools.manage_editor_lock.send_with_unity_instance",
        fake_send,
    )

    result = asyncio.run(manage_editor_lock(SimpleNamespace(), action="get_state"))

    assert result["success"] is True
    assert captured["tool_name"] == "manage_editor_lock"
    assert captured["params"] == {"action": "get_state"}
