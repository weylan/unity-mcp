from __future__ import annotations

from typing import Annotated, Any, Literal

from fastmcp import Context

from models import MCPResponse
from services.registry import mcp_for_unity_resource
from services.tools import get_unity_instance_from_context
from transport.legacy.unity_connection import async_send_command_with_retry
from transport.unity_transport import send_with_unity_instance


def _normalize_response(response: dict[str, Any] | Any) -> MCPResponse:
    if isinstance(response, dict):
        return MCPResponse(**response)
    return response


@mcp_for_unity_resource(
    uri="mcpforunity://playmode/state",
    name="playmode_state",
    description=(
        "Compact Play Mode testing snapshot with scene, frame timing, camera, player, "
        "Animator, and UI summary. Unity must already be in Play Mode.\n\n"
        "URI: mcpforunity://playmode/state"
    ),
)
async def get_playmode_state(
    ctx: Context,
    include_ui: Annotated[bool, "Include a compact UI summary in the snapshot."] = True,
    ui_limit: Annotated[int, "Maximum UI items in the compact summary (0-100)."] = 20,
    player: Annotated[
        str | None,
        "Optional player GameObject name, path, or instance ID. Defaults to the Player tag.",
    ] = None,
) -> MCPResponse:
    if ui_limit < 0 or ui_limit > 100:
        return MCPResponse(
            success=False,
            error="invalid_ui_limit",
            message="ui_limit must be between 0 and 100.",
        )

    unity_instance = await get_unity_instance_from_context(ctx)
    params: dict[str, Any] = {
        "includeUI": include_ui,
        "uiLimit": ui_limit,
    }
    if player is not None:
        params["player"] = player

    response = await send_with_unity_instance(
        async_send_command_with_retry,
        unity_instance,
        "get_playmode_state",
        params,
    )
    return _normalize_response(response)


@mcp_for_unity_resource(
    uri="mcpforunity://playmode/ui",
    name="playmode_ui",
    description=(
        "Paginated visible Play Mode UI text and interactable elements. Supports "
        "uGUI, TextMeshPro, and UI Toolkit when available.\n\n"
        "URI: mcpforunity://playmode/ui"
    ),
)
async def get_playmode_ui(
    ctx: Context,
    page_size: Annotated[int, "Number of UI items to return (1-100)."] = 50,
    cursor: Annotated[int, "Zero-based pagination offset."] = 0,
    framework: Annotated[
        Literal["all", "ugui", "uitoolkit"],
        "UI framework filter.",
    ] = "all",
) -> MCPResponse:
    if page_size < 1 or page_size > 100:
        return MCPResponse(
            success=False,
            error="invalid_page_size",
            message="page_size must be between 1 and 100.",
        )
    if cursor < 0:
        return MCPResponse(
            success=False,
            error="invalid_cursor",
            message="cursor must be non-negative.",
        )

    unity_instance = await get_unity_instance_from_context(ctx)
    response = await send_with_unity_instance(
        async_send_command_with_retry,
        unity_instance,
        "get_playmode_ui",
        {
            "pageSize": page_size,
            "cursor": cursor,
            "framework": framework,
        },
    )
    return _normalize_response(response)
