import tempfile
import unittest
from pathlib import Path

import pyarrow.parquet as pq

from transform_ott_taxonomy import classify_name, transform


class NameClassificationTests(unittest.TestCase):
    def test_keeps_canonical_latin_shapes(self):
        self.assertEqual(classify_name('Mammalia', ''), ('uninomial', 'mammalia'))
        self.assertEqual(classify_name('Homo sapiens', ''), ('binomial', 'homo sapiens'))
        self.assertEqual(classify_name('Homo sapiens sapiens', ''), ('infraspecific', 'homo sapiens sapiens'))
        self.assertEqual(classify_name('Quercus robur subsp. broteroana', ''),
                         ('infraspecific', 'quercus robur subsp. broteroana'))

    def test_rejects_provisional_and_technical_names(self):
        self.assertEqual(classify_name('', ''), ('empty_name', None))
        self.assertEqual(classify_name('Haemophilus sp. CCUG 36040', ''),
                         ('provisional_name', None))
        self.assertEqual(classify_name('Haemophilus influenzae R2846', ''),
                         ('contains_digit', None))
        self.assertEqual(classify_name('Candidatus Portiera', ''),
                         ('candidatus_name', None))
        self.assertEqual(classify_name('Influenza A virus', ''),
                         ('virus_name', None))
        self.assertEqual(classify_name('Homo sapiens', 'viral'),
                         ('viral_flag', None))


class TransformTests(unittest.TestCase):
    def test_streams_named_rows_and_reports_exclusions(self):
        header = 'uid\t|\tparent_uid\t|\tname\t|\trank\t|\tsourceinfo\t|\tuniqname\t|\tflags\t|\t\n'
        rows = [
            '1\t|\t\t|\tMammalia\t|\tclass\t|\tncbi:40674\t|\t\t|\t\t|\t\n',
            '2\t|\t1\t|\tHomo sapiens\t|\tspecies\t|\tncbi:9606\t|\tHomo sapiens (species)\t|\textinct\t|\t\n',
            '3\t|\t1\t|\tHomo sp. A1\t|\tspecies\t|\tncbi:7\t|\t\t|\t\t|\t\n',
            '4\t|\t1\t|\tHomo sapiens\t|\tspecies\t|\tncbi:8\t|\t\t|\tviral\t|\t\n',
        ]
        with tempfile.TemporaryDirectory() as directory:
            directory = Path(directory)
            source = directory / 'taxonomy.tsv'
            source.write_text(header + ''.join(rows))
            output = directory / 'taxonomy_names.parquet'
            report = transform(source, output, directory / 'report.json', batch_size=2)
            data = pq.read_table(output).to_pylist()

        self.assertEqual(report['source_rows'], 4)
        self.assertEqual(report['kept_rows'], 2)
        self.assertEqual(report['excluded_by_reason'], {'provisional_name': 1, 'viral_flag': 1})
        self.assertEqual([row['ott_id'] for row in data], [1, 2])
        self.assertIsNone(data[0]['parent_ott_id'])
        self.assertEqual(data[1]['parent_ott_id'], 1)
        self.assertEqual(data[1]['name_key'], 'homo sapiens')
        self.assertEqual(data[1]['flags'], 'extinct')
        self.assertEqual(data[1]['uniqname'], 'Homo sapiens (species)')


if __name__ == '__main__':
    unittest.main()
