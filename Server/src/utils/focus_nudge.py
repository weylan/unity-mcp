"""
Focus nudge utility for handling OS-level throttling of background Unity.

When Unity is unfocused, the OS (especially macOS App Nap) can heavily throttle
the process, causing PlayMode tests to stall. This utility temporarily brings
Unity to focus, allows it to process, then returns focus to the original app.
"""

from __future__ import annotations

import asyncio
import ipaddress
import logging
import os
import platform
import re
import shutil
import subprocess
import time
from dataclasses import dataclass

logger = logging.getLogger(__name__)


def _parse_env_float(env_var: str, default: float) -> float:
    """Safely parse environment variable as float, logging warnings on failure."""
    value = os.environ.get(env_var)
    if value is None:
        return default
    try:
        parsed = float(value)
        if parsed <= 0:
            logger.warning(f"Invalid {env_var}={value!r}, using default {default}: must be > 0")
            return default
        return parsed
    except (ValueError, TypeError) as e:
        logger.warning(f"Invalid {env_var}={value!r}, using default {default}: {e}")
        return default


# Base interval between nudges (exponentially increases with consecutive nudges)
# Can be overridden via UNITY_MCP_NUDGE_BASE_INTERVAL_S environment variable
_BASE_NUDGE_INTERVAL_S = _parse_env_float("UNITY_MCP_NUDGE_BASE_INTERVAL_S", 1.0)

# Maximum interval between nudges (cap for exponential backoff)
# Can be overridden via UNITY_MCP_NUDGE_MAX_INTERVAL_S environment variable
_MAX_NUDGE_INTERVAL_S = _parse_env_float("UNITY_MCP_NUDGE_MAX_INTERVAL_S", 10.0)

# Default duration to keep Unity focused during a nudge
# Can be overridden via UNITY_MCP_NUDGE_DURATION_S environment variable
_DEFAULT_FOCUS_DURATION_S = _parse_env_float("UNITY_MCP_NUDGE_DURATION_S", 3.0)

_last_nudge_time: float = 0.0
_consecutive_nudges: int = 0
_last_progress_time: float = 0.0


@dataclass
class FocusNudgeState:
    """Backoff state for one Unity TestRunner job."""

    last_nudge_time: float = 0.0
    consecutive_nudges: int = 0
    last_attempt_time: float = 0.0
    consecutive_attempts: int = 0
    last_progress_time: float = 0.0


@dataclass(frozen=True)
class FocusTarget:
    """An already-authorized local Unity process identity."""

    user_id: str
    session_id: str
    process_id: int
    project_root: str
    peer_host: str


@dataclass(frozen=True)
class FocusNudgeResult:
    """Outcome of one explicit focus attempt."""

    outcome: str
    reason: str | None = None

    @property
    def performed(self) -> bool:
        return self.outcome == "completed"


def _return_focus_nudge_result(
    result: FocusNudgeResult,
    return_result: bool,
) -> bool | FocusNudgeResult:
    return result if return_result else result.performed


def canonical_project_root(project_root: str) -> str:
    return os.path.normcase(os.path.realpath(os.path.abspath(project_root)))


def _is_local_peer(peer_host: str) -> bool:
    normalized = (peer_host or "").strip().strip("[]").lower()
    if normalized == "localhost":
        return True
    try:
        return ipaddress.ip_address(normalized).is_loopback
    except ValueError:
        return False


def validate_focus_target(target: FocusTarget | None) -> str | None:
    if target is None:
        return "exact focus target is required"
    if not target.user_id.strip() or not target.session_id.strip():
        return "user_id and session_id are required"
    if not isinstance(target.process_id, int) or target.process_id <= 0:
        return "a positive Unity process_id is required"
    if not target.project_root.strip() or not os.path.isabs(target.project_root):
        return "an absolute Unity project_root is required"
    if not _is_local_peer(target.peer_host):
        return "focus is allowed only for a loopback peer"
    return None


def _extract_project_root_from_command_line(command_line: str) -> str | None:
    match = re.search(
        r"(?:^|\s)-projectpath(?:\s+|=)(?:\"([^\"]+)\"|'([^']+)'|(.+?))(?=\s+-[A-Za-z]|$)",
        command_line or "",
        re.IGNORECASE,
    )
    if match is None:
        return None
    value = next((part for part in match.groups() if part is not None), "")
    return value.strip()


def _get_process_command_line(process_id: int) -> str | None:
    try:
        if platform.system() == "Windows":
            script = (
                f"(Get-CimInstance Win32_Process -Filter \"ProcessId = {process_id}\").CommandLine"
            )
            result = subprocess.run(
                ["powershell", "-NoProfile", "-Command", script],
                capture_output=True,
                text=True,
                timeout=5,
            )
        else:
            result = subprocess.run(
                ["ps", "-p", str(process_id), "-o", "command="],
                capture_output=True,
                text=True,
                timeout=5,
            )
        if result.returncode != 0:
            return None
        value = result.stdout.strip()
        return value or None
    except Exception as exc:
        logger.debug("Could not inspect process %s: %s", process_id, exc)
        return None


def _process_matches_target(process_id: int, project_root: str) -> bool:
    command_line = _get_process_command_line(process_id)
    if command_line is None or "unity" not in command_line.lower():
        return False
    actual_root = _extract_project_root_from_command_line(command_line)
    if actual_root is None:
        return False
    return canonical_project_root(actual_root) == canonical_project_root(project_root)


@dataclass
class _FrontmostAppInfo:
    """Info about the frontmost application for focus restore."""

    name: str
    bundle_id: str | None = None  # macOS only: bundle identifier for precise activation
    pid: int | None = None  # macOS only: process identity for multi-instance verification

    def __str__(self) -> str:
        return self.name


def _is_available() -> bool:
    """Check if focus nudging is available on this platform."""
    system = platform.system()
    if system == "Darwin":
        return shutil.which("osascript") is not None
    elif system == "Windows":
        # PowerShell is typically available on Windows
        return shutil.which("powershell") is not None
    elif system == "Linux":
        return shutil.which("xdotool") is not None
    return False


def _get_current_nudge_interval(backoff_state: FocusNudgeState | None = None) -> float:
    """
    Calculate current nudge interval using exponential backoff.

    Returns interval based on consecutive nudges without progress:
    - 0 nudges: base interval (1.0s)
    - 1 nudge: base * 2 (2.0s)
    - 2 nudges: base * 4 (4.0s)
    - 3+ nudges: base * 8 (8.0s, capped at max)
    """
    consecutive_nudges = (
        backoff_state.consecutive_attempts
        if backoff_state is not None
        else _consecutive_nudges
    )
    if consecutive_nudges == 0:
        return _BASE_NUDGE_INTERVAL_S

    # Exponential backoff: interval = base * (2 ^ consecutive_nudges)
    interval = _BASE_NUDGE_INTERVAL_S * (2 ** consecutive_nudges)
    return min(interval, _MAX_NUDGE_INTERVAL_S)


def _get_current_focus_duration(backoff_state: FocusNudgeState | None = None) -> float:
    """
    Calculate current focus duration using exponential backoff.

    Base durations (3, 5, 8, 12 seconds) are scaled proportionally by the
    configured UNITY_MCP_NUDGE_DURATION_S relative to _DEFAULT_FOCUS_DURATION_S.
    For example, if UNITY_MCP_NUDGE_DURATION_S=6.0 (2x default), all durations
    are doubled: (6, 10, 16, 24 seconds).
    """
    # Base durations for each nudge level
    base_durations = [3.0, 5.0, 8.0, 12.0]
    consecutive_nudges = (
        backoff_state.consecutive_nudges
        if backoff_state is not None
        else _consecutive_nudges
    )
    base_duration = base_durations[min(consecutive_nudges, len(base_durations) - 1)]

    # Scale by ratio of configured to default duration (if UNITY_MCP_NUDGE_DURATION_S is set)
    scale = 1.0
    if os.environ.get("UNITY_MCP_NUDGE_DURATION_S") is not None:
        configured_duration = _parse_env_float("UNITY_MCP_NUDGE_DURATION_S", _DEFAULT_FOCUS_DURATION_S)
        if _DEFAULT_FOCUS_DURATION_S > 0:
            scale = configured_duration / _DEFAULT_FOCUS_DURATION_S
    duration = base_duration * scale
    if duration <= 0:
        return _DEFAULT_FOCUS_DURATION_S
    return duration


def reset_nudge_backoff(backoff_state: FocusNudgeState | None = None) -> None:
    """
    Reset exponential backoff when progress is detected.

    Call this when test job makes progress to reset the nudge interval
    back to the base interval for quick response to future stalls.
    """
    now = time.monotonic()
    if backoff_state is not None:
        backoff_state.consecutive_nudges = 0
        backoff_state.last_nudge_time = 0.0
        backoff_state.consecutive_attempts = 0
        backoff_state.last_attempt_time = 0.0
        backoff_state.last_progress_time = now
        return

    global _consecutive_nudges, _last_progress_time
    _consecutive_nudges = 0
    _last_progress_time = now


def _get_frontmost_app_macos() -> _FrontmostAppInfo | None:
    """Get the name and bundle identifier of the frontmost application on macOS.

    Returns both process name and bundle ID so we can restore focus precisely.
    Using bundle ID avoids the Electron bug where `tell application "Electron"`
    launches a standalone Electron instance instead of returning to VS Code.
    """
    try:
        result = subprocess.run(
            [
                "osascript", "-e",
                'tell application "System Events"\n'
                '    set frontProc to first process whose frontmost is true\n'
                '    set procName to name of frontProc\n'
                '    set bundleID to ""\n'
                '    try\n'
                '        set bID to bundle identifier of frontProc\n'
                '        if bID is not missing value then set bundleID to bID\n'
                '    end try\n'
                '    set procPID to unix id of frontProc\n'
                '    return procName & "|" & bundleID & "|" & procPID\n'
                'end tell',
            ],
            capture_output=True,
            text=True,
            timeout=5,
        )
        if result.returncode == 0:
            output = result.stdout.strip()
            parts = output.split("|", 2)
            name = parts[0]
            bundle_id: str | None = None
            if len(parts) > 1:
                raw_bundle_id = parts[1].strip()
                # Some processes report "missing value" as bundle ID; treat as absent
                if raw_bundle_id and raw_bundle_id.lower() != "missing value":
                    bundle_id = raw_bundle_id
            pid: int | None = None
            if len(parts) > 2:
                try:
                    pid = int(parts[2].strip())
                except ValueError:
                    pass
            return _FrontmostAppInfo(name=name, bundle_id=bundle_id, pid=pid)
    except Exception as e:
        logger.debug(f"Failed to get frontmost app: {e}")
    return None


def _find_unity_pid_by_project_path(project_path: str) -> int | None:
    """Find Unity Editor PID by matching project path in command line args.

    Args:
        project_path: Full path to Unity project root, OR just the project name.
            - Full path: "/Users/name/Projects/MyGame"
            - Project name: "MyGame" (will match any path ending with this)

    Returns:
        PID of matching Unity process, or None if not found
    """
    try:
        # Use ps to find Unity processes with -projectpath argument
        result = subprocess.run(
            ["ps", "aux"],
            capture_output=True,
            text=True,
            timeout=5,
        )
        if result.returncode != 0:
            return None

        # Determine if project_path is a full path or just a name
        is_full_path = "/" in project_path or "\\" in project_path
        target_path = os.path.normpath(project_path)
        project_path_pattern = re.compile(
            r"(?:^|\s)-projectpath(?:\s+|=)(.+?)(?=\s+-[A-Za-z]|$)",
            re.IGNORECASE,
        )

        # Look for Unity.app processes with matching -projectPath. Unity emits a
        # capital P, but older versions and hand-launched commands may not.
        for line in result.stdout.splitlines():
            if "Unity.app/Contents/MacOS/Unity" not in line:
                continue

            match = project_path_pattern.search(line)
            if match is None:
                continue
            candidate_path = match.group(1).strip().strip("\"'")

            if is_full_path:
                if os.path.normpath(candidate_path) != target_path:
                    continue
            else:
                normalized_candidate = candidate_path.replace("\\", "/").rstrip("/")
                if normalized_candidate.rsplit("/", 1)[-1] != project_path:
                    continue

            # Extract PID (second column in ps aux output)
            parts = line.split()
            if len(parts) >= 2:
                try:
                    pid = int(parts[1])
                    logger.debug(f"Found Unity PID {pid} for project path/name {project_path}")
                    return pid
                except ValueError:
                    continue

        logger.warning(f"No Unity process found with project path/name {project_path}")
        return None
    except Exception as e:
        logger.debug(f"Failed to find Unity PID: {e}")
        return None


def _focus_app_macos(
    app_name: str,
    unity_project_path: str | None = None,
    bundle_id: str | None = None,
    unity_pid: int | None = None,
) -> bool:
    """Focus an application on macOS.

    For Unity, can target a specific instance by project path (multi-instance support).
    For other apps, prefers bundle_id activation to avoid the Electron bug where
    generic process names like "Electron" cause macOS to launch the wrong app.

    Args:
        app_name: Application name to focus ("Unity" or specific app name)
        unity_project_path: For Unity apps, the full project root path to match against
            -projectpath command line arg (e.g., "/path/to/project" NOT "/path/to/project/Assets")
        bundle_id: Bundle identifier for precise activation (e.g. "com.microsoft.VSCode").
            Preferred over app_name for non-Unity apps.
        unity_pid: Already-resolved Unity PID, used to avoid resolving a target twice
            and to restore a previously frontmost Unity instance precisely.
    """
    try:
        # For Unity, use PID-based activation for precise targeting
        if app_name == "Unity":
            if unity_project_path or unity_pid is not None:
                pid = unity_pid
                if pid is None and unity_project_path:
                    pid = _find_unity_pid_by_project_path(unity_project_path)
                if pid is None:
                    logger.warning(
                        "Could not find Unity PID for project %s; refusing to focus an unrelated Unity process",
                        unity_project_path,
                    )
                    return False

                # Two-step activation for full Unity wake-up:
                # 1. Bring window to front
                # 2. Activate the application bundle (triggers full app activation like cmd+tab or clicking)
                script = f'''
tell application "System Events"
    set targetProc to first process whose unix id is {pid}
    set frontmost of targetProc to true

    -- Get bundle identifier to activate the app properly
    set bundleID to bundle identifier of targetProc
end tell

-- Activate using bundle identifier (ensures Unity wakes up and starts processing)
tell application id bundleID to activate
'''
                result = subprocess.run(
                    ["osascript", "-e", script],
                    capture_output=True,
                    text=True,
                    timeout=5,
                )
                if result.returncode != 0:
                    logger.debug(f"Failed to activate Unity PID {pid}: {result.stderr}")
                    return False
                logger.info(
                    "Activated Unity instance with PID %s%s",
                    pid,
                    f" for project {unity_project_path}" if unity_project_path else "",
                )
                return True
            else:
                # No project path provided - activate any Unity process
                return _focus_any_unity_macos()
        else:
            # For non-Unity apps, prefer bundle_id to avoid the Electron bug:
            # VS Code's process name is "Electron", and `tell application "Electron"`
            # can launch a standalone Electron instance instead of returning to VS Code.
            if bundle_id:
                escaped_bundle_id = bundle_id.replace('"', '""')
                result = subprocess.run(
                    ["osascript", "-e", f'tell application id "{escaped_bundle_id}" to activate'],
                    capture_output=True,
                    text=True,
                    timeout=5,
                )
                if result.returncode == 0:
                    return True
                logger.debug(
                    "Bundle ID activation failed for %s, falling back to name: %s",
                    bundle_id,
                    result.stderr.strip() if result.stderr else "(no stderr)",
                )

            # Fallback to name-based activation
            escaped_app_name = app_name.replace('"', '""')
            result = subprocess.run(
                ["osascript", "-e", f'tell application "{escaped_app_name}" to activate'],
                capture_output=True,
                text=True,
                timeout=5,
            )
            return result.returncode == 0
    except Exception as e:
        logger.debug(f"Failed to focus app {app_name}: {e}")
    return False


def _focus_any_unity_macos() -> bool:
    """Focus any Unity process on macOS (fallback when no project path specified)."""
    try:
        script = '''
tell application "System Events"
    set unityProc to first process whose name is "Unity"
    set frontmost of unityProc to true
end tell
'''
        result = subprocess.run(
            ["osascript", "-e", script],
            capture_output=True,
            text=True,
            timeout=5,
        )
        if result.returncode != 0:
            logger.debug(f"Failed to activate Unity via System Events: {result.stderr}")
            return False
        return True
    except Exception as e:
        logger.debug(f"Failed to focus Unity: {e}")
        return False


def _get_frontmost_app_windows() -> _FrontmostAppInfo | None:
    """Get the title of the frontmost window on Windows."""
    try:
        # PowerShell command to get active window title
        script = '''
Add-Type @"
using System;
using System.Runtime.InteropServices;
public class Win32 {
    [DllImport("user32.dll")]
    public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")]
    public static extern int GetWindowText(IntPtr hWnd, System.Text.StringBuilder text, int count);
    [DllImport("user32.dll")]
    public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
}
"@
$hwnd = [Win32]::GetForegroundWindow()
$sb = New-Object System.Text.StringBuilder 256
[Win32]::GetWindowText($hwnd, $sb, 256)
$pidValue = 0
[Win32]::GetWindowThreadProcessId($hwnd, [ref]$pidValue) | Out-Null
$sb.ToString() + "|" + $pidValue
'''
        result = subprocess.run(
            ["powershell", "-Command", script],
            capture_output=True,
            text=True,
            timeout=5,
        )
        if result.returncode == 0:
            output = result.stdout.strip()
            title, _, raw_pid = output.rpartition("|")
            try:
                process_id = int(raw_pid)
            except ValueError:
                process_id = None
                title = output
            return _FrontmostAppInfo(name=title, pid=process_id)
    except Exception as e:
        logger.debug(f"Failed to get frontmost window: {e}")
    return None


def _focus_app_windows(window_title: str, process_id: int | None = None) -> bool:
    """Focus a window by title on Windows. For Unity, uses Unity Editor pattern."""
    try:
        if process_id is not None:
            script = f'''
Add-Type @"
using System;
using System.Runtime.InteropServices;
public class Win32 {{
    [DllImport("user32.dll")]
    public static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")]
    public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
}}
"@
$target = Get-Process -Id {process_id} -ErrorAction SilentlyContinue
if ($target -and $target.MainWindowHandle -ne 0) {{
    [Win32]::ShowWindow($target.MainWindowHandle, 9)
    [Win32]::SetForegroundWindow($target.MainWindowHandle)
}} else {{
    exit 3
}}
'''
        # Legacy non-Unity restore remains title-based.
        else:
            # Try to find window by title - escape special PowerShell characters
            safe_title = window_title.replace("'", "''").replace("`", "``")
            script = f'''
Add-Type @"
using System;
using System.Runtime.InteropServices;
public class Win32 {{
    [DllImport("user32.dll")]
    public static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")]
    public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
}}
"@
$proc = Get-Process | Where-Object {{$_.MainWindowTitle -eq '{safe_title}'}} | Select-Object -First 1
if ($proc) {{
    [Win32]::ShowWindow($proc.MainWindowHandle, 9)
    [Win32]::SetForegroundWindow($proc.MainWindowHandle)
}}
'''
        result = subprocess.run(
            ["powershell", "-Command", script],
            capture_output=True,
            text=True,
            timeout=5,
        )
        return result.returncode == 0
    except Exception as e:
        logger.debug(f"Failed to focus window {window_title}: {e}")
    return False


def _get_frontmost_app_linux() -> _FrontmostAppInfo | None:
    """Get the window ID of the frontmost window on Linux."""
    try:
        result = subprocess.run(
            ["xdotool", "getactivewindow"],
            capture_output=True,
            text=True,
            timeout=5,
        )
        if result.returncode == 0:
            window_id = result.stdout.strip()
            pid_result = subprocess.run(
                ["xdotool", "getwindowpid", window_id],
                capture_output=True,
                text=True,
                timeout=5,
            )
            try:
                process_id = int(pid_result.stdout.strip()) if pid_result.returncode == 0 else None
            except ValueError:
                process_id = None
            return _FrontmostAppInfo(name=window_id, pid=process_id)
    except Exception as e:
        logger.debug(f"Failed to get active window: {e}")
    return None


def _find_unity_window_linux() -> str | None:
    """Find the first Unity Editor window ID."""
    try:
        result = subprocess.run(
            ["xdotool", "search", "--name", "Unity"],
            capture_output=True,
            text=True,
            timeout=5,
        )
        if result.returncode == 0 and result.stdout.strip():
            return result.stdout.strip().split("\n")[0]
    except Exception as e:
        logger.debug(f"Failed to find Unity window: {e}")
    return None


def _find_window_for_pid_linux(process_id: int) -> str | None:
    try:
        result = subprocess.run(
            ["xdotool", "search", "--onlyvisible", "--pid", str(process_id)],
            capture_output=True,
            text=True,
            timeout=5,
        )
        if result.returncode == 0 and result.stdout.strip():
            return result.stdout.strip().splitlines()[0]
    except Exception as exc:
        logger.debug("Failed to find window for PID %s: %s", process_id, exc)
    return None


def _focus_app_linux(window_id: str) -> bool:
    """Focus a window by ID on Linux, or Unity by name."""
    try:
        if window_id == "Unity":
            window_id = _find_unity_window_linux()
            if window_id is None:
                return False

        result = subprocess.run(
            ["xdotool", "windowactivate", window_id],
            capture_output=True,
            text=True,
            timeout=5,
        )
        return result.returncode == 0
    except Exception as e:
        logger.debug(f"Failed to focus window {window_id}: {e}")
    return False


def _focus_exact_process(process_id: int, platform_name: str | None = None) -> bool:
    system = platform_name or platform.system()
    if system == "Darwin":
        return _focus_app_macos("Unity", unity_pid=process_id)
    if system == "Windows":
        return _focus_app_windows("Unity", process_id=process_id)
    if system == "Linux":
        window_id = _find_window_for_pid_linux(process_id)
        return window_id is not None and _focus_app_linux(window_id)
    return False


def _restore_original_focus(app_info: _FrontmostAppInfo, platform_name: str) -> bool:
    if platform_name == "Darwin":
        return _focus_app_macos(
            app_info.name,
            bundle_id=app_info.bundle_id,
            unity_pid=app_info.pid if app_info.name == "Unity" else None,
        )
    if platform_name == "Windows":
        return _focus_app_windows(app_info.name, process_id=app_info.pid)
    if platform_name == "Linux":
        return _focus_app_linux(app_info.name)
    return False


def _get_frontmost_app() -> _FrontmostAppInfo | None:
    """Get the frontmost application/window (platform-specific)."""
    system = platform.system()
    if system == "Darwin":
        return _get_frontmost_app_macos()
    elif system == "Windows":
        return _get_frontmost_app_windows()
    elif system == "Linux":
        return _get_frontmost_app_linux()
    return None


def _focus_app(
    app_info: _FrontmostAppInfo | str,
    unity_project_path: str | None = None,
    *,
    unity_pid: int | None = None,
    linux_window_id: str | None = None,
) -> bool:
    """Focus an application/window (platform-specific).

    Args:
        app_info: Application info (name + optional bundle_id) or plain name string
        unity_project_path: For Unity apps on macOS, the full project root path for
            multi-instance support
    """
    if isinstance(app_info, str):
        app_info = _FrontmostAppInfo(name=app_info)

    system = platform.system()
    if system == "Darwin":
        return _focus_app_macos(
            app_info.name,
            unity_project_path,
            app_info.bundle_id,
            unity_pid,
        )
    elif system == "Windows":
        return _focus_app_windows(app_info.name)
    elif system == "Linux":
        target = linux_window_id if app_info.name == "Unity" and linux_window_id else app_info.name
        return _focus_app_linux(target)
    return False


def _is_unity_editor_frontmost(app_info: _FrontmostAppInfo) -> bool:
    """Return whether the frontmost process/window belongs to Unity Editor."""
    normalized_name = app_info.name.strip().lower()
    if "unity hub" in normalized_name:
        return False
    if platform.system() == "Darwin":
        return normalized_name == "unity"
    return "unity" in normalized_name


def _is_target_unity_frontmost(
    app_info: _FrontmostAppInfo,
    *,
    unity_pid: int | None = None,
    linux_window_id: str | None = None,
) -> bool:
    if unity_pid is not None:
        return app_info.pid == unity_pid
    if linux_window_id is not None:
        return app_info.name == linux_window_id
    return _is_unity_editor_frontmost(app_info)


async def nudge_unity_focus(
    target: FocusTarget | None = None,
    focus_duration_s: float | None = None,
    force: bool = False,
    unity_project_path: str | None = None,
    backoff_state: FocusNudgeState | None = None,
    return_result: bool = False,
) -> bool | FocusNudgeResult:
    """
    Temporarily focus Unity to allow it to process, then return focus.

    Uses exponential backoff for both interval and duration:
    - Interval: 1s, 2s, 4s, 8s, 10s (time between nudges)
    - Duration: 3s, 5s, 8s, 12s (how long Unity stays focused)
    Resets on progress.

    Args:
        focus_duration_s: How long to keep Unity focused (seconds).
            If None, uses exponential backoff (3s/5s/8s/12s based on consecutive nudges).
            Can be overridden with UNITY_MCP_NUDGE_DURATION_S env var.
        force: If True, ignore the minimum interval between nudges
        unity_project_path: Deprecated compatibility argument. It is ignored;
            ``target`` is always required and no name-only fallback is allowed.
        backoff_state: Optional job-scoped backoff state. Callers managing multiple
            TestRunner jobs should provide a distinct state for each job.
        return_result: Return a structured outcome instead of the legacy boolean.

    Returns:
        By default, True only when focus and foreground restoration completed.
        With ``return_result=True``, returns the precise structured outcome.
    """
    validation_error = validate_focus_target(target)
    if validation_error is not None:
        logger.warning("Refusing Unity focus nudge: %s", validation_error)
        return _return_focus_nudge_result(
            FocusNudgeResult("not_performed", validation_error),
            return_result,
        )
    assert target is not None
    project_root = canonical_project_root(target.project_root)

    if focus_duration_s is None:
        # Use exponential backoff for focus duration
        focus_duration_s = _get_current_focus_duration(backoff_state)
    if focus_duration_s <= 0:
        focus_duration_s = _DEFAULT_FOCUS_DURATION_S
    global _last_nudge_time, _consecutive_nudges

    if not _is_available():
        logger.debug("Focus nudging not available on this platform")
        return _return_focus_nudge_result(
            FocusNudgeResult("not_performed", "focus nudge is unavailable on this platform"),
            return_result,
        )

    # Rate limit nudges using exponential backoff
    now = time.monotonic()
    current_interval = _get_current_nudge_interval(backoff_state)
    last_attempt_time = (
        max(backoff_state.last_nudge_time, backoff_state.last_attempt_time)
        if backoff_state is not None
        else _last_nudge_time
    )
    consecutive_attempts = (
        backoff_state.consecutive_attempts
        if backoff_state is not None
        else _consecutive_nudges
    )
    if not force and (now - last_attempt_time) < current_interval:
        logger.debug(f"Skipping nudge - too soon since last nudge (interval: {current_interval:.1f}s)")
        return _return_focus_nudge_result(
            FocusNudgeResult("not_performed", "focus nudge is rate limited"),
            return_result,
        )

    if backoff_state is not None:
        backoff_state.last_attempt_time = now
        backoff_state.consecutive_attempts += 1

    system = platform.system()
    if system not in {"Darwin", "Windows", "Linux"}:
        return _return_focus_nudge_result(
            FocusNudgeResult("not_performed", f"focus nudge is unsupported on {system}"),
            return_result,
        )
    if not await asyncio.to_thread(
        _process_matches_target,
        target.process_id,
        project_root,
    ):
        logger.warning(
            "Unity PID %s no longer belongs to project %s",
            target.process_id,
            project_root,
        )
        return _return_focus_nudge_result(
            FocusNudgeResult("not_performed", "Unity process identity no longer matches"),
            return_result,
        )

    # Get current frontmost app
    original_app = await asyncio.to_thread(_get_frontmost_app)
    if original_app is None:
        logger.debug("Could not determine frontmost app")
        return _return_focus_nudge_result(
            FocusNudgeResult("not_performed", "current foreground app is unavailable"),
            return_result,
        )

    # Check if Unity is already focused (no nudge needed)
    if original_app.pid == target.process_id:
        logger.debug("Unity already focused, no nudge needed")
        return _return_focus_nudge_result(
            FocusNudgeResult("not_performed", "target Unity process is already focused"),
            return_result,
        )

    logger.info(
        "Nudging exact Unity session=%s pid=%s project=%s "
        "(interval=%.1fs consecutive=%s duration=%.1fs; return=%s)",
        target.session_id,
        target.process_id,
        project_root,
        current_interval,
        consecutive_attempts,
        focus_duration_s,
        original_app,
    )

    # Close the PID-reuse/project-switch race immediately before OS mutation.
    if not await asyncio.to_thread(
        _process_matches_target,
        target.process_id,
        project_root,
    ):
        logger.warning("Unity focus target changed immediately before activation")
        return _return_focus_nudge_result(
            FocusNudgeResult("not_performed", "Unity process identity changed before focus"),
            return_result,
        )
    if not await asyncio.to_thread(
        _focus_exact_process,
        target.process_id,
        system,
    ):
        logger.warning("Failed to focus Unity PID %s", target.process_id)
        return _return_focus_nudge_result(
            FocusNudgeResult("focus_failed", "Unity activation failed"),
            return_result,
        )

    result = FocusNudgeResult("focus_failed", "Unity activation could not be verified")
    try:
        # macOS activation is asynchronous, so verify the frontmost process after
        # its window-switch animation before counting or waiting on the nudge.
        await asyncio.sleep(0.5)
        current_app = await asyncio.to_thread(_get_frontmost_app)
        if current_app is None or current_app.pid != target.process_id:
            logger.warning(
                "Unity activation didn't complete - current app is %s",
                current_app or "unknown",
            )
        else:
            if backoff_state is not None:
                backoff_state.last_nudge_time = time.monotonic()
                backoff_state.consecutive_nudges += 1
            else:
                _last_nudge_time = time.monotonic()
                _consecutive_nudges += 1

            await asyncio.sleep(focus_duration_s)
            result = FocusNudgeResult("completed")
    finally:
        # Cancellation and failed verification must not strand Unity in front.
        if original_app.pid != target.process_id:
            restored = await asyncio.to_thread(
                _restore_original_focus,
                original_app,
                system,
            )
            if restored:
                logger.info(
                    "Returned focus to %s after Unity focus attempt",
                    original_app,
                )
            else:
                logger.warning(f"Failed to return focus to {original_app}")
                result = FocusNudgeResult(
                    "restore_failed",
                    "original foreground app could not be restored",
                )

    return _return_focus_nudge_result(result, return_result)


def should_nudge(
    status: str,
    editor_is_focused: bool,
    last_update_unix_ms: int | None,
    current_time_ms: int | None = None,
    stall_threshold_ms: int = 3_000,
) -> bool:
    """
    Determine if we should nudge Unity based on test job state.

    Works with exponential backoff in nudge_unity_focus():
    - First nudge happens after 3s of no progress
    - Subsequent nudges use exponential backoff (1s, 2s, 4s, 8s, 10s max)
    - Backoff resets when progress is detected (call reset_nudge_backoff())

    Args:
        status: Job status ("running", "succeeded", "failed")
        editor_is_focused: Whether Unity reports being focused
        last_update_unix_ms: Last time the job was updated (Unix ms)
        current_time_ms: Current time (Unix ms), or None to use current time
        stall_threshold_ms: How long without updates before considering it stalled
            (default 3s for quick stall detection with exponential backoff)

    Returns:
        True if conditions suggest a nudge would help
    """
    # Only nudge running jobs
    if status != "running":
        return False

    # Only nudge unfocused Unity
    if editor_is_focused:
        return False

    # Check if job appears stalled
    if last_update_unix_ms is None:
        return True  # No updates yet, might be stuck at start

    if current_time_ms is None:
        current_time_ms = int(time.time() * 1000)

    time_since_update_ms = current_time_ms - last_update_unix_ms
    return time_since_update_ms > stall_threshold_ms
