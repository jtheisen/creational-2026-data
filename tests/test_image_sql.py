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
    def test_escaped_title_and_metadata_delimiters(self):
        row = parse_image_row(b"('A\\'b,_bird.jpg',12345,330,210,'comma, quote \\' and )',8,'BITMAP','image','jpeg',1,2,'20261001010203','abc'),")
        self.assertEqual("File:A'b, bird.jpg", row['file_title'])
        self.assertEqual(12345, row['original_bytes'])
        self.assertEqual('image/jpeg', row['mime'])
        self.assertEqual('abc', row['sha1_base36'])
        self.assertEqual("a'b, bird.jpg", row_key(b"('A\\'b,_bird.jpg',12345,330,210,'comma, quote \\' and )',8,'BITMAP','image','jpeg',1,2,'20261001010203','abc'),"))

    def test_streaming_filter_and_snappy_output(self):
        sql = COLUMNS + "('A_bird.jpg',123,330,210,'x',8,'BITMAP','image','jpeg',1,2,'20261001010203','abc'),\n" + \
              "('Other.png',900,900,500,'y',8,'BITMAP','image','png',1,2,'20261001010203','def');\n"
        with TemporaryDirectory() as root:
            output = Path(root) / 'images.parquet'
            chunks = [gzip.compress(sql.encode())[i:i+7] for i in range(0, len(gzip.compress(sql.encode())), 7)]
            report = convert_image_sql(chunks, output, 'commonswiki', {canonical_key('File:A bird.jpg')})
            table = pq.read_table(output)
            self.assertEqual(2, report['scanned'])
            self.assertEqual(1, report['retained'])
            self.assertEqual(['File:A bird.jpg'], table.column('file_title').to_pylist())
            self.assertEqual('SNAPPY', pq.ParquetFile(output).metadata.row_group(0).column(0).compression)

    def test_wrong_column_order_is_rejected(self):
        sql = COLUMNS.replace('`img_size` bigint', '`img_size_wrong` bigint')
        with TemporaryDirectory() as root:
            with self.assertRaisesRegex(ValueError, 'column order'):
                convert_image_sql([gzip.compress(sql.encode())], Path(root) / 'out.parquet', 'enwiki')

    def test_truncated_gzip_is_rejected(self):
        with TemporaryDirectory() as root:
            with self.assertRaisesRegex(ValueError, 'truncated gzip'):
                convert_image_sql([gzip.compress(COLUMNS.encode())[:-3]], Path(root) / 'out.parquet', 'enwiki')

    def test_local_file_takes_precedence_over_commons(self):
        with TemporaryDirectory() as root:
            root = Path(root)
            en = root / 'en.parquet'
            commons = root / 'commons.parquet'
            refs = root / 'refs.parquet'
            pq.write_table(pa.Table.from_pylist([{'canonical_key': 'bird.jpg', 'file_title': 'File:Bird.jpg',
                'source_wiki': 'enwiki', 'original_bytes': 100, 'width': 330, 'height': 200,
                'mime': 'image/jpeg', 'uploaded_at': '20261001010203', 'sha1_base36': 'a'}]), en)
            pq.write_table(pa.Table.from_pylist([{'canonical_key': 'bird.jpg', 'file_title': 'File:Bird.jpg',
                'source_wiki': 'commonswiki', 'original_bytes': 999, 'width': 900, 'height': 600,
                'mime': 'image/jpeg', 'uploaded_at': '20261001010203', 'sha1_base36': 'b'}]), commons)
            pq.write_table(pa.table({'file_title': ['File:Bird.jpg', 'File:Bird.jpg', 'File:Missing.jpg']}), refs)

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
            self.assertEqual(2, len(rows))
            self.assertEqual(100, rows['bird.jpg']['original_bytes'])
            self.assertEqual('enwiki-local', rows['bird.jpg']['status'])
            self.assertEqual('missing', rows['missing.jpg']['status'])


if __name__ == '__main__':
    unittest.main()
