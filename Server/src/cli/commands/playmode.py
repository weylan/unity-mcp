"""Editor Play Mode testing commands."""

from __future__ import annotations

import click

from cli.utils.config import get_config
from cli.utils.connection import handle_unity_errors, run_command
from cli.utils.output import format_output
from cli.utils.parsers import parse_json_dict_or_exit, parse_json_list_or_exit


@click.group()
def playmode():
    """Inspect and automate a game running in Unity Editor Play Mode."""


@playmode.command("state")
@click.option("--player", default=None, help="Player GameObject name, path, or instance ID.")
@click.option("--include-ui/--no-include-ui", default=True)
@click.option("--ui-limit", type=click.IntRange(0, 100), default=20)
@handle_unity_errors
def state(player, include_ui, ui_limit):
    """Read the compact Play Mode state snapshot."""
    config = get_config()
    params = {"includeUI": include_ui, "uiLimit": ui_limit}
    if player:
        params["player"] = player
    result = run_command("get_playmode_state", params, config)
    click.echo(format_output(result, config.format))


@playmode.command("ui")
@click.option("--framework", type=click.Choice(["all", "ugui", "uitoolkit"]), default="all")
@click.option("--page-size", type=click.IntRange(1, 100), default=50)
@click.option("--cursor", type=click.IntRange(min=0), default=0)
@handle_unity_errors
def ui(framework, page_size, cursor):
    """Read a page of visible runtime UI elements."""
    config = get_config()
    result = run_command(
        "get_playmode_ui",
        {"framework": framework, "pageSize": page_size, "cursor": cursor},
        config,
    )
    click.echo(format_output(result, config.format))


@playmode.command("input")
@click.argument("action")
@click.option("--params", "params_json", default="{}", help="Additional input parameters as JSON.")
@click.option("--editor-lock-token", default=None, help="Shared Editor operation lock token.")
@handle_unity_errors
def input_command(action, params_json, editor_lock_token):
    """Inject one input action into the running game."""
    config = get_config()
    params = parse_json_dict_or_exit(params_json, "input params")
    params["action"] = action
    if editor_lock_token:
        params["editor_lock_token"] = editor_lock_token
    result = run_command("simulate_input", params, config)
    click.echo(format_output(result, config.format))


@playmode.command("wait")
@click.argument("condition_json")
@click.option("--timeout", "timeout_seconds", type=click.FloatRange(min=0.01, max=300), default=30.0)
@click.option("--stable-frames", type=click.IntRange(1, 300), default=1)
@click.option("--editor-lock-token", default=None, help="Shared Editor operation lock token.")
@handle_unity_errors
def wait(condition_json, timeout_seconds, stable_frames, editor_lock_token):
    """Start a condition wait and return its job ID."""
    config = get_config()
    condition = parse_json_dict_or_exit(condition_json, "wait condition")
    params = {
        "action": "wait",
        "condition": condition,
        "timeoutSeconds": timeout_seconds,
        "stableForFrames": stable_frames,
    }
    if editor_lock_token:
        params["editor_lock_token"] = editor_lock_token
    result = run_command("manage_playmode_test", params, config)
    click.echo(format_output(result, config.format))


@playmode.command("sequence")
@click.argument("steps_json")
@click.option("--timeout", "timeout_seconds", type=click.FloatRange(min=0.01, max=300), default=30.0)
@click.option("--editor-lock-token", default=None, help="Shared Editor operation lock token.")
@handle_unity_errors
def sequence(steps_json, timeout_seconds, editor_lock_token):
    """Start a deterministic action sequence from a JSON step array."""
    config = get_config()
    steps = parse_json_list_or_exit(steps_json, "sequence steps")
    params = {"action": "run_sequence", "steps": steps, "timeoutSeconds": timeout_seconds}
    if editor_lock_token:
        params["editor_lock_token"] = editor_lock_token
    result = run_command("manage_playmode_test", params, config)
    click.echo(format_output(result, config.format))


@playmode.command("status")
@click.argument("job_id")
@handle_unity_errors
def status(job_id):
    """Read Play Mode test job status."""
    config = get_config()
    result = run_command(
        "manage_playmode_test", {"action": "status", "jobId": job_id}, config
    )
    click.echo(format_output(result, config.format))


@playmode.command("cancel")
@click.argument("job_id")
@handle_unity_errors
def cancel(job_id):
    """Cancel a running Play Mode test job and release simulated input."""
    config = get_config()
    result = run_command(
        "manage_playmode_test", {"action": "cancel", "jobId": job_id}, config
    )
    click.echo(format_output(result, config.format))
