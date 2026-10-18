import hashlib
import io
import unittest
from unittest.mock import patch

import copy_dump_set as copier


class Process:
    def __init__(self, data, exit_code=0):
        self.stdout = io.BytesIO(data)
        self.exit_code = exit_code

    def wait(self, timeout=None):
        return self.exit_code

    def poll(self):
        return self.exit_code


class TransferTests(unittest.TestCase):
    def test_verified_stream_commits_checksum_metadata(self):
        data = b'a tiny dump split across blocks'
        checksum = hashlib.sha256(data).hexdigest()
        with patch.object(copier.subprocess, 'Popen', return_value=Process(data)), \
             patch.object(copier, 'BLOCK_SIZE', 7), \
             patch.object(copier, 'azure') as azure, \
             patch.object(copier, 'complete', return_value=True), \
             patch.object(copier, 'log'):
            copier.transfer('sample.xml.bz2', len(data), checksum)
        commits = [c for c in azure.call_args_list if 'comp=blocklist' in c.args[1]]
        self.assertEqual(len(commits), 1)
        self.assertEqual(commits[0].args[3]['x-ms-meta-sha256'], checksum)

    def test_bad_or_truncated_source_never_commits(self):
        for data, size, checksum, exit_code in [
            (b'bad', 3, '0' * 64, 0),
            (b'short', 10, hashlib.sha256(b'short').hexdigest(), 0),
            (b'failed', 6, hashlib.sha256(b'failed').hexdigest(), 22),
        ]:
            with self.subTest(data=data), \
                 patch.object(copier.subprocess, 'Popen', return_value=Process(data, exit_code)), \
                 patch.object(copier, 'azure') as azure, \
                 patch.object(copier, 'log'):
                with self.assertRaises((ValueError, RuntimeError)):
                    copier.transfer('sample.xml.bz2', size, checksum)
                self.assertFalse(any('comp=blocklist' in c.args[1] for c in azure.call_args_list))

    def test_existing_blob_requires_size_and_checksum(self):
        with patch.object(copier, 'azure', return_value={
            'Content-Length': '123', 'x-ms-meta-sha256': 'abc',
        }):
            self.assertTrue(copier.complete('sample', 123, 'abc'))
            self.assertFalse(copier.complete('sample', 124, 'abc'))
            self.assertFalse(copier.complete('sample', 123, 'other'))


if __name__ == '__main__':
    unittest.main()
