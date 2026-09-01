import sys
from pathlib import Path

_TOOLS_DIR = Path(__file__).resolve().parents[1]
if str(_TOOLS_DIR) not in sys.path:
    sys.path.insert(0, str(_TOOLS_DIR))

from generate_docs_reference import (  # noqa: E402
    EXAMPLES_CLOSE,
    EXAMPLES_OPEN,
    _diff_trees,
    _read_existing_examples,
    _render_type,
    _write,
)


def test_write_uses_lf_line_endings(tmp_path):
    output = tmp_path / "generated.md"

    assert _write(output, "first\nsecond\n") is True

    assert output.read_bytes() == b"first\nsecond\n"


def test_diff_trees_ignores_platform_line_endings(tmp_path):
    committed = tmp_path / "committed"
    generated = tmp_path / "generated"
    committed.mkdir()
    generated.mkdir()
    (committed / "index.md").write_bytes(b"first\r\nsecond\r\n")
    (generated / "index.md").write_bytes(b"first\nsecond\n\n")

    assert _diff_trees(committed, generated) == []


def test_render_pep604_union_with_bare_containers():
    assert _render_type(list | float | None) == "list[Any] | float | None"
    assert _render_type(dict | bool | None) == "dict[Any] | bool | None"


def test_read_existing_examples_does_not_create_blank_line_at_eof(tmp_path):
    page = tmp_path / "tool.md"
    page.write_text(
        f"# Tool\n\n{EXAMPLES_OPEN}\nexample\n{EXAMPLES_CLOSE}\n",
        encoding="utf-8",
    )

    assert _read_existing_examples(page) == (
        f"{EXAMPLES_OPEN}\nexample\n{EXAMPLES_CLOSE}"
    )

    page.write_text(
        f"# Tool\n\n{EXAMPLES_OPEN}\nexample\n{EXAMPLES_CLOSE}\n\n",
        encoding="utf-8",
    )

    assert _read_existing_examples(page) == (
        f"{EXAMPLES_OPEN}\nexample\n{EXAMPLES_CLOSE}\n"
    )
