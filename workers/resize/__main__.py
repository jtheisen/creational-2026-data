import argparse
import json
import os
import sys

from .azure_store import AzureResizeStore
from .core import run_sweep


def main():
    parser = argparse.ArgumentParser(description='Derive all smaller thumbnail widths')
    parser.add_argument('--run-id', required=True)
    args = parser.parse_args()
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
