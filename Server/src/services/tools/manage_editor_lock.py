"""Explicit shared editor operation lock management.

An attached TestRunner lock is a physical-owner fence, not a normal TTL lease.
Release, extend, token reuse, and force_release must all fail until the matching
test run reaches its durable terminal state; force_release is not cancellation.
"""

from typing import Annotated, Any, Literal

from fastmcp import Context
from mcp.types import ToolAnnotations

from services.registry import mcp_for_unity_tool
from services.tools import get_unity_instance_from_context
from transport.legacy.unity_connection import async_send_command_with_retry
from transport.unity_transport import send_with_unity_instance


@mcp_for_unity_tool(
    group="core",
    description=(
        "Manage the shared Unity editor operation lock. "
        "Use acquire to hold the lock across multiple high-risk editor operations, "
        "then pass the returned token as editor_lock_token to those tools and release it when done."
    ),
    annotations=ToolAnnotations(
        title="Manage Editor Lock",
        destructiveHint=True,
        readOnlyHint=False,
    ),
)
async def manage_editor_lock(
    ctx: Context,
    action: Annotated[
        Literal["acquire", "release", "extend", "get_state", "force_release"],
        "Lock action: acquire, release, extend, get_state, force_release.",
    ],
    holder_hint: Annotated[
        str | None,
        "Short holder label for acquire, e.g. client/session name.",
    ] = None,
    reason: Annotated[
        str | None,
        "Reason for acquiring the lock.",
    ] = None,
    token: Annotated[
        str | None,
        "Lock token returned by acquire, required for release and extend.",
    ] = None,
    ttl_seconds: Annotated[
        int | None,
        "Optional acquire TTL in seconds. Unity uses its default when omitted or non-positive.",
    ] = None,
    additional_seconds: Annotated[
        int | None,
        "Optional extension duration in seconds.",
    ] = None,
) -> dict[str, Any]:
    unity_instance = await get_unity_instance_from_context(ctx)

    params_dict: dict[str, Any] = {
        "action": action,
        "holder_hint": holder_hint,
        "reason": reason,
        "token": token,
        "ttl_seconds": ttl_seconds,
        "additional_seconds": additional_seconds,
    }
    params_dict = {k: v for k, v in params_dict.items() if v is not None}

    result = await send_with_unity_instance(
        async_send_command_with_retry,
        unity_instance,
        "manage_editor_lock",
        params_dict,
    )
    return result if isinstance(result, dict) else {"success": False, "message": str(result)}
