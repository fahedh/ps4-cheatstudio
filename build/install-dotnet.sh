#!/usr/bin/env bash
set -euo pipefail
studio_sdk_root="${1:-/workspace/.dotnet}"
if [[ -x "$studio_sdk_root/dotnet" ]] && [[ "$("$studio_sdk_root/dotnet" --version)" == "10.0.403" ]]; then
  echo '.NET SDK 10.0.403 is already installed.'
  exit 0
fi
python3 - "$studio_sdk_root" <<'PY'
import hashlib, pathlib, sys, tarfile, tempfile, urllib.request
destination = pathlib.Path(sys.argv[1]); destination.mkdir(parents=True, exist_ok=True)
url = 'https://builds.dotnet.microsoft.com/dotnet/Sdk/10.0.403/dotnet-sdk-10.0.403-linux-x64.tar.gz'
# SHA-512 from Microsoft's 10.0 release metadata, verified during initial setup.
expected = 'b7b3d8461cf98b07d2552ce9199702fa3ad010ac2671a5f8cd5136f7534e50f6855ba7bbb02b11494563f1d04156276a31cda1103b900e5eecd0d9473dc7b9c5'
with tempfile.TemporaryDirectory() as directory:
    archive = pathlib.Path(directory) / 'sdk.tar.gz'
    urllib.request.urlretrieve(url, archive)
    if hashlib.sha512(archive.read_bytes()).hexdigest() != expected:
        raise SystemExit('SDK SHA-512 mismatch; refusing installation')
    with tarfile.open(archive) as content:
        content.extractall(destination, filter='data')
print('Installed checksum-verified .NET SDK 10.0.403 at', destination)
PY
