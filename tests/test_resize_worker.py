import hashlib
from io import BytesIO
import unittest
from unittest.mock import patch

from PIL import Image

from workers.resize.core import RECIPE, WIDTHS, derived_blob_name, process_source, resize_webp, run_sweep


def picture(size=(250, 100), mode='RGB', orientation=None):
    image = Image.new(mode, size, (255, 0, 0, 90) if mode == 'RGBA' else (255, 0, 0))
    output = BytesIO()
    exif = Image.Exif()
    if orientation:
        exif[274] = orientation
    image.save(output, 'PNG', exif=exif)
    return output.getvalue()


def source_row(title='File:Cat.jpg', blob='thumbnails/250px/ab/hash/Cat.jpg', data=None):
    data = data or picture()
    return {'RowKey': 'hash-250', 'Title': title, 'Width': 250, 'BlobName': blob,
            'Status': 'complete', 'Stale': False, 'Bytes': len(data),
            'Sha256': hashlib.sha256(data).hexdigest()}


class FakeStore:
    def __init__(self, sources, data):
        self.sources = sources
        self.data = data
        self.rows = {}
        self.blobs = {}
        self.progress = []
        self.fail_width = None

    def sources_after(self, cursor):
        yield from (x for x in self.sources if x['RowKey'] > cursor)

    def read_source(self, source):
        return self.data[source['RowKey']]

    def rendition(self, row_key):
        return self.rows.get(row_key)

    def is_current(self, row, name):
        blob = self.blobs.get(name)
        return blob is not None and len(blob) == row.get('Bytes') and hashlib.sha256(blob).hexdigest() == row.get('Sha256')

    def publish(self, name, data, source_hash, recipe):
        if self.fail_width and f'/{self.fail_width}px/' in name:
            raise OSError('image encode failed')
        self.blobs[name] = data

    def save_rendition(self, row):
        self.rows[row['RowKey']] = row

    def checkpoint(self, **fields):
        self.progress.append(fields)


class ResizeWorkerTests(unittest.TestCase):
    def test_width_is_exact_without_cropping_and_orientation_is_applied(self):
        result = resize_webp(picture((250, 100), orientation=6), 40)
        with Image.open(BytesIO(result)) as image:
            self.assertEqual((40, 100), image.size)
            self.assertEqual('WEBP', image.format)

    def test_transparency_is_retained_and_small_images_are_not_enlarged(self):
        result = resize_webp(picture((15, 12), mode='RGBA'), 20)
        with Image.open(BytesIO(result)) as image:
            self.assertEqual((15, 12), image.size)
            self.assertEqual('RGBA', image.mode)
            self.assertLess(image.getpixel((0, 0))[3], 255)

    def test_derived_filename_uses_webp_inside_same_hash_directory(self):
        self.assertEqual('thumbnails/60px/ab/hash/Cat.webp',
                         derived_blob_name('thumbnails/250px/ab/hash/Cat.jpg', 60))

    def test_all_four_widths_are_recorded_with_source_lineage(self):
        data = picture()
        source = source_row(data=data)
        store = FakeStore([source], {source['RowKey']: data})
        result = process_source(source, store)
        self.assertEqual((4, 0, 0), result)
        self.assertEqual(WIDTHS, tuple(sorted(row['Width'] for row in store.rows.values())))
        for row in store.rows.values():
            self.assertEqual(source['Sha256'], row['SourceSha256'])
            self.assertEqual(RECIPE, row['RecipeVersion'])
            self.assertEqual('complete', row['Status'])
            self.assertTrue(row['BlobName'].endswith('.webp'))

    def test_current_renditions_are_skipped_and_old_source_is_rebuilt(self):
        data = picture()
        source = source_row(data=data)
        store = FakeStore([source], {source['RowKey']: data})
        self.assertEqual((4, 0, 0), process_source(source, store))
        self.assertEqual((0, 4, 0), process_source(source, store))
        stale = store.rows['hash-60']
        stale['SourceSha256'] = 'old'
        self.assertEqual((1, 3, 0), process_source(source, store))
        self.assertEqual(source['Sha256'], store.rows['hash-60']['SourceSha256'])

    def test_source_checksum_mismatch_cannot_publish_output(self):
        data = picture()
        source = source_row(data=data) | {'Sha256': 'wrong'}
        store = FakeStore([source], {source['RowKey']: data})
        self.assertEqual((0, 0, 4), process_source(source, store))
        self.assertEqual({}, store.blobs)
        self.assertEqual({'failed'}, {row['Status'] for row in store.rows.values()})

    def test_blob_service_failure_stops_without_advancing_cursor(self):
        data = picture()
        source = source_row(data=data)
        store = FakeStore([source], {source['RowKey']: data})
        store.read_source = lambda source: (_ for _ in ()).throw(RuntimeError('Blob service unavailable'))

        with self.assertRaisesRegex(RuntimeError, 'Blob service unavailable'):
            run_sweep(store)

        self.assertEqual([], store.progress)
        self.assertEqual({}, store.rows)

    def test_one_failed_width_does_not_block_other_images_and_cursor_advances(self):
        data = picture()
        first = source_row(data=data)
        second = source_row(title='File:Dog.jpg', blob='thumbnails/250px/bc/hash2/Dog.jpg', data=data) | {'RowKey': 'hash2-250'}
        store = FakeStore([first, second], {first['RowKey']: data, second['RowKey']: data})
        original = resize_webp
        def selective_failure(data, width):
            if width == 40:
                raise OSError('image encode failed')
            return original(data, width)
        with patch('workers.resize.core.resize_webp', selective_failure):
            result = run_sweep(store, cursor='', deadline_seconds=60)
        self.assertEqual('complete-with-errors', result['Status'])
        self.assertEqual(2, result['Processed'])
        self.assertEqual(2, result['Failed'])
        self.assertEqual(second['RowKey'], store.progress[-1]['Cursor'])

    def test_output_blob_service_failure_stops_without_advancing_cursor(self):
        data = picture()
        source = source_row(data=data)
        store = FakeStore([source], {source['RowKey']: data})
        store.publish = lambda *args: (_ for _ in ()).throw(RuntimeError('Blob upload unavailable'))

        with self.assertRaisesRegex(RuntimeError, 'Blob upload unavailable'):
            run_sweep(store)

        self.assertEqual([], store.progress)


if __name__ == '__main__':
    unittest.main()
