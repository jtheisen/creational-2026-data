"""Independent recall probe using taxonbar and taxobox-like template names."""
import gzip
import json
import re
import time
from collections import Counter
from pathlib import Path

import mwparserfromhell as mw
from extract_lifeforms import pages, normalize, FAMILIES

BASE = Path('results/enwiki-2026-10-01-p10p1113635')
SOURCE = Path('data/enwiki-2026-10-01-p10p1113635.xml.bz2')


def main():
    with gzip.open(BASE / 'candidates.ndjson.gz', 'rt') as stream:
        selected = {json.loads(line)['page_id'] for line in stream}
    stats = Counter()
    unknown_names = Counter()
    examples = []
    families = {normalize(n) for n in FAMILIES}
    started = time.monotonic()
    with gzip.open(BASE / 'selection-audit.ndjson.gz', 'wt', compresslevel=3) as output:
        for page in pages(SOURCE):
            stats['pages_scanned'] += 1
            text = page['wikitext']
            if page['namespace'] != 0 or page['is_redirect'] or re.match(r'^\s*#redirect\b', text, re.I):
                continue
            # A broad independent signal, not a claim that all such pages are taxa.
            if re.search(r'\{\{[^{}|\n]{0,100}(?:taxonbar|taxobox|speciesbox|virusbox|taxonavigation)', text, re.I):
                names = {normalize(str(t.name)) for t in mw.parse(text).filter_templates()}
                signals = sorted(n for n in names if n == 'taxonbar' or n == 'taxonavigation'
                                 or 'taxobox' in n or 'speciesbox' in n or 'virusbox' in n)
                if not signals:
                    continue
                stats['pages_with_audit_signal'] += 1
                if 'taxonbar' in names:
                    stats['taxonbar_pages'] += 1
                    stats['selected_taxonbar_pages'] += page['page_id'] in selected
                if page['page_id'] not in selected:
                    stats['additional_review_pages'] += 1
                    unknown_names.update(signals)
                    page['audit_signals'] = signals
                    output.write(json.dumps(page, ensure_ascii=False) + '\n')
                    if len(examples) < 100:
                        examples.append(dict(title=page['title'], signals=signals,
                                             lead=text[:1400]))
                    if names & families:
                        stats['known_family_missed_by_prefilter'] += 1
            if stats['pages_scanned'] % 100000 == 0:
                print(json.dumps(dict(pages=stats['pages_scanned'], seconds=round(time.monotonic()-started, 1))), flush=True)
    result = dict(statistics=dict(stats), extra_template_signals=dict(unknown_names.most_common()),
                  examples=examples, elapsed_seconds=round(time.monotonic()-started, 2),
                  caveat='Taxonbar is an independent review signal, not ground truth or an exhaustive recall measure.')
    (BASE / 'selection-audit-report.json').write_text(json.dumps(result, indent=2, ensure_ascii=False) + '\n')
    print(json.dumps(dict(state='complete', **stats)), flush=True)


if __name__ == '__main__':
    main()
