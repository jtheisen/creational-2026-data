"""Pure resize logic and the resumable sweep loop."""

import hashlib
from io import BytesIO
from pathlib import PurePosixPath
import time

from PIL import Image, ImageOps, UnidentifiedImageError


WIDTHS = (20, 40, 60, 120)
RECIPE = 'webp-lanczos-q82-v1'


class SourceDataError(Exception):
    """A specific source image is missing or no longer matches its catalog row."""


def derived_blob_name(source_name, width):
    if width not in WIDTHS or not source_name.startswith('thumbnails/250px/'):
        raise ValueError('Expected a 250px thumbnail and one supported derived width')
    source = PurePosixPath(source_name)
    return str(PurePosixPath('thumbnails', f'{width}px', *source.parts[2:-1],
                            source.stem + '.webp'))


def resize_webp(data, width):
    if width not in WIDTHS:
        raise ValueError('Unsupported thumbnail width')
    with Image.open(BytesIO(data)) as opened:
        opened.seek(0)  # Animated input uses the first frame as a static preview.
        image = ImageOps.exif_transpose(opened)
        if image.mode not in ('RGB', 'RGBA'):
            image = image.convert('RGBA' if 'A' in image.getbands() or 'transparency' in image.info else 'RGB')
        if image.width > width:
            height = max(1, round(image.height * width / image.width))
            image = image.resize((width, height), Image.Resampling.LANCZOS)
        result = BytesIO()
        image.save(result, 'WEBP', quality=82, method=4)
        return result.getvalue()


def _hash(data):
    return hashlib.sha256(data).hexdigest()


def process_source(source, store):
    """Return generated, skipped, failed rendition counts for one source."""
    if source.get('Status') != 'complete' or source.get('Stale'):
        return 0, 0, 0
    generated = skipped = failed = 0
    try:
        data = store.read_source(source)
        if len(data) != source.get('Bytes') or _hash(data) != source.get('Sha256'):
            raise ValueError('250px source differs from its rendition checksum')
        source_error = None
    except (ValueError, SourceDataError) as error:
        data = b''
        source_error = str(error)
    for width in WIDTHS:
        row_key = source['RowKey'].rsplit('-', 1)[0] + f'-{width}'
        name = derived_blob_name(source['BlobName'], width)
        old = store.rendition(row_key)
        if source_error is None and old and old.get('Status') == 'complete' and not old.get('Stale') and \
                old.get('SourceSha256') == source['Sha256'] and old.get('RecipeVersion') == RECIPE and \
                store.is_current(old, name):
            skipped += 1
            continue
        row = {'RowKey': row_key, 'Title': source['Title'], 'Width': width,
               'BlobName': name, 'SourceSha256': source['Sha256'],
               'RecipeVersion': RECIPE, 'Stale': False}
        try:
            if source_error is not None:
                raise SourceDataError(source_error)
            output = resize_webp(data, width)
        except (SourceDataError, UnidentifiedImageError, OSError, Image.DecompressionBombError) as error:
            row.update(Status='failed', Stale=True, Error=str(error)[:1024])
            failed += 1
            store.save_rendition(row)
            continue
        store.publish(name, output, source['Sha256'], RECIPE)
        row.update(Status='complete', Bytes=len(output), Sha256=_hash(output))
        generated += 1
        store.save_rendition(row)
    return generated, skipped, failed


def run_sweep(store, cursor='', deadline_seconds=3 * 3600 + 40 * 60, counters=None):
    """Process the source Table stream until exhausted or the safe deadline."""
    result = dict(counters or {'Processed': 0, 'Generated': 0, 'Skipped': 0, 'Failed': 0})
    started = time.monotonic()
    for source in store.sources_after(cursor):
        if time.monotonic() - started >= deadline_seconds:
            result.update(Status='continuing', Cursor=cursor)
            store.checkpoint(**result)
            return result
        generated, skipped, failed = process_source(source, store)
        result['Processed'] += 1
        result['Generated'] += generated
        result['Skipped'] += skipped
        result['Failed'] += failed
        cursor = source['RowKey']
        result.update(Status='running', Cursor=cursor)
        store.checkpoint(**result)
    result.update(Status='complete-with-errors' if result['Failed'] else 'complete', Cursor=cursor)
    store.checkpoint(**result)
    return result
