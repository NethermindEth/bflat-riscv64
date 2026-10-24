#!/usr/bin/env python3
"""Validates bflat manifests against bflat-manifest.schema.json.

With no arguments: every module manifest in src/bflat/modules and every
JSON example in docs/manifest.md. With arguments: the given manifest files
(for example a bflat-* package's bflat-manifest.json).

The schema is the contract for editors and for the bflat-* packages. bflat
itself parses manifests with its own strict reader (BflatManifest.cs); CI
runs both over the same files so that the two stay in agreement.

Copyright (C) 2026 Demerzel Solutions Limited (Nethermind)
"""
import json
import re
import sys
from pathlib import Path

from jsonschema import Draft202012Validator

ROOT = Path(__file__).resolve().parents[3]
SCHEMA = ROOT / "src" / "bflat" / "bflat-manifest.schema.json"


def doc_examples(path):
    """Yields (label, manifest) for each complete JSON example in a page.

    An example that is a fragment ("sources": [...]) is wrapped in a minimal
    manifest. Examples with "..." are outlines, not manifests, and are
    skipped."""
    text = path.read_text()
    for n, block in enumerate(re.findall(r"```json\n(.*?)```", text, re.S), 1):
        if "..." in block:
            continue
        block = block.strip()
        label = f"{path.name} example {n}"
        if block.startswith('{ "symbol"') or block.startswith('{"symbol"'):
            yield label, {"schema": 2, "name": "x", "link": {"wrap": [json.loads(block)]}}
        elif block.startswith("{"):
            yield label, json.loads(block)
        else:
            yield label, json.loads('{"schema": 2, "name": "x", ' + block + "}")


def main(argv):
    schema = json.loads(SCHEMA.read_text())
    Draft202012Validator.check_schema(schema)
    validator = Draft202012Validator(schema)

    if argv:
        items = [(p, json.loads(Path(p).read_text())) for p in argv]
    else:
        modules = sorted((ROOT / "src" / "bflat" / "modules").glob("*/bflat-manifest.json"))
        if not modules:
            print("no module manifests found", file=sys.stderr)
            return 1
        items = [(str(p.relative_to(ROOT)), json.loads(p.read_text())) for p in modules]
        items += list(doc_examples(ROOT / "docs" / "manifest.md"))

    failed = 0
    for label, manifest in items:
        errors = sorted(validator.iter_errors(manifest), key=lambda e: list(e.path))
        for e in errors:
            where = "$" + "".join(f"[{p}]" if isinstance(p, int) else f".{p}" for p in e.path)
            print(f"{label}: {where}: {e.message}", file=sys.stderr)
        failed += bool(errors)
    print(f"{len(items) - failed} of {len(items)} manifests match the schema")
    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
