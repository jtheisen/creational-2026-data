import argparse
import json
import sys
from tempfile import TemporaryDirectory

from .manifest import AzureObjectStore
from .stages import execute


def main():
    parser = argparse.ArgumentParser(description='Run one collection target')
    parser.add_argument('--target-id', required=True)
    parser.add_argument('--inputs-manifest', required=True)
    parser.add_argument('--output-prefix', required=True)
    parser.add_argument('--recipe-version', required=True)
    args = parser.parse_args()
    if not args.output_prefix.endswith('/'):
        parser.error('--output-prefix must end in /')
    collection, kind, key = args.target_id.rsplit('/', 2)
    store = AzureObjectStore()
    inputs = store.read_json(args.inputs_manifest)
    expected = {'Collection': collection, 'Kind': kind, 'Key': key}
    if inputs.get('Target') != expected:
        raise ValueError('Input manifest target does not match --target-id')
    with TemporaryDirectory(prefix='creational-worker-') as temporary:
        result = execute(kind, inputs, temporary, store, args.output_prefix, args.recipe_version)
    print(json.dumps({'state': 'complete', 'target': args.target_id,
                      'artifacts': len(result['Artifacts'])}), flush=True)


if __name__ == '__main__':
    try:
        main()
    except Exception as error:
        print(json.dumps({'state': 'failed', 'error': str(error)}), file=sys.stderr, flush=True)
        raise
