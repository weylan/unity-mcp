import pytest

from transport.plugin_hub import PluginHub
from services.registry import mcp_for_unity_tool
from services.registry import tool_registry as registry_mod


class FakeMcp:
    def __init__(self):
        self._transforms = ["startup"]
        self.calls = []

    def enable(self, *, tags, components):
        self.calls.append(("enable", next(iter(tags)), tuple(sorted(components))))
        self._transforms.append(("enable", tuple(sorted(tags))))

    def disable(self, *, tags, components):
        self.calls.append(("disable", next(iter(tags)), tuple(sorted(components))))
        self._transforms.append(("disable", tuple(sorted(tags))))


@pytest.fixture(autouse=True)
def isolated_tool_registry(monkeypatch):
    PluginHub._mcp = None
    PluginHub._unity_transform_start = None
    monkeypatch.setattr(registry_mod, "_tool_registry", [])

    @mcp_for_unity_tool(name="core_enabled_tool", group="core")
    async def _core_enabled_tool():
        return None

    @mcp_for_unity_tool(name="core_disabled_tool", group="core")
    async def _core_disabled_tool():
        return None

    yield

    PluginHub._mcp = None
    PluginHub._unity_transform_start = None


def test_per_tool_visibility_disables_one_core_tool_while_enabling_another():
    fake = FakeMcp()
    PluginHub._mcp = fake

    PluginHub._sync_server_tool_visibility([
        {"name": "core_enabled_tool", "group": "core", "enabled": True, "source": "project-config"},
        {"name": "core_disabled_tool", "group": "core", "enabled": False, "source": "project-config"},
    ])

    assert ("enable", "group:core", ("tool",)) in fake.calls
    assert ("enable", "tool:core_enabled_tool", ("tool",)) in fake.calls
    assert ("disable", "tool:core_disabled_tool", ("tool",)) in fake.calls
    core_enable_index = fake.calls.index(("enable", "group:core", ("tool",)))
    tool_disable_index = fake.calls.index(("disable", "tool:core_disabled_tool", ("tool",)))
    assert tool_disable_index > core_enable_index


def test_legacy_tool_list_without_enabled_field_uses_group_fallback():
    fake = FakeMcp()
    PluginHub._mcp = fake

    PluginHub._sync_server_tool_visibility([
        {"name": "core_enabled_tool", "group": "core"},
    ])

    assert ("enable", "group:core", ("tool",)) in fake.calls
    assert not any(tag.startswith("tool:") for _, tag, _ in fake.calls)


def test_repeated_sync_replaces_prior_unity_transforms():
    fake = FakeMcp()
    PluginHub._mcp = fake

    PluginHub._sync_server_tool_visibility([
        {"name": "core_disabled_tool", "enabled": False},
    ])
    first_start = PluginHub._unity_transform_start
    assert first_start == 1
    assert len(fake._transforms) > first_start

    PluginHub._sync_server_tool_visibility([
        {"name": "core_disabled_tool", "enabled": True},
    ])

    assert PluginHub._unity_transform_start == first_start
    assert ("enable", "tool:core_disabled_tool", ("tool",)) in fake.calls
