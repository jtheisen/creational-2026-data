import bz2
import hashlib
import json
from pathlib import Path
import tempfile
import unittest

import pyarrow.parquet as pq

from build_candidate_pool import build_pool, check_aliases
from test_extract_lifeforms import fixture


class CandidatePoolTests(unittest.TestCase):
    def test_one_pass_preserves_markup_and_indexes_redirects(self):
        with tempfile.TemporaryDirectory() as directory:
            source = Path(directory) / 'sample.xml.bz2'
            output = Path(directory) / 'out'
            original = '{{Template:Alias_one <!-- comment -->|taxon=Fox|image=Fox.jpg}}\nFull text.'
            data = bz2.compress(fixture([
                ('Fox', 0, original),
                ('Review', 0, '{{Taxonbar|from=Q1}}'),
                ('Person', 0, '{{Infobox person}}'),
                ('Talk:Fox', 1, '{{Speciesbox|taxon=Fox}}'),
                ('Redirect', 0, '#REDIRECT [[Fox]]'),
                ('Template:Alias one', 10, '#REDIRECT [[Template:Speciesbox]]'),
                ('Template:Taxonomy/Fox', 10, '{{Taxonomy|parent=Life}}'),
                ('Module:Autotaxobox', 828, 'return {}'),
            ]))
            source.write_bytes(data)
            aliases = {'alias one': 'Speciesbox', 'speciesbox': 'Speciesbox'}
            report = build_pool(source, output, aliases, hashlib.sha256(data).hexdigest())
            pool = pq.read_table(output / 'pool.parquet').to_pylist()
            self.assertEqual([row['title'] for row in pool], ['Fox', 'Review'])
            self.assertEqual(pool[0]['wikitext'], original)
            self.assertEqual(pool[0]['page_id'], 1)
            self.assertEqual(pool[0]['revision_id'], 101)
            self.assertEqual(pool[0]['source_file'], source.name)
            self.assertEqual(pool[0]['selection_signal'], 'taxobox_name')
            self.assertEqual(pool[1]['selection_signal'], 'taxonbar')
            self.assertEqual(pq.ParquetFile(output / 'pool.parquet').metadata.row_group(0).column(0).compression, 'SNAPPY')
            self.assertEqual({r['title'] for r in pq.read_table(output / 'dependencies.parquet').to_pylist()},
                             {'Template:Alias one', 'Template:Taxonomy/Fox', 'Module:Autotaxobox'})
            redirects = json.loads((output / 'redirects.json').read_text())
            self.assertEqual(redirects, {'alias one': 'speciesbox'})
            self.assertEqual(report['pool_pages'], 2)
            self.assertEqual(report['pages_scanned'], 8)

    def test_corrupt_checksum_has_no_completion_report(self):
        with tempfile.TemporaryDirectory() as directory:
            source = Path(directory) / 'sample.xml.bz2'
            source.write_bytes(bz2.compress(fixture([('Fox', 0, '{{Taxobox}}')])))
            output = Path(directory) / 'out'
            with self.assertRaises(ValueError):
                build_pool(source, output, {'taxobox': 'Taxobox'}, '0' * 64)
            self.assertFalse((output / 'report.json').exists())

    def test_alias_change_requires_reprocessing(self):
        with self.assertRaisesRegex(ValueError, 'alias'):
            check_aliases({'taxobox': 'Taxobox'}, {'taxobox': 'Taxobox', 'new alias': 'Taxobox'})


if __name__ == '__main__':
    unittest.main()
