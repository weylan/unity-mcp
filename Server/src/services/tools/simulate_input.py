from __future__ import annotations

from typing import Annotated, Any, Literal, get_args

from fastmcp import Context
from mcp.types import ToolAnnotations

from services.registry import mcp_for_unity_tool
from services.tools import get_unity_instance_from_context
from transport.legacy.unity_connection import async_send_command_with_retry
from transport.unity_transport import send_with_unity_instance


InputAction = Literal[
    "capabilities",
    "ui_click",
    "ui_drag",
    "key",
    "mouse",
    "touch",
    "gamepad",
    "release_all",
]
ALL_ACTIONS: list[str] = list(get_args(InputAction))


@mcp_for_unity_tool(
    group="testing",
    description=(
        "Inject deterministic input while the Unity Editor is in Play Mode. "
        "Target-based ui_click uses strict EventSystem hit testing by default. "
        "Use target-based ui_click/ui_drag when possible. Keyboard, mouse, touch, "
        "and gamepad injection require the optional Unity Input System backend."
    ),
    annotations=ToolAnnotations(
        title="Simulate Play Mode Input",
        destructiveHint=True,
    ),
)
async def simulate_input(
    ctx: Context,
    action: Annotated[InputAction, "Input action to perform."],
    target: Annotated[str | int | None, "Target GameObject name, path, or instance ID."] = None,
    from_target: Annotated[str | int | None, "Drag source GameObject."] = None,
    to_target: Annotated[str | int | None, "Drag destination GameObject."] = None,
    position: Annotated[list[float] | None, "Normalized [x,y] position with top-left origin."] = None,
    end_position: Annotated[list[float] | None, "Normalized drag/touch end position."] = None,
    key: Annotated[str | None, "Input System key name."] = None,
    phase: Annotated[Literal["tap", "press", "release"] | None, "Input phase."] = None,
    button: Annotated[str | None, "Mouse or gamepad button name."] = None,
    control: Annotated[str | None, "Gamepad control or axis name."] = None,
    value: Annotated[float | None, "Axis/control value."] = None,
    delta: Annotated[list[float] | None, "Mouse delta [dx,dy] in pixels."] = None,
    hold_frames: Annotated[int | None, "Frames between press and release for tap."] = None,
    backend: Annotated[Literal["auto", "event_system", "input_system"] | None, "Input backend."] = None,
    hit_test: Annotated[
        Literal["strict", "direct"] | None,
        "UI click hit-test policy. strict requires the expected target to own the top EventSystem hit.",
    ] = None,
    editor_lock_token: Annotated[
        str | None,
        "Token returned by manage_editor_lock acquire for shared Editor mutation.",
    ] = None,
) -> dict[str, Any]:
    action_normalized = action.lower()
    if action_normalized not in ALL_ACTIONS:
        return {
            "success": False,
            "message": f"Unknown action '{action}'. Valid: {', '.join(ALL_ACTIONS)}",
        }
    if hold_frames is not None and (hold_frames < 1 or hold_frames > 120):
        return {
            "success": False,
            "message": "hold_frames must be between 1 and 120.",
        }
    if hit_test is not None and hit_test not in {"strict", "direct"}:
        return {
            "success": False,
            "message": "hit_test must be 'strict' or 'direct'.",
        }
    if hit_test is not None and action_normalized != "ui_click":
        return {
            "success": False,
            "message": "hit_test is only valid for ui_click.",
        }

    params: dict[str, Any] = {"action": action_normalized}
    values = {
        "target": target,
        "fromTarget": from_target,
        "toTarget": to_target,
        "position": position,
        "endPosition": end_position,
        "key": key,
        "phase": phase,
        "button": button,
        "control": control,
        "value": value,
        "delta": delta,
        "holdFrames": hold_frames,
        "backend": backend,
        "hitTest": hit_test,
        "editor_lock_token": editor_lock_token,
    }
    params.update({key: val for key, val in values.items() if val is not None})

    unity_instance = await get_unity_instance_from_context(ctx)
    result = await send_with_unity_instance(
        async_send_command_with_retry,
        unity_instance,
        "simulate_input",
        params,
    )
    return result if isinstance(result, dict) else {
        "success": False,
        "message": str(result),
    }
