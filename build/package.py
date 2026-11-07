"""Package already-published Windows outputs. Uses only Python's standard library."""
import argparse
import hashlib
import pathlib
import shutil
import struct
import zipfile


def require_windows_x64(path):
    with path.open("rb") as executable:
        if executable.read(2) != b"MZ":
            raise ValueError(f"Not a Windows executable: {path}")
        executable.seek(0x3C)
        offset = struct.unpack("<I", executable.read(4))[0]
        executable.seek(offset)
        if executable.read(4) != b"PE\0\0" or executable.read(2) != b"\x64\x86":
            raise ValueError(f"Not a Windows x64 PE: {path}")


def main():
    root = pathlib.Path(__file__).resolve().parent.parent
    parser = argparse.ArgumentParser()
    parser.add_argument("--artifacts", type=pathlib.Path, default=root / "artifacts")
    args = parser.parse_args()
    artifacts = args.artifacts.resolve()
    app = artifacts / "publish" / "app"
    mock = artifacts / "publish" / "mock"
    require_windows_x64(app / "PS4GoldHENCheatStudio.exe")
    require_windows_x64(mock / "Studio.MockHost.exe")
    stage = artifacts / "package"
    if stage.exists():
        shutil.rmtree(stage)
    shutil.copytree(app, stage, ignore=shutil.ignore_patterns("*.pdb"))
    shutil.copytree(mock, stage / "simulator", ignore=shutil.ignore_patterns("*.pdb"))
    for name in ["README.md", "LICENSE", "THIRD-PARTY-NOTICES.md", "docs", "examples", "third-party"]:
        source = root / name
        if source.is_dir():
            shutil.copytree(source, stage / name)
        else:
            shutil.copyfile(source, stage / name)
    output = artifacts / "PS4GoldHENCheatStudio-win-x64.zip"
    with zipfile.ZipFile(output, "w", zipfile.ZIP_DEFLATED, compresslevel=6) as archive:
        for path in sorted(stage.rglob("*")):
            if path.is_file():
                archive.write(path, path.relative_to(stage))
    # Validate the actual finished archive, not merely the existence of a ZIP file.
    with zipfile.ZipFile(output) as archive:
        if archive.testzip() is not None:
            raise ValueError("ZIP integrity check failed")
        required = {"PS4GoldHENCheatStudio.exe", "simulator/Studio.MockHost.exe", "docs/USER-GUIDE.md", "docs/HARDWARE-CHECKLIST.md", "examples/synthetic-demo.json"}
        if not required.issubset(archive.namelist()):
            raise ValueError("Missing package assets")
    checksum = hashlib.file_digest(output.open("rb"), "sha256").hexdigest()
    (artifacts / "SHA256SUMS.txt").write_text(f"{checksum}  {output.name}\n", encoding="utf-8")
    print(f"Packaged and integrity-checked {output} ({output.stat().st_size:,} bytes)")
    print("SHA256", checksum)


if __name__ == "__main__":
    main()
