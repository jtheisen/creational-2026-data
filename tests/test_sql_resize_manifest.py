import hashlib
from io import BytesIO
import unittest
from types import SimpleNamespace

from PIL import Image

from workers.resize.sql_manifest import AzureSqlResizeStore, run_manifest


def _source():
    output = BytesIO()
    Image.new('RGB', (250, 125), 'red').save(output, 'PNG')
    return output.getvalue()


class FakeStore:
    def __init__(self, source):
        self.source = source
        self.outputs = {}
        self.result = None

    def read_source(self, image_id, expected_bytes, expected_hash):
        assert image_id == 42
        assert len(self.source) == expected_bytes
        assert hashlib.sha256(self.source).hexdigest() == expected_hash
        return self.source

    def current(self, image_id, width, source_hash, recipe):
        return self.outputs.get(width)

    def publish(self, image_id, width, data, source_hash, recipe, checksum):
        self.outputs[width] = {'Bytes': len(data), 'Sha256': checksum}

    def publish_result(self, batch_id, result):
        self.result = result


class SqlResizeManifestTests(unittest.TestCase):
    def test_cleanup_failure_does_not_fail_verified_publication(self):
        class Blob:
            def __init__(self):
                self.props = SimpleNamespace(version_id='old-version', etag='old-etag',
                    metadata={}, content_settings=SimpleNamespace(content_type='image/webp'))

            def get_blob_properties(self):
                return self.props

            def upload_blob(self, data, **kwargs):
                self.props = SimpleNamespace(version_id='new-version', etag='new-etag',
                    metadata=kwargs['metadata'], content_settings=kwargs['content_settings'])
                return {'etag': 'new-etag'}

        class Version:
            def delete_blob(self):
                raise TimeoutError('cleanup timed out')

        blob = Blob()
        store = AzureSqlResizeStore.__new__(AzureSqlResizeStore)
        store._images = SimpleNamespace(get_blob_client=lambda path, version_id=None:
                                        Version() if version_id else blob)
        store.current = lambda *args: {'Bytes': 3, 'Sha256': 'sha'}
        store.publish(42, 20, b'new', 'source', 'recipe', 'sha')
        self.assertEqual(blob.props.etag, 'new-etag')

    def test_lost_upload_response_restores_previous_rendition(self):
        prior = b'previous image'
        prior_metadata = {'sha256': hashlib.sha256(prior).hexdigest()}

        class Blob:
            def __init__(self):
                self.data = prior
                self.properties = SimpleNamespace(version_id='old-version', etag='old-etag',
                    metadata=prior_metadata, content_settings=SimpleNamespace(content_type='image/webp'))
                self.writes = 0

            def get_blob_properties(self):
                return self.properties

            def upload_blob(self, data, **kwargs):
                self.writes += 1
                self.data = data
                self.properties = SimpleNamespace(version_id='new-version',
                    etag=f'etag-{self.writes}', metadata=kwargs['metadata'],
                    content_settings=kwargs['content_settings'])
                if self.writes == 1:
                    raise TimeoutError('response lost after commit')
                return {'etag': self.properties.etag}

        class Version:
            def download_blob(self):
                return SimpleNamespace(readall=lambda: prior)

        blob = Blob()
        store = AzureSqlResizeStore.__new__(AzureSqlResizeStore)
        store._images = SimpleNamespace(get_blob_client=lambda path, version_id=None:
                                        Version() if version_id else blob)
        with self.assertRaises(TimeoutError):
            store.publish(42, 20, b'replacement', 'source', 'recipe', 'checksum')
        self.assertEqual(blob.data, prior)
        self.assertEqual(blob.properties.metadata, prior_metadata)

    def test_manifest_resizes_then_skips_current_shapes(self):
        source = _source()
        digest = hashlib.sha256(source).hexdigest()
        store = FakeStore(source)
        manifest = {'BatchId': 'batch', 'Items': [
            {'ImageId': 42, 'SourceBytes': len(source), 'SourceSha256': digest}]}
        first = run_manifest(store, manifest)
        self.assertEqual([item['Status'] for item in first['Items'][0]['Shapes']], ['complete'] * 4)
        second = run_manifest(store, manifest)
        self.assertEqual([item['Status'] for item in second['Items'][0]['Shapes']], ['current'] * 4)
        self.assertIs(second, store.result)

    def test_manifest_reports_source_failure_for_every_shape(self):
        store = FakeStore(_source())

        def broken(*_):
            raise ValueError('source changed')

        store.read_source = broken
        result = run_manifest(store, {'BatchId': 'batch', 'Items': [
            {'ImageId': 42, 'SourceBytes': 1, 'SourceSha256': 'bad'}]})
        self.assertEqual(len(result['Items'][0]['Shapes']), 4)
        self.assertTrue(all(shape['Status'] == 'failed' for shape in result['Items'][0]['Shapes']))
        self.assertIs(result, store.result)
