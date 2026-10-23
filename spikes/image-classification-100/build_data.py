"""Validate the independently recorded results and build local-browser data.js."""

import json
from pathlib import Path


ROOT = Path(__file__).resolve().parent
REQUIRED = {
    "id", "format", "compound", "animal_count", "same_kind", "people",
    "artifacts", "background", "head", "confidence", "note",
}
CHOICES = {
    "format": {"photo", "graphic", "mixed", "uncertain"},
    "same_kind": {"yes", "no", "unknown", "na"},
    "people": {"yes", "no", "uncertain"},
    "artifacts": {"yes", "no", "uncertain"},
    "background": {"natural", "cleared", "studio", "graphic", "uncertain"},
    "confidence": {"high", "medium", "low"},
}


def read_results(model: str, expected: set[str]) -> list[dict]:
    results = []
    for path in sorted((ROOT / "results").glob(f"{model}-*.jsonl")):
        for number, line in enumerate(path.read_text().splitlines(), 1):
            result = json.loads(line)
            where = f"{path.name}:{number}"
            if set(result) != REQUIRED:
                raise ValueError(f"{where}: unexpected fields {set(result) ^ REQUIRED}")
            if not isinstance(result["id"], str):
                raise ValueError(f"{where}: id is not a string")
            for field, allowed in CHOICES.items():
                if result[field] not in allowed:
                    raise ValueError(f"{where}: invalid {field}={result[field]!r}")
            if not isinstance(result["compound"], bool):
                raise ValueError(f"{where}: compound must be a boolean")
            count = result["animal_count"]
            if count is not None and (type(count) is not int or count < 0):
                raise ValueError(f"{where}: invalid animal count")
            head = result["head"]
            if head is not None and (
                not isinstance(head, dict) or set(head) != {"x", "y"}
                or any(not isinstance(head[axis], (int, float)) or
                       not 0 <= head[axis] <= 1 for axis in ("x", "y"))
            ):
                raise ValueError(f"{where}: invalid head point")
            if not isinstance(result["note"], str):
                raise ValueError(f"{where}: note must be a string")
            results.append(result)
    ids = [result["id"] for result in results]
    if len(ids) != len(expected) or set(ids) != expected:
        raise ValueError(f"{model}: expected {len(expected)} unique IDs; got {len(ids)} rows, {len(set(ids))} unique IDs")
    return sorted(results, key=lambda result: result["id"])


def main() -> None:
    samples = json.loads((ROOT / "samples.json").read_text())
    ids = [sample["id"] for sample in samples]
    if len(ids) != 100 or len(set(ids)) != 100:
        raise ValueError("Expected exactly 100 distinct sample images")
    expected = set(ids)
    data = {"samples": samples, "luna": read_results("luna", expected),
            "sol": read_results("sol", expected)}
    (ROOT / "data.js").write_text(
        "window.IMAGE_TEST_DATA = " + json.dumps(data, ensure_ascii=True, separators=(",", ":")) + ";\n"
    )
    print("Validated 100 samples and 100 results per model; wrote data.js")


if __name__ == "__main__":
    main()
