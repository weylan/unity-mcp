from __future__ import annotations

import json
import subprocess
import sys
from pathlib import Path
from types import SimpleNamespace
from unittest.mock import AsyncMock

import pytest
from click.testing import CliRunner

from cli.main import cli
import services.resources.playmode as playmode_resources
from services.tools.manage_playmode_test import manage_playmode_test
from services.tools.simulate_input import simulate_input
from services.registry import get_registered_tools


@pytest.fixture
def captured_unity(monkeypatch):
    captured: list[dict[str, object]] = []

    async def fake_send(send_fn, unity_instance, command, params):
        captured.append(
            {
                "unity_instance": unity_instance,
                "command": command,
                "params": params,
            }
        )
        return {"success": True, "message": "ok", "data": {}}

    for module in (
        "services.tools.simulate_input",
        "services.tools.manage_playmode_test",
        "services.resources.playmode",
    ):
        monkeypatch.setattr(
            f"{module}.get_unity_instance_from_context",
            AsyncMock(return_value="test-instance"),
        )
        monkeypatch.setattr(f"{module}.send_with_unity_instance", fake_send)

    return captured


@pytest.mark.asyncio
async def test_playmode_state_resource_forwards_compact_options(captured_unity):
    response = await playmode_resources.get_playmode_state(
        SimpleNamespace(), include_ui=False, ui_limit=12, player="Player"
    )

    assert response.success is True
    call = captured_unity[-1]
    assert call["command"] == "get_playmode_state"
    assert call["params"] == {
        "includeUI": False,
        "uiLimit": 12,
        "player": "Player",
    }


@pytest.mark.asyncio
async def test_playmode_ui_resource_forwards_pagination(captured_unity):
    response = await playmode_resources.get_playmode_ui(
        SimpleNamespace(), page_size=25, cursor=50, framework="ugui"
    )

    assert response.success is True
    call = captured_unity[-1]
    assert call["command"] == "get_playmode_ui"
    assert call["params"] == {
        "pageSize": 25,
        "cursor": 50,
        "framework": "ugui",
    }


def test_playmode_resources_register_with_fastmcp():
    completed = subprocess.run(
        [
            sys.executable,
            "-c",
            (
                "import asyncio, json; "
                "from fastmcp import FastMCP; "
                "from services.resources import register_all_resources; "
                "mcp = FastMCP('playmode-resource-registration'); "
                "register_all_resources(mcp, project_scoped_tools=True); "
                "templates = asyncio.run(mcp.list_resource_templates()); "
                "print(json.dumps({template.name: str(template.uri_template) "
                "for template in templates if template.name.startswith('playmode_')}))"
            ),
        ],
        cwd=Path(__file__).resolve().parents[1],
        capture_output=True,
        text=True,
        timeout=30,
        check=False,
    )

    assert completed.returncode == 0, completed.stderr
    assert json.loads(completed.stdout) == {
        "playmode_state": "mcpforunity://playmode/state{?include_ui,ui_limit,player}",
        "playmode_ui": "mcpforunity://playmode/ui{?page_size,cursor,framework}",
    }


@pytest.mark.asyncio
async def test_simulate_input_forwards_ui_click(captured_unity):
    response = await simulate_input(
        SimpleNamespace(),
        action="ui_click",
        target="Canvas/StartButton",
        backend="event_system",
        hit_test="strict",
    )

    assert response["success"] is True
    call = captured_unity[-1]
    assert call["command"] == "simulate_input"
    assert call["params"] == {
        "action": "ui_click",
        "target": "Canvas/StartButton",
        "backend": "event_system",
        "hitTest": "strict",
    }


@pytest.mark.asyncio
async def test_simulate_input_forwards_key_tap(captured_unity):
    await simulate_input(
        SimpleNamespace(),
        action="key",
        key="space",
        phase="tap",
        hold_frames=2,
        editor_lock_token="lock-1",
    )

    params = captured_unity[-1]["params"]
    assert params["key"] == "space"
    assert params["phase"] == "tap"
    assert params["holdFrames"] == 2
    assert params["editor_lock_token"] == "lock-1"


@pytest.mark.asyncio
async def test_simulate_input_rejects_unknown_action(captured_unity):
    response = await simulate_input(SimpleNamespace(), action="teleport")

    assert response["success"] is False
    assert "Unknown action" in response["message"]
    assert captured_unity == []


@pytest.mark.asyncio
async def test_simulate_input_rejects_unknown_hit_test(captured_unity):
    response = await simulate_input(
        SimpleNamespace(),
        action="ui_click",
        target="Canvas/StartButton",
        hit_test="optimistic",
    )

    assert response["success"] is False
    assert "hit_test" in response["message"]
    assert captured_unity == []


@pytest.mark.asyncio
async def test_simulate_input_rejects_hit_test_for_non_click_action(captured_unity):
    response = await simulate_input(
        SimpleNamespace(),
        action="key",
        key="space",
        hit_test="strict",
    )

    assert response["success"] is False
    assert "only valid for ui_click" in response["message"]
    assert captured_unity == []


@pytest.mark.asyncio
async def test_manage_playmode_test_forwards_wait_contract(captured_unity):
    condition = {
        "type": "component_property",
        "target": "Player",
        "component": "Health",
        "property": "currentHP",
        "operator": "lte",
        "value": 0,
    }

    response = await manage_playmode_test(
        SimpleNamespace(),
        action="wait",
        condition=condition,
        timeout_seconds=8.0,
        stable_for_frames=2,
        editor_lock_token="lock-2",
    )

    assert response["success"] is True
    call = captured_unity[-1]
    assert call["command"] == "manage_playmode_test"
    assert call["params"] == {
        "action": "wait",
        "condition": condition,
        "timeoutSeconds": 8.0,
        "stableForFrames": 2,
        "editor_lock_token": "lock-2",
    }


@pytest.mark.asyncio
async def test_manage_playmode_test_forwards_sequence_and_status(captured_unity):
    steps = [
        {"type": "ui_click", "target": "Canvas/StartButton"},
        {"type": "delay", "frames": 1},
        {
            "type": "assert",
            "condition": {"type": "ui_text", "operator": "contains", "value": "Ready"},
        },
    ]

    await manage_playmode_test(
        SimpleNamespace(), action="run_sequence", steps=steps, timeout_seconds=20.0
    )
    await manage_playmode_test(
        SimpleNamespace(), action="status", job_id="playmode-test-1"
    )

    assert captured_unity[-2]["params"]["steps"] == steps
    assert captured_unity[-1]["params"] == {
        "action": "status",
        "jobId": "playmode-test-1",
    }


@pytest.mark.asyncio
async def test_manage_playmode_test_validates_action_requirements(captured_unity):
    wait_response = await manage_playmode_test(SimpleNamespace(), action="wait")
    sequence_response = await manage_playmode_test(
        SimpleNamespace(), action="run_sequence", steps=[]
    )
    status_response = await manage_playmode_test(SimpleNamespace(), action="status")

    assert wait_response["success"] is False
    assert sequence_response["success"] is False
    assert status_response["success"] is False
    assert captured_unity == []


def test_playmode_tools_are_in_testing_group():
    tools = {item["name"]: item for item in get_registered_tools()}

    assert tools["simulate_input"]["group"] == "testing"
    assert tools["manage_playmode_test"]["group"] == "testing"


def test_playmode_sequence_cli_forwards_json_and_lock_token(monkeypatch):
    captured = {}

    def fake_run(command, params, config):
        captured.update(command=command, params=params)
        return {"success": True, "data": {"job_id": "job-1"}}

    monkeypatch.setattr("cli.commands.playmode.run_command", fake_run)
    steps = [{"type": "delay", "frames": 1}]

    result = CliRunner().invoke(
        cli,
        [
            "--format",
            "json",
            "playmode",
            "sequence",
            json.dumps(steps),
            "--timeout",
            "8",
            "--editor-lock-token",
            "lock-3",
        ],
    )

    assert result.exit_code == 0, result.output
    assert captured["command"] == "manage_playmode_test"
    assert captured["params"]["steps"] == steps
    assert captured["params"]["timeoutSeconds"] == 8.0
    assert captured["params"]["editor_lock_token"] == "lock-3"
