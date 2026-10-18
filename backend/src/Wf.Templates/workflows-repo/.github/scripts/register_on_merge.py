"""Validate merged workflow changes, then publish with an externally acquired app token."""

import argparse
import json
import os
from pathlib import Path
import subprocess
import sys

from pr_gate import affected_workflows, git, run, validate_and_test, workflow_roots


def select(repository, before, head):
    if before in ("0" * 40, "0" * 64):
        affected_workflows(repository, head, head, head)
        return sorted(map(str, workflow_roots(repository, head)))
    return affected_workflows(repository, before, head, head)


def publish(repository, selected, head):
    if (os.environ.get("GITHUB_EVENT_NAME") != "push"
            or os.environ.get("GITHUB_REF") != "refs/heads/main"
            or os.environ.get("GITHUB_SHA") != head):
        raise ValueError("Publication requires the exact checked-out main-branch push")
    if not os.environ.get("WF_ACCESS_TOKEN"):
        raise ValueError("WF_ACCESS_TOKEN must contain the acquired Registrar app token")
    engine = os.environ.get("WF_ENGINE_URL", "")
    if not engine:
        raise ValueError("WF_ENGINE_URL is required")
    author = git(repository, "show", "-s", "--format=%an <%ae>", head).decode("utf-8").strip()
    for relative in selected:
        directory = repository / relative
        directory.resolve().relative_to(repository)
        arguments = [
            "dotnet", "tool", "run", "wf", "--", "publish", str(directory),
            "--engine", engine, "--git-commit", head, "--git-author", author,
            "--input", str(repository),
        ]
        if (directory / "schedules.json").is_file():
            arguments += ["--schedule-source-path",
                          (directory / "schedules.json").relative_to(repository).as_posix()]
        run(repository, *arguments)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    mode = parser.add_mutually_exclusive_group(required=True)
    mode.add_argument("--list", action="store_true")
    mode.add_argument("--validate", action="store_true")
    mode.add_argument("--publish", action="store_true")
    options = parser.parse_args()
    repository = Path.cwd().resolve()
    if git(repository, "rev-parse", "--show-prefix").strip():
        raise ValueError("Run registration from the repository root")
    head = os.environ.get("PUBLISH_HEAD_SHA", "")
    selected = select(repository, os.environ.get("PUBLISH_BASE_SHA", ""), head)
    if options.list:
        print(json.dumps(selected))
    elif options.validate:
        validate_and_test(repository, selected)
        if os.environ.get("GITHUB_OUTPUT"):
            with open(os.environ["GITHUB_OUTPUT"], "a", encoding="utf-8") as result:
                result.write("has-workflows=" + str(bool(selected)).lower() + "\n")
    else:
        publish(repository, selected, head)


if __name__ == "__main__":
    try:
        main()
    except (OSError, ValueError, subprocess.CalledProcessError) as error:
        print(f"Registration failed: {error}", file=sys.stderr)
        sys.exit(1)
