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


async def _async_none():
    return None
