import argparse
import json
import os
import sys

from .azure_store import AzureResizeStore
from .core import run_sweep
from .sql_manifest import AzureSqlResizeStore, run_manifest


def main():
    parser = argparse.ArgumentParser(description='Derive all smaller thumbnail widths')
    source = parser.add_mutually_exclusive_group(required=True)
    source.add_argument('--run-id')
    source.add_argument('--sql-manifest')
    args = parser.parse_args()
    if args.sql_manifest:
        store = AzureSqlResizeStore(os.getenv('CREATIONAL_STORAGE_ACCOUNT_URL',
                                             'https://creational.blob.core.windows.net'))
        print(json.dumps(run_manifest(store, store.load_manifest(args.sql_manifest))), flush=True)
        return
    store = AzureResizeStore(os.getenv('CREATIONAL_STORAGE_ACCOUNT_URL',
                                     'https://creational.blob.core.windows.net'))
    store.attach_run(args.run_id)
    run = store.run(args.run_id)
    if run.get('Status') not in ('pending', 'running', 'continuing'):
        raise ValueError('Resize run is not active')
    counters = {name: run.get(name, 0) for name in ('Processed', 'Generated', 'Skipped', 'Failed')}
    try:
        result = run_sweep(store, run.get('Cursor', ''), counters=counters)
        print(json.dumps(result), flush=True)
    except Exception as error:
        store.checkpoint(Status='failed', Error=str(error)[:1024])
        raise


if __name__ == '__main__':
    try:
        main()
    except Exception as error:
        print(json.dumps({'status': 'failed', 'error': str(error)}), file=sys.stderr, flush=True)
        raise
