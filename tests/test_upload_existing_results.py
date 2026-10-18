import json
from pathlib import Path
from tempfile import TemporaryDirectory
import unittest

from tools.upload_existing_results import inventory


class UploadInventoryTests(unittest.TestCase):
    def test_rejects_missing_parse_member(self):
        with TemporaryDirectory() as directory:
            root = Path(directory)
            group = root / 'results/candidate-parse-v1/enwiki-2026-10-01-p1'
            group.mkdir(parents=True)
            (group / 'report.json').write_text(json.dumps({'output_bytes': {'articles.parquet': 1}}))
            with self.assertRaisesRegex(ValueError, 'articles.parquet'):
                inventory(root)

    def test_rejects_wrong_report_length(self):
        with TemporaryDirectory() as directory:
            root = Path(directory)
            group = root / 'results/candidate-parse-v1/enwiki-2026-10-01-p1'
            group.mkdir(parents=True)
            (group / 'articles.parquet').write_bytes(b'abc')
            (group / 'report.json').write_text(json.dumps({'output_bytes': {'articles.parquet': 4}}))
            with self.assertRaisesRegex(ValueError, 'length'):
                inventory(root)


if __name__ == '__main__':
    unittest.main()
