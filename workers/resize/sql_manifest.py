"""Bounded SQL-owned resize batches. This worker only reads and writes Blobs."""

import hashlib
import json
import sys
import uuid

from azure.core import MatchConditions
from azure.core.exceptions import HttpResponseError, ResourceExistsError, ResourceNotFoundError
from azure.identity import DefaultAzureCredential
from azure.storage.blob import BlobServiceClient, ContentSettings

from .core import RECIPE, WIDTHS, resize_webp


def _digest(data):
    return hashlib.sha256(data).hexdigest()


def run_manifest(store, manifest):
    results = []
    for item in manifest['Items']:
        image_id = item['ImageId']
        source_hash = item['SourceSha256']
        outputs = []
        try:
            source = store.read_source(image_id, item['SourceBytes'], source_hash)
            for width in WIDTHS:
                try:
                    current = store.current(image_id, width, source_hash, RECIPE)
                    if current is not None:
                        outputs.append({'Width': width, 'Status': 'current', **current})
                        continue
                    data = resize_webp(source, width)
                    checksum = _digest(data)
                    store.publish(image_id, width, data, source_hash, RECIPE, checksum)
                    outputs.append({'Width': width, 'Status': 'complete', 'Bytes': len(data),
                                    'Sha256': checksum})
                except Exception as error:
                    outputs.append({'Width': width, 'Status': 'failed', 'Error': str(error)[:1024]})
        except Exception as error:
            outputs = [{'Width': width, 'Status': 'failed', 'Error': str(error)[:1024]}
                       for width in WIDTHS]
        results.append({'ImageId': image_id, 'SourceSha256': source_hash, 'Shapes': outputs})
    result = {'BatchId': manifest['BatchId'], 'Items': results}
    store.publish_result(manifest['BatchId'], result)
    return result


class AzureSqlResizeStore:
    def __init__(self, account_url='https://creational.blob.core.windows.net'):
        blobs = BlobServiceClient(account_url, DefaultAzureCredential())
        self._images = blobs.get_container_client('images')
        self._work = blobs.get_container_client('wikimedia-extracts')

    def load_manifest(self, path):
        return json.loads(self._work.get_blob_client(path).download_blob().readall())

    def read_source(self, image_id, expected_bytes, expected_hash):
        blob = self._images.get_blob_client(f'creatures/w250/{image_id}')
        props = blob.get_blob_properties()
        if props.size != expected_bytes or props.metadata.get('sha256') != expected_hash:
            raise ValueError('Source Blob properties differ from resize manifest')
        data = blob.download_blob(max_concurrency=1).readall()
        if len(data) != expected_bytes or _digest(data) != expected_hash:
            raise ValueError('Source Blob checksum differs from resize manifest')
        return data

    def current(self, image_id, width, source_hash, recipe):
        blob = self._images.get_blob_client(f'creatures/w{width}/{image_id}')
        try:
            props = blob.get_blob_properties()
        except ResourceNotFoundError:
            return None
        metadata = props.metadata
        if metadata.get('source_sha256') != source_hash or metadata.get('recipe') != recipe or \
                props.content_settings.content_type != 'image/webp':
            return None
        data = blob.download_blob(max_concurrency=1).readall()
        checksum = _digest(data)
        if len(data) != props.size or checksum != metadata.get('sha256'):
            return None
        return {'Bytes': len(data), 'Sha256': checksum}

    def publish(self, image_id, width, data, source_hash, recipe, checksum):
        blob = self._images.get_blob_client(f'creatures/w{width}/{image_id}')
        try:
            old = blob.get_blob_properties()
        except ResourceNotFoundError:
            old = None
        if old and not old.version_id:
            raise ValueError('Blob versioning is required to replace an existing rendition')
        conditions = {'etag': old.etag, 'match_condition': MatchConditions.IfNotModified} \
            if old else {}
        attempt_id = uuid.uuid4().hex
        try:
            written = blob.upload_blob(data, overwrite=bool(old),
                                       content_settings=ContentSettings(content_type='image/webp'),
                                       metadata={'sha256': checksum, 'source_sha256': source_hash,
                                                 'recipe': recipe, 'write_attempt': attempt_id},
                                       **conditions)
            current = blob.get_blob_properties()
            if current.etag != written['etag'] or \
                    self.current(image_id, width, source_hash, recipe) != \
                    {'Bytes': len(data), 'Sha256': checksum}:
                raise ValueError('Derived Blob failed verification')
        except Exception:
            try:
                committed = blob.get_blob_properties()
            except ResourceNotFoundError:
                committed = None
            try:
                if committed and committed.metadata.get('write_attempt') == attempt_id:
                    if old:
                        previous = self._images.get_blob_client(
                            f'creatures/w{width}/{image_id}', version_id=old.version_id)
                        blob.upload_blob(previous.download_blob().readall(), overwrite=True,
                                         etag=committed.etag,
                                         match_condition=MatchConditions.IfNotModified,
                                         content_settings=old.content_settings, metadata=old.metadata)
                    else:
                        blob.delete_blob(etag=committed.etag,
                                         match_condition=MatchConditions.IfNotModified)
            except HttpResponseError as error:
                if error.status_code != 412:
                    raise
            raise
        if old:
            try:
                self._images.get_blob_client(f'creatures/w{width}/{image_id}',
                                             version_id=old.version_id).delete_blob()
            except Exception as error:
                if not isinstance(error, HttpResponseError) or \
                        (error.status_code != 404 and
                         error.error_code != 'OperationNotAllowedOnAutomaticSnapshot'):
                    print(f'Previous rendition version cleanup deferred for {image_id}/{width}: '
                          f'{error}', file=sys.stderr)

    def publish_result(self, batch_id, result):
        blob = self._work.get_blob_client(f'sql-resize/results/{batch_id}.json')
        payload = json.dumps(result, separators=(',', ':')).encode()
        try:
            blob.upload_blob(payload, overwrite=False,
                             content_settings=ContentSettings(content_type='application/json'))
        except ResourceExistsError:
            if blob.download_blob().readall() != payload:
                raise ValueError('Existing resize result marker differs from this execution')
