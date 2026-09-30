#!/usr/bin/env python3
"""Build a versioned, self-contained Replicera release archive."""

from __future__ import annotations

import argparse
import hashlib
import json
import os
import re
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
                "-p:RestoreLockedMode=true",
            ],
            cwd=workspace,
            check=True,
        )
        for source, destination in (
            ("README.md", "README.md"),
            ("LICENSE", "LICENSE"),
            ("COMMUNITY.md", "COMMUNITY.md"),
            ("CONTRIBUTING.md", "CONTRIBUTING.md"),
            ("THIRD-PARTY-NOTICES.md", "THIRD-PARTY-NOTICES.md"),
            ("docs/operations.md", "OPERATIONS.md"),
        ):
            shutil.copy2(workspace / source, publish / destination)
        license_directory = publish / "third-party-licenses"
        shutil.copytree(workspace / "third-party-licenses", license_directory)
        runtime_pack = runtime_pack_directory(workspace, args.runtime)
        shutil.copy2(runtime_pack / "LICENSE.TXT", license_directory / "dotnet-runtime-LICENSE.txt")
        shutil.copy2(
            runtime_pack / "THIRD-PARTY-NOTICES.TXT",
            license_directory / "dotnet-runtime-THIRD-PARTY-NOTICES.txt",
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


def runtime_pack_directory(workspace: Path, runtime: str) -> Path:
    """Locate the .NET runtime pack that the self-contained publish bundled."""
    assets = json.loads(
        (workspace / "src/Replicera.Cli/obj/project.assets.json").read_text(encoding="utf-8")
    )
    name = f"Microsoft.NETCore.App.Runtime.{runtime}"
    for framework in assets["project"]["frameworks"].values():
        for dependency in framework.get("downloadDependencies", []):
            if dependency["name"] == name:
                version = dependency["version"].strip("[]").split(",")[0].strip()
                directory = global_packages_folder() / name.lower() / version
                if (directory / "THIRD-PARTY-NOTICES.TXT").exists():
                    return directory
                raise SystemExit(f"The .NET runtime notices were not found in {directory}.")
    raise SystemExit(f"The publish did not use the {name} runtime pack.")


def global_packages_folder() -> Path:
    configured = os.environ.get("NUGET_PACKAGES")
    if configured:
        return Path(configured)
    output = subprocess.run(
        ["dotnet", "nuget", "locals", "global-packages", "--list"],
        check=True,
        capture_output=True,
        text=True,
    ).stdout
    match = re.search(r"global-packages:\s*(.+)", output)
    if match is None:
        raise SystemExit("Could not determine the NuGet global packages folder.")
    return Path(match.group(1).strip())


if __name__ == "__main__":
    main()
