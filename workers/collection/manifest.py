"""Small Blob adapter used only inside short-lived Container Apps jobs."""

import hashlib
import json
from pathlib import Path
from urllib.parse import urlparse


def sha256_file(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open('rb') as stream:
        for chunk in iter(lambda: stream.read(4 * 1024 * 1024), b''):
            digest.update(chunk)
    return digest.hexdigest()


class AzureObjectStore:
    def __init__(self):
        from azure.identity import DefaultAzureCredential
        self.credential = DefaultAzureCredential()

    def _blob(self, uri):
        from azure.storage.blob import BlobClient
        parsed = urlparse(uri)
        if parsed.scheme != 'https' or not parsed.netloc.endswith('.blob.core.windows.net'):
            raise ValueError('Expected an Azure Blob HTTPS URI')
        return BlobClient.from_blob_url(uri, credential=self.credential)

    def read_json(self, uri):
        return json.loads(self._blob(uri).download_blob().readall())

    def download(self, uri, path):
        path = Path(path)
        path.parent.mkdir(parents=True, exist_ok=True)
        with path.open('wb') as output:
            for chunk in self._blob(uri).download_blob().chunks():
                output.write(chunk)
        return path

    def upload(self, path, uri):
        path = Path(path)
        digest = sha256_file(path)
        blob = self._blob(uri)
        existing = self._properties_or_none(blob)
        if existing is not None:
            if existing.size == path.stat().st_size and existing.metadata.get('sha256') == digest:
                return {'Length': existing.size, 'Sha256': digest}
            raise RuntimeError(f'Existing artifact differs: {uri}')
        try:
            with path.open('rb') as source:
                blob.upload_blob(source, overwrite=False, metadata={'sha256': digest},
                                 max_concurrency=2)
        except Exception as error:
            if getattr(error, 'status_code', None) not in (409, 412):
                raise
        props = blob.get_blob_properties()
        if props.size != path.stat().st_size or props.metadata.get('sha256') != digest:
            raise RuntimeError(f'Uploaded artifact verification failed: {uri}')
        return {'Length': props.size, 'Sha256': digest}

    def write_json(self, uri, value):
        payload = json.dumps(value, sort_keys=True, separators=(',', ':')).encode()
        digest = hashlib.sha256(payload).hexdigest()
        blob = self._blob(uri)
        existing = self._properties_or_none(blob)
        if existing is not None:
            if existing.size == len(payload) and existing.metadata.get('sha256') == digest:
                return
            raise RuntimeError(f'Existing completion marker differs: {uri}')
        try:
            blob.upload_blob(payload, overwrite=False, metadata={'sha256': digest})
        except Exception as error:
            if getattr(error, 'status_code', None) not in (409, 412):
                raise
            existing = blob.get_blob_properties()
            if existing.size != len(payload) or existing.metadata.get('sha256') != digest:
                raise RuntimeError(f'Existing completion marker differs: {uri}') from error

    @staticmethod
    def _properties_or_none(blob):
        try:
            return blob.get_blob_properties()
        except Exception as error:
            if getattr(error, 'status_code', None) == 404:
                return None
            raise
