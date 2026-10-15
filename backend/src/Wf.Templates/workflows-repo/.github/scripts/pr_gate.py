"""Validate the complete checked-out PR diff; no API limits or shell evaluation."""

import argparse
import json
import os
from pathlib import Path, PurePosixPath
import re
import subprocess
import sys


def git(repository, *arguments):
    return subprocess.run(
        ["git", "--no-pager", *arguments], cwd=repository, check=True, stdout=subprocess.PIPE
    ).stdout


def workflow_roots(repository, revision):
    roots = set()
    for entry in git(repository, "ls-tree", "-r", "-z", revision, "--").split(b"\0"):
        if not entry:
            continue
        metadata, name = entry.split(b"\t", 1)
        path = PurePosixPath(os.fsdecode(name))
        if path.name == "workflow.cs":
            if metadata.split()[0] not in (b"100644", b"100755"):
                raise ValueError("workflow.cs must be a regular tracked file")
            roots.add(path.parent)
    return roots


def affected_workflows(repository, base, head, checkout):
    for revision in (base, head, checkout):
        if not re.fullmatch(r"(?:[0-9a-fA-F]{40}|[0-9a-fA-F]{64})", revision):
            raise ValueError("PR base, head and checkout must be complete commit SHAs")
        git(repository, "cat-file", "-e", revision + "^{commit}")
    if git(repository, "rev-parse", "--is-shallow-repository").strip() != b"false":
        raise ValueError("Full checkout history required (fetch-depth: 0)")
    if git(repository, "rev-parse", "HEAD").decode().strip() != checkout.lower():
        raise ValueError("Checkout does not match PR_CHECKOUT_SHA")
    for revision in (base, head):
        git(repository, "merge-base", "--is-ancestor", revision, checkout)

    current = workflow_roots(repository, checkout)
    previous = workflow_roots(repository, base)
    changes = [
        PurePosixPath(os.fsdecode(name))
        for name in git(
            repository, "diff", "--no-ext-diff", "--no-renames", "--name-only",
            "-z", base, checkout, "--"
        ).split(b"\0") if name
    ]
    selected = set()
    for path in changes:
        owners = {root for root in current | previous if path.is_relative_to(root)}
        # Unknown/shared inputs and removed workflows can affect any survivor.
        if not owners or owners - current:
            return sorted(map(str, current))
        selected.update(owners)
    return sorted(map(str, selected))


def run(repository, *arguments):
    print(json.dumps({"cwd": str(repository), "command": arguments}), flush=True)
    subprocess.run(arguments, cwd=repository, check=True)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--list", action="store_true", help="Only emit affected directories as JSON")
    options = parser.parse_args()
    repository = Path.cwd().resolve()
    if git(repository, "rev-parse", "--show-prefix").strip():
        raise ValueError("Run the PR gate from the repository root")
    selected = affected_workflows(
        repository, os.environ.get("PR_BASE_SHA", ""), os.environ.get("PR_HEAD_SHA", ""),
        os.environ.get("PR_CHECKOUT_SHA", "")
    )
    if options.list:
        print(json.dumps(selected))
        return
    print("Affected workflows: " + json.dumps(selected), flush=True)
    if not selected:
        return
    run(repository, "dotnet", "tool", "restore", "--tool-manifest",
        str(repository / ".config" / "dotnet-tools.json"))
    for relative in selected:
        directory = repository / relative
        directory.resolve().relative_to(repository)
        run(repository, "dotnet", "tool", "run", "wf", "--", "validate",
            str(directory), "--input", str(repository))
        run(directory, "dotnet", "test")


if __name__ == "__main__":
    try:
        main()
    except (OSError, ValueError, subprocess.CalledProcessError) as error:
        print(f"PR gate failed: {error}", file=sys.stderr)
        sys.exit(1)
