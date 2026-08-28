import sys
from pathlib import Path

_TOOLS_DIR = Path(__file__).resolve().parents[1]
if str(_TOOLS_DIR) not in sys.path:
    sys.path.insert(0, str(_TOOLS_DIR))

from generate_docs_reference import _diff_trees, _write  # noqa: E402


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
