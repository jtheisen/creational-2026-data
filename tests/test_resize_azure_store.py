import hashlib
from types import SimpleNamespace
import unittest
from unittest.mock import patch

from azure.core.exceptions import ResourceNotFoundError
from azure.data.tables import EdmType, EntityProperty

from workers.resize.azure_store import AzureResizeStore


class FakeBlob:
    def __init__(self, container, version_id=None):
        self.container = container
        self.version_id = version_id

    def get_blob_properties(self):
        if self.container.current is None:
            raise ResourceNotFoundError('missing')
        return self.container.current

    def upload_blob(self, data, overwrite, content_settings, metadata):
        assert overwrite
        if self.container.current is not None:
            self.container.previous = self.container.current
        self.container.current = SimpleNamespace(
            size=len(data), metadata=metadata, version_id='new-version',
            content_settings=content_settings, data=data)

    def download_blob(self, **kwargs):
        return SimpleNamespace(readall=lambda: self.container.current.data)

    def delete_blob(self):
        self.container.deleted.append(self.version_id)


class FakeContainer:
    def __init__(self, current=None):
        self.current = current
        self.previous = None
        self.deleted = []

    def get_blob_client(self, name, version_id=None):
        return FakeBlob(self, version_id)


class AzureResizeStoreTests(unittest.TestCase):
    def test_constructs_table_client_with_keyword_credential(self):
        with patch('workers.resize.azure_store.DefaultAzureCredential') as credential, \
                patch('workers.resize.azure_store.TableServiceClient') as tables, \
                patch('workers.resize.azure_store.BlobServiceClient'):
            AzureResizeStore()
        self.assertIs(tables.call_args.kwargs['credential'], credential.return_value)

    def test_int64_values_are_unwrapped_for_reads_and_written_as_int64(self):
        class FakeTable:
            def __init__(self): self.saved = None
            def query_entities(self, query_filter):
                yield {'RowKey': 'hash-250', 'Width': 250, 'Status': 'complete',
                       'Bytes': EntityProperty(123, EdmType.INT64)}
            def upsert_entity(self, entity, mode): self.saved = entity
        table = FakeTable()
        store = object.__new__(AzureResizeStore)
        store._table = table

        self.assertEqual(123, list(store.sources_after(''))[0]['Bytes'])
        store.save_rendition({'RowKey': 'hash-20', 'Width': 20, 'Bytes': 42,
                              'Status': 'complete'})
        self.assertEqual(EntityProperty(42, EdmType.INT64), table.saved['Bytes'])

    def test_failed_rendition_is_indexed_by_run_for_dashboard(self):
        class FakeTable:
            def __init__(self): self.rows = []
            def upsert_entity(self, entity, mode): self.rows.append(entity)
        table = FakeTable()
        store = object.__new__(AzureResizeStore)
        store._table = table
        store.attach_run('run-123')

        store.save_rendition({'RowKey': 'hash-40', 'Title': 'File:Cat.jpg',
                              'Width': 40, 'Status': 'failed', 'Error': 'decode failed'})

        self.assertEqual('__resize_failures', table.rows[1]['PartitionKey'])
        self.assertEqual('run-123-hash-40', table.rows[1]['RowKey'])
        self.assertEqual('decode failed', table.rows[1]['Error'])

    def test_replacement_soft_deletes_previous_version_after_verified_upload(self):
        old = SimpleNamespace(size=3, metadata={'sha256': 'old'}, version_id='old-version',
                              content_settings=SimpleNamespace(content_type='image/webp'), data=b'old')
        container = FakeContainer(old)
        store = object.__new__(AzureResizeStore)
        store._container = container

        store.publish('thumbnails/20px/a/cat.webp', b'new', 'source-hash', 'recipe-v1')

        self.assertEqual(b'new', container.current.data)
        self.assertEqual(['old-version'], container.deleted)
        self.assertEqual('source-hash', container.current.metadata['source_sha256'])
        self.assertEqual('image/webp', container.current.content_settings.content_type)

    def test_recovery_deletes_old_version_without_reuploading_current_bytes(self):
        current = SimpleNamespace(size=3, metadata={
            'sha256': hashlib.sha256(b'new').hexdigest(),
            'source_sha256': 'source-hash', 'recipe': 'recipe-v1',
            'previous_version': 'old-version'}, version_id='new-version',
            content_settings=SimpleNamespace(content_type='image/webp'), data=b'new')
        container = FakeContainer(current)
        store = object.__new__(AzureResizeStore)
        store._container = container

        store.publish('thumbnails/20px/a/cat.webp', b'new', 'source-hash', 'recipe-v1')

        self.assertIs(current, container.current)
        self.assertEqual(['old-version'], container.deleted)

    def test_corrupt_current_bytes_are_replaced_even_if_metadata_matches(self):
        current = SimpleNamespace(size=3, metadata={
            'sha256': hashlib.sha256(b'new').hexdigest(),
            'source_sha256': 'source-hash', 'recipe': 'recipe-v1'},
            version_id='corrupt-version',
            content_settings=SimpleNamespace(content_type='image/webp'), data=b'bad')
        container = FakeContainer(current)
        store = object.__new__(AzureResizeStore)
        store._container = container

        store.publish('thumbnails/20px/a/cat.webp', b'new', 'source-hash', 'recipe-v1')

        self.assertEqual(b'new', container.current.data)
        self.assertEqual(['corrupt-version'], container.deleted)

    def test_current_check_hashes_blob_bytes(self):
        current = SimpleNamespace(size=3, metadata={
            'sha256': hashlib.sha256(b'new').hexdigest(),
            'source_sha256': 'source-hash', 'recipe': 'recipe-v1'},
            version_id='v1', content_settings=SimpleNamespace(content_type='image/webp'),
            data=b'bad')
        store = object.__new__(AzureResizeStore)
        store._container = FakeContainer(current)
        row = {'Bytes': 3, 'Sha256': hashlib.sha256(b'new').hexdigest(),
               'SourceSha256': 'source-hash', 'RecipeVersion': 'recipe-v1'}

        self.assertFalse(store.is_current(row, 'thumbnails/20px/a/cat.webp'))


if __name__ == '__main__':
    unittest.main()
