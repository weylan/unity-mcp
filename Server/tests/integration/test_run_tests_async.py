from types import SimpleNamespace
from unittest.mock import AsyncMock, MagicMock

import asyncio

import pytest

from .test_helpers import DummyContext


@pytest.mark.asyncio
async def test_get_test_job_is_observational_for_stalled_unfocused_job(monkeypatch):
    """A read must not resolve focus identity, mutate nudge state, or touch the OS."""
    import services.tools.run_tests as mod

    stalled = {
        "success": True,
        "data": {
            "job_id": "job-observe",
            "status": "running",
            "phase": "running",
            "last_update_unix_ms": 1,
            "physical_owner_retained": True,
            "progress": {"editor_is_focused": False},
        },
    }
    monkeypatch.setattr(
        mod,
        "get_unity_instance_from_context",
        AsyncMock(side_effect=["Project@hash", "local-user"]),
    )
    send = AsyncMock(return_value=stalled)
    monkeypatch.setattr(mod.unity_transport, "send_with_unity_instance", send)
    focus = AsyncMock(return_value=True)
    monkeypatch.setattr(mod, "nudge_unity_focus", focus)
    if hasattr(mod, "_get_unity_project_path"):
        monkeypatch.setattr(
            mod,
            "_get_unity_project_path",
            AsyncMock(return_value="/tmp/ExactProject"),
        )

    before = dict(getattr(mod, "_nudge_states", {}))
    response = await mod.get_test_job(MagicMock(), "job-observe")
    await asyncio.sleep(0)

    assert response.data is not None
    assert response.data.job_id == "job-observe"
    assert send.await_count == 1
    focus.assert_not_awaited()
    assert dict(getattr(mod, "_nudge_states", {})) == before


@pytest.mark.asyncio
async def test_run_tests_async_forwards_params(monkeypatch):
    from services.tools.run_tests import run_tests

    captured = {}

    async def fake_send_with_unity_instance(send_fn, unity_instance, command_type, params, **kwargs):
        captured["command_type"] = command_type
        captured["params"] = params
        return {"success": True, "data": {"job_id": "abc123", "status": "running", "mode": "EditMode"}}

    import services.tools.run_tests as mod
    monkeypatch.setattr(
        mod.unity_transport, "send_with_unity_instance", fake_send_with_unity_instance)

    resp = await run_tests(
        DummyContext(),
        mode="EditMode",
        test_names="MyNamespace.MyTests.TestA",
        include_details=True,
    )
    assert captured["command_type"] == "run_tests"
    assert captured["params"]["mode"] == "EditMode"
    assert captured["params"]["testNames"] == ["MyNamespace.MyTests.TestA"]
    assert captured["params"]["includeDetails"] is True
    assert resp.success is True
    assert resp.data is not None
    assert resp.data.job_id == "abc123"


@pytest.mark.asyncio
async def test_run_tests_forwards_init_timeout(monkeypatch):
    from services.tools.run_tests import run_tests

    captured = {}

    async def fake_send_with_unity_instance(send_fn, unity_instance, command_type, params, **kwargs):
        captured["params"] = params
        return {"success": True, "data": {"job_id": "abc123", "status": "running", "mode": "PlayMode"}}

    import services.tools.run_tests as mod
    monkeypatch.setattr(
        mod.unity_transport, "send_with_unity_instance", fake_send_with_unity_instance)

    resp = await run_tests(
        DummyContext(),
        mode="PlayMode",
        init_timeout=120000,
    )
    assert captured["params"]["initTimeout"] == 120000
    assert resp.success is True


@pytest.mark.asyncio
async def test_run_tests_omits_init_timeout_when_none(monkeypatch):
    from services.tools.run_tests import run_tests

    captured = {}

    async def fake_send_with_unity_instance(send_fn, unity_instance, command_type, params, **kwargs):
        captured["params"] = params
        return {"success": True, "data": {"job_id": "abc123", "status": "running", "mode": "EditMode"}}

    import services.tools.run_tests as mod
    monkeypatch.setattr(
        mod.unity_transport, "send_with_unity_instance", fake_send_with_unity_instance)

    resp = await run_tests(DummyContext(), mode="EditMode")
    assert "initTimeout" not in captured["params"]
    assert resp.success is True


@pytest.mark.asyncio
async def test_run_tests_rejects_negative_init_timeout():
    from services.tools.run_tests import run_tests

    resp = await run_tests(DummyContext(), mode="EditMode", init_timeout=-1)
    assert resp.success is False
    assert "init_timeout" in resp.error


@pytest.mark.asyncio
async def test_run_tests_rejects_zero_init_timeout():
    from services.tools.run_tests import run_tests

    resp = await run_tests(DummyContext(), mode="EditMode", init_timeout=0)
    assert resp.success is False
    assert "init_timeout" in resp.error


@pytest.mark.asyncio
async def test_run_tests_clear_stuck_forwards_only_the_flag(monkeypatch):
    from services.tools.run_tests import run_tests

    captured = {}

    async def fake_send_with_unity_instance(send_fn, unity_instance, command_type, params, **kwargs):
        captured["command_type"] = command_type
        captured["params"] = params
        return {"success": True, "message": "Stuck job cleared.", "data": {"cleared": True}}

    import services.tools.run_tests as mod
    monkeypatch.setattr(
        mod.unity_transport, "send_with_unity_instance", fake_send_with_unity_instance)

    resp = await run_tests(DummyContext(), clear_stuck=True)

    # C# reads @params["clear_stuck"] verbatim (RunTests.cs:23), so the key must stay snake_case.
    assert captured["command_type"] == "run_tests"
    assert captured["params"] == {"clear_stuck": True}
    assert resp.success is True
    assert resp.data == {"cleared": True}


@pytest.mark.asyncio
async def test_run_tests_clear_stuck_preserves_explicit_lock_token(monkeypatch):
    from services.tools.run_tests import run_tests
    import services.tools.run_tests as mod

    captured = {}

    async def fake_send_with_unity_instance(send_fn, unity_instance, command_type, params, **kwargs):
        captured["command_type"] = command_type
        captured["params"] = params
        return {
            "success": True,
            "message": "Logical job cleared; physical fence retained.",
            "data": {
                "cleared": True,
                "safe_to_start_new_run": False,
                "physical_owner_retained": True,
            },
        }

    monkeypatch.setattr(
        mod.unity_transport, "send_with_unity_instance", fake_send_with_unity_instance)

    resp = await run_tests(
        DummyContext(),
        clear_stuck=True,
        editor_lock_token="tok-clear",
    )

    assert captured["command_type"] == "run_tests"
    assert captured["params"] == {
        "clear_stuck": True,
        "editor_lock_token": "tok-clear",
    }
    assert resp.success is True
    assert resp.data["safe_to_start_new_run"] is False


@pytest.mark.asyncio
async def test_run_tests_clear_stuck_bypasses_preflight(monkeypatch):
    """#1272: preflight(requires_no_tests=True) would reject the call that clears the job blocking it."""
    from services.tools.run_tests import run_tests

    async def fake_send_with_unity_instance(send_fn, unity_instance, command_type, params, **kwargs):
        return {"success": True, "message": "Stuck job cleared.", "data": {"cleared": True}}

    async def exploding_preflight(*args, **kwargs):
        raise AssertionError("clear_stuck must short-circuit before preflight")

    import services.tools.run_tests as mod
    monkeypatch.setattr(
        mod.unity_transport, "send_with_unity_instance", fake_send_with_unity_instance)
    monkeypatch.setattr(mod, "preflight", exploding_preflight)

    resp = await run_tests(DummyContext(), clear_stuck=True)
    assert resp.success is True


@pytest.mark.asyncio
async def test_run_tests_clear_stuck_ignores_invalid_init_timeout(monkeypatch):
    """Recovery must be unconditional: an unrelated bad arg must not block clearing."""
    from services.tools.run_tests import run_tests

    async def fake_send_with_unity_instance(send_fn, unity_instance, command_type, params, **kwargs):
        return {"success": True, "message": "Stuck job cleared.", "data": {"cleared": True}}

    import services.tools.run_tests as mod
    monkeypatch.setattr(
        mod.unity_transport, "send_with_unity_instance", fake_send_with_unity_instance)

    resp = await run_tests(DummyContext(), clear_stuck=True, init_timeout=0)
    assert resp.success is True


@pytest.mark.asyncio
async def test_run_tests_without_clear_stuck_still_preflights(monkeypatch):
    from services.tools.run_tests import run_tests

    calls = []

    async def fake_send_with_unity_instance(send_fn, unity_instance, command_type, params, **kwargs):
        return {"success": True, "data": {"job_id": "abc123", "status": "running", "mode": "EditMode"}}

    async def recording_preflight(*args, **kwargs):
        calls.append(kwargs)
        return None

    import services.tools.run_tests as mod
    monkeypatch.setattr(
        mod.unity_transport, "send_with_unity_instance", fake_send_with_unity_instance)
    monkeypatch.setattr(mod, "preflight", recording_preflight)

    resp = await run_tests(
        DummyContext(), mode="EditMode", editor_lock_token="tok-preflight")
    assert len(calls) == 1
    assert calls[0]["requires_no_tests"] is True
    assert calls[0]["editor_lock_token"] == "tok-preflight"
    assert resp.success is True


@pytest.mark.asyncio
async def test_get_test_job_forwards_job_id(monkeypatch):
    from services.tools.run_tests import get_test_job

    captured = {}

    async def fake_send_with_unity_instance(send_fn, unity_instance, command_type, params, **kwargs):
        captured["command_type"] = command_type
        captured["params"] = params
        return {"success": True, "data": {"job_id": params["job_id"], "status": "running", "mode": "EditMode"}}

    import services.tools.run_tests as mod
    monkeypatch.setattr(
        mod.unity_transport, "send_with_unity_instance", fake_send_with_unity_instance)

    resp = await get_test_job(DummyContext(), job_id="job-1")
    assert captured["command_type"] == "get_test_job"
    assert captured["params"]["job_id"] == "job-1"
    assert resp.success is True
    assert resp.data is not None
    assert resp.data.job_id == "job-1"


@pytest.mark.asyncio
async def test_get_test_job_preserves_physical_receipt_and_result_counts(monkeypatch):
    """The public Python boundary must preserve C#'s lifecycle acceptance evidence."""
    from services.tools.run_tests import get_test_job
    import services.tools.run_tests as mod

    async def fake_send_with_unity_instance(send_fn, unity_instance, command_type, params, **kwargs):
        return {
            "success": True,
            "data": {
                "job_id": params["job_id"],
                "generation": 42,
                "status": "succeeded",
                "phase": "terminal",
                "safe_to_start_new_run": True,
                "physical_owner_retained": False,
                "result": {
                    "mode": "EditMode",
                    "summary": {
                        "total": 1,
                        "passed": 1,
                        "failed": 0,
                        "skipped": 0,
                        "durationSeconds": 0.02,
                        "resultState": "Passed",
                    },
                    "results": [],
                    "total": 1,
                    "matched": 1,
                },
                "receipt": {
                    "physical_owner": {
                        "job_id": params["job_id"],
                        "generation": 42,
                    },
                    "owner_persisted": True,
                    "run_started": True,
                    "run_started_unix_ms": 123,
                    "physical_terminal": True,
                    "cleanup_count": 1,
                    "cleanup_thread_id": 1,
                    "attached_lock_released": True,
                    "fence_released": True,
                },
            },
        }

    monkeypatch.setattr(
        mod.unity_transport, "send_with_unity_instance", fake_send_with_unity_instance)

    resp = await get_test_job(DummyContext(), job_id="job-receipt")

    assert resp.success is True
    assert resp.data is not None
    assert resp.data.result is not None
    assert resp.data.result.total == 1
    assert resp.data.result.matched == 1
    assert resp.data.receipt is not None
    assert resp.data.receipt.physical_owner is not None
    assert resp.data.receipt.physical_owner.job_id == "job-receipt"
    assert resp.data.receipt.physical_owner.generation == 42
    assert resp.data.receipt.owner_persisted is True
    assert resp.data.receipt.run_started is True
    assert resp.data.receipt.run_started_unix_ms == 123
    assert resp.data.receipt.physical_terminal is True
    assert resp.data.receipt.cleanup_count == 1
    assert resp.data.receipt.cleanup_thread_id == 1
    assert resp.data.receipt.attached_lock_released is True
    assert resp.data.receipt.fence_released is True


@pytest.mark.asyncio
async def test_get_test_job_allows_running_receipt_with_nullable_owner_fields(monkeypatch):
    """A running C# job has not necessarily recorded owner/run/cleanup timestamps yet."""
    from services.tools.run_tests import get_test_job
    import services.tools.run_tests as mod

    async def fake_send_with_unity_instance(send_fn, unity_instance, command_type, params, **kwargs):
        return {
            "success": True,
            "data": {
                "job_id": params["job_id"],
                "generation": 43,
                "status": "running",
                "phase": "queued",
                "receipt": {
                    "physical_owner": None,
                    "owner_persisted": False,
                    "run_started": False,
                    "run_started_unix_ms": None,
                    "physical_terminal": False,
                    "cleanup_count": 0,
                    "cleanup_thread_id": None,
                    "attached_lock_released": False,
                    "fence_released": False,
                },
            },
        }

    monkeypatch.setattr(
        mod.unity_transport, "send_with_unity_instance", fake_send_with_unity_instance)

    resp = await get_test_job(DummyContext(), job_id="job-running")

    assert resp.success is True
    assert resp.data is not None
    assert resp.data.receipt is not None
    assert resp.data.receipt.physical_owner is None
    assert resp.data.receipt.run_started_unix_ms is None
    assert resp.data.receipt.cleanup_thread_id is None


@pytest.mark.asyncio
async def test_get_test_job_allows_legacy_result_without_receipt_fields(monkeypatch):
    """The new response model remains compatible with an older C# package."""
    from services.tools.run_tests import get_test_job
    import services.tools.run_tests as mod

    async def fake_send_with_unity_instance(send_fn, unity_instance, command_type, params, **kwargs):
        return {
            "success": True,
            "data": {
                "job_id": params["job_id"],
                "status": "succeeded",
                "result": {
                    "mode": "EditMode",
                    "summary": {
                        "total": 1,
                        "passed": 1,
                        "failed": 0,
                        "skipped": 0,
                        "durationSeconds": 0.02,
                        "resultState": "Passed",
                    },
                    "results": [],
                },
            },
        }

    monkeypatch.setattr(
        mod.unity_transport, "send_with_unity_instance", fake_send_with_unity_instance)

    resp = await get_test_job(DummyContext(), job_id="job-legacy")

    assert resp.success is True
    assert resp.data is not None
    assert resp.data.result is not None
    assert resp.data.result.total is None
    assert resp.data.result.matched is None
    assert resp.data.receipt is None


@pytest.mark.asyncio
async def test_focus_target_resolution_reuses_user_scoped_hub_session(monkeypatch):
    import services.tools.run_tests as mod

    resolve_session = AsyncMock(return_value="session-user-a")
    get_session = AsyncMock(return_value=SimpleNamespace(
        session_id="session-user-a",
        project_path="/Users/test/UnityProject",
        project_name="UnityProject",
        process_id=4242,
        peer_host="127.0.0.1",
        user_id="user-a",
    ))
    monkeypatch.setattr(mod.unity_transport, "_is_http_transport", lambda: True)
    monkeypatch.setattr(mod.PluginHub, "_resolve_session_id", resolve_session)
    monkeypatch.setattr(mod.PluginHub, "_registry", SimpleNamespace(get_session=get_session))

    target, error = await mod._resolve_focus_target(
        "UnityProject@project-hash",
        user_id="user-a",
    )

    assert error is None
    assert target == mod.FocusTarget(
        user_id="user-a",
        session_id="session-user-a",
        process_id=4242,
        project_root=mod.canonical_project_root("/Users/test/UnityProject"),
        peer_host="127.0.0.1",
    )
    resolve_session.assert_awaited_once_with(
        "UnityProject@project-hash",
        user_id="user-a",
        retry_on_reload=False,
    )
    get_session.assert_awaited_once_with("session-user-a")


@pytest.mark.asyncio
async def test_focus_target_resolution_uses_stdio_instance_assets_path(monkeypatch):
    import services.tools.run_tests as mod

    instance = SimpleNamespace(
        id="stdio-instance",
        path="/Users/test/UnityProject/Assets",
        name="UnityProject",
        process_id=5252,
    )
    registry = SimpleNamespace(get_instance=lambda _instance: instance)
    monkeypatch.setattr(mod.unity_transport, "_is_http_transport", lambda: False)
    monkeypatch.setattr(mod, "stdio_port_registry", registry, raising=False)

    target, error = await mod._resolve_focus_target("UnityProject@project-hash", None)

    assert error is None
    assert target == mod.FocusTarget(
        user_id="local",
        session_id="stdio-instance",
        process_id=5252,
        project_root=mod.canonical_project_root("/Users/test/UnityProject"),
        peer_host="127.0.0.1",
    )


@pytest.mark.asyncio
async def test_get_test_job_waits_for_physical_terminal_after_logical_failure(monkeypatch):
    from services.tools.run_tests import get_test_job
    import services.tools.run_tests as mod

    responses = [
        {
            "success": True,
            "data": {
                "job_id": "job-fenced",
                "status": "failed",
                "phase": "awaiting_run_started",
                "physical_owner_retained": True,
                "last_update_unix_ms": 100,
                "progress": {"editor_is_focused": True},
            },
        },
        {
            "success": True,
            "data": {
                "job_id": "job-fenced",
                "status": "failed",
                "phase": "terminal",
                "physical_owner_retained": False,
                "finished_unix_ms": 100,
                "physical_finished_unix_ms": 200,
                "last_update_unix_ms": 200,
            },
        },
    ]
    calls = 0

    async def fake_send_with_unity_instance(send_fn, unity_instance, command_type, params, **kwargs):
        nonlocal calls
        response = responses[min(calls, len(responses) - 1)]
        calls += 1
        return response

    async def no_sleep(_delay):
        return None

    monkeypatch.setattr(
        mod.unity_transport, "send_with_unity_instance", fake_send_with_unity_instance)
    monkeypatch.setattr(mod.asyncio, "sleep", no_sleep)
    resp = await get_test_job(DummyContext(), job_id="job-fenced", wait_timeout=1)

    assert calls == 2
    assert resp.data is not None
    assert resp.data.phase == "terminal"
    assert resp.data.finished_unix_ms == 100
    assert resp.data.physical_finished_unix_ms == 200


@pytest.mark.asyncio
async def test_get_test_job_never_starts_background_nudge(monkeypatch):
    from services.tools.run_tests import get_test_job
    import services.tools.run_tests as mod

    async def fake_send_with_unity_instance(send_fn, unity_instance, command_type, params, **kwargs):
        return {
            "success": True,
            "data": {
                "job_id": params["job_id"],
                "status": "running",
                "last_update_unix_ms": 1,
                "progress": {"editor_is_focused": False},
            },
        }

    monkeypatch.setattr(
        mod.unity_transport, "send_with_unity_instance", fake_send_with_unity_instance)
    focus = AsyncMock(return_value=True)
    monkeypatch.setattr(mod, "nudge_unity_focus", focus)

    await mod.asyncio.gather(
        get_test_job(DummyContext(), job_id="job-one"),
        get_test_job(DummyContext(), job_id="job-one"),
    )
    await mod.asyncio.sleep(0)

    focus.assert_not_awaited()


@pytest.mark.asyncio
async def test_get_test_job_wait_timeout_is_not_extended_by_nudge(monkeypatch):
    from services.tools.run_tests import get_test_job
    import services.tools.run_tests as mod

    async def fake_send_with_unity_instance(send_fn, unity_instance, command_type, params, **kwargs):
        return {
            "success": True,
            "data": {
                "job_id": params["job_id"],
                "status": "running",
                "last_update_unix_ms": 1,
                "progress": {"editor_is_focused": False},
            },
        }

    monkeypatch.setattr(
        mod.unity_transport, "send_with_unity_instance", fake_send_with_unity_instance)
    focus = AsyncMock(return_value=True)
    monkeypatch.setattr(mod, "nudge_unity_focus", focus)

    response = await mod.asyncio.wait_for(
        get_test_job(DummyContext(), job_id="job-timeout", wait_timeout=0.02),
        timeout=0.3,
    )

    assert response.success is True
    assert response.data is not None
    assert response.data.status == "running"
    focus.assert_not_awaited()


@pytest.mark.asyncio
async def test_nudge_test_job_resolves_and_uses_one_exact_target(monkeypatch):
    import services.tools.run_tests as mod
    from utils.focus_nudge import FocusTarget

    target = FocusTarget(
        user_id="user-a",
        session_id="session-a",
        process_id=4242,
        project_root="/workspace/ExactProject",
        peer_host="127.0.0.1",
    )
    monkeypatch.setattr(
        mod,
        "get_unity_instance_from_context",
        AsyncMock(side_effect=["ExactProject@hash", "user-a"]),
    )
    monkeypatch.setattr(
        mod.unity_transport,
        "send_with_unity_instance",
        AsyncMock(return_value={
            "success": True,
            "data": {
                "job_id": "job-exact",
                "status": "running",
                "phase": "running",
                "physical_owner_retained": True,
            },
        }),
    )
    resolve = AsyncMock(return_value=(target, None))
    focus = AsyncMock(return_value=True)
    monkeypatch.setattr(mod, "_resolve_focus_target", resolve)
    monkeypatch.setattr(mod, "nudge_unity_focus", focus)

    response = await mod.nudge_test_job(DummyContext(), "job-exact", focus_duration=0.5)

    assert response.success is True
    assert response.data["performed"] is True
    resolve.assert_awaited_once_with("ExactProject@hash", "user-a")
    focus.assert_awaited_once_with(
        target, focus_duration_s=0.5, force=True, return_result=True)


@pytest.mark.asyncio
async def test_nudge_test_job_rejects_remote_hosting_before_unity_or_focus(monkeypatch):
    import services.tools.run_tests as mod
    from core.config import config

    monkeypatch.setattr(config, "http_remote_hosted", True)
    context_lookup = AsyncMock()
    send = AsyncMock()
    focus = AsyncMock()
    monkeypatch.setattr(mod, "get_unity_instance_from_context", context_lookup)
    monkeypatch.setattr(mod.unity_transport, "send_with_unity_instance", send)
    monkeypatch.setattr(mod, "nudge_unity_focus", focus)

    response = await mod.nudge_test_job(DummyContext(), "job-remote")

    assert response.success is False
    assert "remote" in response.error.lower()
    context_lookup.assert_not_awaited()
    send.assert_not_awaited()
    focus.assert_not_awaited()


@pytest.mark.asyncio
@pytest.mark.parametrize(
    ("outcome", "reason"),
    [
        ("not_performed", "focus is unavailable"),
        ("focus_failed", "Unity activation failed"),
        ("restore_failed", "original foreground app could not be restored"),
    ],
)
async def test_nudge_test_job_reports_non_success_outcomes(monkeypatch, outcome, reason):
    import services.tools.run_tests as mod
    from core.config import config
    from utils.focus_nudge import FocusTarget

    monkeypatch.setattr(config, "http_remote_hosted", False)
    target = FocusTarget(
        "local", "session-a", 4242, "/workspace/ExactProject", "127.0.0.1")
    monkeypatch.setattr(
        mod,
        "get_unity_instance_from_context",
        AsyncMock(side_effect=["ExactProject@hash", "local"]),
    )
    monkeypatch.setattr(
        mod.unity_transport,
        "send_with_unity_instance",
        AsyncMock(return_value={
            "success": True,
            "data": {
                "job_id": "job-exact",
                "status": "running",
                "phase": "running",
                "physical_owner_retained": True,
            },
        }),
    )
    monkeypatch.setattr(
        mod, "_resolve_focus_target", AsyncMock(return_value=(target, None)))
    monkeypatch.setattr(
        mod,
        "nudge_unity_focus",
        AsyncMock(return_value=SimpleNamespace(
            outcome=outcome,
            performed=False,
            reason=reason,
        )),
    )

    response = await mod.nudge_test_job(DummyContext(), "job-exact")

    assert response.success is False
    assert response.error == reason
    assert response.data["outcome"] == outcome
    assert response.data["performed"] is False
