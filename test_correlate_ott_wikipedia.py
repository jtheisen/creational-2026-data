import tempfile
import unittest
from pathlib import Path

import duckdb
import pyarrow as pa
import pyarrow.parquet as pq

from correlate_ott_wikipedia import (derive_name, extract_wiki_names,
                                    match_exact, suggest_near_names)


class DeriveNameTests(unittest.TestCase):
    def test_explicit_taxon_and_wikilink_markup(self):
        result = derive_name('Speciesbox', 'Page', [(2, 'taxon', ' [[Homo sapiens]] ')])
        self.assertEqual(result['proposed_name'], 'Homo sapiens')
        self.assertEqual(result['name_key'], 'homo sapiens')
        self.assertEqual(result['method'], 'explicit_taxon')
        self.assertEqual(result['used_parameter_indices'], [2])

    def test_italic_markup_is_removed_from_legacy_binomial(self):
        result = derive_name('Taxobox', 'Wildcat',
                             [(0, 'binomial', "''Felis silvestris''")])
        self.assertEqual(result['proposed_name'], 'Felis silvestris')
        self.assertEqual(result['name_key'], 'felis silvestris')

    def test_subspecies_is_not_lost_to_binomial_fallback(self):
        result = derive_name('Subspeciesbox', 'Horse', [
            (0, 'genus', 'Equus'), (1, 'species', 'ferus'),
            (2, 'subspecies', 'caballus')])
        self.assertEqual(result['proposed_name'], 'Equus ferus caballus')
        self.assertEqual(result['name_form'], 'infraspecific')

    def test_unresolved_markup_is_not_stripped_into_a_false_match(self):
        result = derive_name('Speciesbox', 'Page', [(0, 'taxon', '{{PAGENAME}}')])
        self.assertIsNone(result['name_key'])
        self.assertEqual(result['review_reason'], 'unresolved_markup')


class CorrelationTests(unittest.TestCase):
    def test_near_name_candidates_are_review_only_and_skip_exact_rows(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            status, ott, output = (root / name for name in
                                   ('status.parquet', 'ott.parquet', 'near.parquet'))
            pq.write_table(pa.Table.from_pylist([
                dict(page_id=1, revision_id=1, box_index=0, title='A',
                     family='Speciesbox', proposed_name='Homo sapines',
                     name_key='homo sapines', name_form='binomial', match_status='no_exact'),
                dict(page_id=2, revision_id=2, box_index=0, title='B',
                     family='Speciesbox', proposed_name='Homa sapiens',
                     name_key='homa sapiens', name_form='binomial', match_status='no_exact'),
                dict(page_id=3, revision_id=3, box_index=0, title='C',
                     family='Speciesbox', proposed_name='Homo sapiens',
                     name_key='homo sapiens', name_form='binomial', match_status='exact_unique'),
            ]), status)
            pq.write_table(pa.Table.from_pylist([
                dict(ott_id=7, name='Homo sapiens', name_key='homo sapiens',
                     name_form='binomial', rank='species'),
            ]), ott)
            suggest_near_names(status, ott, output)
            rows = duckdb.sql(f"SELECT page_id,ott_id,differing_component FROM read_parquet('{output}') ORDER BY page_id").fetchall()
        self.assertEqual(rows, [(1, 7, 'epithet'), (2, 7, 'genus')])

    def test_exact_join_preserves_ambiguous_ott_names(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            boxes = root / 'boxes.parquet'
            articles = root / 'articles.parquet'
            parameters = root / 'parameters.parquet'
            ott = root / 'ott.parquet'
            names = root / 'names.parquet'
            exact = root / 'exact.parquet'
            status = root / 'status.parquet'
            pq.write_table(pa.Table.from_pylist([
                dict(page_id=1, revision_id=10, box_index=0, family='Speciesbox'),
                dict(page_id=2, revision_id=20, box_index=0, family='Speciesbox'),
            ]), boxes)
            pq.write_table(pa.Table.from_pylist([
                dict(page_id=1, revision_id=10, title='Human'),
                dict(page_id=2, revision_id=20, title='Unknown species'),
            ]), articles)
            pq.write_table(pa.Table.from_pylist([
                dict(page_id=1, revision_id=10, box_index=0, parameter_index=0,
                     normalized_name='taxon', raw_value='Homo sapiens'),
                dict(page_id=2, revision_id=20, box_index=0, parameter_index=0,
                     normalized_name='taxon', raw_value='Homo ignotus'),
            ]), parameters)
            pq.write_table(pa.Table.from_pylist([
                dict(ott_id=3, parent_ott_id=1, name='Homo sapiens',
                     name_key='homo sapiens', rank='species'),
                dict(ott_id=4, parent_ott_id=2, name='Homo sapiens',
                     name_key='homo sapiens', rank='species'),
            ]), ott)
            extract_wiki_names(boxes, parameters, articles, names, batch_size=1)
            match_exact(names, ott, exact, status)
            con = duckdb.connect()
            matches = con.sql(f"SELECT ott_id FROM read_parquet('{exact}') ORDER BY ott_id").fetchall()
            statuses = con.sql(f"SELECT page_id,ott_match_count,match_status FROM read_parquet('{status}') ORDER BY page_id").fetchall()

        self.assertEqual(matches, [(3,), (4,)])
        self.assertEqual(statuses, [(1, 2, 'exact_ambiguous'), (2, 0, 'no_exact')])


if __name__ == '__main__':
    unittest.main()
