"""Async Unity Test Runner jobs: start + poll."""
from __future__ import annotations

import asyncio
import logging
import os
from typing import Annotated, Any, Literal

from fastmcp import Context
from mcp.types import ToolAnnotations
from pydantic import BaseModel

from core.config import config
from models import MCPResponse
from services.registry import mcp_for_unity_tool
from services.tools import get_unity_instance_from_context
from services.tools.preflight import preflight
import transport.unity_transport as unity_transport
from transport.legacy.unity_connection import async_send_command_with_retry
from transport.legacy.stdio_port_registry import stdio_port_registry
from transport.plugin_hub import PluginHub
from utils.focus_nudge import (
    FocusTarget,
    canonical_project_root,
    nudge_unity_focus,
)

logger = logging.getLogger(__name__)

async def _resolve_focus_target(
    unity_instance: str | None,
    user_id: str | None,
) -> tuple[FocusTarget | None, str | None]:
    """Resolve one selected Unity instance into an exact local focus identity."""
    try:
        if unity_transport._is_http_transport():
            registry = PluginHub._registry
            if registry is None:
                return None, "WebSocket session registry is unavailable"
            session_id = await PluginHub._resolve_session_id(
                unity_instance,
                user_id=user_id,
                retry_on_reload=False,
            )
            session = await registry.get_session(session_id)
            if session is None:
                return None, "selected Unity session is no longer connected"
            expected_user = user_id or "local"
            actual_user = session.user_id or "local"
            if actual_user != expected_user:
                return None, "selected Unity session does not belong to the current user"
            if not session.project_path or not session.process_id or not session.peer_host:
                return None, "Unity registration is missing PID, project root, or peer identity"
            return FocusTarget(
                user_id=expected_user,
                session_id=session.session_id,
                process_id=session.process_id,
                project_root=canonical_project_root(session.project_path),
                peer_host=session.peer_host,
            ), None

        instance = await asyncio.to_thread(stdio_port_registry.get_instance, unity_instance)
        if instance is None:
            return None, "selected stdio Unity instance is unavailable"
        if not instance.path or not instance.process_id:
            return None, "stdio heartbeat is missing PID or project identity"
        project_root = instance.path
        if os.path.basename(project_root.rstrip("/\\")).lower() == "assets":
            project_root = os.path.dirname(project_root.rstrip("/\\"))
        return FocusTarget(
            user_id=user_id or "local",
            session_id=instance.id,
            process_id=instance.process_id,
            project_root=canonical_project_root(project_root),
            peer_host="127.0.0.1",
        ), None
    except asyncio.CancelledError:
        raise
    except Exception as exc:
        logger.debug("Could not resolve exact Unity focus target: %s", exc)
        return None, str(exc)


class RunTestsSummary(BaseModel):
    total: int
    passed: int
    failed: int
    skipped: int
    durationSeconds: float
    resultState: str


class RunTestsTestResult(BaseModel):
    name: str
    fullName: str
    state: str
    durationSeconds: float
    message: str | None = None
    stackTrace: str | None = None
    output: str | None = None


class RunTestsResult(BaseModel):
    mode: str
    summary: RunTestsSummary
    results: list[RunTestsTestResult] | None = None
    total: int | None = None
    matched: int | None = None


class RunTestsStartData(BaseModel):
    job_id: str
    status: str
    mode: str | None = None
    include_details: bool | None = None
    include_failed_tests: bool | None = None


class RunTestsStartResponse(MCPResponse):
    data: RunTestsStartData | None = None


class TestJobFailure(BaseModel):
    full_name: str | None = None
    message: str | None = None


class TestJobProgress(BaseModel):
    completed: int | None = None
    total: int | None = None
    current_test_full_name: str | None = None
    current_test_started_unix_ms: int | None = None
    last_finished_test_full_name: str | None = None
    last_finished_unix_ms: int | None = None
    stuck_suspected: bool | None = None
    editor_is_focused: bool | None = None
    blocked_reason: str | None = None
    failures_so_far: list[TestJobFailure] | None = None
    failures_capped: bool | None = None


class TestJobPhysicalOwner(BaseModel):
    job_id: str
    generation: int


class TestJobReceipt(BaseModel):
    physical_owner: TestJobPhysicalOwner | None = None
    owner_persisted: bool
    run_started: bool
    run_started_unix_ms: int | None = None
    physical_terminal: bool
    cleanup_count: int
    cleanup_thread_id: int | None = None
    attached_lock_released: bool
    fence_released: bool


class GetTestJobData(BaseModel):
    job_id: str
    generation: int | None = None
    status: str
    phase: str | None = None
    mode: str | None = None
    created_unix_ms: int | None = None
    queued_deadline_unix_ms: int | None = None
    started_unix_ms: int | None = None
    awaiting_run_started_since_unix_ms: int | None = None
    finished_unix_ms: int | None = None
    physical_finished_unix_ms: int | None = None
    last_update_unix_ms: int | None = None
    safe_to_start_new_run: bool | None = None
    physical_owner_retained: bool | None = None
    restart_required_if_orphaned: bool | None = None
    progress: TestJobProgress | None = None
    error: str | None = None
    result: RunTestsResult | None = None
    receipt: TestJobReceipt | None = None


class GetTestJobResponse(MCPResponse):
    data: GetTestJobData | None = None


def _is_physical_terminal(data: dict[str, Any]) -> bool:
    """Keep polling a logically failed job while Unity still owns the physical run."""
    if data.get("phase") == "terminal":
        return True
    if data.get("physical_owner_retained") is True:
        return False
    return data.get("status") in ("succeeded", "failed", "cancelled")


def _physical_nudge_status(data: dict[str, Any]) -> str:
    if data.get("physical_owner_retained") is True and data.get("phase") != "terminal":
        return "running"
    return str(data.get("status", ""))


@mcp_for_unity_tool(
    group="testing",
    description="Starts a Unity test run asynchronously and returns a job_id immediately. Poll with get_test_job for progress.",
    annotations=ToolAnnotations(
        title="Run Tests",
        destructiveHint=True,
    ),
)
async def run_tests(
    ctx: Context,
    mode: Annotated[Literal["EditMode", "PlayMode"],
                    "Unity test mode to run"] = "EditMode",
    test_names: Annotated[list[str] | str,
                          "Full names of specific tests to run"] | None = None,
    group_names: Annotated[list[str] | str,
                           "Same as test_names, except it allows for Regex"] | None = None,
    category_names: Annotated[list[str] | str,
                              "NUnit category names to filter by"] | None = None,
    assembly_names: Annotated[list[str] | str,
                              "Assembly names to filter tests by"] | None = None,
    include_failed_tests: Annotated[bool,
                                    "Include details for failed/skipped tests only (default: false)"] = False,
    include_details: Annotated[bool,
                               "Include details for all tests (default: false)"] = False,
    editor_lock_token: Annotated[str | None,
                                 "Token returned by manage_editor_lock acquire for multi-operation editor locks"] = None,
    init_timeout: Annotated[int | None,
                            "Initialization timeout in milliseconds, measured only after Unity's "
                            "TestRunner Execute call returns while RunStarted is still pending "
                            "(default: 15000). Recommended: 120000 for PlayMode."] = None,
    clear_stuck: Annotated[bool,
                           "Logically fail/clear a stuck job instead of starting a run. This does not "
                           "cancel Unity's physical TestRunner owner. If safe_to_start_new_run is false, "
                           "wait for its terminal callback or restart Unity."] = False,
) -> RunTestsStartResponse | MCPResponse:
    unity_instance = await get_unity_instance_from_context(ctx)

    # Runs before both the init_timeout check and preflight on purpose: neither is relevant to
    # clearing, and requires_no_tests would reject the very call that exists to clear the
    # orphaned job blocking it.
    if clear_stuck:
        clear_params: dict[str, Any] = {"clear_stuck": True}
        if editor_lock_token is not None:
            clear_params["editor_lock_token"] = editor_lock_token
        response = await unity_transport.send_with_unity_instance(
            async_send_command_with_retry,
            unity_instance,
            "run_tests",
            clear_params,
        )
        if isinstance(response, dict):
            return MCPResponse(**response)
        return MCPResponse(success=False, error=str(response))

    if init_timeout is not None and init_timeout <= 0:
        return MCPResponse(success=False, error="init_timeout must be a positive integer (milliseconds) or None")

    gate = await preflight(
        ctx,
        requires_no_tests=True,
        wait_for_no_compile=True,
        refresh_if_dirty=True,
        editor_lock_token=editor_lock_token,
    )
    if isinstance(gate, MCPResponse):
        return gate

    def _coerce_string_list(value) -> list[str] | None:
        if value is None:
            return None
        if isinstance(value, str):
            return [value] if value.strip() else None
        if isinstance(value, list):
            result = [str(v).strip() for v in value if v and str(v).strip()]
            return result if result else None
        return None

    params: dict[str, Any] = {"mode": mode}
    if (t := _coerce_string_list(test_names)):
        params["testNames"] = t
    if (g := _coerce_string_list(group_names)):
        params["groupNames"] = g
    if (c := _coerce_string_list(category_names)):
        params["categoryNames"] = c
    if (a := _coerce_string_list(assembly_names)):
        params["assemblyNames"] = a
    if include_failed_tests:
        params["includeFailedTests"] = True
    if include_details:
        params["includeDetails"] = True
    if editor_lock_token is not None:
        params["editor_lock_token"] = editor_lock_token
    if init_timeout is not None and init_timeout > 0:
        params["initTimeout"] = init_timeout

    response = await unity_transport.send_with_unity_instance(
        async_send_command_with_retry,
        unity_instance,
        "run_tests",
        params,
    )

    if isinstance(response, dict):
        if not response.get("success", True):
            return MCPResponse(**response)
        return RunTestsStartResponse(**response)
    return MCPResponse(success=False, error=str(response))


@mcp_for_unity_tool(
    group="testing",
    description="Observationally polls an async Unity test job by job_id without changing focus or lifecycle state.",
    annotations=ToolAnnotations(
        title="Get Test Job",
        readOnlyHint=True,
        destructiveHint=False,
        idempotentHint=True,
        openWorldHint=False,
    ),
)
async def get_test_job(
    ctx: Context,
    job_id: Annotated[str, "Job id returned by run_tests"],
    include_failed_tests: Annotated[bool,
                                    "Include details for failed/skipped tests only (default: false)"] = False,
    include_details: Annotated[bool,
                               "Include details for all tests (default: false)"] = False,
    wait_timeout: Annotated[int | None,
                            "If set, wait up to this many seconds for tests to complete before returning. "
                            "Reduces polling frequency and avoids client-side loop detection. "
                            "Recommended: 30-60 seconds. Returns immediately if tests complete sooner."] = None,
) -> GetTestJobResponse | MCPResponse:
    unity_instance = await get_unity_instance_from_context(ctx)

    params: dict[str, Any] = {"job_id": job_id}
    if include_failed_tests:
        params["includeFailedTests"] = True
    if include_details:
        params["includeDetails"] = True

    async def _fetch_status() -> dict[str, Any]:
        return await unity_transport.send_with_unity_instance(
            async_send_command_with_retry,
            unity_instance,
            "get_test_job",
            params,
        )

    # If wait_timeout is specified, poll server-side until complete or timeout
    if wait_timeout and wait_timeout > 0:
        deadline = asyncio.get_running_loop().time() + wait_timeout
        poll_interval = 2.0  # Poll Unity every 2 seconds

        while True:
            response = await _fetch_status()

            if not isinstance(response, dict):
                return MCPResponse(success=False, error=str(response))

            if not response.get("success", True):
                return MCPResponse(**response)

            # Check if tests are done
            data = response.get("data", {})
            if _is_physical_terminal(data):
                return GetTestJobResponse(**response)

            # Check timeout
            remaining = deadline - asyncio.get_running_loop().time()
            if remaining <= 0:
                # Timeout reached, return current status
                return GetTestJobResponse(**response)

            # Wait before next poll (but don't exceed remaining time)
            await asyncio.sleep(min(poll_interval, remaining))
    
    # No wait_timeout - return immediately (original behavior)
    response = await _fetch_status()
    if not isinstance(response, dict):
        return MCPResponse(success=False, error=str(response))
    if not response.get("success", True):
        return MCPResponse(**response)

    return GetTestJobResponse(**response)


@mcp_for_unity_tool(
    group="testing",
    unity_target="get_test_job",
    description=(
        "Explicitly and temporarily focuses the exact local Unity process for an active test job. "
        "Polling never invokes this tool automatically."
    ),
    annotations=ToolAnnotations(
        title="Nudge Test Job",
        readOnlyHint=False,
        destructiveHint=False,
        idempotentHint=False,
        openWorldHint=False,
    ),
)
async def nudge_test_job(
    ctx: Context,
    job_id: Annotated[str, "Active job id returned by run_tests"],
    focus_duration: Annotated[
        float,
        "Seconds to keep the exact Unity process focused before restoring the original app (0.1-30).",
    ] = 1.0,
) -> MCPResponse:
    if not job_id.strip():
        return MCPResponse(success=False, error="job_id is required")
    if focus_duration < 0.1 or focus_duration > 30:
        return MCPResponse(success=False, error="focus_duration must be between 0.1 and 30 seconds")
    if config.http_remote_hosted:
        return MCPResponse(
            success=False,
            error="nudge_test_job is unavailable when HTTP remote hosting is enabled",
        )

    unity_instance = await get_unity_instance_from_context(ctx)
    user_id = await get_unity_instance_from_context(ctx, key="user_id")
    response = await unity_transport.send_with_unity_instance(
        async_send_command_with_retry,
        unity_instance,
        "get_test_job",
        {"job_id": job_id},
    )
    if not isinstance(response, dict):
        return MCPResponse(success=False, error=str(response))
    if not response.get("success", True):
        return MCPResponse(**response)
    data = response.get("data") or {}
    if data.get("job_id") != job_id:
        return MCPResponse(success=False, error="Unity returned a mismatched test job identity")
    if _is_physical_terminal(data):
        return MCPResponse(success=False, error="test job is already physically terminal")

    target, error = await _resolve_focus_target(unity_instance, user_id)
    if target is None:
        return MCPResponse(success=False, error=error or "exact local Unity identity is unavailable")

    nudge_result = await nudge_unity_focus(
        target,
        focus_duration_s=focus_duration,
        force=True,
        return_result=True,
    )
    if isinstance(nudge_result, bool):
        outcome = "completed" if nudge_result else "not_performed"
        performed = nudge_result
        reason = None if performed else "Unity focus nudge was not performed"
    else:
        outcome = nudge_result.outcome
        performed = nudge_result.performed
        reason = nudge_result.reason

    success = outcome == "completed" and performed
    return MCPResponse(
        success=success,
        message="Exact Unity focus nudge completed" if success else None,
        error=None if success else (reason or f"Unity focus nudge ended with {outcome}"),
        data={
            "job_id": job_id,
            "session_id": target.session_id,
            "process_id": target.process_id,
            "project_root": target.project_root,
            "outcome": outcome,
            "performed": performed,
        },
    )
