"""Async Unity Test Runner jobs: start + poll."""
from __future__ import annotations

import asyncio
import logging
import os
import time
from dataclasses import dataclass, field
from typing import Annotated, Any, Literal

from fastmcp import Context
from mcp.types import ToolAnnotations
from pydantic import BaseModel

from models import MCPResponse
from services.registry import mcp_for_unity_tool
from services.tools import get_unity_instance_from_context
from services.tools.preflight import preflight
import transport.unity_transport as unity_transport
from transport.legacy.unity_connection import async_send_command_with_retry
from transport.legacy.stdio_port_registry import stdio_port_registry
from transport.plugin_hub import PluginHub
from utils.focus_nudge import (
    FocusNudgeState,
    nudge_unity_focus,
    reset_nudge_backoff,
    should_nudge,
)

logger = logging.getLogger(__name__)

_NudgeKey = tuple[str, str, str]
_MAX_NUDGE_STATES = 128
_PROJECT_LOOKUP_RETRY_S = 5.0


@dataclass
class _JobNudgeState:
    backoff: FocusNudgeState = field(default_factory=FocusNudgeState)
    project_path: str | None = None
    last_project_lookup_time: float = 0.0
    last_seen_time: float = field(default_factory=time.monotonic)


# A job may be polled concurrently by several clients. Keep one nudge task and
# one backoff sequence per Unity instance/job instead of sharing module globals.
_background_tasks: dict[_NudgeKey, asyncio.Task[bool]] = {}
_nudge_states: dict[_NudgeKey, _JobNudgeState] = {}


def _nudge_key(
    unity_instance: str | None,
    job_id: str,
    user_id: str | None,
) -> _NudgeKey:
    return (user_id or "", unity_instance or "", job_id)


def _get_job_nudge_state(key: _NudgeKey) -> _JobNudgeState:
    now = time.monotonic()
    existing = _nudge_states.get(key)
    if existing is not None:
        existing.last_seen_time = now
        return existing

    if len(_nudge_states) >= _MAX_NUDGE_STATES:
        inactive_keys = [candidate for candidate in _nudge_states if candidate not in _background_tasks]
        if inactive_keys:
            oldest = min(
                inactive_keys,
                key=lambda candidate: _nudge_states[candidate].last_seen_time,
            )
            _nudge_states.pop(oldest, None)

    state = _JobNudgeState(last_seen_time=now)
    _nudge_states[key] = state
    return state


async def _get_job_project_path(
    key: _NudgeKey,
    unity_instance: str | None,
    user_id: str | None,
) -> tuple[str | None, bool]:
    state = _get_job_nudge_state(key)
    if state.project_path is not None:
        return state.project_path, False

    now = time.monotonic()
    if (
        state.last_project_lookup_time > 0
        and now - state.last_project_lookup_time < _PROJECT_LOOKUP_RETRY_S
    ):
        return None, False

    state.last_project_lookup_time = now
    if user_id is None:
        project_path = await _get_unity_project_path(unity_instance)
    else:
        project_path = await _get_unity_project_path(unity_instance, user_id=user_id)
    if project_path is not None:
        state.project_path = project_path
    return project_path, True


async def _perform_job_nudge(
    key: _NudgeKey,
    project_path: str,
    state: FocusNudgeState,
) -> bool:
    try:
        return await nudge_unity_focus(
            unity_project_path=project_path,
            backoff_state=state,
        )
    except asyncio.CancelledError:
        raise
    except Exception:
        logger.exception("Focus nudge failed for TestRunner job %s", key[2])
        return False


def _remove_finished_nudge(key: _NudgeKey, task: asyncio.Task[bool]) -> None:
    if _background_tasks.get(key) is task:
        _background_tasks.pop(key, None)


def _get_or_start_job_nudge(key: _NudgeKey, project_path: str) -> asyncio.Task[bool]:
    existing = _background_tasks.get(key)
    if existing is not None and not existing.done():
        return existing

    state = _get_job_nudge_state(key)
    task = asyncio.create_task(_perform_job_nudge(key, project_path, state.backoff))
    _background_tasks[key] = task
    task.add_done_callback(lambda done, task_key=key: _remove_finished_nudge(task_key, done))
    return task


def _forget_job_nudge_state(key: _NudgeKey) -> None:
    _nudge_states.pop(key, None)


async def _get_unity_project_path(
    unity_instance: str | None,
    user_id: str | None = None,
) -> str | None:
    """Get the project root path for a Unity instance (for focus nudging).

    Args:
        unity_instance: Unity instance hash or "Name@hash" format or None

    Returns:
        Project root path (e.g., "/Users/name/project"), or falls back to project_name if path unavailable
    """
    try:
        if unity_transport._is_http_transport():
            registry = PluginHub._registry
            if registry is None:
                return None
            session_id = await PluginHub._resolve_session_id(
                unity_instance,
                user_id=user_id,
                retry_on_reload=False,
            )
            session = await registry.get_session(session_id)
            if session is None:
                return None
            if session.project_path:
                return session.project_path
            return session.project_name if session.project_name else None

        instance = await asyncio.to_thread(
            stdio_port_registry.get_instance,
            unity_instance,
        )
        if instance is None:
            return None
        if instance.path:
            project_path = os.path.normpath(instance.path)
            if os.path.basename(project_path).lower() == "assets":
                project_path = os.path.dirname(project_path)
            return project_path
        return instance.name if instance.name else None

    except Exception as e:
        # Re-raise cancellation errors so task cancellation propagates
        if isinstance(e, asyncio.CancelledError):
            raise
        logger.debug(f"Could not get Unity project path: {e}")
        return None


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


class GetTestJobData(BaseModel):
    job_id: str
    generation: int | None = None
    status: str
    phase: str | None = None
    mode: str | None = None
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
    description="Polls an async Unity test job by job_id.",
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
    user_id = await get_unity_instance_from_context(ctx, key="user_id")
    nudge_key = _nudge_key(unity_instance, job_id, user_id)

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
        prev_last_update_unix_ms = None

        while True:
            response = await _fetch_status()

            if not isinstance(response, dict):
                _forget_job_nudge_state(nudge_key)
                return MCPResponse(success=False, error=str(response))

            if not response.get("success", True):
                _forget_job_nudge_state(nudge_key)
                return MCPResponse(**response)

            # Check if tests are done
            data = response.get("data", {})
            if _is_physical_terminal(data):
                _forget_job_nudge_state(nudge_key)
                return GetTestJobResponse(**response)

            # Detect progress and reset exponential backoff
            last_update_unix_ms = data.get("last_update_unix_ms")
            if prev_last_update_unix_ms is not None and last_update_unix_ms != prev_last_update_unix_ms:
                # Progress detected - reset exponential backoff for next potential stall
                state = _nudge_states.get(nudge_key)
                if state is not None:
                    reset_nudge_backoff(state.backoff)
                logger.debug(f"Test job {job_id} made progress - reset nudge backoff")
            prev_last_update_unix_ms = last_update_unix_ms

            # Check if Unity needs a focus nudge to make progress
            # This handles OS-level throttling (e.g., macOS App Nap) that can
            # stall PlayMode tests when Unity is in the background.
            # Uses exponential backoff: 1s, 2s, 4s, 8s, 10s max between nudges.
            progress = data.get("progress") or {}
            editor_is_focused = progress.get("editor_is_focused", True)
            current_time_ms = int(time.time() * 1000)

            if should_nudge(
                status=_physical_nudge_status(data),
                editor_is_focused=editor_is_focused,
                last_update_unix_ms=last_update_unix_ms,
                current_time_ms=current_time_ms,
                # Use default stall_threshold_ms (3s)
            ):
                logger.info(f"Test job {job_id} appears stalled (unfocused Unity), attempting nudge...")
                project_path, lookup_attempted = await _get_job_project_path(
                    nudge_key,
                    unity_instance,
                    user_id,
                )
                if project_path is None:
                    if lookup_attempted:
                        logger.warning(
                            "Skipping focus nudge for TestRunner job %s because its Unity project identity is unavailable",
                            job_id,
                        )
                else:
                    remaining = deadline - asyncio.get_running_loop().time()
                    if remaining <= 0:
                        return GetTestJobResponse(**response)
                    nudge_task = _get_or_start_job_nudge(nudge_key, project_path)
                    try:
                        nudged = await asyncio.wait_for(
                            asyncio.shield(nudge_task),
                            timeout=remaining,
                        )
                    except asyncio.TimeoutError:
                        # The shielded nudge keeps running so it can restore the
                        # original app, while this poll honors wait_timeout.
                        return GetTestJobResponse(**response)
                    if nudged:
                        logger.info(f"Test job {job_id} nudge completed")

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
        _forget_job_nudge_state(nudge_key)
        return MCPResponse(success=False, error=str(response))
    if not response.get("success", True):
        _forget_job_nudge_state(nudge_key)
        return MCPResponse(**response)

    # Fire-and-forget nudge check: even without wait_timeout, clients may poll
    # externally. Check if Unity needs a nudge on every call so stalls get
    # detected regardless of polling style.
    data = response.get("data", {})
    if _is_physical_terminal(data):
        _forget_job_nudge_state(nudge_key)
        return GetTestJobResponse(**response)

    status = _physical_nudge_status(data)
    if status == "running":
        progress = data.get("progress") or {}
        editor_is_focused = progress.get("editor_is_focused", True)
        last_update_unix_ms = data.get("last_update_unix_ms")
        current_time_ms = int(time.time() * 1000)
        if should_nudge(
            status=status,
            editor_is_focused=editor_is_focused,
            last_update_unix_ms=last_update_unix_ms,
            current_time_ms=current_time_ms,
        ):
            logger.info(f"Test job {job_id} appears stalled (unfocused Unity), scheduling background nudge...")
            project_path, lookup_attempted = await _get_job_project_path(
                nudge_key,
                unity_instance,
                user_id,
            )
            if project_path is None:
                if lookup_attempted:
                    logger.warning(
                        "Skipping focus nudge for TestRunner job %s because its Unity project identity is unavailable",
                        job_id,
                    )
            else:
                _get_or_start_job_nudge(nudge_key, project_path)

    return GetTestJobResponse(**response)
