"""Exploratory field/context records, deliberately not a production extractor."""
import gzip
import json
from collections import Counter, defaultdict
from pathlib import Path
import re
import time

import mwparserfromhell as mw
from extract_lifeforms import normalize, FAMILIES

BASE = Path('results/enwiki-2026-10-01-p10p1113635')
IDENTITY = ('taxon', 'genus', 'species', 'subspecies', 'varietas', 'variety', 'binomial', 'trinomial', 'parent', 'name')


def clean(value):
    code = mw.parse(value)
    for comment in code.filter_comments():
        code.remove(comment)
    for tag in list(code.filter_tags()):
        if str(tag.tag).casefold() == 'ref':
            code.remove(tag)
    unresolved = bool(code.filter_templates() or code.filter_arguments())
    return re.sub(r'\s+', ' ', code.strip_code()).strip(), unresolved


def identity(family, params, title):
    values = {}
    unresolved = []
    for key in IDENTITY:
        if key in params:
            values[key], complex_value = clean(params[key])
            if complex_value:
                unresolved.append(key)
    # These are conservative candidate rules; title fallback is separately marked.
    name, method = None, 'unresolved'
    used = []
    if values.get('taxon'):
        name, method = values['taxon'], 'explicit_taxon'
        used = ['taxon']
    elif family == 'Speciesbox' and values.get('genus') and values.get('species'):
        name, method = values['genus'] + ' ' + values['species'], 'genus_species'
        used = ['genus', 'species']
    elif family in ('Subspeciesbox', 'Infraspeciesbox') and all(values.get(k) for k in ('genus', 'species', 'subspecies')):
        marker = ' subsp. ' if family == 'Infraspeciesbox' else ' '
        name = values['genus'] + ' ' + values['species'] + marker + values['subspecies']
        method = 'genus_species_subspecies'
        used = ['genus', 'species', 'subspecies']
    elif family == 'Infraspeciesbox' and all(values.get(k) for k in ('genus', 'species', 'varietas')):
        name = values['genus'] + ' ' + values['species'] + ' var. ' + values['varietas']
        method = 'genus_species_variety'
        used = ['genus', 'species', 'varietas']
    elif family == 'Virusbox' and values.get('species'):
        name, method = values['species'], 'virus_species'
        used = ['species']
    elif values.get('trinomial') or values.get('binomial'):
        name, method = values.get('trinomial') or values['binomial'], 'legacy_scientific_name'
        used = ['trinomial' if values.get('trinomial') else 'binomial']
    elif family == 'Automatic taxobox':
        name, method = title, 'title_fallback_needs_validation'
    if set(used) & set(unresolved):
        name, method = None, 'unresolved_markup'
    return dict(components=values, proposed_name=name, method=method,
                unresolved_fields=unresolved,
                needs_taxonomy_name_resolution=bool(name and '(' in name),
                contains_hybrid_marker=bool(name and '×' in name))


def main():
    families = {normalize(n): n for n in FAMILIES}
    stats = Counter()
    methods = Counter()
    by_family = defaultdict(Counter)
    unusual = defaultdict(list)
    section_counts = Counter()
    identity_examples = defaultdict(list)
    scalar_counts = defaultdict(Counter)
    scalar_complex = defaultdict(list)
    param_complex = Counter()
    started = time.monotonic()
    with gzip.open(BASE / 'candidates.ndjson.gz', 'rt') as source, \
         gzip.open(BASE / 'taxobox-spike.ndjson.gz', 'wt', compresslevel=3) as target:
        for line in source:
            page = json.loads(line)
            text = page['wikitext']
            code = mw.parse(text)
            # Locate real top-level headings, avoiding apparent headings in comments/nowiki.
            headings = []
            offset = 0
            for node in code.nodes:
                if isinstance(node, mw.nodes.Heading):
                    headings.append((offset, node.level, str(node.title).strip()))
                offset += len(str(node))
            top = {id(t) for t in code.filter_templates(recursive=False)}
            templates = code.filter_templates()
            boxes = [t for t in templates if normalize(str(t.name)) in families]
            other_infoboxes = sorted({str(t.name).strip() for t in templates
                                     if normalize(str(t.name)).startswith('infobox')})
            cursor = 0
            for number, box in enumerate(boxes):
                raw = str(box)
                start = text.find(raw, cursor)
                if start < 0:
                    raise ValueError('Unable to locate template')
                cursor = start + 1
                section = []
                for position, level, name in headings:
                    if position >= start:
                        break
                    while section and section[-1][0] >= level:
                        section.pop()
                    section.append((level, name))
                params = {str(p.name).strip(): str(p.value) for p in box.params}
                family = families[normalize(str(box.name))]
                ident = identity(family, params, page['title'])
                record = dict(page_id=page['page_id'], title=page['title'],
                              revision_id=page['revision_id'], taxobox_index=number,
                              family=family, start=start, end=start + len(raw),
                              section=[s[1] for s in section], is_top_level=id(box) in top,
                              raw_parameters=params, identity=ident, other_infoboxes=other_infoboxes)
                target.write(json.dumps(record, ensure_ascii=False) + '\n')
                stats['boxes'] += 1
                stats['lead_boxes' if not section else 'section_boxes'] += 1
                if id(box) not in top:
                    stats['nested_boxes'] += 1
                    unusual['nested'].append(record)
                if section:
                    section_counts[' / '.join(s[1] for s in section)] += 1
                    unusual['section_boxes'].append(record)
                methods[ident['method']] += 1
                by_family[family][ident['method']] += 1
                if len(identity_examples[ident['method']]) < 12:
                    identity_examples[ident['method']].append(dict(title=page['title'], family=family, **ident))
                if ident['method'] in ('unresolved', 'unresolved_markup', 'title_fallback_needs_validation'):
                    unusual['identity_review'].append(record)
                for key, value in params.items():
                    if mw.parse(value).filter_templates():
                        param_complex[key] += 1
                for key in ('status', 'status_system', 'extinct', 'display_parents'):
                    if key in params:
                        value, complex_value = clean(params[key])
                        if value:
                            scalar_counts[key][value] += 1
                        if complex_value:
                            scalar_complex[key].append(dict(title=page['title'], raw=params[key]))
            stats['articles'] += 1
            if stats['articles'] % 1000 == 0:
                print(json.dumps(dict(pages=stats['articles'], seconds=round(time.monotonic()-started, 1))), flush=True)
    # Keep the summary readable; full raw parameters are in the compressed records.
    for records in unusual.values():
        for r in records:
            r.pop('raw_parameters')
    result = dict(statistics=dict(stats), identity_methods=dict(methods),
                  identity_by_family={k: dict(v) for k, v in by_family.items()},
                  identity_examples=dict(identity_examples), review_cases=dict(unusual),
                  section_frequencies=dict(section_counts.most_common()),
                  parameters_containing_templates=dict(param_complex.most_common()),
                  scalar_values={k: dict(v.most_common()) for k, v in scalar_counts.items()},
                  complex_scalar_values=dict(scalar_complex),
                  elapsed_seconds=round(time.monotonic()-started, 2),
                  caveat='Proposed names are heuristic, not validated taxonomy. Original raw values remain authoritative.')
    (BASE / 'taxobox-spike-report.json').write_text(json.dumps(result, indent=2, ensure_ascii=False) + '\n')
    print(json.dumps(dict(state='complete', **stats)), flush=True)


if __name__ == '__main__':
    main()
