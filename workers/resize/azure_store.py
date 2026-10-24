"""Azure Tables and Blobs adapter for the derived-thumbnail sweep."""

import hashlib

from azure.core.exceptions import ResourceNotFoundError
from azure.data.tables import EdmType, EntityProperty, TableServiceClient, UpdateMode
from azure.identity import DefaultAzureCredential
from azure.storage.blob import BlobServiceClient, ContentSettings

from .core import SourceDataError


RENDITIONS = '__thumbnail_renditions'
RUNS = '__resize_runs'


def _values(row):
    return {key: value.value if isinstance(value, EntityProperty) else value
            for key, value in row.items()}


class AzureResizeStore:
    def __init__(self, account_url='https://creational.blob.core.windows.net'):
        credential = DefaultAzureCredential()
        self._table = TableServiceClient(
            account_url.replace('.blob.', '.table.'), credential=credential
        ).get_table_client('CollectionTargets')
        self._container = BlobServiceClient(account_url, credential).get_container_client('wikimedia-extracts')

    def run(self, run_id):
        return _values(self._table.get_entity(partition_key=RUNS, row_key=run_id))

    def sources_after(self, cursor):
        query = "PartitionKey eq '__thumbnail_renditions' and Width eq 250"
        if cursor:
            query += f" and RowKey gt '{cursor}'"
        for row in self._table.query_entities(query_filter=query):
            if row.get('Status') == 'complete' and not row.get('Stale'):
                yield _values(row)

    def read_source(self, source):
        blob = self._container.get_blob_client(source['BlobName'])
        try:
            props = blob.get_blob_properties()
        except ResourceNotFoundError as error:
            raise SourceDataError('250px source Blob is missing') from error
        if props.size > 16 * 1024 * 1024 or props.size != source.get('Bytes') or \
                props.metadata.get('sha256') != source.get('Sha256'):
            raise SourceDataError('250px source Blob does not match its rendition row')
        try:
            return blob.download_blob(max_concurrency=1).readall()
        except ResourceNotFoundError as error:
            raise SourceDataError('250px source Blob disappeared during download') from error

    def rendition(self, row_key):
        try:
            return _values(self._table.get_entity(partition_key=RENDITIONS, row_key=row_key))
        except ResourceNotFoundError:
            return None

    def is_current(self, row, name):
        try:
            props = self._container.get_blob_client(name).get_blob_properties()
        except ResourceNotFoundError:
            return False
        if not (props.size == row.get('Bytes') and props.metadata.get('sha256') == row.get('Sha256') and \
            props.metadata.get('source_sha256') == row.get('SourceSha256') and \
            props.metadata.get('recipe') == row.get('RecipeVersion') and \
            props.content_settings.content_type == 'image/webp'):
            return False
        return hashlib.sha256(self._container.get_blob_client(name).download_blob(
            max_concurrency=1).readall()).hexdigest() == row.get('Sha256')

    def publish(self, name, data, source_hash, recipe):
        blob = self._container.get_blob_client(name)
        digest = hashlib.sha256(data).hexdigest()
        try:
            old = blob.get_blob_properties()
        except ResourceNotFoundError:
            old = None
        if old and old.size == len(data) and old.metadata.get('sha256') == digest and \
                old.metadata.get('source_sha256') == source_hash and old.metadata.get('recipe') == recipe and \
                old.content_settings.content_type == 'image/webp':
            actual = hashlib.sha256(blob.download_blob(max_concurrency=1).readall()).hexdigest()
            if actual == digest:
                self._delete_previous(name, old.metadata.get('previous_version'))
                return
        if old and not old.version_id:
            raise ValueError('Blob versioning is required to replace a derived thumbnail')
        metadata = {'sha256': digest, 'source_sha256': source_hash, 'recipe': recipe}
        if old:
            metadata['previous_version'] = old.version_id
        blob.upload_blob(data, overwrite=True, content_settings=ContentSettings(content_type='image/webp'),
                         metadata=metadata)
        committed = blob.get_blob_properties()
        if committed.size != len(data) or committed.metadata.get('sha256') != digest or \
                committed.content_settings.content_type != 'image/webp':
            raise ValueError('Derived thumbnail upload did not verify')
        actual = hashlib.sha256(blob.download_blob(max_concurrency=1).readall()).hexdigest()
        if actual != digest:
            raise ValueError('Derived thumbnail content checksum did not verify')
        if old:
            self._delete_previous(name, old.version_id)

    def _delete_previous(self, name, version_id):
        if not version_id:
            return
        try:
            self._container.get_blob_client(name, version_id=version_id).delete_blob()
        except ResourceNotFoundError:
            pass

    def save_rendition(self, row):
        entity = {'PartitionKey': RENDITIONS, **row}
        if 'Bytes' in entity:
            entity['Bytes'] = EntityProperty(entity['Bytes'], EdmType.INT64)
        self._table.upsert_entity(entity, mode=UpdateMode.REPLACE)
        if row.get('Status') == 'failed':
            self._table.upsert_entity({
                'PartitionKey': '__resize_failures',
                'RowKey': f"{self._run_id}-{row['RowKey']}",
                'Title': row.get('Title', ''),
                'Width': row['Width'],
                'Error': row.get('Error', ''),
            }, mode=UpdateMode.REPLACE)

    def checkpoint(self, **fields):
        entity = {'PartitionKey': RUNS, 'RowKey': self._run_id, **fields}
        self._table.upsert_entity(entity, mode=UpdateMode.MERGE)

    def attach_run(self, run_id):
        self._run_id = run_id
