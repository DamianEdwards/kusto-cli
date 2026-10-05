#!/usr/bin/env python3
"""Sign and notarize prebuilt macOS CLI payloads without rebuilding them."""

import argparse
import base64
import binascii
import contextlib
import json
import os
from pathlib import Path, PurePosixPath
import re
import secrets
import shlex
import shutil
import subprocess
import sys
import tarfile
import tempfile
import uuid


REQUIRED_SETTINGS = (
    "MACOS_CERTIFICATE_P12", "MACOS_CERTIFICATE_PASSWORD", "MACOS_SIGNING_IDENTITY",
    "MACOS_NOTARY_KEY", "MACOS_NOTARY_KEY_ID", "MACOS_NOTARY_ISSUER",
)
REQUIRED_FILES = (
    "kusto", "libSkiaSharp.dylib", "libHarfBuzzSharp.dylib", "libsodium.dylib",
    "LICENSE", "THIRD-PARTY-NOTICES.md", "payload-manifest.json",
)
MACH_O_MAGICS = {
    b"\xfe\xed\xfa\xce", b"\xce\xfa\xed\xfe", b"\xfe\xed\xfa\xcf", b"\xcf\xfa\xed\xfe",
    b"\xca\xfe\xba\xbe", b"\xbe\xba\xfe\xca", b"\xca\xfe\xba\xbf", b"\xbf\xba\xfe\xca",
}


def run(*arguments, capture=False, environment=None):
    try:
        return subprocess.run(
            [str(argument) for argument in arguments],
            check=True, text=True, capture_output=capture, env=environment,
        )
    except subprocess.CalledProcessError as error:
        if arguments[:2] == ("xcrun", "notarytool") and error.stderr:
            print(error.stderr, file=sys.stderr)
        # Never include command arguments: security import includes the P12 password.
        raise RuntimeError(
            f"{Path(arguments[0]).name} {arguments[1]} failed (exit code {error.returncode})."
        ) from None


def require_settings():
    missing = [name for name in REQUIRED_SETTINGS if not os.environ.get(name, "").strip()]
    if missing:
        raise RuntimeError(
            "macOS signing and notarization are mandatory. Configure "
            + ", ".join(missing) + " in the production environment."
        )


def trusted_team_id():
    installer = Path(__file__).parent / "install/install-kusto-cli.sh"
    match = re.search(r'^MACOS_SIGNING_TEAM_ID="([A-Z0-9]{10})"$', installer.read_text(), re.MULTILINE)
    if not match:
        raise RuntimeError("The Unix installer does not define its trusted Apple Developer team.")
    return match.group(1)


def select_identity(selector, all_output, valid_output, expected_team=None):
    pattern = r'^\s*\d+\)\s+([0-9A-Fa-f]{40})\s+"([^"\r\n]+)"'
    identities = list(dict.fromkeys(re.findall(pattern, all_output, re.MULTILINE)))
    valid = {fingerprint.upper() for fingerprint, _ in re.findall(pattern, valid_output, re.MULTILINE)}
    if not identities:
        raise RuntimeError(
            "The P12 has no signing identity. Export the Developer ID Application certificate "
            "together with its private key from Keychain Access > My Certificates as .p12, not .cer."
        )
    by_fingerprint = re.fullmatch(r"[0-9A-Fa-f]{40}", selector) is not None
    matches = [
        (fingerprint, name) for fingerprint, name in identities
        if (fingerprint.upper() == selector.upper() if by_fingerprint else name == selector)
    ]
    if len(matches) != 1:
        raise RuntimeError(
            "MACOS_SIGNING_IDENTITY must match exactly one imported identity. "
            "Use its SHA-1 fingerprint or exact Developer ID Application name."
        )
    fingerprint, name = matches[0]
    if not name.startswith("Developer ID Application: "):
        raise RuntimeError("MACOS_SIGNING_IDENTITY must select a Developer ID Application certificate.")
    if expected_team is not None and not name.endswith(f"({expected_team})"):
        raise RuntimeError(
            f"The signing certificate must belong to Apple Developer team {expected_team}, "
            "which the installer and self-updater trust."
        )
    if fingerprint.upper() not in valid:
        raise RuntimeError(
            "The imported identity is not trusted for code signing. Check certificate expiration, "
            "revocation, and the Apple Developer ID intermediate chain; do not override certificate trust."
        )
    return fingerprint.upper()


def update_keychain_search(operation, keychain):
    current = shlex.split(run("/usr/bin/security", "list-keychains", "-d", "user", capture=True).stdout)
    updated = [entry for entry in current if entry != str(keychain)]
    if operation == "add":
        updated.insert(0, str(keychain))
    if updated != current:
        run("/usr/bin/security", "list-keychains", "-d", "user", "-s", *updated, capture=True)


@contextlib.contextmanager
def signing_keychain(temporary):
    certificate = temporary / "certificate.p12"
    notary_key = temporary / "notary.p8"
    try:
        certificate.write_bytes(base64.b64decode(os.environ["MACOS_CERTIFICATE_P12"], validate=True))
    except (ValueError, binascii.Error):
        raise RuntimeError("MACOS_CERTIFICATE_P12 must be the base64-encoded P12 certificate and private key.") from None
    notary_key.write_text(os.environ["MACOS_NOTARY_KEY"], encoding="utf-8")
    certificate.chmod(0o600)
    notary_key.chmod(0o600)
    keychain = temporary / "signing.keychain-db"
    password = secrets.token_hex(32)
    created = False
    try:
        run("/usr/bin/security", "create-keychain", "-p", password, keychain, capture=True)
        created = True
        update_keychain_search("add", keychain)
        run("/usr/bin/security", "set-keychain-settings", "-lut", "21600", keychain, capture=True)
        run("/usr/bin/security", "unlock-keychain", "-p", password, keychain, capture=True)
        try:
            run("/usr/bin/security", "import", certificate, "-k", keychain, "-f", "pkcs12",
                "-P", os.environ["MACOS_CERTIFICATE_PASSWORD"], "-T", "/usr/bin/codesign", capture=True)
        except RuntimeError:
            raise RuntimeError(
                "Could not import MACOS_CERTIFICATE_P12. Re-export the Developer ID Application "
                "certificate and private key as .p12 and configure the matching export password."
            ) from None
        identity = select_identity(
            os.environ["MACOS_SIGNING_IDENTITY"],
            run("/usr/bin/security", "find-identity", "-p", "basic", keychain, capture=True).stdout,
            run("/usr/bin/security", "find-identity", "-v", "-p", "codesigning", keychain, capture=True).stdout,
            trusted_team_id(),
        )
        run("/usr/bin/security", "set-key-partition-list", "-S", "apple-tool:,apple:,codesign:",
            "-s", "-k", password, keychain, capture=True)
        yield keychain, identity, notary_key
    finally:
        failures = []
        cleanup_actions = [lambda: update_keychain_search("remove", keychain)]
        if created or keychain.exists():
            cleanup_actions.append(lambda: run("/usr/bin/security", "delete-keychain", keychain, capture=True))
        for cleanup in cleanup_actions:
            try:
                cleanup()
            except (OSError, RuntimeError) as error:
                failures.append(str(error))
        if failures:
            raise RuntimeError("Temporary signing Keychain cleanup failed: " + "; ".join(failures))


def extract_payload(archive_path, destination):
    with tarfile.open(archive_path, "r:gz") as archive:
        seen = set()
        for member in archive.getmembers():
            relative = PurePosixPath(member.name)
            if relative.is_absolute() or ".." in relative.parts or "\\" in member.name:
                raise RuntimeError(f"Unsafe archive path: {member.name!r}.")
            if not (member.isdir() or member.isfile()):
                raise RuntimeError(f"Archive links and special files are prohibited: {member.name!r}.")
            if relative in seen:
                raise RuntimeError(f"Duplicate archive path: {member.name!r}.")
            seen.add(relative)
            target = destination / relative
            if member.isdir():
                target.mkdir(parents=True, exist_ok=True)
            else:
                target.parent.mkdir(parents=True, exist_ok=True)
                with archive.extractfile(member) as source, target.open("wb") as output:
                    shutil.copyfileobj(source, output)
                target.chmod(member.mode & 0o777)
    validate_payload(destination)


def validate_payload(payload):
    missing = [name for name in REQUIRED_FILES if not (payload / name).is_file()]
    if missing:
        raise RuntimeError("macOS payload is missing required files: " + ", ".join(missing))
    if not (payload / "kusto").stat().st_mode & 0o111:
        raise RuntimeError("The prebuilt kusto executable must retain its Unix execute permission.")
    manifest = json.loads((payload / "payload-manifest.json").read_text(encoding="utf-8-sig"))
    declared = manifest.get("files") if isinstance(manifest, dict) else None
    if not isinstance(declared, list) or not declared or any(
        not isinstance(name, str) or not name or "\\" in name
        or PurePosixPath(name).is_absolute() or ".." in PurePosixPath(name).parts
        or str(PurePosixPath(name)) != name
        for name in declared
    ):
        raise RuntimeError("payload-manifest.json must declare safe, slash-normalized relative file paths.")
    actual = sorted(
        path.relative_to(payload).as_posix() for path in payload.rglob("*")
        if path.is_file() and path != payload / "payload-manifest.json"
    )
    if declared != sorted(set(declared)) or declared != actual:
        raise RuntimeError("payload-manifest.json must be sorted and exactly describe the macOS payload.")


def is_mach_o(path):
    with path.open("rb") as stream:
        return stream.read(4) in MACH_O_MAGICS


def sign_payload(payload, rid, keychain, identity):
    native_files = [path for path in sorted(payload.rglob("*")) if path.is_file() and is_mach_o(path)]
    required_native_files = [payload / name for name in REQUIRED_FILES[:4]]
    required_native_files.extend(payload.rglob("*.dylib"))
    for path in required_native_files:
        if path not in native_files:
            raise RuntimeError(f"Required native payload file {path.relative_to(payload).as_posix()!r} is not Mach-O.")
    # Sign sidecars first so hardened-runtime library validation uses the same team.
    native_files.remove(payload / "kusto")
    native_files.append(payload / "kusto")
    architecture = "arm64" if rid == "osx-arm64" else "x86_64"
    for path in native_files:
        run("xcrun", "lipo", "-verify_arch", architecture, path)
        run("/usr/bin/codesign", "--force", "--sign", identity, "--keychain", keychain,
            "--options", "runtime", "--timestamp", path)
        run("/usr/bin/codesign", "--verify", "--strict", "--verbose=2", path)
    files = json.loads((payload / "payload-manifest.json").read_text(encoding="utf-8-sig"))["files"]
    run("/bin/bash", Path(__file__).parent / "install/install-kusto-cli.sh",
        "--verify-macos-payload", payload, *files)


def notarize(archive, rid, notary_key, diagnostics):
    diagnostics.mkdir(parents=True, exist_ok=True)
    receipt = diagnostics / f"{rid}-submission.json"
    status = diagnostics / f"{rid}-status.json"
    if receipt.exists():
        raise RuntimeError(f"A submission already exists in {receipt}; check its status before uploading again.")
    authentication = (
        "--key", notary_key, "--key-id", os.environ["MACOS_NOTARY_KEY_ID"],
        "--issuer", os.environ["MACOS_NOTARY_ISSUER"],
    )
    result = run("xcrun", "notarytool", "submit", archive, *authentication, "--output-format", "json", capture=True)
    receipt.write_text(result.stdout, encoding="utf-8")
    submission = json.loads(result.stdout)
    if not isinstance(submission, dict) or not isinstance(submission.get("id"), str):
        raise RuntimeError("Apple's notarization submission response did not contain a submission ID.")
    submission_id = str(uuid.UUID(submission["id"]))
    print(f"Notarization {rid} submission ID: {submission_id}", flush=True)
    # Write even a partial response on timeout; retain the submission ID separately.
    result = subprocess.run(
        [str(argument) for argument in (
            "xcrun", "notarytool", "wait", submission_id, *authentication,
            "--timeout", "60m", "--output-format", "json",
        )], text=True, capture_output=True,
    )
    status.write_text(result.stdout, encoding="utf-8")
    if result.stderr:
        (diagnostics / f"{rid}-wait-error.txt").write_text(result.stderr, encoding="utf-8")
    try:
        response = json.loads(result.stdout)
    except ValueError:
        if not result.returncode:
            raise
        response = None
    rejected = isinstance(response, dict) and response.get("status") not in (None, "Accepted", "In Progress")
    if result.returncode and not rejected:
        raise RuntimeError(
            f"Notarization wait failed for {submission_id}. A timeout does not cancel Apple's processing. "
            "Check the retained submission before retrying."
        )
    if not isinstance(response, dict):
        raise RuntimeError("Apple's notarization status response was not a JSON object.")
    if response.get("status") != "Accepted":
        log_path = diagnostics / f"{rid}-log.json"
        try:
            log = run("xcrun", "notarytool", "log", submission_id, *authentication, capture=True)
            log_path.write_text(log.stdout, encoding="utf-8")
        except (OSError, RuntimeError) as error:
            print(f"Could not retrieve notarization log: {error}", file=sys.stderr)
        raise RuntimeError(f"Notarization {submission_id} was not Accepted (status: {response.get('status', 'missing')}).")
    print(f"Notarization {rid} accepted.", flush=True)


def package_payload(payload, archive):
    # AppleDouble sidecars would violate the manifest; Mach-O signatures are embedded.
    run("/usr/bin/tar", "-czf", archive, "-C", payload, ".",
        environment={**os.environ, "COPYFILE_DISABLE": "1"})


def sign_release(bundle, output, diagnostics, rid):
    if sys.platform != "darwin":
        raise RuntimeError("macOS release signing requires a macOS runner with Xcode command-line tools.")
    require_settings()
    archive_name = f"kusto-{rid}.tar.gz"
    archive = bundle / archive_name
    if not archive.is_file():
        raise RuntimeError(f"Missing prebuilt macOS archive: {archive}.")
    with tempfile.TemporaryDirectory(prefix="kusto-macos-signing-") as directory:
        temporary = Path(directory)
        payload = temporary / "payload"
        payload.mkdir()
        extract_payload(archive, payload)
        with signing_keychain(temporary) as (keychain, identity, notary_key):
            sign_payload(payload, rid, keychain, identity)
            submission = temporary / f"kusto-{rid}.zip"
            run("/usr/bin/ditto", "-c", "-k", "--norsrc", payload, submission)
            notarize(submission, rid, notary_key, diagnostics)
            output.mkdir(parents=True, exist_ok=True)
            package_payload(payload, output / archive_name)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--bundle-directory", required=True, type=Path)
    parser.add_argument("--output-directory", required=True, type=Path)
    parser.add_argument("--diagnostics-directory", required=True, type=Path)
    parser.add_argument("--runtime-identifier", required=True, choices=("osx-x64", "osx-arm64"))
    arguments = parser.parse_args()
    try:
        sign_release(
            arguments.bundle_directory.resolve(), arguments.output_directory.resolve(),
            arguments.diagnostics_directory.resolve(), arguments.runtime_identifier,
        )
    except (OSError, RuntimeError, ValueError, KeyError, tarfile.TarError) as error:
        parser.exit(1, f"macOS release signing failed: {error}\n")


if __name__ == "__main__":
    main()
