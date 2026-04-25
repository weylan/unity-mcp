"""Tests for manage_editor_lock tool."""

import asyncio
from types import SimpleNamespace
from unittest.mock import AsyncMock

import pytest

from services.registry import get_registered_tools
from services.tools.manage_editor_lock import manage_editor_lock


@pytest.fixture
def mock_unity(monkeypatch):
    captured: dict[str, object] = {}

    async def fake_send(send_fn, unity_instance, tool_name, params):
        captured["unity_instance"] = unity_instance
        captured["tool_name"] = tool_name
        captured["params"] = params
        return {"success": True, "message": "ok", "data": {"token": "tok"}}

    monkeypatch.setattr(
        "services.tools.manage_editor_lock.get_unity_instance_from_context",
        AsyncMock(return_value="unity-instance-1"),
    )
    monkeypatch.setattr(
        "services.tools.manage_editor_lock.send_with_unity_instance",
        fake_send,
    )
    return captured


def test_manage_editor_lock_forwards_acquire_params(mock_unity):
    result = asyncio.run(
        manage_editor_lock(
            SimpleNamespace(),
            action="acquire",
            holder_hint="agent-a",
            reason="package verification",
            ttl_seconds=30,
        )
    )

    assert result["success"] is True
    assert mock_unity["unity_instance"] == "unity-instance-1"
    assert mock_unity["tool_name"] == "manage_editor_lock"
    assert mock_unity["params"] == {
        "action": "acquire",
        "holder_hint": "agent-a",
        "reason": "package verification",
        "ttl_seconds": 30,
    }


def test_manage_editor_lock_omits_none_params(mock_unity):
    result = asyncio.run(manage_editor_lock(SimpleNamespace(), action="get_state"))

    assert result["success"] is True
    assert mock_unity["params"] == {"action": "get_state"}


def test_manage_editor_lock_is_always_visible_server_wrapper():
    tool_info = next(
        item for item in get_registered_tools() if item["name"] == "manage_editor_lock"
    )

    assert tool_info["unity_target"] is None
    assert tool_info["group"] is None
