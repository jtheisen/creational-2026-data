import hashlib
import tempfile
from pathlib import Path
import unittest
from unittest.mock import patch

import extract_blob_set as runner


class RunnerTests(unittest.TestCase):
    def test_skip_requires_complete_matching_artifacts_and_source(self):
        descriptors = {name: {'bytes': 100, 'sha256': 'abc'} for name in runner.ARTIFACTS}
        marker = {'fingerprint': 'code', 'source_sha256': 'source', 'artifacts': descriptors}
        with patch.object(runner, 'blob_matches', return_value=True):
            self.assertTrue(runner.completed_part(marker, 'code', 'source', 'https://example/'))
            self.assertFalse(runner.completed_part(marker, 'other', 'source', 'https://example/'))
            self.assertFalse(runner.completed_part(marker, 'code', 'other', 'https://example/'))
            self.assertFalse(runner.completed_part(None, 'code', 'source', 'https://example/'))
            del descriptors['report.json']
            self.assertFalse(runner.completed_part(marker, 'code', 'source', 'https://example/'))

    def test_missing_or_changed_output_forces_reprocessing(self):
        marker = {'fingerprint': 'code', 'source_sha256': 'source',
                  'artifacts': {name: {'bytes': 100, 'sha256': 'abc'} for name in runner.ARTIFACTS}}
        with patch.object(runner, 'blob_matches', return_value=False):
            self.assertFalse(runner.completed_part(marker, 'code', 'source', 'https://example/'))

    def test_upload_records_digest_and_rejects_failed_verification(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / 'example.gz'
            path.write_bytes(b'example extracted artifact')
            with patch.object(runner, 'azure'), patch.object(runner, 'blob_matches', return_value=True):
                result = runner.upload(path, 'https://example/output')
                self.assertEqual(result['sha256'], hashlib.sha256(path.read_bytes()).hexdigest())
                self.assertEqual(result['bytes'], path.stat().st_size)
            with patch.object(runner, 'azure'), patch.object(runner, 'blob_matches', return_value=False):
                with self.assertRaises(RuntimeError):
                    runner.upload(path, 'https://example/output')


if __name__ == '__main__':
    unittest.main()
