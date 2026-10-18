"""Exploratory taxobox/imagery profile; no rendering or media downloads."""
import argparse
from collections import Counter, defaultdict
import gzip
import json
from pathlib import Path
import re
import time

import mwparserfromhell
from extract_lifeforms import normalize, FAMILIES


def inspect(source, output):
    start = time.monotonic()
    families = {normalize(n): n for n in FAMILIES}
    parameters = Counter()
    populated = Counter()
    by_family = defaultdict(Counter)
    stats = Counter()
    image_fields = Counter()
    image_values = set()
    complex_images = []
    multi_boxes = []
    medical = []
    examples = []
    identifiers = Counter()
    other_templates = Counter()
    gallery_examples = []
    image_pattern = re.compile(r'^(?:image\d*|range[_ ]map\d*|(?:subdivision|synonyms|fossil_range)[_ ]image)$', re.I)
    with gzip.open(source, 'rt', encoding='utf-8') as stream:
        for line in stream:
            page = json.loads(line)
            code = mwparserfromhell.parse(page['wikitext'])
            templates = code.filter_templates()
            boxes = [t for t in templates if normalize(str(t.name)) in families]
            top_boxes = [t for t in code.filter_templates(recursive=False)
                         if normalize(str(t.name)) in families]
            stats['pages'] += 1
            stats['taxobox_invocations'] += len(boxes)
            if len(boxes) > 1:
                stats['pages_with_multiple_taxoboxes'] += 1
                multi_boxes.append({'title': page['title'], 'boxes': [str(t.name).strip() for t in boxes]})
            if not top_boxes:
                stats['pages_with_only_nested_taxoboxes'] += 1
            image_present = False
            identity_present = False
            snapshots = []
            for box in boxes:
                snapshot = {}
                seen = Counter()
                for param in box.params:
                    key = str(param.name).strip()
                    value = str(param.value).strip()
                    parameters[key] += 1
                    seen[key] += 1
                    by_family[families[normalize(str(box.name))]][key] += 1
                    if value:
                        populated[key] += 1
                    if key in ('taxon', 'genus', 'species', 'binomial', 'trinomial', 'name') and value:
                        identifiers[key] += 1
                        if key != 'name':
                            identity_present = True
                    if image_pattern.match(key) and value:
                        image_present = True
                        image_fields[key] += 1
                        image_values.add(value)
                        parsed = mwparserfromhell.parse(value)
                        if parsed.filter_templates() or parsed.filter_wikilinks() or '\n' in value:
                            complex_images.append({'title': page['title'], 'field': key, 'value': value[:500]})
                    if len(snapshot) < 40:
                        snapshot[key] = value[:350]
                if any(n > 1 for n in seen.values()):
                    stats['taxoboxes_with_duplicate_parameter_names'] += 1
                if len(examples) < 20 or page['title'] in ('Rift Valley fever', 'Yellow fever', 'HIV', 'Horse', 'Olive'):
                    snapshots.append({'template': str(box.name).strip(), 'parameters': snapshot})
            if snapshots:
                examples.append({'title': page['title'], 'taxoboxes': snapshots})
            stats['pages_with_populated_taxobox_image_or_map'] += image_present
            stats['pages_with_explicit_scientific_identity_parameter'] += identity_present
            names = {normalize(str(t.name)) for t in templates}
            other_templates.update(n for n in names if n.startswith('infobox'))
            medical_names = sorted(n for n in names if re.search(r'^infobox.*(?:medical|disease|drug)', n))
            if medical_names:
                medical.append({'title': page['title'], 'templates': medical_names})
            tags = code.filter_tags()
            if any(str(tag.tag).strip().casefold() == 'gallery' for tag in tags):
                stats['pages_with_galleries'] += 1
                if len(gallery_examples) < 15:
                    gallery_examples.append(page['title'])
            files = [str(link.title).strip() for link in code.filter_wikilinks()
                     if re.match(r'^\s*(?:File|Image)\s*:', str(link.title), re.I)]
            stats['explicit_file_links'] += len(files)
            stats['pages_with_explicit_file_links'] += bool(files)
            if stats['pages'] % 1000 == 0:
                print(json.dumps({'pages': stats['pages'], 'seconds': round(time.monotonic() - start, 1)}), flush=True)
    result = {
        'source': str(source), 'elapsed_seconds': round(time.monotonic() - start, 2),
        'statistics': dict(stats), 'parameter_invocations': dict(parameters.most_common()),
        'nonempty_parameter_invocations': dict(populated.most_common()),
        'parameters_by_family': {k: dict(v.most_common()) for k, v in by_family.items()},
        'identity_parameter_invocations': dict(identifiers),
        'nonempty_image_fields': dict(image_fields.most_common()),
        'unique_raw_image_values': len(image_values),
        'complex_image_values': complex_images,
        'multiple_taxobox_pages': multi_boxes,
        'medical_infobox_cooccurrences': medical,
        'other_infobox_page_counts': dict(other_templates.most_common()),
        'gallery_examples': gallery_examples, 'taxobox_examples': examples,
        'limitations': [
            'Exploratory profile of this candidate shard, not a completeness assessment.',
            'Raw image values are not normalized filenames or verified downloads.',
            'File links exclude images introduced only by template expansion or gallery syntax.',
            'Medical infobox cooccurrence is a review signal, not an automatic exclusion rule.',
        ],
    }
    Path(output).write_text(json.dumps(result, indent=2, ensure_ascii=False) + '\n', encoding='utf-8')
    print(json.dumps({'state': 'complete', **result['statistics']}), flush=True)


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('source', type=Path)
    parser.add_argument('--output', type=Path, required=True)
    args = parser.parse_args()
    inspect(args.source, args.output)
