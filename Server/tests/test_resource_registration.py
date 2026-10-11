import os
import subprocess
import sys
import textwrap
from pathlib import Path

import pytest


@pytest.fixture
def run_registration_script():
    def run(script):
        completed = subprocess.run(
            [sys.executable, "-c", textwrap.dedent(script)],
            cwd=Path(__file__).resolve().parents[1],
            env={**os.environ, "DISABLE_TELEMETRY": "true"},
            capture_output=True,
            text=True,
            timeout=30,
            check=False,
        )
        assert completed.returncode == 0, completed.stdout + completed.stderr

    return run


@pytest.mark.parametrize("project_scoped_tools", [True, False])
def test_all_resources_register_with_fastmcp_3(
    run_registration_script, project_scoped_tools
):
    run_registration_script(f"""
        import asyncio
        from fastmcp import FastMCP
        from services.resources import register_all_resources
        from services.registry import get_registered_resources

        async def check():
            server = FastMCP('resource-registration-test')
            register_all_resources(server, project_scoped_tools={project_scoped_tools!r})
            resources = {{resource.name: str(resource.uri)
                         for resource in await server.list_resources()}}
            templates = {{template.name: template.uri_template
                         for template in await server.list_resource_templates()}}
            expected = {{info['name']: info['uri'] for info in get_registered_resources()
                        if {project_scoped_tools!r} or info['name'] != 'custom_tools'}}
            assert resources and templates
            assert len(resources) + len(templates) == len(expected)
            assert resources | templates == expected
            assert templates['playmode_state'] == (
                'mcpforunity://playmode/state{{?include_ui,ui_limit,player}}'
            )
            assert templates['playmode_ui'] == (
                'mcpforunity://playmode/ui{{?page_size,cursor,framework}}'
            )

        asyncio.run(check())
    """)


@pytest.mark.parametrize("default", ["", " = 50"], ids=["required", "optional"])
def test_fixed_uri_rejects_unaddressable_parameters(run_registration_script, default):
    run_registration_script(f"""
        from fastmcp import Context, FastMCP
        from services.registry import mcp_for_unity_resource
        from services.resources import register_all_resources

        @mcp_for_unity_resource(uri='review://invalid', name='invalid')
        async def invalid(ctx: Context, page_size: int{default}):
            return {{'page_size': page_size}}

        try:
            register_all_resources(FastMCP('invalid-resource-test'))
        except ValueError as error:
            assert 'URI template must contain at least one parameter' in str(error)
        else:
            raise AssertionError('A fixed URI cannot expose function parameters')
    """)


def test_resource_reads_and_repeated_registration(run_registration_script):
    run_registration_script("""
        import asyncio
        import json
        from unittest.mock import patch
        from fastmcp import Client, Context, FastMCP
        from models import MCPResponse
        from services.registry import mcp_for_unity_resource
        from services.resources import register_all_resources

        @mcp_for_unity_resource(
            uri='review://fixed', name='fixed', mime_type='application/json'
        )
        async def fixed(ctx: Context):
            assert isinstance(ctx, Context)
            return MCPResponse(success=True, data={'value': 42})

        @mcp_for_unity_resource(
            uri='review://items/{item_id}{?page_size}', name='items',
            mime_type='application/json',
        )
        async def items(ctx: Context, item_id: str, page_size: int = 50):
            assert isinstance(ctx, Context)
            return {'item_id': item_id, 'page_size': page_size}

        async def check():
            first, second = FastMCP('first'), FastMCP('second')
            register_all_resources(first)
            register_all_resources(second)
            for server in (first, second):
                async with Client(server) as client:
                    with patch('core.telemetry_decorator.record_resource_usage') as record:
                        contents = await client.read_resource('review://fixed')
                        assert contents[0].mimeType == 'application/json'
                        assert json.loads(contents[0].text)['data'] == {'value': 42}
                        record.assert_called_once()
                        assert record.call_args.args[:2] == ('fixed', True)

                    for uri, expected_size in (
                        ('review://items/abc', 50),
                        ('review://items/abc?page_size=7', 7),
                    ):
                        with patch('core.telemetry_decorator.record_resource_usage') as record:
                            contents = await client.read_resource(uri)
                            assert json.loads(contents[0].text) == {
                                'item_id': 'abc', 'page_size': expected_size,
                            }
                            record.assert_called_once()
                            assert record.call_args.args[:2] == ('items', True)

        asyncio.run(check())
    """)
