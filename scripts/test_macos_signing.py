import base64
import importlib.util
import io
import json
import os
from pathlib import Path
import subprocess
import tarfile
import tempfile
import unittest
from unittest.mock import patch
import zipfile


SPEC = importlib.util.spec_from_file_location("macos_signing", Path(__file__).with_name("sign-macos-release.py"))
signing = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(signing)

FINGERPRINT = "A" * 40
IDENTITY = f"Developer ID Application: Example ({signing.trusted_team_id()})"
IDENTITIES = f'  1) {FINGERPRINT} "{IDENTITY}"\n'
SUBMISSION_ID = "f674e4e5-1967-4a9d-8159-08f376d4c03b"
SETTINGS = {
    "MACOS_CERTIFICATE_P12": base64.b64encode(b"test-certificate").decode(),
    "MACOS_CERTIFICATE_PASSWORD": "test-password",
    "MACOS_SIGNING_IDENTITY": IDENTITY,
    "MACOS_NOTARY_KEY": "test-private-key",
    "MACOS_NOTARY_KEY_ID": "TESTKEY123",
    "MACOS_NOTARY_ISSUER": "df6143eb-c640-42c5-9d6d-74226e4985c6",
}


class MacOSSigningTests(unittest.TestCase):
    def setUp(self):
        self.directory = tempfile.TemporaryDirectory(prefix="kusto-signing-test-")
        self.addCleanup(self.directory.cleanup)
        self.root = Path(self.directory.name)
        self.payload = self.root / "payload"
        self.payload.mkdir()
        for name in signing.REQUIRED_FILES[:-1]:
            data = b"\xcf\xfa\xed\xfe-test-native" if name == "kusto" or name.endswith(".dylib") else b"test-document"
            (self.payload / name).write_bytes(data)
        (self.payload / "kusto").chmod(0o755)
        self.write_manifest()
        self.bundle = self.root / "bundle"
        self.bundle.mkdir()
        self.output = self.root / "signed"
        self.diagnostics = self.root / "diagnostics"
        self.calls = []
        self.keychains = ["/Users/runner/Library/Keychains/login.keychain-db"]

    def write_manifest(self):
        files = sorted(
            path.relative_to(self.payload).as_posix() for path in self.payload.rglob("*")
            if path.is_file() and path != self.payload / "payload-manifest.json"
        )
        (self.payload / "payload-manifest.json").write_text(json.dumps({"files": files}))

    def archive(self, rid="osx-arm64"):
        archive = self.bundle / f"kusto-{rid}.tar.gz"
        with tarfile.open(archive, "w:gz") as stream:
            stream.add(self.payload, arcname=".")
        return archive

    def fake_run(self, *arguments, capture=False, environment=None):
        arguments = tuple(str(value) for value in arguments)
        self.calls.append(arguments)
        command, verb, *rest = arguments
        stdout = ""
        if command == "/usr/bin/security":
            if verb == "list-keychains":
                if "-s" in rest:
                    self.keychains = rest[rest.index("-s") + 1:]
                else:
                    stdout = "\n".join(json.dumps(value) for value in self.keychains)
            elif verb == "find-identity":
                stdout = IDENTITIES
        elif command == "/usr/bin/codesign" and verb == "--force":
            path = Path(arguments[-1])
            path.write_bytes(path.read_bytes() + b"-signed")
        elif command == "/usr/bin/ditto":
            payload, archive = map(Path, arguments[-2:])
            with zipfile.ZipFile(archive, "w") as stream:
                for path in payload.rglob("*"):
                    if path.is_file():
                        stream.write(path, path.relative_to(payload))
        elif command == "/usr/bin/tar":
            self.assertEqual("1", environment["COPYFILE_DISABLE"])
            archive, _, payload, _ = rest
            with tarfile.open(archive, "w:gz") as stream:
                stream.add(payload, arcname=".")
        elif command == "xcrun" and verb == "notarytool":
            if rest[0] == "submit":
                with zipfile.ZipFile(rest[1]) as stream:
                    for name in signing.REQUIRED_FILES[:4]:
                        self.assertTrue(stream.read(name).endswith(b"-signed"))
                stdout = json.dumps({"id": SUBMISSION_ID, "status": "In Progress"})
            elif rest[0] == "log":
                stdout = '{"issues": ["test-rejection"]}'
        return subprocess.CompletedProcess(arguments, 0, stdout, "")

    def execute(self, rid="osx-arm64", status="Accepted", returncode=0):
        self.archive(rid)
        wait = subprocess.CompletedProcess(
            [], returncode, json.dumps({"id": SUBMISSION_ID, "status": status}), "",
        )
        with patch.dict(os.environ, SETTINGS, clear=True), patch.object(signing.sys, "platform", "darwin"), \
                patch.object(signing, "run", side_effect=self.fake_run), \
                patch.object(signing.subprocess, "run", return_value=wait) as waiting:
            signing.sign_release(self.bundle, self.output, self.diagnostics, rid)
        return waiting

    def test_signs_all_native_files_notarizes_and_preserves_archive_contract(self):
        (self.payload / "extra.dylib").write_bytes(b"\xcf\xfa\xed\xfe-extra")
        self.write_manifest()
        original_manifest = (self.payload / "payload-manifest.json").read_bytes()
        waiting = self.execute()
        signed = self.root / "expanded"
        signing.extract_payload(self.output / "kusto-osx-arm64.tar.gz", signed)
        self.assertEqual(original_manifest, (signed / "payload-manifest.json").read_bytes())
        self.assertEqual(0o755, (signed / "kusto").stat().st_mode & 0o777)
        signatures = [call for call in self.calls if call[:2] == ("/usr/bin/codesign", "--force")]
        self.assertEqual(5, len(signatures))
        self.assertEqual("kusto", Path(signatures[-1][-1]).name)
        for signature in signatures:
            self.assertIn("--timestamp", signature)
            self.assertEqual("runtime", signature[signature.index("--options") + 1])
            self.assertEqual(FINGERPRINT, signature[signature.index("--sign") + 1])
        self.assertEqual(5, len([call for call in self.calls if call[:2] == ("/usr/bin/codesign", "--verify")]))
        self.assertIn(SUBMISSION_ID, waiting.call_args.args[0])
        self.assertIn("60m", waiting.call_args.args[0])
        self.assertEqual(
            {"id": SUBMISSION_ID, "status": "Accepted"},
            json.loads((self.diagnostics / "osx-arm64-status.json").read_text()),
        )
        self.assertEqual(["/Users/runner/Library/Keychains/login.keychain-db"], self.keychains)
        self.assertTrue(any(call[:2] == ("/usr/bin/security", "delete-keychain") for call in self.calls))
        imported = next(call for call in self.calls if call[:2] == ("/usr/bin/security", "import"))
        self.assertFalse(Path(imported[2]).exists())
        self.assertEqual(["kusto-osx-arm64.tar.gz"], [path.name for path in self.output.iterdir()])
        verification = next(call for call in self.calls if call[0] == "/bin/bash")
        self.assertIn("--verify-macos-payload", verification)
        self.assertIn("extra.dylib", verification)

    def test_x64_payload_requires_x86_64_architecture(self):
        self.execute(rid="osx-x64")
        checks = [call for call in self.calls if call[:3] == ("xcrun", "lipo", "-verify_arch")]
        self.assertEqual(4, len(checks))
        self.assertTrue(all(call[3] == "x86_64" for call in checks))

    def test_rejected_notarization_retains_receipt_status_and_log_without_output(self):
        with self.assertRaisesRegex(RuntimeError, "was not Accepted"):
            self.execute(status="Invalid")
        self.assertFalse(self.output.exists())
        self.assertEqual(SUBMISSION_ID, json.loads((self.diagnostics / "osx-arm64-submission.json").read_text())["id"])
        self.assertTrue((self.diagnostics / "osx-arm64-log.json").is_file())
        self.assertEqual(["/Users/runner/Library/Keychains/login.keychain-db"], self.keychains)

    def test_rejected_wait_with_nonzero_exit_still_retains_rejection_log(self):
        with self.assertRaisesRegex(RuntimeError, "was not Accepted"):
            self.execute(status="Invalid", returncode=1)
        self.assertTrue((self.diagnostics / "osx-arm64-log.json").is_file())
        self.assertFalse(self.output.exists())

    def test_timeout_retains_submission_and_does_not_publish(self):
        with self.assertRaisesRegex(RuntimeError, "does not cancel Apple's processing"):
            self.execute(returncode=1, status="In Progress")
        self.assertFalse(self.output.exists())
        self.assertTrue((self.diagnostics / "osx-arm64-submission.json").is_file())
        self.assertTrue((self.diagnostics / "osx-arm64-status.json").is_file())
        self.assertTrue(any(call[:2] == ("/usr/bin/security", "delete-keychain") for call in self.calls))

    def test_signature_failure_cleans_keychain_and_never_submits(self):
        self.archive()
        def fail_codesign(*arguments, **kwargs):
            if arguments[0] == "/usr/bin/codesign":
                raise RuntimeError("codesign failed")
            return self.fake_run(*arguments, **kwargs)
        with patch.dict(os.environ, SETTINGS, clear=True), patch.object(signing.sys, "platform", "darwin"), \
                patch.object(signing, "run", side_effect=fail_codesign), self.assertRaisesRegex(RuntimeError, "codesign failed"):
            signing.sign_release(self.bundle, self.output, self.diagnostics, "osx-arm64")
        self.assertFalse(self.diagnostics.exists())
        self.assertFalse(self.output.exists())
        self.assertEqual(["/Users/runner/Library/Keychains/login.keychain-db"], self.keychains)
        self.assertTrue(any(call[:2] == ("/usr/bin/security", "delete-keychain") for call in self.calls))

    def test_missing_settings_fail_closed(self):
        with patch.dict(os.environ, {}, clear=True), self.assertRaisesRegex(RuntimeError, "MACOS_CERTIFICATE_P12"):
            signing.require_settings()

    def test_missing_private_key_and_untrusted_or_wrong_identity_fail(self):
        cases = (
            ("", "", "no signing identity"),
            (IDENTITIES, "", "not trusted"),
            (f' 1) {FINGERPRINT} "Apple Development: Example"\n',
             f' 1) {FINGERPRINT} "Apple Development: Example"\n', "Developer ID Application"),
        )
        for all_output, valid_output, message in cases:
            with self.subTest(message=message), self.assertRaisesRegex(RuntimeError, message):
                signing.select_identity(FINGERPRINT, all_output, valid_output)
        self.assertEqual(FINGERPRINT, signing.select_identity(FINGERPRINT.lower(), IDENTITIES * 2, IDENTITIES))
        with self.assertRaisesRegex(RuntimeError, "exactly one"):
            signing.select_identity("wrong name", IDENTITIES, IDENTITIES)
        with self.assertRaisesRegex(RuntimeError, "installer and self-updater trust"):
            signing.select_identity(FINGERPRINT, IDENTITIES, IDENTITIES, "WRONGTEAM1")

    def test_invalid_base64_fails_before_keychain_creation(self):
        with patch.dict(os.environ, {**SETTINGS, "MACOS_CERTIFICATE_P12": "not base64!"}), \
                patch.object(signing, "run") as command, self.assertRaisesRegex(RuntimeError, "base64"):
            with signing.signing_keychain(self.root):
                self.fail("An invalid certificate must not be imported.")
        command.assert_not_called()

    def test_keychain_cleanup_preserves_entries_added_during_signing(self):
        with patch.dict(os.environ, SETTINGS, clear=True), patch.object(signing, "run", side_effect=self.fake_run):
            with signing.signing_keychain(self.root):
                self.keychains.append("/Users/runner/other.keychain-db")
        self.assertEqual(
            ["/Users/runner/Library/Keychains/login.keychain-db", "/Users/runner/other.keychain-db"],
            self.keychains,
        )

    def test_command_failure_does_not_expose_password(self):
        error = subprocess.CalledProcessError(1, ["security", "import", SETTINGS["MACOS_CERTIFICATE_PASSWORD"]])
        with patch.object(signing.subprocess, "run", side_effect=error):
            with self.assertRaises(RuntimeError) as result:
                signing.run("/usr/bin/security", "import", "-P", SETTINGS["MACOS_CERTIFICATE_PASSWORD"])
        self.assertNotIn(SETTINGS["MACOS_CERTIFICATE_PASSWORD"], str(result.exception))

    def test_manifest_rejects_undeclared_and_missing_files(self):
        (self.payload / "undeclared.dylib").write_bytes(b"extra")
        with self.assertRaisesRegex(RuntimeError, "exactly describe"):
            signing.validate_payload(self.payload)
        (self.payload / "kusto").unlink()
        with self.assertRaisesRegex(RuntimeError, "missing required files"):
            signing.validate_payload(self.payload)

    def test_manifest_rejects_unsafe_duplicate_unsorted_and_non_string_paths(self):
        for paths in (["../kusto"], ["/kusto"], ["dir\\kusto"], ["./kusto"], ["kusto", "kusto"], ["z", "a"], [1]):
            with self.subTest(paths=paths):
                (self.payload / "payload-manifest.json").write_text(json.dumps({"files": paths}))
                with self.assertRaises(RuntimeError):
                    signing.validate_payload(self.payload)

    def test_archive_rejects_links_and_traversal_before_signing(self):
        for name, kind in (("../outside", tarfile.REGTYPE), ("/outside", tarfile.REGTYPE),
                           ("link", tarfile.SYMTYPE), ("hardlink", tarfile.LNKTYPE)):
            with self.subTest(name=name):
                archive = self.root / "unsafe.tar.gz"
                with tarfile.open(archive, "w:gz") as stream:
                    member = tarfile.TarInfo(name)
                    member.type = kind
                    member.linkname = "/outside" if kind != tarfile.REGTYPE else ""
                    stream.addfile(member, io.BytesIO())
                with self.assertRaises(RuntimeError):
                    signing.extract_payload(archive, self.root / "unsafe-payload")

    @unittest.skipUnless(signing.sys.platform == "darwin", "Requires Apple's archive tools.")
    def test_actual_apple_archive_tools_preserve_payload_bytes_and_permissions(self):
        submission = self.root / "submission.zip"
        signing.run("/usr/bin/ditto", "-c", "-k", "--norsrc", self.payload, submission)
        with zipfile.ZipFile(submission) as stream:
            for name in signing.REQUIRED_FILES:
                self.assertEqual((self.payload / name).read_bytes(), stream.read(name))
        archive = self.root / "signed.tar.gz"
        signing.package_payload(self.payload, archive)
        expanded = self.root / "expanded"
        signing.extract_payload(archive, expanded)
        for name in signing.REQUIRED_FILES:
            self.assertEqual((self.payload / name).read_bytes(), (expanded / name).read_bytes())
        self.assertEqual(0o755, (expanded / "kusto").stat().st_mode & 0o777)

    def test_workflow_requires_both_signing_jobs_before_attestation(self):
        workflow = (Path(__file__).parents[1] / ".github/workflows/release.yml").read_text()
        self.assertIn("needs: [prepare-release, sign-windows, sign-macos]", workflow)
        self.assertIn("needs.sign-windows.result == 'success' && needs.sign-macos.result == 'success'", workflow)
        self.assertIn("pattern: release-signed-macos-*", workflow)
        self.assertLess(workflow.index("Copy-Item './artifacts/signed-macos-assets/*'"),
                        workflow.index("dotnet ./scripts/update-release-bundle-metadata.cs"))
        self.assertLess(workflow.index("dotnet ./scripts/update-release-bundle-metadata.cs"),
                        workflow.index("name: Attest final release archives"))
        self.assertIn("runner: macos-15-intel", workflow)
        self.assertIn("MACOS_SIGNING_IDENTITY: ${{ vars.MACOS_SIGNING_IDENTITY }}", workflow)


if __name__ == "__main__":
    unittest.main()
