# Image classification sample

Open [index.html](index.html) in a browser. The table shows 100 thumbnails with
independent Luna and Sol classifications. Blue `L` and red `S` markers are each
model's normalized head point; no marker means that model did not identify a
visible head. Click an image to inspect the markers more closely. Search by
filename or filter rows with disagreements. Images are loaded from the existing
public `wikimedia-extracts` Azure Blob container, so the page needs network
access. The repository does not copy or republish the image binaries.

The sample is reproducible from [samples.json](samples.json): the first 100
`.jpg`, `.jpeg`, `.png`, or `.webp` blob names in the first 160 names returned
lexicographically under `thumbnails/250px/` on 2026-10-23. Because Blob paths
begin with a hash, this is a convenient pseudo-random sample of the thumbnails
that had been imported at that time, not a curated ground-truth set. The two
agents inspected neutral local names `n001` through `n100`; original filenames
were withheld until their results were recorded.

The agents used the same instructions: classify format, compound layout, visible
animal count, whether multiple animals appear to be the same kind, people,
human-made artifacts, background, and a visible head position. A compound image
means separate panels or a montage, not multiple animals in one scene. Shells
alone are not counted as animal bodies. When no head is identifiable, `head` is
null. The model-supplied confidence is subjective and uncalibrated.

This is a **qualitative Codex-agent test**, not an OpenAI API evaluation. It does
not establish API token cost, Batch behavior, throughput, or measured accuracy.
There are no human reference labels yet. Model agreement is useful for finding
examples to inspect but is not a correctness score. The HTML comparison treats
head points as different when they are more than 0.12 apart in normalized
image coordinates or when only one model supplies a point.

`python3 build_data.py` validates the result JSONL files and regenerates
`data.js` for the local HTML page. Each model's results live in separate
25-image files in `results/`.
