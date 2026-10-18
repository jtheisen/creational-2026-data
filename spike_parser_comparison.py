"""Compare template parsing modes without changing the original candidate set."""
import gzip
import json
from collections import Counter
from pathlib import Path
import time

import mwparserfromhell as mw
from extract_lifeforms import FAMILIES, normalize

BASE = Path('results/enwiki-2026-10-01-p10p1113635')


def name(template):
    code = mw.parse(str(template.name))
    for comment in code.filter_comments():
        code.remove(comment)
    return normalize(str(code))


def main():
    known = {normalize(n) for n in FAMILIES}
    old = Counter()
    with gzip.open(BASE / 'taxobox-spike.ndjson.gz', 'rt') as stream:
        for line in stream:
            old[json.loads(line)['page_id']] += 1
    changes = []
    extras = []
    stats = Counter()
    start = time.monotonic()
    for filename, selected in [('candidates.ndjson.gz', True), ('selection-audit.ndjson.gz', False)]:
        with gzip.open(BASE / filename, 'rt') as stream:
            for line in stream:
                page = json.loads(line)
                code = mw.parse(page['wikitext'], skip_style_tags=True)
                templates = code.filter_templates()
                names = [name(t) for t in templates]
                boxes = [n for n in names if n in known]
                if selected:
                    stats['selected_pages_checked'] += 1
                    stats['boxes_in_selected_with_style_skipping_and_name_cleanup'] += len(boxes)
                    if len(boxes) != old[page['page_id']]:
                        changes.append(dict(title=page['title'], old_boxes=old[page['page_id']], new_boxes=boxes))
                else:
                    if boxes:
                        category = 'known_taxobox_recovered'
                    elif 'automated taxobox' in names:
                        category = 'additional_template_name'
                    elif 'infobox cultivar' in names:
                        category = 'cultivar_infobox'
                    elif 'taxobox colour' in names:
                        category = 'generic_infobox_with_taxobox_colour'
                    else:
                        category = 'taxonbar_only_review'
                    stats[category] += 1
                    short = next((str(t.params[0].value).strip() for t in templates
                                  if name(t) == 'short description' and t.params), '')
                    extras.append(dict(title=page['title'], category=category,
                                       boxes=boxes, short_description=short))
    result = dict(statistics=dict(stats), changed_selected_pages=changes,
                  extra_candidates=extras, elapsed_seconds=round(time.monotonic()-start, 2),
                  caveat='Parser-mode comparison, not a replacement extraction or completeness claim.')
    (BASE / 'parser-comparison.json').write_text(json.dumps(result, indent=2, ensure_ascii=False) + '\n')
    print(json.dumps(result['statistics']), flush=True)


if __name__ == '__main__':
    main()
