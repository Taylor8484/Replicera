#!/usr/bin/env python3
"""Build a versioned, self-contained Replicera release archive."""

from __future__ import annotations

import argparse
import hashlib
import shutil
import subprocess
import tarfile
import tempfile
import zipfile
from pathlib import Path


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--version", required=True)
    parser.add_argument("--runtime", required=True, choices=("linux-x64", "win-x64"))
    parser.add_argument("--output", default="artifacts/packages")
    args = parser.parse_args()

    root = Path(__file__).resolve().parent.parent
    output = (root / args.output).resolve()
    output.mkdir(parents=True, exist_ok=True)
    package_name = f"replicera-{args.version}-{args.runtime}"
    archive = output / (
        f"{package_name}.zip" if args.runtime.startswith("win-") else f"{package_name}.tar.gz"
    )

    with tempfile.TemporaryDirectory(prefix="replicera-package-") as temporary:
        workspace = Path(temporary) / "repository"
        shutil.copytree(
            root,
            workspace,
            ignore=shutil.ignore_patterns(
                ".git",
                ".env*",
                ".replicera-test-state",
                "artifacts",
                "bin",
                "obj",
                "__pycache__",
            ),
        )
        publish = Path(temporary) / package_name
        subprocess.run(
            [
                "dotnet",
                "publish",
                str(workspace / "src/Replicera.Cli/Replicera.Cli.csproj"),
                "--configuration",
                "Release",
                "--runtime",
                args.runtime,
                "--self-contained",
                "true",
                "--output",
                str(publish),
                f"-p:Version={args.version}",
            ],
            cwd=workspace,
            check=True,
        )
        for source, destination in (
            ("README.md", "README.md"),
            ("LICENSE", "LICENSE"),
            ("THIRD-PARTY-NOTICES.md", "THIRD-PARTY-NOTICES.md"),
            ("docs/operations.md", "OPERATIONS.md"),
        ):
            shutil.copy2(workspace / source, publish / destination)
        license_directory = publish / "third-party-licenses"
        license_directory.mkdir()
        shutil.copy2(
            workspace / "third-party-licenses/Oracle.ManagedDataAccess.Core-LICENSE.txt",
            license_directory / "Oracle.ManagedDataAccess.Core-LICENSE.txt",
        )

        if args.runtime.startswith("win-"):
            with zipfile.ZipFile(archive, "w", compression=zipfile.ZIP_DEFLATED) as bundle:
                for item in sorted(publish.rglob("*")):
                    if item.is_file():
                        bundle.write(item, Path(package_name) / item.relative_to(publish))
        else:
            with tarfile.open(archive, "w:gz") as bundle:
                bundle.add(publish, arcname=package_name)

    digest = hashlib.sha256(archive.read_bytes()).hexdigest()
    Path(f"{archive}.sha256").write_text(
        f"{digest}  {archive.name}\n", encoding="utf-8"
    )
    print(archive)


if __name__ == "__main__":
    main()
