"""Package the built plugin and derive a Dalamud feed from its generated manifest."""
import argparse
import json
from pathlib import Path
import time
import zipfile

parser = argparse.ArgumentParser()
parser.add_argument('--build', type=Path, default=Path('MarketMage/bin/x64/Release'))
parser.add_argument('--output', type=Path, default=Path('release'))
parser.add_argument('--tag', required=True)
parser.add_argument('--repository', default='vitolscolin/MarketMage')
args = parser.parse_args()
manifest = json.loads((args.build / 'MarketMage.json').read_text(encoding='utf-8-sig'))
version = manifest['AssemblyVersion']
if args.tag != 'v' + '.'.join(version.split('.')[:3]):
    raise SystemExit(f'Tag {args.tag} does not match assembly version {version}')
if manifest['InternalName'] != 'MarketMage' or not isinstance(manifest['DalamudApiLevel'], int):
    raise SystemExit('Unexpected built plugin manifest')
args.output.mkdir(parents=True, exist_ok=True)
with zipfile.ZipFile(args.output / 'MarketMage.zip', 'w', zipfile.ZIP_DEFLATED) as archive:
    for name in ('MarketMage.dll', 'MarketMage.json', 'MarketMage.deps.json', 'goat.png'):
        path = args.build / name
        if not path.is_file():
            raise SystemExit(f'Missing release file: {path}')
        archive.write(path, name)
url = f'https://github.com/{args.repository}/releases/download/{args.tag}/MarketMage.zip'
manifest.update(RepoUrl=f'https://github.com/{args.repository}', IsHide=False,
                IsTestingExclusive=False, DownloadLinkInstall=url,
                DownloadLinkUpdate=url, DownloadLinkTesting=url, LastUpdate=int(time.time()))
(args.output / 'pluginmaster.json').write_text(json.dumps([manifest], indent=2) + '\n', encoding='utf-8')
print(f'Packaged MarketMage {version}, Dalamud API {manifest["DalamudApiLevel"]}')
