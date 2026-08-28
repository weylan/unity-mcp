#!/usr/bin/env python3
"""Live-Editor E2E smoke for Play Mode state, input, waits, and sequences."""
from __future__ import annotations

import argparse
import os
import sys
import time
import uuid
from pathlib import Path
from typing import Any

_SRC = Path(__file__).resolve().parents[2] / "src"
if str(_SRC) not in sys.path:
    sys.path.insert(0, str(_SRC))

from transport.legacy.unity_connection import send_command_with_retry  # noqa: E402


def _ok(response: Any) -> bool:
    return isinstance(response, dict) and (
        response.get("success") is True or response.get("status") == "success"
    )


def _dig(value: Any, key: str) -> Any:
    pending = [value]
    while pending:
        current = pending.pop()
        if isinstance(current, dict):
            if key in current:
                return current[key]
            pending.extend(current.values())
        elif isinstance(current, list):
            pending.extend(current)
    return None


def _message(response: Any) -> str:
    if not isinstance(response, dict):
        return repr(response)
    return str(response.get("error") or response.get("message") or _dig(response, "message") or "")


def _require(condition: bool, message: str) -> None:
    if not condition:
        raise AssertionError(message)


class Runner:
    def __init__(self, instance: str | None, max_retries: int, retry_ms: int):
        self.instance = instance
        self.max_retries = max_retries
        self.retry_ms = retry_ms
        self.results: list[tuple[str, bool, str]] = []

    def send(self, command: str, params: dict[str, Any]) -> Any:
        return send_command_with_retry(
            command,
            params,
            instance_id=self.instance,
            max_retries=self.max_retries,
            retry_ms=self.retry_ms,
            retry_on_reload=True,
        )

    def step(self, name: str, command: str, params: dict[str, Any], check=None) -> Any:
        started = time.time()
        try:
            response = self.send(command, params)
            if not _ok(response):
                raise AssertionError(f"{_message(response)}; response={response!r}")
            if check:
                check(response)
            detail = f"{time.time() - started:.2f}s"
            self.results.append((name, True, detail))
            print(f"  [PASS] {name} ({detail})", flush=True)
            return response
        except Exception as exc:
            detail = str(exc)
            self.results.append((name, False, detail))
            print(f"  [FAIL] {name} -- {detail}", flush=True)
            raise

    def wait_for_state(self, timeout: float = 30.0) -> Any:
        deadline = time.time() + timeout
        last = None
        while time.time() < deadline:
            try:
                last = self.send(
                    "get_playmode_state",
                    {"includeUI": False, "uiLimit": 0},
                )
                if _ok(last) and _dig(last, "schema_version") == "unity-mcp/playmode-state@1":
                    return last
            except Exception:
                pass
            time.sleep(0.5)
        raise AssertionError(f"Play Mode state did not become ready: {_message(last)}")

    def poll_job(self, job_id: str, timeout: float = 15.0) -> Any:
        deadline = time.time() + timeout
        last = None
        while time.time() < deadline:
            last = self.send(
                "manage_playmode_test",
                {"action": "status", "jobId": job_id},
            )
            status = _dig(last, "status")
            if status == "completed":
                return last
            if status in {"failed", "cancelled", "interrupted", "timeout"}:
                raise AssertionError(f"Job {status}: {_dig(last, 'error')}")
            time.sleep(0.2)
        raise AssertionError(f"Job did not finish: {last}")


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--instance", default=os.environ.get("UNITY_MCP_DEFAULT_INSTANCE"))
    parser.add_argument("--max-retries", type=int, default=20)
    parser.add_argument("--retry-ms", type=int, default=250)
    args = parser.parse_args()

    run_id = uuid.uuid4().hex[:8]
    canvas = f"MCP_PlayMode_Canvas_{run_id}"
    button = f"MCP_PlayMode_Button_{run_id}"
    probe = f"MCP_PlayMode_Probe_{run_id}"
    runner = Runner(args.instance, args.max_retries, args.retry_ms)
    print(f"== Play Mode automation E2E (instance={args.instance or 'auto'}) ==", flush=True)

    setup_code = f"""
var eventSystemObject = new GameObject("MCP_PlayMode_EventSystem_{run_id}");
eventSystemObject.AddComponent<UnityEngine.EventSystems.EventSystem>();
var canvasObject = new GameObject("{canvas}", typeof(RectTransform), typeof(UnityEngine.Canvas));
canvasObject.GetComponent<UnityEngine.Canvas>().renderMode = RenderMode.ScreenSpaceOverlay;
var buttonObject = new GameObject("{button}", typeof(RectTransform));
buttonObject.transform.SetParent(canvasObject.transform, false);
var image = buttonObject.AddComponent<UnityEngine.UI.Image>();
var testButton = buttonObject.AddComponent<UnityEngine.UI.Button>();
var probeObject = new GameObject("{probe}");
testButton.onClick.AddListener(() => probeObject.transform.position = new Vector3(5f, 0f, 0f));
return probeObject.GetInstanceID();
"""

    try:
        runner.step("enter_play_mode", "manage_editor", {"action": "play"})
        state = runner.wait_for_state()
        runner.results.append(("playmode_state_ready", True, "schema v1"))
        print("  [PASS] playmode_state_ready (schema v1)", flush=True)

        runner.step(
            "input_capabilities",
            "simulate_input",
            {"action": "capabilities"},
            lambda response: _require(_dig(response, "available") is not None, "capability payload missing"),
        )
        runner.step(
            "mouse_input",
            "simulate_input",
            {
                "action": "mouse",
                "position": [0.5, 0.5],
                "delta": [1.0, 0.0],
                "button": "left",
                "phase": "tap",
            },
        )
        runner.step(
            "touch_input",
            "simulate_input",
            {"action": "touch", "position": [0.5, 0.5], "phase": "tap"},
        )
        runner.step(
            "gamepad_input",
            "simulate_input",
            {"action": "gamepad", "control": "buttonSouth", "phase": "tap"},
        )
        runner.step(
            "setup_runtime_ui",
            "execute_code",
            {"action": "execute", "code": setup_code, "safety_checks": True},
        )
        runner.step(
            "playmode_ui_resource",
            "get_playmode_ui",
            {"framework": "ugui", "pageSize": 100, "cursor": 0},
            lambda response: _require(
                any(item.get("name") == button for item in (_dig(response, "items") or [])),
                "runtime button not found in UI resource",
            ),
        )
        runner.step(
            "event_system_click",
            "simulate_input",
            {"action": "ui_click", "target": f"{canvas}/{button}", "backend": "event_system"},
        )

        wait_start = runner.step(
            "wait_after_click_start",
            "manage_playmode_test",
            {
                "action": "wait",
                "timeoutSeconds": 5.0,
                "stableForFrames": 2,
                "condition": {
                    "type": "component_property",
                    "target": probe,
                    "component": "Transform",
                    "property": "position.x",
                    "operator": "equals",
                    "value": 5.0,
                    "tolerance": 0.001,
                },
            },
        )
        runner.poll_job(str(_dig(wait_start, "job_id")))
        runner.results.append(("wait_after_click_complete", True, "completed"))
        print("  [PASS] wait_after_click_complete (completed)", flush=True)

        runner.step(
            "reset_probe",
            "execute_code",
            {
                "action": "execute",
                "code": f'GameObject.Find("{probe}").transform.position = Vector3.zero; return true;',
                "safety_checks": True,
            },
        )
        sequence_start = runner.step(
            "sequence_start",
            "manage_playmode_test",
            {
                "action": "run_sequence",
                "timeoutSeconds": 8.0,
                "steps": [
                    {"type": "key", "key": "space", "phase": "tap", "holdFrames": 1},
                    {"type": "ui_click", "target": f"{canvas}/{button}", "backend": "event_system"},
                    {
                        "type": "wait",
                        "timeout_seconds": 5.0,
                        "stable_for_frames": 2,
                        "condition": {
                            "type": "component_property",
                            "target": probe,
                            "component": "Transform",
                            "property": "position.x",
                            "operator": "equals",
                            "value": 5.0,
                        },
                    },
                    {
                        "type": "assert",
                        "condition": {
                            "type": "component_property",
                            "target": probe,
                            "component": "Transform",
                            "property": "position.x",
                            "operator": "equals",
                            "value": 5.0,
                        },
                    },
                ],
            },
        )
        runner.poll_job(str(_dig(sequence_start, "job_id")))
        runner.results.append(("sequence_complete", True, "completed"))
        print("  [PASS] sequence_complete (completed)", flush=True)
    except Exception:
        return_code = 1
    else:
        return_code = 0
    finally:
        try:
            runner.send("simulate_input", {"action": "release_all"})
        except Exception:
            pass
        try:
            runner.send("manage_editor", {"action": "stop"})
        except Exception:
            pass

    passed = sum(1 for _, ok, _ in runner.results if ok)
    print(f"== {passed}/{len(runner.results)} passed ==", flush=True)
    return return_code


if __name__ == "__main__":
    raise SystemExit(main())
