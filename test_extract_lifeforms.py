import bz2
import gzip
import json
from pathlib import Path
import tempfile
import subprocess
import sys
import unittest
from xml.sax.saxutils import escape

from extract_lifeforms import extract, pages


def fixture(pages):
    body = ''.join(f'<page><title>{escape(title)}</title><ns>{ns}</ns><id>{i}</id>'
                   f'<revision><id>{i+100}</id><timestamp>2026-10-01T00:00:00Z</timestamp>'
                   f'<text>{escape(text)}</text></revision></page>'
                   for i, (title, ns, text) in enumerate(pages, 1))
    return ('<mediawiki xmlns="http://www.mediawiki.org/xml/export-0.11/">' + body + '</mediawiki>').encode()


class ExtractionTests(unittest.TestCase):
    def test_redirect_probe_is_linear_on_comment_heavy_nonredirects(self):
        cases = [
            ['<!-- comment -->' + ' ' * 100000 + '{{Not a redirect}}', None],
            [('<!-- comment -->\n' * 10000) + '{{Not a redirect}}', None],
            ['<!-- #REDIRECT [[Wrong]] -->\n<!-- second -->\n#REDIRECT [[Template:Speciesbox]]', 'speciesbox'],
            ['<!-- unclosed #REDIRECT [[Wrong]]', None],
        ]
        script = ('import json,sys; from extract_lifeforms import redirect_target; '
                  'cases=json.load(sys.stdin); '
                  'assert all(redirect_target(text)==expected for text,expected in cases)')
        result = subprocess.run([sys.executable, '-c', script], input=json.dumps(cases),
                                text=True, capture_output=True, timeout=3)
        self.assertEqual(result.returncode, 0, result.stderr)

    def test_streamed_source_is_checksum_verified(self):
        import hashlib
        with tempfile.TemporaryDirectory() as directory:
            source = Path(directory) / 'remote.xml.bz2'
            data = bz2.compress(fixture([('Example', 0, '{{Speciesbox|taxon=Example}}')]))
            source.write_bytes(data)
            records = list(pages(source.as_uri(), hashlib.sha256(data).hexdigest()))
            self.assertEqual(records[0]['source_file'], source.name)
            with self.assertRaises(ValueError):
                list(pages(source.as_uri(), '0' * 64))

    def test_external_aliases_style_overlap_and_review_stream(self):
        with tempfile.TemporaryDirectory() as directory:
            source = Path(directory) / 'sample.xml.bz2'
            output = Path(directory) / 'out'
            source.write_bytes(bz2.compress(fixture([
                ('Commented name', 0, '{{Automatic taxobox <!-- note -->|taxon=Example}}'),
                ('Crossing style', 0, "{{Speciesbox|taxon=Example|authority=''A}} text''"),
                ('External alias', 0, '{{Another alias|taxon=Example}}'),
                ('Review only', 0, '{{Taxonbar|from=Q123}}'),
            ])))
            report = extract(source, output, aliases={'another alias': 'Speciesbox'})
            self.assertEqual(report['candidates'], 3)
            self.assertEqual(report['review_candidates'], 1)
            with gzip.open(output / 'review.ndjson.gz', 'rt') as stream:
                self.assertEqual(json.loads(next(stream))['title'], 'Review only')

    def test_candidates_alias_chains_dependencies_and_markup(self):
        with tempfile.TemporaryDirectory() as directory:
            source = Path(directory) / 'sample.xml.bz2'
            output = Path(directory) / 'out'
            text = '{{Template:Alias_two|taxon=Example|image={{nested|a=b}}}}\nFull text.'
            source.write_bytes(bz2.compress(fixture([
                ('Organism', 0, text),
                ('Comment', 0, '<!-- {{Taxobox}} --> <nowiki>{{Speciesbox}}</nowiki>'),
                ('Unrelated', 0, '{{Infobox person}}'),
                ('Talk:Organism', 1, '{{Taxobox}}'),
                ('Redirect', 0, '#REDIRECT [[Organism]]'),
                ('Template:Alias two', 10, '#REDIRECT [[Template:Alias one]]'),
                ('Template:Alias one', 10, '#REDIRECT [[Template:Speciesbox]]'),
                ('Template:Taxonomy/Example', 10, '{{Taxonomy|parent=Life}}'),
            ])))
            report = extract(source, output)
            with gzip.open(output / 'candidates.ndjson.gz', 'rt') as stream:
                records = [json.loads(line) for line in stream]
            self.assertEqual([r['title'] for r in records], ['Organism'])
            self.assertEqual(records[0]['wikitext'], text)
            self.assertEqual(records[0]['matched_templates'], ['Speciesbox'])
            self.assertEqual(records[0]['page_id'], 1)
            self.assertEqual(records[0]['revision_id'], 101)
            with gzip.open(output / 'dependencies.ndjson.gz', 'rt') as stream:
                titles = {json.loads(line)['title'] for line in stream}
            self.assertEqual(titles, {'Template:Alias one', 'Template:Alias two', 'Template:Taxonomy/Example'})
            self.assertEqual(report['pages_scanned'], 8)
            self.assertEqual(report['candidates'], 1)
            self.assertFalse(list(output.glob('*.partial')))

    def test_broken_xml_does_not_publish_results(self):
        with tempfile.TemporaryDirectory() as directory:
            source = Path(directory) / 'bad.xml.bz2'
            output = Path(directory) / 'out'
            source.write_bytes(bz2.compress(b'<mediawiki><page>'))
            with self.assertRaises(Exception):
                extract(source, output)
            self.assertFalse((output / 'report.json').exists())
            self.assertFalse((output / 'candidates.ndjson.gz').exists())


if __name__ == '__main__':
    unittest.main()
