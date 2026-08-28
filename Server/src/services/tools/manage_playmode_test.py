from __future__ import annotations

from typing import Annotated, Any, Literal, get_args

from fastmcp import Context
from mcp.types import ToolAnnotations

from services.registry import mcp_for_unity_tool
from services.tools import get_unity_instance_from_context
from transport.legacy.unity_connection import async_send_command_with_retry
from transport.unity_transport import send_with_unity_instance


PlayModeTestAction = Literal["wait", "run_sequence", "status", "cancel"]
ALL_ACTIONS: list[str] = list(get_args(PlayModeTestAction))


@mcp_for_unity_tool(
    group="testing",
    description=(
        "Run cancellable Play Mode waits and deterministic action sequences. "
        "wait/run_sequence return a job_id and are polled through status. "
        "Sequence steps support input, UI actions, delays, waits, and assertions."
    ),
    annotations=ToolAnnotations(
        title="Manage Play Mode Test",
        destructiveHint=True,
    ),
)
async def manage_playmode_test(
    ctx: Context,
    action: Annotated[PlayModeTestAction, "wait, run_sequence, status, or cancel."],
    condition: Annotated[dict[str, Any] | None, "Condition for wait."] = None,
    steps: Annotated[list[dict[str, Any]] | None, "Sequence step list."] = None,
    timeout_seconds: Annotated[float | None, "Job timeout in unscaled seconds."] = None,
    stable_for_frames: Annotated[int | None, "Consecutive matching frames required."] = None,
    job_id: Annotated[str | None, "Job identifier for status/cancel."] = None,
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
    if action_normalized == "wait" and not condition:
        return {"success": False, "message": "condition is required for wait."}
    if action_normalized == "run_sequence" and not steps:
        return {"success": False, "message": "steps must contain at least one step."}
    if action_normalized in {"status", "cancel"} and not job_id:
        return {"success": False, "message": f"job_id is required for {action_normalized}."}
    if timeout_seconds is not None and (timeout_seconds <= 0 or timeout_seconds > 300):
        return {"success": False, "message": "timeout_seconds must be in (0, 300]."}
    if stable_for_frames is not None and not 1 <= stable_for_frames <= 300:
        return {"success": False, "message": "stable_for_frames must be between 1 and 300."}
    if steps is not None and len(steps) > 50:
        return {"success": False, "message": "A sequence may contain at most 50 steps."}

    params: dict[str, Any] = {"action": action_normalized}
    values = {
        "condition": condition,
        "steps": steps,
        "timeoutSeconds": timeout_seconds,
        "stableForFrames": stable_for_frames,
        "jobId": job_id,
        "editor_lock_token": editor_lock_token,
    }
    params.update({key: val for key, val in values.items() if val is not None})

    unity_instance = await get_unity_instance_from_context(ctx)
    result = await send_with_unity_instance(
        async_send_command_with_retry,
        unity_instance,
        "manage_playmode_test",
        params,
    )
    return result if isinstance(result, dict) else {
        "success": False,
        "message": str(result),
    }
