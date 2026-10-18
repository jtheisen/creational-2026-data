"""Select taxobox articles from an English Wikimedia current XML export.

Two streaming passes resolve template aliases before selecting articles. This
does not render wikitext or resolve the full displayed taxonomic classification.
"""
import argparse
import bz2
from collections import Counter
import gzip
import hashlib
import json
from contextlib import ExitStack
from pathlib import Path
import re
import time
import urllib.parse
import urllib.request
import xml.etree.ElementTree as ET

import mwparserfromhell

FAMILIES = (
    'Taxobox', 'Automatic taxobox', 'Speciesbox', 'Subspeciesbox',
    'Infraspeciesbox', 'Infraspeciesbox special', 'Hybridbox', 'Ichnobox',
    'Oobox', 'Virusbox', 'Paraphyletic group', 'Population taxobox',
)


def normalize(name):
    name = re.sub(r'<!--.*?-->', '', name, flags=re.S)
    name = re.sub(r'\s+', ' ', name.replace('_', ' ')).strip()
    name = re.sub(r'^(?:subst:|safesubst:)', '', name, flags=re.I).strip()
    name = re.sub(r'^Template\s*:', '', name, flags=re.I).strip()
    return name.casefold()


def progress(**fields):
    print(json.dumps(fields), flush=True)


def source_name(source):
    return Path(urllib.parse.urlparse(str(source)).path).name


class CheckedReader:
    def __init__(self, stream):
        self.stream = stream
        self.digest = hashlib.sha256()

    def read(self, size=-1):
        data = self.stream.read(size)
        self.digest.update(data)
        return data


def pages(source, expected_sha256=None):
    with ExitStack() as stack:
        remote = '://' in str(source)
        raw = stack.enter_context(urllib.request.urlopen(str(source), timeout=120)
                                  if remote else open(source, 'rb'))
        checked = CheckedReader(raw)
        stream = stack.enter_context(bz2.BZ2File(checked, 'rb'))
        context = ET.iterparse(stream, events=('start', 'end'))
        _, root = next(context)
        prefix = root.tag.split('}')[0] + '}' if '}' in root.tag else ''
        for event, element in context:
            if event != 'end' or element.tag != prefix + 'page':
                continue
            def text(path):
                return element.findtext('/'.join(prefix + part for part in path.split('/')))
            revision = element.find(prefix + 'revision')
            content = revision.find('.//' + prefix + 'text') if revision is not None else None
            record = {
                'page_id': int(text('id')),
                'title': text('title'),
                'namespace': int(text('ns')),
                'revision_id': int(text('revision/id')),
                'timestamp': text('revision/timestamp'),
                'source_file': source_name(source),
                'wikitext': content.text or '' if content is not None else '',
                'is_redirect': element.find(prefix + 'redirect') is not None,
            }
            yield record
            root.clear()
        if expected_sha256 and checked.digest.hexdigest() != expected_sha256:
            raise ValueError('Compressed source SHA-256 mismatch: ' + source_name(source))


def redirect_target(wikitext):
    """Read leading whitespace/comments once, without regex backtracking."""
    position = 0
    whitespace = re.compile(r'\s*')
    while True:
        position = whitespace.match(wikitext, position).end()
        if not wikitext.startswith('<!--', position):
            break
        end = wikitext.find('-->', position + 4)
        if end < 0:
            return None
        position = end + 3
    match = re.compile(r'#redirect\s*\[\[\s*([^\]|#]+)', re.I).match(wikitext, position)
    return normalize(match[1]) if match else None


def index_redirects(source, expected_sha256=None):
    redirects = {}
    for count, page in enumerate(pages(source, expected_sha256), 1):
        if page['namespace'] == 10:
            target = redirect_target(page['wikitext'])
            if target is not None:
                redirects[normalize(page['title'])] = target
        if count % 100000 == 0:
            progress(phase='index_aliases', source=source_name(source), pages=count)
    return redirects


def resolve_aliases(redirects):
    families = {normalize(name): name for name in FAMILIES}
    resolved = dict(families)
    resolved['automated taxobox'] = 'Automatic taxobox'
    for name in redirects:
        current = name
        seen = set()
        while current in redirects and current not in seen:
            seen.add(current)
            current = redirects[current]
        if current in resolved:
            resolved[name] = resolved[current]
    return resolved


def extract(source, output, aliases=None, expected_sha256=None, source_bytes=None):
    output = Path(output)
    output.mkdir(parents=True, exist_ok=True)
    names = ('candidates.ndjson.gz', 'dependencies.ndjson.gz', 'review.ndjson.gz', 'report.json')
    if any((output / name).exists() for name in names):
        raise FileExistsError('Choose a fresh output directory; existing results are never overwritten')
    started = time.monotonic()
    shared_aliases = aliases is not None
    if aliases is None:
        progress(phase='index_aliases', source=str(source))
        aliases = resolve_aliases(index_redirects(source, expected_sha256))
    else:
        aliases = {**resolve_aliases({}), **aliases}
    # A broad lexical prefilter is followed by a real wikitext parse. The parser
    # excludes apparent calls inside comments and nowiki, unlike a regex alone.
    prefilter = re.compile(r'\{\{\s*(?:(?:subst:|safesubst:)\s*)?(?:Template\s*:\s*)?('
                           + '|'.join(re.escape(name).replace(r'\ ', r'[\s_]+')
                                      for name in sorted(aliases, key=len, reverse=True))
                           + r')\s*(?=[|}])', re.I)
    report = {
        'schema_version': 2, 'source_file': source_name(source),
        'source_bytes': source_bytes if source_bytes is not None else Path(source).stat().st_size,
        'source_sha256': expected_sha256,
        'alias_scope': 'complete_export' if shared_aliases else 'input_file',
        'scope': 'English main-namespace non-redirect articles using taxobox-family templates',
        'pages_scanned': 0, 'articles_scanned': 0, 'candidates': 0, 'dependencies': 0, 'review_candidates': 0,
        'template_frequencies': {}, 'candidate_examples': {},
        'template_aliases': aliases,
        'limitations': [
            'Dependency records are the subset present in this shard, not a complete rendering dependency closure.',
            'No rendering or taxonomy expansion is performed.',
            'Taxobox selection does not cover every lifeform-related article.',
            'Template frequencies count invocations in selected candidates, including nested templates.',
        ],
    }
    frequencies = Counter()
    match_counts = Counter()
    candidate_path = output / (names[0] + '.partial')
    dependency_path = output / (names[1] + '.partial')
    review_path = output / (names[2] + '.partial')
    progress(phase='select_pages', aliases=len(aliases))
    with gzip.open(candidate_path, 'wt', encoding='utf-8', compresslevel=3) as candidates, \
         gzip.open(dependency_path, 'wt', encoding='utf-8', compresslevel=3) as dependencies, \
         gzip.open(review_path, 'wt', encoding='utf-8', compresslevel=3) as review:
        for page in pages(source, expected_sha256):
            report['pages_scanned'] += 1
            title = page['title']
            dependency = (page['namespace'] == 10 and (
                title.casefold().startswith('template:taxonomy/') or normalize(title) in aliases))
            dependency = dependency or (page['namespace'] == 828 and
                                        title.casefold().startswith('module:autotaxobox'))
            if dependency:
                dependencies.write(json.dumps(page, ensure_ascii=False) + '\n')
                report['dependencies'] += 1
            if page['namespace'] == 0 and not page['is_redirect'] and not re.match(
                    r'^\s*#redirect\b', page['wikitext'], re.I):
                report['articles_scanned'] += 1
                probe = re.sub(r'<!--.*?-->', '', page['wikitext'], flags=re.S)
                review_signal = bool(re.search(r'\{\{\s*(?:Template\s*:\s*)?Taxonbar\s*[|}]', probe, re.I))
                if prefilter.search(probe) or review_signal:
                    templates = mwparserfromhell.parse(page['wikitext'], skip_style_tags=True).filter_templates()
                    matched = sorted({aliases[normalize(str(t.name))] for t in templates
                                      if normalize(str(t.name)) in aliases})
                    if matched:
                        page['matched_templates'] = matched
                        candidates.write(json.dumps(page, ensure_ascii=False) + '\n')
                        report['candidates'] += 1
                        frequencies.update(normalize(str(t.name)) for t in templates)
                        match_counts.update(matched)
                        for family in matched:
                            examples = report['candidate_examples'].setdefault(family, [])
                            if len(examples) < 5:
                                examples.append(title)
                    elif any(normalize(str(t.name)) == 'taxonbar' for t in templates):
                        page['review_reasons'] = ['Taxonbar without recognized taxobox']
                        review.write(json.dumps(page, ensure_ascii=False) + '\n')
                        report['review_candidates'] += 1
            if report['pages_scanned'] % 25000 == 0:
                progress(phase='select_pages', pages=report['pages_scanned'],
                         candidates=report['candidates'], seconds=round(time.monotonic() - started, 1))
    report['template_frequencies'] = dict(frequencies.most_common())
    report['candidates_by_taxobox'] = dict(match_counts.most_common())
    report['elapsed_seconds'] = round(time.monotonic() - started, 2)
    report['output_bytes'] = {names[0]: candidate_path.stat().st_size,
                              names[1]: dependency_path.stat().st_size,
                              names[2]: review_path.stat().st_size}
    report_path = output / (names[3] + '.partial')
    report_path.write_text(json.dumps(report, indent=2, ensure_ascii=False) + '\n', encoding='utf-8')
    candidate_path.rename(output / names[0])
    dependency_path.rename(output / names[1])
    review_path.rename(output / names[2])
    # The report is published last and acts as the completed-run marker.
    report_path.rename(output / names[3])
    progress(phase='complete', pages=report['pages_scanned'], candidates=report['candidates'],
             dependencies=report['dependencies'], seconds=report['elapsed_seconds'])
    return report


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('source', type=Path)
    parser.add_argument('--output', type=Path, required=True)
    args = parser.parse_args()
    extract(args.source, args.output)
