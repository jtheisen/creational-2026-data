import json
from pathlib import Path
import tempfile
import unittest

import pyarrow as pa
import pyarrow.parquet as pq

from build_candidate_pool import SCHEMA
from parse_candidate_pool import parse_page, parse_shard


ALIASES = {'speciesbox': 'Speciesbox', 'infobox species': 'Speciesbox',
           'taxobox': 'Taxobox'}


def page(page_id, title, wikitext, signal='taxobox_name'):
    return dict(page_id=page_id, title=title, namespace=0, revision_id=page_id + 100,
                timestamp='2026-10-01T00:00:00Z', source_file='sample.xml.bz2',
                wikitext=wikitext, selection_signal=signal)


class PageParsingTests(unittest.TestCase):
    def test_boxes_duplicate_parameters_context_and_images(self):
        text = ('<!-- {{Taxobox|name=Fake}} -->\n'
                '{{stack|{{Infobox species|genus=Vulpes|species=vulpes|image=Fox.jpg|'
                'image=Other.jpg}}}}\n'
                '== Relatives ==\n{{Taxobox|name=Second|range_map={{map|Fox.png}}}}\n'
                '[[File:Fox running.jpg|thumb|A fox]]\n'
                '<gallery>File:Fox sleeping.jpg|Sleep\nFox walking.jpg|Walk</gallery>\n')
        article, boxes, params, images = parse_page(page(1, 'Fox', text), ALIASES)
        self.assertEqual(article['taxobox_count'], 2)
        self.assertEqual([b['family'] for b in boxes], ['Speciesbox', 'Taxobox'])
        self.assertFalse(boxes[0]['is_top_level'])
        self.assertEqual(boxes[0]['section_path'], [])
        self.assertEqual(boxes[1]['section_path'], ['Relatives'])
        self.assertEqual(text[boxes[0]['start']:boxes[0]['end']], boxes[0]['raw_markup'])
        self.assertEqual([p['raw_value'] for p in params if p['raw_name'] == 'image'],
                         ['Fox.jpg', 'Other.jpg'])
        self.assertEqual([p['parameter_index'] for p in params if p['raw_name'] == 'image'], [2, 3])
        self.assertEqual({i['file_title'] for i in images if i['file_title']},
                         {'File:Fox.jpg', 'File:Other.jpg', 'File:Fox running.jpg',
                          'File:Fox sleeping.jpg', 'File:Fox walking.jpg'})
        self.assertIn(None, [i['file_title'] for i in images])

    def test_taxonbar_and_regex_false_positive_are_reviewed(self):
        article, boxes, params, images = parse_page(page(2, 'Only review', '{{Taxonbar|from=Q1}}', 'taxonbar'), ALIASES)
        self.assertEqual(article['review_reason'], 'taxonbar_without_taxobox')
        self.assertTrue(article['has_taxonbar'])
        self.assertFalse(boxes or params or images)
        article, boxes, _, _ = parse_page(page(3, 'Nowiki', '<nowiki>{{Taxobox}}</nowiki>'), ALIASES)
        self.assertEqual(article['review_reason'], 'regex_false_positive')
        self.assertFalse(boxes)

    def test_shard_writes_queryable_parquet_and_report_last(self):
        with tempfile.TemporaryDirectory() as directory:
            source = Path(directory) / 'pool.parquet'
            output = Path(directory) / 'parsed'
            rows = [page(1, 'Fox', '{{Speciesbox|image=Fox.jpg|taxon=Vulpes vulpes}}'),
                    page(2, 'Review', '{{Taxonbar|from=Q1}}', 'taxonbar')]
            pq.write_table(pa.Table.from_pylist(rows, schema=SCHEMA), source, compression='snappy')
            report = parse_shard(source, output, ALIASES)
            self.assertEqual(report['articles'], 2)
            self.assertEqual(report['taxoboxes'], 1)
            self.assertEqual(report['image_references'], 1)
            self.assertEqual(pq.read_table(output / 'articles.parquet').num_rows, 2)
            self.assertEqual(pq.read_table(output / 'taxoboxes.parquet').num_rows, 1)
            self.assertEqual(pq.read_table(output / 'taxobox_parameters.parquet').num_rows, 2)
            self.assertEqual(pq.read_table(output / 'image_references.parquet').num_rows, 1)
            self.assertEqual(json.loads((output / 'report.json').read_text())['articles'], 2)
            self.assertFalse(list(output.glob('*.partial')))


if __name__ == '__main__':
    unittest.main()
