"""Tests for explicit editor lock token forwarding on high-risk tools."""

import asyncio
import importlib
from types import SimpleNamespace
from unittest.mock import AsyncMock

import pytest


def _patch_direct_unity_send(monkeypatch, module):
    captured: dict[str, object] = {}

    async def fake_send(send_fn, unity_instance, tool_name, params):
        captured["tool_name"] = tool_name
        captured["params"] = params
        return {"success": True, "message": "ok"}

    monkeypatch.setattr(
        module,
        "get_unity_instance_from_context",
        AsyncMock(return_value="unity-instance-1"),
    )
    monkeypatch.setattr(module, "send_with_unity_instance", fake_send)
    return captured


@pytest.mark.parametrize(
    ("module_name", "function_name", "kwargs", "expected_tool"),
    [
        (
            "services.tools.manage_editor",
            "manage_editor",
            {"action": "stop"},
            "manage_editor",
        ),
        (
            "services.tools.execute_menu_item",
            "execute_menu_item",
            {"menu_path": "File/Save Project"},
            "execute_menu_item",
        ),
        (
            "services.tools.manage_build",
            "manage_build",
            {"action": "platform", "target": "windows64"},
            "manage_build",
        ),
        (
            "services.tools.manage_packages",
            "manage_packages",
            {"action": "resolve_packages"},
            "manage_packages",
        ),
        (
            "services.tools.manage_graphics",
            "manage_graphics",
            {"action": "bake_clear"},
            "manage_graphics",
        ),
    ],
)
def test_direct_high_risk_tools_forward_editor_lock_token(
    monkeypatch,
    module_name,
    function_name,
    kwargs,
    expected_tool,
):
    module = importlib.import_module(module_name)
    captured = _patch_direct_unity_send(monkeypatch, module)

    function = getattr(module, function_name)
    asyncio.run(function(SimpleNamespace(), **kwargs, editor_lock_token="tok-1"))

    assert captured["tool_name"] == expected_tool
    assert captured["params"]["editor_lock_token"] == "tok-1"


def test_batch_execute_forwards_editor_lock_token(monkeypatch):
    from services.tools.batch_execute import batch_execute

    captured: dict[str, object] = {}

    async def fake_send(send_fn, unity_instance, tool_name, params):
        captured["tool_name"] = tool_name
        captured["params"] = params
        return {"success": True, "message": "ok"}

    monkeypatch.setattr(
        "services.tools.batch_execute.get_unity_instance_from_context",
        AsyncMock(return_value="unity-instance-1"),
    )
    monkeypatch.setattr(
        "services.tools.batch_execute._get_max_commands_from_editor_state",
        AsyncMock(return_value=25),
    )
    monkeypatch.setattr("services.tools.batch_execute.send_with_unity_instance", fake_send)

    asyncio.run(
        batch_execute(
            SimpleNamespace(),
            commands=[{"tool": "manage_editor", "params": {"action": "stop"}}],
            editor_lock_token="tok-1",
        )
    )

    assert captured["tool_name"] == "batch_execute"
    assert captured["params"]["editor_lock_token"] == "tok-1"


@pytest.mark.asyncio
async def test_run_tests_forwards_editor_lock_token(monkeypatch):
    from services.tools.run_tests import run_tests
    import services.tools.run_tests as mod

    captured: dict[str, object] = {}

    async def fake_send(send_fn, unity_instance, command_type, params, **kwargs):
        captured["command_type"] = command_type
        captured["params"] = params
        return {
            "success": True,
            "data": {"job_id": "abc123", "status": "running", "mode": "EditMode"},
        }

    monkeypatch.setattr(mod, "preflight", AsyncMock(return_value=None))
    monkeypatch.setattr(mod.unity_transport, "send_with_unity_instance", fake_send)

    await run_tests(
        SimpleNamespace(),
        mode="EditMode",
        test_names="MyNamespace.MyTests.TestA",
        editor_lock_token="tok-1",
    )

    assert captured["command_type"] == "run_tests"
    assert captured["params"]["editor_lock_token"] == "tok-1"


@pytest.mark.asyncio
async def test_refresh_unity_forwards_editor_lock_token(monkeypatch):
    from services.tools.refresh_unity import refresh_unity
    import services.tools.refresh_unity as mod

    captured: dict[str, object] = {}

    async def fake_send(send_fn, unity_instance, command_type, params, **kwargs):
        captured["command_type"] = command_type
        captured["params"] = params
        return {"success": True, "message": "ok"}

    monkeypatch.setattr(
        "services.tools.refresh_unity.get_unity_instance_from_context",
        AsyncMock(return_value="unity-instance-1"),
    )
    monkeypatch.setattr(mod.unity_transport, "send_with_unity_instance", fake_send)

    await refresh_unity(
        SimpleNamespace(),
        mode="force",
        wait_for_ready=False,
        editor_lock_token="tok-1",
    )

    assert captured["command_type"] == "refresh_unity"
    assert captured["params"]["editor_lock_token"] == "tok-1"
