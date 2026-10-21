import gzip
from pathlib import Path
from tempfile import TemporaryDirectory
import unittest

import pyarrow.parquet as pq
import pyarrow as pa

from workers.collection.image_sql import canonical_key, convert_image_sql, parse_image_row, row_key
from workers.collection.stages import resolved_image_metadata


COLUMNS = '''CREATE TABLE `image` (
  `img_name` varbinary(255) NOT NULL,
  `img_size` bigint NOT NULL,
  `img_width` int NOT NULL,
  `img_height` int NOT NULL,
  `img_metadata` mediumblob NOT NULL,
  `img_bits` int NOT NULL,
  `img_media_type` enum('BITMAP') DEFAULT NULL,
  `img_major_mime` enum('image') NOT NULL,
  `img_minor_mime` varbinary(100) NOT NULL,
  `img_description_id` bigint NOT NULL,
  `img_actor` bigint NOT NULL,
  `img_timestamp` binary(14) NOT NULL,
  `img_sha1` varbinary(32) NOT NULL,
  PRIMARY KEY (`img_name`)
);
INSERT INTO `image` VALUES
'''


class ImageSqlTests(unittest.TestCase):
    def test_filename_key_preserves_case_after_initial_letter(self):
        self.assertEqual('Bird.jpg', canonical_key('File:bird.jpg'))
        self.assertNotEqual(canonical_key('File:Bird.jpg'), canonical_key('File:Bird.JPG'))

    def test_escaped_title_and_metadata_delimiters(self):
        row = parse_image_row(b"('A\\'b,_bird.jpg',12345,330,210,'comma, quote \\' and )',8,'BITMAP','image','jpeg',1,2,'20261001010203','abc'),")
        self.assertEqual("File:A'b, bird.jpg", row['file_title'])
        self.assertEqual(12345, row['original_bytes'])
        self.assertEqual('image/jpeg', row['mime'])
        self.assertEqual('abc', row['sha1_base36'])
        self.assertEqual("A'b, bird.jpg", row_key(b"('A\\'b,_bird.jpg',12345,330,210,'comma, quote \\' and )',8,'BITMAP','image','jpeg',1,2,'20261001010203','abc'),"))

    def test_streaming_filter_and_snappy_output(self):
        sql = COLUMNS + "('A_bird.jpg',123,330,210,'x',8,'BITMAP','image','jpeg',1,2,'20261001010203','abc'),\n" + \
              "('Other.png',900,900,500,'y',8,'BITMAP','image','png',1,2,'20261001010203','def');\n"
        with TemporaryDirectory() as root:
            output = Path(root) / 'images.parquet'
            chunks = [gzip.compress(sql.encode())[i:i+7] for i in range(0, len(gzip.compress(sql.encode())), 7)]
            report = convert_image_sql(chunks, output, 'commonswiki', 'source-sha1',
                                       {canonical_key('File:A bird.jpg')})
            table = pq.read_table(output)
            self.assertEqual(2, report['scanned'])
            self.assertEqual(1, report['retained'])
            self.assertEqual(['File:A bird.jpg'], table.column('file_title').to_pylist())
            self.assertEqual(['source-sha1'], table.column('source_dump_sha1').to_pylist())
            self.assertEqual('SNAPPY', pq.ParquetFile(output).metadata.row_group(0).column(0).compression)

    def test_wrong_column_order_is_rejected(self):
        sql = COLUMNS.replace('`img_size` bigint', '`img_size_wrong` bigint')
        with TemporaryDirectory() as root:
            with self.assertRaisesRegex(ValueError, 'column order'):
                convert_image_sql([gzip.compress(sql.encode())], Path(root) / 'out.parquet', 'enwiki', 'source-sha1')

    def test_truncated_gzip_is_rejected(self):
        with TemporaryDirectory() as root:
            with self.assertRaisesRegex(ValueError, 'truncated gzip'):
                convert_image_sql([gzip.compress(COLUMNS.encode())[:-3]], Path(root) / 'out.parquet', 'enwiki', 'source-sha1')

    def test_local_file_takes_precedence_over_commons(self):
        with TemporaryDirectory() as root:
            root = Path(root)
            en = root / 'en.parquet'
            commons = root / 'commons.parquet'
            refs = root / 'refs.parquet'
            pq.write_table(pa.Table.from_pylist([{'canonical_key': 'Bird.jpg', 'file_title': 'File:Bird.jpg',
                'source_wiki': 'enwiki', 'source_dump_sha1': 'en-pin', 'original_bytes': 100, 'width': 330, 'height': 200,
                'mime': 'image/jpeg', 'media_type': 'BITMAP', 'uploaded_at': '20261001010203', 'sha1_base36': 'a'}]), en)
            pq.write_table(pa.Table.from_pylist([{'canonical_key': 'Bird.jpg', 'file_title': 'File:Bird.jpg',
                'source_wiki': 'commonswiki', 'source_dump_sha1': 'commons-pin', 'original_bytes': 999, 'width': 900, 'height': 600,
                'mime': 'image/jpeg', 'media_type': 'BITMAP', 'uploaded_at': '20261001010203', 'sha1_base36': 'b'},
                {'canonical_key': 'Bird.ogg', 'file_title': 'File:Bird.ogg',
                 'source_wiki': 'commonswiki', 'source_dump_sha1': 'commons-pin', 'original_bytes': 1000000, 'width': 0, 'height': 0,
                 'mime': 'audio/ogg', 'media_type': 'AUDIO', 'uploaded_at': '20261001010203', 'sha1_base36': 'c'},
                {'canonical_key': 'Unknown.bin', 'file_title': 'File:Unknown.bin',
                 'source_wiki': 'commonswiki', 'source_dump_sha1': 'commons-pin', 'original_bytes': 2000000, 'width': 0, 'height': 0,
                 'mime': 'application/octet-stream', 'media_type': None,
                 'uploaded_at': '20261001010203', 'sha1_base36': 'd'}]), commons)
            pq.write_table(pa.table({'file_title': ['File:Bird.jpg', 'File:Bird.jpg', 'File:Missing.jpg',
                                                   'File:Bird.ogg', 'File:Unknown.bin']}), refs)

            class Store:
                def download(self, uri, destination):
                    destination.parent.mkdir(parents=True, exist_ok=True)
                    destination.write_bytes(Path(uri).read_bytes())
                    return destination

            output_dir = root / 'output'
            output_dir.mkdir()
            resolved_image_metadata({'enwiki_metadata': str(en), 'commons_metadata': str(commons),
                                     'image_references': [str(refs)]}, output_dir, Store())
            rows = {row['canonical_key']: row for row in pq.read_table(output_dir / 'resolved_images.parquet').to_pylist()}
            self.assertEqual(4, len(rows))
            self.assertEqual(100, rows['Bird.jpg']['original_bytes'])
            self.assertEqual('enwiki-local', rows['Bird.jpg']['status'])
            self.assertEqual('en-pin', rows['Bird.jpg']['source_dump_sha1'])
            self.assertEqual('missing', rows['Missing.jpg']['status'])
            self.assertEqual('unsupported-media', rows['Bird.ogg']['status'])
            self.assertEqual('unsupported-media', rows['Unknown.bin']['status'])

    def test_same_canonical_key_with_multiple_source_rows_is_ambiguous(self):
        with TemporaryDirectory() as root:
            root = Path(root)
            schema = pa.schema([('canonical_key', pa.string()), ('file_title', pa.string()),
                                ('source_wiki', pa.string()), ('source_dump_sha1', pa.string()),
                                ('original_bytes', pa.int64()), ('width', pa.int32()), ('height', pa.int32()),
                                ('mime', pa.string()), ('media_type', pa.string()),
                                ('uploaded_at', pa.string()), ('sha1_base36', pa.string())])
            rows = [{'canonical_key': 'Bird.jpg', 'file_title': name, 'source_wiki': 'enwiki',
                     'source_dump_sha1': 'en-pin', 'original_bytes': size, 'width': 1, 'height': 1,
                     'mime': 'image/jpeg', 'media_type': 'BITMAP', 'uploaded_at': '20261001',
                     'sha1_base36': 'a'} for name, size in [('File:Bird.jpg', 100), ('File:bird.jpg', 200)]]
            en, commons, refs = (root / name for name in ('en.parquet', 'commons.parquet', 'refs.parquet'))
            pq.write_table(pa.Table.from_pylist(rows, schema=schema), en)
            pq.write_table(pa.Table.from_pylist([], schema=schema), commons)
            pq.write_table(pa.table({'file_title': ['File:Bird.jpg']}), refs)

            class Store:
                def download(self, uri, destination):
                    destination.parent.mkdir(parents=True, exist_ok=True)
                    destination.write_bytes(Path(uri).read_bytes())
                    return destination

            output = root / 'output'
            output.mkdir()
            resolved_image_metadata({'enwiki_metadata': str(en), 'commons_metadata': str(commons),
                                     'image_references': [str(refs)]}, output, Store())
            rows = pq.read_table(output / 'resolved_images.parquet').to_pylist()
            self.assertEqual(1, len(rows))
            self.assertEqual('ambiguous', rows[0]['status'])
            self.assertIsNone(rows[0]['original_bytes'])


if __name__ == '__main__':
    unittest.main()
