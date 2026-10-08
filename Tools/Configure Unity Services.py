"""Stage a public Unity Cloud Project ID and environment before Unity builds.

No service-account key, password or signing secret is required by Relay clients.
When no ID is supplied, leave existing local Editor linking intact. An unlinked
checkout can still build and play single-player/LAN; INTERNET explains setup.
"""
import argparse
import json
import os
import re
import uuid
from pathlib import Path


def configure(root: Path, project_id: str, environment: str) -> bool:
    if not project_id.strip():
        return False
    project_id = str(uuid.UUID(project_id.strip()))
    if project_id == str(uuid.UUID(int=0)):
        raise ValueError("Unity Project ID cannot be the zero UUID")
    if not re.fullmatch(r"[a-z0-9][a-z0-9_-]{0,63}", environment):
        raise ValueError("Invalid Unity environment name")
    project_settings = root / "ProjectSettings/ProjectSettings.asset"
    connect_settings = root / "ProjectSettings/UnityConnectSettings.asset"
    project = project_settings.read_text(encoding="utf-8")
    connect = connect_settings.read_text(encoding="utf-8")
    project, count = re.subn(r"^  cloudProjectId:.*$", "  cloudProjectId: " + project_id, project, flags=re.M)
    if count != 1:
        raise ValueError("Expected exactly one cloudProjectId in PlayerSettings")
    # Enable the top-level Services link; preserve Analytics/Ads/diagnostics.
    connect, count = re.subn(r"^(  m_Enabled: )\d+$", r"\g<1>1", connect, count=1, flags=re.M)
    if count != 1:
        raise ValueError("Missing UnityConnectSettings enabled flag")
    config = root / "Assets/Resources/Idas3UnityServices.json"
    config.parent.mkdir(parents=True, exist_ok=True)
    project_settings.write_text(project, encoding="utf-8")
    connect_settings.write_text(connect, encoding="utf-8")
    config.write_text(json.dumps({"environment": environment}, indent=2) + "\n", encoding="utf-8")
    return True


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--project-root", type=Path, default=Path(__file__).resolve().parent.parent)
    parser.add_argument("--project-id", default=os.environ.get("UNITY_PROJECT_ID", ""))
    parser.add_argument("--environment", default=os.environ.get("UNITY_ENVIRONMENT", "production") or "production")
    args = parser.parse_args()
    linked = configure(args.project_root, args.project_id, args.environment)
    print("Unity Relay build configuration staged." if linked else "No Unity Project ID supplied; existing Editor link retained (unlinked builds support LAN).")


if __name__ == "__main__":
    main()
