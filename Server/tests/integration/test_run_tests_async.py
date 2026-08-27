from types import SimpleNamespace
from unittest.mock import AsyncMock

import pytest

from .test_helpers import DummyContext


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
async def test_project_path_resolution_reuses_user_scoped_hub_session(monkeypatch):
    import services.tools.run_tests as mod

    resolve_session = AsyncMock(return_value="session-user-a")
    get_session = AsyncMock(return_value=SimpleNamespace(
        project_path="/Users/test/UnityProject",
        project_name="UnityProject",
    ))
    monkeypatch.setattr(mod.unity_transport, "_is_http_transport", lambda: True)
    monkeypatch.setattr(mod.PluginHub, "_resolve_session_id", resolve_session)
    monkeypatch.setattr(mod.PluginHub, "_registry", SimpleNamespace(get_session=get_session))

    project_path = await mod._get_unity_project_path(
        "UnityProject@project-hash",
        user_id="user-a",
    )

    assert project_path == "/Users/test/UnityProject"
    resolve_session.assert_awaited_once_with(
        "UnityProject@project-hash",
        user_id="user-a",
        retry_on_reload=False,
    )
    get_session.assert_awaited_once_with("session-user-a")


@pytest.mark.asyncio
async def test_project_path_resolution_uses_stdio_instance_assets_path(monkeypatch):
    import services.tools.run_tests as mod

    instance = SimpleNamespace(
        path="/Users/test/UnityProject/Assets",
        name="UnityProject",
    )
    registry = SimpleNamespace(get_instance=lambda _instance: instance)
    monkeypatch.setattr(mod.unity_transport, "_is_http_transport", lambda: False)
    monkeypatch.setattr(mod, "stdio_port_registry", registry, raising=False)

    project_path = await mod._get_unity_project_path("UnityProject@project-hash")

    assert project_path == "/Users/test/UnityProject"


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
    monkeypatch.setattr(mod, "_get_unity_project_path", lambda _instance: _async_none())

    resp = await get_test_job(DummyContext(), job_id="job-fenced", wait_timeout=1)

    assert calls == 2
    assert resp.data is not None
    assert resp.data.phase == "terminal"
    assert resp.data.finished_unix_ms == 100
    assert resp.data.physical_finished_unix_ms == 200


@pytest.mark.asyncio
async def test_get_test_job_single_flights_background_nudge_per_job(monkeypatch):
    from services.tools.run_tests import get_test_job
    import services.tools.run_tests as mod

    nudge_started = 0
    release_nudge = mod.asyncio.Event()

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

    async def fake_project_path(_instance):
        return "/Users/test/UnityProject"

    async def fake_nudge(**_kwargs):
        nonlocal nudge_started
        nudge_started += 1
        await release_nudge.wait()
        return True

    monkeypatch.setattr(
        mod.unity_transport, "send_with_unity_instance", fake_send_with_unity_instance)
    monkeypatch.setattr(mod, "_get_unity_project_path", fake_project_path)
    monkeypatch.setattr(mod, "should_nudge", lambda **_kwargs: True)
    monkeypatch.setattr(mod, "nudge_unity_focus", fake_nudge)

    await mod.asyncio.gather(
        get_test_job(DummyContext(), job_id="job-one"),
        get_test_job(DummyContext(), job_id="job-one"),
    )
    await mod.asyncio.sleep(0)

    assert nudge_started == 1
    release_nudge.set()
    await mod.asyncio.sleep(0)


@pytest.mark.asyncio
async def test_get_test_job_nudge_backoff_is_isolated_by_job(monkeypatch):
    from services.tools.run_tests import get_test_job
    import services.tools.run_tests as mod

    states = []

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

    async def fake_project_path(_instance):
        return "/Users/test/UnityProject"

    async def fake_nudge(**kwargs):
        states.append(kwargs.get("backoff_state"))
        return True

    monkeypatch.setattr(
        mod.unity_transport, "send_with_unity_instance", fake_send_with_unity_instance)
    monkeypatch.setattr(mod, "_get_unity_project_path", fake_project_path)
    monkeypatch.setattr(mod, "should_nudge", lambda **_kwargs: True)
    monkeypatch.setattr(mod, "nudge_unity_focus", fake_nudge)

    await get_test_job(DummyContext(), job_id="job-one")
    await mod.asyncio.sleep(0)
    await get_test_job(DummyContext(), job_id="job-two")
    await mod.asyncio.sleep(0)

    assert len(states) == 2
    assert states[0] is not None
    assert states[1] is not None
    assert states[0] is not states[1]


@pytest.mark.asyncio
async def test_get_test_job_wait_timeout_is_not_extended_by_nudge(monkeypatch):
    from services.tools.run_tests import get_test_job
    import services.tools.run_tests as mod

    release_nudge = mod.asyncio.Event()

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

    async def fake_project_path(_instance):
        return "/Users/test/UnityProject"

    async def fake_nudge(**_kwargs):
        await release_nudge.wait()
        return True

    monkeypatch.setattr(
        mod.unity_transport, "send_with_unity_instance", fake_send_with_unity_instance)
    monkeypatch.setattr(mod, "_get_unity_project_path", fake_project_path)
    monkeypatch.setattr(mod, "should_nudge", lambda **_kwargs: True)
    monkeypatch.setattr(mod, "nudge_unity_focus", fake_nudge)

    response = await mod.asyncio.wait_for(
        get_test_job(DummyContext(), job_id="job-timeout", wait_timeout=0.02),
        timeout=0.3,
    )

    assert response.success is True
    assert response.data is not None
    assert response.data.status == "running"
    release_nudge.set()
    await mod.asyncio.sleep(0)


@pytest.mark.asyncio
async def test_get_test_job_skips_nudge_without_project_identity(monkeypatch):
    from services.tools.run_tests import get_test_job
    import services.tools.run_tests as mod

    nudge_calls = 0

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

    async def fake_nudge(**_kwargs):
        nonlocal nudge_calls
        nudge_calls += 1
        return True

    monkeypatch.setattr(
        mod.unity_transport, "send_with_unity_instance", fake_send_with_unity_instance)
    project_lookups = 0

    async def missing_project(*_args, **_kwargs):
        nonlocal project_lookups
        project_lookups += 1
        return None

    monkeypatch.setattr(mod, "_get_unity_project_path", missing_project)
    monkeypatch.setattr(mod, "should_nudge", lambda **_kwargs: True)
    monkeypatch.setattr(mod, "nudge_unity_focus", fake_nudge)

    await get_test_job(DummyContext(), job_id="job-without-project")
    await mod.asyncio.sleep(0)
    await get_test_job(DummyContext(), job_id="job-without-project")
    await mod.asyncio.sleep(0)

    assert nudge_calls == 0
    assert project_lookups == 1


def test_job_nudge_state_cache_has_a_hard_upper_bound(monkeypatch):
    import services.tools.run_tests as mod

    monkeypatch.setattr(mod, "_nudge_states", {})
    monkeypatch.setattr(mod, "_background_tasks", {})

    for index in range(mod._MAX_NUDGE_STATES + 20):
        mod._get_job_nudge_state(("user", "instance", f"job-{index}"))

    assert len(mod._nudge_states) <= mod._MAX_NUDGE_STATES


def test_nudge_state_key_is_user_scoped():
    import services.tools.run_tests as mod

    assert mod._nudge_key("Project@hash", "job-one", "user-a") != mod._nudge_key(
        "Project@hash", "job-one", "user-b")


async def _async_none():
    return None
