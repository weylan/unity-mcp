from typing import Annotated, Any, Literal

from fastmcp import Context
from mcp.types import ToolAnnotations

from services.registry import mcp_for_unity_tool
from services.tools import get_unity_instance_from_context
from transport.legacy.unity_connection import async_send_command_with_retry
from transport.unity_transport import send_with_unity_instance


@mcp_for_unity_tool(
    unity_target=None,
    group=None,
    description=(
        "Manage the shared Unity Editor operation lock. Actions: acquire, release, "
        "extend, get_state, force_release. Use acquire/release around multiple "
        "high-risk Unity operations that must be serialized across MCP clients."
    ),
    annotations=ToolAnnotations(
        title="Manage Editor Lock",
        readOnlyHint=False,
    ),
)
async def manage_editor_lock(
    ctx: Context,
    action: Annotated[
        Literal["acquire", "release", "extend", "get_state", "force_release"],
        "Lock action to perform.",
    ],
    holder_hint: Annotated[str | None, "Human-readable lock holder name for acquire."] = None,
    reason: Annotated[str | None, "Reason for acquiring or extending the lock."] = None,
    token: Annotated[str | None, "Lock token returned by acquire. Required for release/extend."] = None,
    ttl_seconds: Annotated[int | None, "Requested lock TTL in seconds for acquire."] = None,
    additional_seconds: Annotated[int | None, "Additional seconds to extend the current lock."] = None,
) -> dict[str, Any]:
    unity_instance = await get_unity_instance_from_context(ctx)

    params: dict[str, Any] = {"action": action}
    optional_params = {
        "holder_hint": holder_hint,
        "reason": reason,
        "token": token,
        "ttl_seconds": ttl_seconds,
        "additional_seconds": additional_seconds,
    }
    for key, value in optional_params.items():
        if value is not None:
            params[key] = value

    response = await send_with_unity_instance(
        async_send_command_with_retry,
        unity_instance,
        "manage_editor_lock",
        params,
    )
    return response if isinstance(response, dict) else {"success": False, "message": str(response)}
