"""FM24 archive reading and the current-portrait lookup used by Update face."""

import io
import struct
import sys
import tempfile
import unittest
from pathlib import Path

import zstandard
from PIL import Image

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "fm24"))
import existing_portrait
import game_archive
from fmface import PNG_SIGNATURE


def _text(value: str) -> bytes:
    data = value.encode()
    return struct.pack("<I", len(data)) + data


def build_archive(path: Path, files: dict[str, bytes]) -> None:
    """A minimal .fmf with the validated layout: header, files, then the index."""
    body = bytearray(b"\x02\x01fmf.\x08\x00\x00" + bytes(8) + bytes(11))
    tree: dict = {}
    for name, data in files.items():
        offset = len(body)
        stored = zstandard.ZstdCompressor().compress(data)
        body += bytes(game_archive.LOCAL_HEADER) + stored
        *folders, filename = name.strip("/").split("/")
        node = tree
        for folder in folders:
            node = node.setdefault(folder, {})
        stem, dot, extension = filename.rpartition(".")
        node.setdefault("", []).append((stem, dot + extension, offset, len(stored), len(data)))

    def directory(name: str, node: dict) -> bytes:
        out = _text(name) + struct.pack("<I", len(node.get("", [])))
        for stem, extension, offset, compressed, size in node.get("", []):
            out += _text(stem) + _text(extension) + struct.pack("<5Q", offset, compressed, size, 0, 0)
        children = [(key, value) for key, value in node.items() if key]
        out += struct.pack("<I", len(children))
        return out + b"".join(directory(key, value) for key, value in children)

    (root_name, root), = tree.items()
    index = directory(root_name, root)
    # Large indexes are split into frames, each after the first preceded by its length.
    frames = [zstandard.ZstdCompressor().compress(index[i:i + 64]) for i in range(0, len(index), 64)]
    offset = len(body)
    body += bytes(9) + b"\x02\x01fmf.\x08\x00\x00" + struct.pack("<I", len(frames[0])) + frames[0]
    for frame in frames[1:]:
        body += struct.pack("<I", len(frame)) + frame
    struct.pack_into("<Q", body, 9, offset)
    path.write_bytes(bytes(body))


def image_bytes(mode: str, colour, kind: str) -> bytes:
    output = io.BytesIO()
    Image.new(mode, (8, 8), colour).save(output, kind)
    return output.getvalue()


class ExistingPortraitTests(unittest.TestCase):
    def setUp(self):
        temp = tempfile.TemporaryDirectory()
        self.addCleanup(temp.cleanup)
        self.root = Path(temp.name)
        self.game = self.root / "Football Manager 2024"
        (self.game / "data" / "game").mkdir(parents=True)
        self.graphics = self.root / "graphics"
        self.fmface = self.graphics / "fmface"
        self.fmface.mkdir(parents=True)
        mask = Image.new("L", (8, 8), 0)
        mask.paste(255, (2, 2, 6, 6))
        masked = io.BytesIO()
        mask.save(masked, "PNG")
        club = "/graphics/pictures/players/normal/usa/inter miami/"
        build_archive(self.game / "data" / "game" / "graphics.fmf", {
            club + "config.xml": b'<record><list id="maps">'
                                 b'<record from="lionel messi" to="graphics/pictures/person/7458500/portrait"/>'
                                 b'</list></record>',
            club + "lionel messi.jpg": image_bytes("RGB", (200, 120, 90), "JPEG"),
            club + "lionel messi_[mask].png": masked.getvalue(),
        })
        existing_portrait._bundled.clear()

    def test_reads_files_from_an_archive(self):
        archive = self.game / "data" / "game" / "graphics.fmf"
        entries = game_archive.read_index(archive)
        config = entries["/graphics/pictures/players/normal/usa/inter miami/config.xml"]
        self.assertIn(b"lionel messi", game_archive.read_file(archive, config))

    def test_bundled_photo_becomes_a_transparent_png(self):
        png, source = existing_portrait.find_existing_portrait("7458500", self.fmface, self.game)
        self.assertTrue(png.startswith(PNG_SIGNATURE))
        self.assertIn("bundled", source)
        with Image.open(io.BytesIO(png)) as picture:
            self.assertEqual(picture.mode, "RGBA")
            self.assertEqual(picture.getpixel((0, 0))[3], 0)
            self.assertEqual(picture.getpixel((4, 4))[3], 255)

    def test_users_graphics_replace_bundled_and_fm_face_comes_first(self):
        facepack = self.graphics / "facepack"
        facepack.mkdir()
        (facepack / "config.xml").write_text(
            '<record><list id="maps"><record from="messi" to="graphics/pictures/person/7458500/portrait"/>'
            '</list></record>')
        (facepack / "messi.png").write_bytes(image_bytes("RGBA", (0, 0, 255, 255), "PNG"))
        _, source = existing_portrait.find_existing_portrait("7458500", self.fmface, self.game)
        self.assertEqual(Path(source), facepack / "messi.png")
        (self.fmface / "config.xml").write_text(
            '<record><list id="maps"><record from="7458500-0123456789abcdef" '
            'to="graphics/pictures/person/r-7458500/portrait"/></list></record>')
        (self.fmface / "7458500-0123456789abcdef.png").write_bytes(image_bytes("RGBA", (0, 255, 0, 255), "PNG"))
        _, source = existing_portrait.find_existing_portrait("7458500", self.fmface, self.game)
        self.assertEqual(Path(source), self.fmface / "7458500-0123456789abcdef.png")

    def test_person_without_a_picture_has_no_portrait(self):
        self.assertIsNone(existing_portrait.find_existing_portrait("123", self.fmface, self.game))
        self.assertIsNone(existing_portrait.find_existing_portrait("7458500", self.fmface, None))
        with self.assertRaises(ValueError):
            existing_portrait.find_existing_portrait("../7458500", self.fmface, self.game)


if __name__ == "__main__":
    unittest.main()
