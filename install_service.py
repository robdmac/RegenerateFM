#!/usr/bin/env python3
"""Install the local server as a user service from this checkout."""

from __future__ import annotations

import subprocess
from pathlib import Path


def service_text(project: Path) -> str:
    location = str(project.resolve())
    if "\n" in location or "\r" in location:
        raise ValueError("The project path cannot contain a newline.")
    location = location.replace("%", "%%").replace("\\", "\\\\")
    return (
        "[Unit]\n"
        "Description=RegenerateFM local portrait generator\n\n"
        "[Service]\n"
        "Type=simple\n"
        f"WorkingDirectory={location}\n"
        "ExecStart=python3 -m regeneratefm serve\n"
        "Restart=on-failure\n\n"
        "[Install]\n"
        "WantedBy=default.target\n"
    )


def main() -> None:
    project = Path(__file__).resolve().parent
    target = Path.home() / ".config/systemd/user/regeneratefm.service"
    if target.exists() and "Description=RegenerateFM local portrait generator" not in target.read_text():
        raise SystemExit(f"Refusing to replace an unrelated service: {target}")
    target.parent.mkdir(parents=True, exist_ok=True)
    target.write_text(service_text(project))
    subprocess.run(["systemctl", "--user", "daemon-reload"], check=True)
    subprocess.run(["systemctl", "--user", "enable", "--now", "regeneratefm.service"], check=True)
    subprocess.run(["systemctl", "--user", "restart", "regeneratefm.service"], check=True)
    print(f"Installed and started {target}")


if __name__ == "__main__":
    main()
