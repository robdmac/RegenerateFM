import os
import shutil
import sys
import tempfile
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent.parent.parent / "tools"))
import make_release  # noqa: E402


class LineEndingTests(unittest.TestCase):
    def test_windows_line_endings_are_found_in_text_only(self):
        self.assertTrue(make_release.has_crlf(b"#!/bin/sh\r\nexit 0\r\n"))
        self.assertFalse(make_release.has_crlf(b"#!/bin/sh\nexit 0\n"))
        # A PNG's signature holds \r\n, but it is binary.
        self.assertFalse(make_release.has_crlf(b"\x89PNG\r\n\x1a\n\x00\x00\x00\rIHDR"))

    def test_the_release_files_are_checked(self):
        with tempfile.TemporaryDirectory() as name:
            root = Path(name)
            (root / "install-linux.sh").write_bytes(b"#!/bin/sh\r\n")
            (root / "README.md").write_bytes(b"# RegenerateFM\n")
            self.assertEqual(make_release.crlf_files(root, ("install-linux.sh", "README.md")), ["install-linux.sh"])

    @unittest.skipUnless(shutil.which("git") and (make_release.ROOT / ".git").exists(), "needs a git checkout")
    def test_this_checkout_has_lf_line_endings(self):
        # .gitattributes keeps them so, on Windows too.
        self.assertEqual(make_release.crlf_files(make_release.ROOT, make_release.public_files()), [])


@unittest.skipUnless(shutil.which("git") and (make_release.ROOT / ".git").exists(), "needs a git checkout")
class ModeAndFileTests(unittest.TestCase):
    def test_scripts_take_their_mode_from_git(self):
        modes = make_release.git_modes()
        self.assertEqual(modes["install-linux.sh"], 0o755)
        self.assertEqual(modes["native/fm24/run_regeneratefm24.sh"], 0o755)
        self.assertEqual(modes["README.md"], 0o644)
        self.assertEqual(make_release.file_mode(make_release.ROOT / "install-linux.sh", modes), 0o755)

    def test_other_files_take_theirs_from_the_disk(self):
        with tempfile.TemporaryDirectory() as name:
            program = Path(name) / "regeneratefm-fm24.exe"
            text = Path(name) / "notes.txt"
            program.write_bytes(b"MZ")
            text.write_bytes(b"text")
            if os.name != "nt":  # Windows reports .exe files as executable itself.
                program.chmod(0o755)
                text.chmod(0o644)
            self.assertEqual(make_release.file_mode(program), 0o755)
            self.assertEqual(make_release.file_mode(text), 0o644)

    def test_the_source_folder_is_the_tracked_files_without_research(self):
        public = make_release.public_files()
        self.assertIn("src/RegenerateFM.Core/Prompts.cs", public)
        self.assertIn("install-linux.sh", public)
        self.assertFalse([name for name in public if name.startswith(("docs/", "native/fm24/research/"))])
        self.assertNotIn(".env", public)

    def test_the_version_is_the_plugins(self):
        self.assertRegex(make_release.plugin_version(), r"^\d+\.\d+\.\d+$")

    def test_a_program_naming_this_computer_is_refused(self):
        with self.assertRaises(SystemExit):
            make_release.check_private("x", b"built in " + str(Path.home()).encode())
        make_release.check_private("x", b"/RegenerateFM/src/RegenerateFM.Core/Prompts.cs")


if __name__ == "__main__":
    unittest.main()
