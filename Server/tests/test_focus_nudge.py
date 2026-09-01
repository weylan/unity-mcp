"""Tests for focus_nudge utility — should_nudge() logic and nudge_unity_focus() gating."""

import subprocess
import time
from unittest.mock import AsyncMock, MagicMock, call, patch

import pytest

from utils.focus_nudge import (
    FocusNudgeState,
    FocusTarget,
    _extract_project_root_from_command_line,
    _find_unity_pid_by_project_path,
    _focus_any_unity_macos,
    _focus_app_macos,
    should_nudge,
    reset_nudge_backoff,
    nudge_unity_focus,
    _is_available,
)


def test_extract_project_root_preserves_unquoted_spaces_until_next_unity_option():
    command = (
        "/Applications/Unity.app/Contents/MacOS/Unity "
        "-projectPath /Users/test/My Unity Project -batchmode -quit"
    )

    assert _extract_project_root_from_command_line(command) == "/Users/test/My Unity Project"


@pytest.mark.asyncio
async def test_nudge_test_job_uses_exact_local_identity(monkeypatch):
    """Every supported platform targets one PID and rechecks its root before focus."""
    import utils.focus_nudge as fn

    target = FocusTarget(
        user_id="local-user",
        session_id="session-7",
        process_id=4242,
        project_root="/workspace/ExactProject",
        peer_host="127.0.0.1",
    )

    for system in ("Darwin", "Windows", "Linux"):
        checks: list[tuple[int, str]] = []
        focused: list[tuple[str, int]] = []
        frontmost = fn._FrontmostAppInfo(name="Terminal", pid=73)

        monkeypatch.setattr(fn.platform, "system", lambda value=system: value)
        monkeypatch.setattr(fn, "_is_available", lambda: True)
        monkeypatch.setattr(
            fn,
            "_process_matches_target",
            lambda pid, root: checks.append((pid, root)) or True,
        )
        monkeypatch.setattr(
            fn,
            "_focus_exact_process",
            lambda pid, platform_name: focused.append((platform_name, pid)) or True,
        )
        monkeypatch.setattr(fn, "_restore_original_focus", lambda _app, _system: True)
        monkeypatch.setattr(
            fn,
            "_get_frontmost_app",
            MagicMock(side_effect=[frontmost, fn._FrontmostAppInfo(name="Unity", pid=4242)]),
        )

        async def no_sleep(_seconds):
            return None

        monkeypatch.setattr(fn.asyncio, "sleep", no_sleep)

        assert await fn.nudge_unity_focus(target, focus_duration_s=0.01, force=True)
        assert checks == [(4242, target.project_root), (4242, target.project_root)]
        assert focused[0] == (system, 4242)


class TestShouldNudge:
    """Tests for should_nudge() decision logic."""

    def test_returns_false_when_not_running(self):
        assert should_nudge(status="succeeded", editor_is_focused=False, last_update_unix_ms=0, current_time_ms=99999) is False

    def test_returns_false_when_focused(self):
        assert should_nudge(status="running", editor_is_focused=True, last_update_unix_ms=0, current_time_ms=99999) is False

    def test_returns_true_when_stalled_and_unfocused(self):
        now_ms = int(time.time() * 1000)
        stale_ms = now_ms - 5000  # 5s ago
        assert should_nudge(status="running", editor_is_focused=False, last_update_unix_ms=stale_ms, current_time_ms=now_ms) is True

    def test_returns_false_when_recently_updated(self):
        now_ms = int(time.time() * 1000)
        recent_ms = now_ms - 1000  # 1s ago (within 3s threshold)
        assert should_nudge(status="running", editor_is_focused=False, last_update_unix_ms=recent_ms, current_time_ms=now_ms) is False

    def test_returns_true_when_no_updates_yet(self):
        """No last_update_unix_ms means tests might be stuck at start."""
        assert should_nudge(status="running", editor_is_focused=False, last_update_unix_ms=None) is True

    def test_custom_stall_threshold(self):
        now_ms = int(time.time() * 1000)
        stale_ms = now_ms - 2000  # 2s ago
        # Default threshold (3s) — not stale yet
        assert should_nudge(status="running", editor_is_focused=False, last_update_unix_ms=stale_ms, current_time_ms=now_ms) is False
        # Custom threshold (1s) — stale
        assert should_nudge(status="running", editor_is_focused=False, last_update_unix_ms=stale_ms, current_time_ms=now_ms, stall_threshold_ms=1000) is True

    def test_returns_false_for_failed_status(self):
        assert should_nudge(status="failed", editor_is_focused=False, last_update_unix_ms=0, current_time_ms=99999) is False

    def test_returns_false_for_cancelled_status(self):
        assert should_nudge(status="cancelled", editor_is_focused=False, last_update_unix_ms=0, current_time_ms=99999) is False


class TestResetNudgeBackoff:
    """Tests for reset_nudge_backoff() state management."""

    def test_resets_consecutive_nudges(self):
        import utils.focus_nudge as fn
        fn._consecutive_nudges = 5
        reset_nudge_backoff()
        assert fn._consecutive_nudges == 0

    def test_updates_last_progress_time(self):
        import utils.focus_nudge as fn
        old_time = fn._last_progress_time
        reset_nudge_backoff()
        assert fn._last_progress_time >= old_time


class TestNudgeUnityFocus:
    """Tests for nudge_unity_focus() gating logic."""

    @pytest.mark.asyncio
    async def test_structured_result_distinguishes_nonexecution_focus_and_restore_failures(self):
        import utils.focus_nudge as fn

        target = FocusTarget(
            "local", "session", 222, "/Users/test/UnityProject", "127.0.0.1")
        original = fn._FrontmostAppInfo(name="Terminal", pid=111)
        focused = fn._FrontmostAppInfo(name="Unity", pid=222)

        with patch("utils.focus_nudge._is_available", return_value=False):
            not_performed = await nudge_unity_focus(
                target, force=True, return_result=True)
        assert not_performed.outcome == "not_performed"
        assert not_performed.performed is False

        with patch("utils.focus_nudge.platform.system", return_value="Darwin"), \
             patch("utils.focus_nudge._is_available", return_value=True), \
             patch("utils.focus_nudge._process_matches_target", return_value=True), \
             patch("utils.focus_nudge._get_frontmost_app", return_value=original), \
             patch("utils.focus_nudge._focus_exact_process", return_value=False), \
             patch("utils.focus_nudge._restore_original_focus") as restore:
            focus_failed = await nudge_unity_focus(
                target, focus_duration_s=0.1, force=True, return_result=True)
        assert focus_failed.outcome == "focus_failed"
        assert focus_failed.performed is False
        restore.assert_not_called()

        with patch("utils.focus_nudge.platform.system", return_value="Darwin"), \
             patch("utils.focus_nudge._is_available", return_value=True), \
             patch("utils.focus_nudge._process_matches_target", return_value=True), \
             patch("utils.focus_nudge._get_frontmost_app", side_effect=[original, focused]), \
             patch("utils.focus_nudge._focus_exact_process", return_value=True), \
             patch("utils.focus_nudge._restore_original_focus", return_value=False), \
             patch("utils.focus_nudge.asyncio.sleep", AsyncMock()):
            restore_failed = await nudge_unity_focus(
                target, focus_duration_s=0.1, force=True, return_result=True)
        assert restore_failed.outcome == "restore_failed"
        assert restore_failed.performed is False

        with patch("utils.focus_nudge.platform.system", return_value="Darwin"), \
             patch("utils.focus_nudge._is_available", return_value=True), \
             patch("utils.focus_nudge._process_matches_target", return_value=True), \
             patch("utils.focus_nudge._get_frontmost_app", side_effect=[original, focused]), \
             patch("utils.focus_nudge._focus_exact_process", return_value=True), \
             patch("utils.focus_nudge._restore_original_focus", return_value=True), \
             patch("utils.focus_nudge.asyncio.sleep", AsyncMock()):
            completed = await nudge_unity_focus(
                target, focus_duration_s=0.1, force=True, return_result=True)
        assert completed.outcome == "completed"
        assert completed.performed is True

    @pytest.mark.asyncio
    async def test_skips_when_not_available(self):
        with patch("utils.focus_nudge._is_available", return_value=False):
            result = await nudge_unity_focus(force=True)
            assert result is False

    @pytest.mark.asyncio
    async def test_skips_when_unity_already_focused(self):
        from utils.focus_nudge import _FrontmostAppInfo
        with patch("utils.focus_nudge._is_available", return_value=True), \
             patch("utils.focus_nudge._get_frontmost_app", return_value=_FrontmostAppInfo(name="Unity")):
            result = await nudge_unity_focus(force=True)
            assert result is False

    @pytest.mark.asyncio
    async def test_skips_when_frontmost_app_unknown(self):
        with patch("utils.focus_nudge._is_available", return_value=True), \
             patch("utils.focus_nudge._get_frontmost_app", return_value=None):
            result = await nudge_unity_focus(force=True)
            assert result is False

    @pytest.mark.asyncio
    async def test_rate_limited_by_backoff(self):
        import utils.focus_nudge as fn
        from utils.focus_nudge import _FrontmostAppInfo
        # Simulate a very recent nudge
        fn._last_nudge_time = time.monotonic()
        fn._consecutive_nudges = 0
        with patch("utils.focus_nudge._is_available", return_value=True), \
             patch("utils.focus_nudge._get_frontmost_app", return_value=_FrontmostAppInfo(name="Terminal")):
            result = await nudge_unity_focus(force=False)
            assert result is False

    @pytest.mark.asyncio
    async def test_rejects_missing_exact_identity_before_focus_wait(self, monkeypatch):
        import utils.focus_nudge as fn

        terminal = fn._FrontmostAppInfo(name="Terminal")
        still_terminal = fn._FrontmostAppInfo(name="WeChat")
        sleep = AsyncMock()
        monkeypatch.setattr(fn, "_last_nudge_time", 0.0)
        monkeypatch.setattr(fn, "_consecutive_nudges", 0)

        with patch("utils.focus_nudge._is_available", return_value=True), \
             patch("utils.focus_nudge._find_unity_pid_by_project_path", return_value=222), \
             patch("utils.focus_nudge._get_frontmost_app", side_effect=[terminal, still_terminal]), \
             patch("utils.focus_nudge._focus_app", return_value=True), \
             patch("utils.focus_nudge.asyncio.sleep", sleep):
            result = await nudge_unity_focus(
                focus_duration_s=12.0,
                force=True,
                unity_project_path="/Users/test/UnityProject",
            )

        assert result is False
        assert sleep.await_args_list == []
        assert fn._consecutive_nudges == 0

    @pytest.mark.asyncio
    async def test_failed_attempt_is_rate_limited_per_job(self, monkeypatch):
        import utils.focus_nudge as fn

        state = FocusNudgeState()
        target = FocusTarget("local", "session", 222, "/Users/test/UnityProject", "127.0.0.1")
        terminal = fn._FrontmostAppInfo(name="Terminal", pid=111)
        focus = patch("utils.focus_nudge._focus_exact_process", return_value=False)

        with patch("utils.focus_nudge._is_available", return_value=True), \
             patch("utils.focus_nudge._process_matches_target", return_value=True), \
             patch("utils.focus_nudge._get_frontmost_app", return_value=terminal), \
             patch("utils.focus_nudge.time.monotonic", return_value=100.0), \
             focus as focus_app:
            first = await nudge_unity_focus(target, force=False, backoff_state=state)
            second = await nudge_unity_focus(target, force=False, backoff_state=state)

        assert first is False
        assert second is False
        assert focus_app.call_count == 1

    @pytest.mark.asyncio
    async def test_project_target_does_not_skip_for_another_frontmost_unity(self, monkeypatch):
        import utils.focus_nudge as fn

        other_unity = fn._FrontmostAppInfo(name="Unity", pid=111)
        target_unity = fn._FrontmostAppInfo(name="Unity", pid=222)
        sleep = AsyncMock()
        exact_target = FocusTarget(
            "local", "session", 222, "/Users/test/TargetProject", "127.0.0.1")
        monkeypatch.setattr(fn, "_last_nudge_time", 0.0)
        monkeypatch.setattr(fn, "_consecutive_nudges", 0)

        with patch("utils.focus_nudge.platform.system", return_value="Darwin"), \
             patch("utils.focus_nudge._is_available", return_value=True), \
             patch("utils.focus_nudge._process_matches_target", return_value=True), \
             patch("utils.focus_nudge._get_frontmost_app", side_effect=[other_unity, target_unity]), \
             patch("utils.focus_nudge._focus_exact_process", return_value=True) as focus_app, \
             patch("utils.focus_nudge._restore_original_focus", return_value=True), \
             patch("utils.focus_nudge.asyncio.sleep", sleep):
            result = await nudge_unity_focus(
                exact_target,
                focus_duration_s=0.1,
                force=True,
            )

        assert result is True
        assert focus_app.call_args_list[0] == call(222, "Darwin")
        assert sleep.await_args_list == [call(0.5), call(0.1)]

    @pytest.mark.asyncio
    async def test_linux_verifies_activated_window_id(self, monkeypatch):
        import utils.focus_nudge as fn

        original = fn._FrontmostAppInfo(name="100", pid=111)
        target = fn._FrontmostAppInfo(name="200", pid=222)
        exact_target = FocusTarget(
            "local", "session", 222, "/home/test/UnityProject", "127.0.0.1")
        sleep = AsyncMock()
        monkeypatch.setattr(fn, "_find_unity_window_linux", lambda: "200", raising=False)

        with patch("utils.focus_nudge.platform.system", return_value="Linux"), \
             patch("utils.focus_nudge._is_available", return_value=True), \
             patch("utils.focus_nudge._process_matches_target", return_value=True), \
             patch("utils.focus_nudge._get_frontmost_app", side_effect=[original, target]), \
             patch("utils.focus_nudge._focus_exact_process", return_value=True), \
             patch("utils.focus_nudge._restore_original_focus", return_value=True), \
             patch("utils.focus_nudge.asyncio.sleep", sleep):
            result = await nudge_unity_focus(
                exact_target,
                focus_duration_s=0.1,
                force=True,
            )

        assert result is True
        assert sleep.await_args_list == [call(0.5), call(0.1)]


class TestMacosUnityTargeting:
    def test_finds_project_path_flag_case_insensitively(self):
        ps_output = (
            "tester 49922 0.0 0.0 0 0 ?? S 2:58AM 0:01 "
            "/Applications/Unity.app/Contents/MacOS/Unity "
            "-projectPath /Users/test/UnityProject\n"
        )
        completed = subprocess.CompletedProcess(["ps", "aux"], 0, ps_output, "")

        with patch("utils.focus_nudge.subprocess.run", return_value=completed):
            pid = _find_unity_pid_by_project_path("/Users/test/UnityProject")

        assert pid == 49922

    def test_project_path_match_is_exact(self):
        ps_output = (
            "tester 49922 0.0 0.0 0 0 ?? S 2:58AM 0:01 "
            "/Applications/Unity.app/Contents/MacOS/Unity "
            "-projectPath /Users/test/UnityProjectBackup -useHub\n"
        )
        completed = subprocess.CompletedProcess(["ps", "aux"], 0, ps_output, "")

        with patch("utils.focus_nudge.subprocess.run", return_value=completed):
            pid = _find_unity_pid_by_project_path("/Users/test/UnityProject")

        assert pid is None

    def test_project_target_fails_closed_when_pid_is_missing(self):
        with patch("utils.focus_nudge._find_unity_pid_by_project_path", return_value=None), \
             patch("utils.focus_nudge._focus_any_unity_macos", return_value=True) as fallback:
            result = _focus_app_macos("Unity", "/Users/test/MissingProject")

        assert result is False
        fallback.assert_not_called()

    def test_any_unity_fallback_excludes_unity_hub(self):
        completed = subprocess.CompletedProcess(["osascript"], 0, "", "")

        with patch("utils.focus_nudge.subprocess.run", return_value=completed) as run:
            assert _focus_any_unity_macos() is True

        script = run.call_args.args[0][-1]
        assert 'whose name is "Unity"' in script
        assert 'name contains "Unity"' not in script
