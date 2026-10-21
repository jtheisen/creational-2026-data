from pathlib import Path
from tempfile import TemporaryDirectory
import unittest

from workers.collection.stages import STAGES, execute
from workers.collection.manifest import AzureObjectStore
from types import SimpleNamespace


class WorkerTests(unittest.TestCase):
    def test_all_compute_target_modes_are_registered(self):
        self.assertEqual({
            'alias-index', 'alias-seed', 'candidate-pool-shard',
            'candidate-pool-finalize', 'parse-shard', 'parse-finalize',
            'ott-names', 'ott-correlation',
            'source-set', 'enwiki-image-metadata', 'commons-image-metadata',
            'resolved-image-metadata',
        }, set(STAGES))

    def test_failed_stage_does_not_publish_complete_marker(self):
        class FakeStore:
            def __init__(self): self.writes = []
            def upload(self, path, uri): self.writes.append(uri)
            def write_json(self, uri, value): self.writes.append(uri)

        def failing(inputs, scratch, store):
            (scratch / 'partial.parquet').write_bytes(b'partial')
            raise RuntimeError('failed')

        store = FakeStore()
        with TemporaryDirectory() as directory:
            with self.assertRaisesRegex(RuntimeError, 'failed'):
                execute('parse-shard', {}, Path(directory), store,
                        'https://example.test/attempt/', 'v1', failing)
        self.assertEqual([], store.writes)

    def test_marker_is_published_after_all_artifacts(self):
        class FakeStore:
            def __init__(self): self.writes = []
            def upload(self, path, uri):
                self.writes.append(uri)
                return {'Length': path.stat().st_size, 'Sha256': 'a' * 64}
            def write_json(self, uri, value): self.writes.append(uri)

        def stage(inputs, scratch, store):
            (scratch / 'one.parquet').write_bytes(b'one')
            (scratch / 'two.json').write_bytes(b'{}')
            return ['one.parquet', 'two.json']

        store = FakeStore()
        inputs = {'Target': {'Collection': 'c', 'Kind': 'parse-shard', 'Key': 'p1'},
                  'InputFingerprints': {'pool': 'f'}}
        with TemporaryDirectory() as directory:
            marker = execute('parse-shard', inputs, Path(directory), store,
                             'https://example.test/attempt/', 'v1', stage)
        self.assertEqual(['https://example.test/attempt/one.parquet',
                          'https://example.test/attempt/two.json',
                          'https://example.test/attempt/_COMPLETE.json'], store.writes)
        self.assertEqual('f', marker['InputFingerprints']['pool'])

    def test_upload_retry_reuses_identical_existing_blob(self):
        class ExistingBlob:
            def get_blob_properties(self):
                return SimpleNamespace(size=3, metadata={'sha256':
                    '2c26b46b68ffc68ff99b453c1d30413413422d706483bfa0f98a5e886266e7ae'})
            def upload_blob(self, *args, **kwargs):
                raise AssertionError('identical Blob should not be rewritten')

        store = object.__new__(AzureObjectStore)
        store._blob = lambda uri: ExistingBlob()
        with TemporaryDirectory() as directory:
            path = Path(directory) / 'artifact'
            path.write_bytes(b'foo')
            result = store.upload(path, 'https://example.blob.core.windows.net/c/a')
        self.assertEqual(3, result['Length'])


if __name__ == '__main__':
    unittest.main()
