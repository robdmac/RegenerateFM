"""The FM26 plugin's C# generator must build the same prompts and files as fmface.py."""

import io
import json
import os
import shutil
import subprocess
import sys
import tempfile
import threading
import unittest
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path

from PIL import Image

import fmface

ROOT = Path(__file__).resolve().parent.parent
PROJECT = ROOT / "tests" / "fm26_generator" / "GeneratorCheck.csproj"


def _dotnet():
    found = shutil.which("dotnet")
    local = Path.home() / ".local" / "dotnet" / "dotnet"
    return found or (str(local) if local.is_file() else None)


DOTNET = _dotnet()

PLAYER = {
    "name": "Jak  Stewart", "age": 21, "nationality": "English",
    "position": "Goalkeeper", "club": "Example FC", "height": "190 cm",
    "physical": {"Acceleration": 10, "Agility": 15, "Balance": 12, "Jumping Reach": 16,
                 "Natural Fitness": 14, "Pace": 9, "Stamina": 11, "Strength": 13},
    "technical": {"Passing": 12, "First Touch": 7, "Kicking": "11-14"},
    "mental": {"Decisions": 14, "Bravery": 5, "Composure": "12–12"},
    "goalkeeping": {"Reflexes": 18, "Handling": 17, "One On Ones": 4},
}


def _png(size=1024):
    image = Image.new("RGBA", (size, size), (0, 0, 0, 0))
    for y in range(size // 4, 3 * size // 4):
        for x in range(size // 4, 3 * size // 4, 8):
            image.paste((200, (x * 7) % 256, (y * 3) % 256, 255 if (x // 64 + y // 64) % 2 else 128), (x, y, x + 8, y + 1))
    output = io.BytesIO()
    image.save(output, "PNG")
    return output.getvalue()


def _image_api(image):
    """A stand-in for OpenAI's image API: records each request and returns ``image``."""
    import base64

    class Handler(BaseHTTPRequestHandler):
        def do_POST(self):
            body = self.rfile.read(int(self.headers["Content-Length"]))
            server.requests.append((self.path, self.headers.get("Authorization"), self.headers.get("Content-Type"), body))
            if "bad-key" in self.headers.get("Authorization", ""):
                reply, status = {"error": {"message": "Incorrect API key provided: sk-bad-key."}}, 401
            else:
                reply, status = {"data": [{"b64_json": base64.b64encode(image).decode()}]}, 200
            data = json.dumps(reply).encode()
            self.send_response(status)
            self.send_header("Content-Type", "application/json")
            self.send_header("Content-Length", str(len(data)))
            self.end_headers()
            self.wfile.write(data)

        def do_GET(self):
            server.requests.append((self.path, self.headers.get("Authorization"), None, b""))
            status = 200 if self.headers.get("Authorization") == "Bearer sk-good-key-0123456789abcdef" else 401
            data = json.dumps({"data": []} if status == 200 else {"error": {"message": "Incorrect API key"}}).encode()
            self.send_response(status)
            self.send_header("Content-Type", "application/json")
            self.send_header("Content-Length", str(len(data)))
            self.end_headers()
            self.wfile.write(data)

        def log_message(self, *args):
            pass

    server = ThreadingHTTPServer(("127.0.0.1", 0), Handler)
    server.requests = []
    threading.Thread(target=server.serve_forever, daemon=True).start()
    return server


@unittest.skipUnless(DOTNET, "the .NET 6 SDK is needed to check the FM26 generator")
class GeneratorParityTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        env = dict(os.environ, DOTNET_CLI_TELEMETRY_OPTOUT="1", DOTNET_NOLOGO="1")
        build = subprocess.run([DOTNET, "build", str(PROJECT), "-c", "Release", "-nologo", "-v", "q"],
                               capture_output=True, text=True, env=env)
        if build.returncode:
            raise RuntimeError(build.stdout + build.stderr)
        cls.dll = PROJECT.parent / "bin" / "Release" / "net6.0" / "GeneratorCheck.dll"
        cls.env = env
        cls.image = _png()
        cls.server = _image_api(cls.image)
        cls.requests = cls.server.requests
        cls.base = f"http://127.0.0.1:{cls.server.server_port}/v1"

    @classmethod
    def tearDownClass(cls):
        cls.server.shutdown()
        cls.server.server_close()

    def setUp(self):
        self.requests.clear()

    def run_cases(self, cases):
        result = subprocess.run([DOTNET, str(self.dll)], input=json.dumps(cases), capture_output=True,
                                text=True, env=self.env, timeout=120)
        self.assertEqual(result.returncode, 0, result.stderr)
        return json.loads(result.stdout)

    def python_prompt(self, action, profile, **kwargs):
        try:
            if action == "update":
                return {"prompt": fmface.update_prompt(profile)}
            return {"prompt": fmface.profile_prompt(profile, **kwargs)}
        except ValueError as exc:
            return {"error": str(exc)}

    def test_prompts_match_python(self):
        long_bio = "A dependable keeper. " * 40
        staff = {"type": "staff", "name": "Ana Ruiz", "age": 52, "nationality": "Spanish",
                 "jobTitle": "Head Coach", "club": "Example FC", "biographyHeadline": "Ana coaches.\n\nSecond paragraph"}
        profiles = [
            PLAYER,
            dict(PLAYER, biographyHeadline=long_bio),
            dict(PLAYER, biographyHeadline="Ends with a question?"),
            dict(PLAYER, club="Free Agent"),
            staff,
            dict(staff, club=""),
            dict(PLAYER, age=61),
            dict(PLAYER, age=13),
            dict(PLAYER, height=""),
            # A partly scouted player: only the physical stats known.
            dict(PLAYER, physical={"Pace": 9}),
            dict(PLAYER, physical={"Strength": "11-15", "Pace": 9}),
            dict(PLAYER, physical={}),
            dict(PLAYER, physical={"Pace": 9, "Flying": 3}),
            dict(PLAYER, technical={}, mental={"Decisions": 14}, goalkeeping={}),
            dict(PLAYER, technical={"Passing": "15-12"}),
            dict(PLAYER, technical={"Passing": 21}),
            dict(staff, jobTitle=None),
            dict(staff, type="fan"),
        ]
        cases, expected = [], []
        for profile in profiles:
            for action in ("profile", "update"):
                cases.append({"action": action, "profile": profile})
                expected.append(self.python_prompt(action, profile))
        self.assertEqual(self.run_cases(cases), expected)

    def test_saved_appearance_traits_are_shared(self):
        with tempfile.TemporaryDirectory() as tmp:
            # Python saves the traits for one person and C# saves them for another;
            # each reads the other's file and builds the same prompt.
            python_first = fmface.profile_prompt(PLAYER, person_id="111", appearance_dir=Path(tmp))
            csharp = self.run_cases([
                {"action": "profile", "profile": PLAYER, "personId": "111", "directory": tmp},
                {"action": "profile", "profile": PLAYER, "personId": "222", "directory": tmp},
                {"action": "profile", "profile": PLAYER, "personId": "x", "directory": tmp},
            ])
            self.assertEqual(csharp[0], {"prompt": python_first})
            self.assertTrue((Path(tmp) / ".fmface-appearances" / "222.json").is_file())
            self.assertEqual(csharp[1], {"prompt": fmface.profile_prompt(PLAYER, person_id="222", appearance_dir=Path(tmp))})
            self.assertEqual(csharp[2], {"error": "Player ID must contain only digits (up to 20)."})
            for age in (16, 30, 50):
                for seed in ("5118334", "name:jak stewart"):
                    self.assertEqual(fmface.appearance_traits(seed, age),
                                     self.traits(seed, age), (seed, age))

    def traits(self, seed, age):
        # Traits are only reachable through a prompt; recover them from it.
        profile = dict(PLAYER, age=age)
        with tempfile.TemporaryDirectory() as tmp:
            if seed.startswith("name:"):
                result = self.run_cases([{"action": "profile", "profile": dict(profile, name="Jak Stewart")}])[0]
                expected = fmface.profile_prompt(dict(profile, name="Jak Stewart"))
            else:
                result = self.run_cases([{"action": "profile", "profile": profile, "personId": seed, "directory": tmp}])[0]
                saved = json.loads((Path(tmp) / ".fmface-appearances" / f"{seed}.json").read_text())
                self.assertEqual(saved["schemaVersion"], 1)
                return saved["traits"]
        self.assertEqual(result["prompt"], expected)
        return fmface.appearance_traits(seed, age)

    def test_portraits_are_scaled_like_pillow_and_saved_compatibly(self):
        with tempfile.TemporaryDirectory() as tmp:
            source = Path(tmp) / "source.png"
            source.write_bytes(_png())
            graphics = Path(tmp) / "graphics"
            fitted = Path(tmp) / "fitted.png"
            results = self.run_cases([
                {"action": "fit", "input": str(source), "output": str(fitted)},
                {"action": "current", "directory": str(graphics), "personId": "5"},
            ])
            self.assertEqual(results, [{}, {"found": False}])
            with Image.open(fitted) as ours, Image.open(io.BytesIO(fmface.fit_portrait(source.read_bytes()))) as pillow:
                self.assertEqual((ours.size, ours.mode), ((256, 256), "RGBA"))
                # Compare premultiplied colour: a nearly transparent pixel's colour is invisible.
                diff = [abs(a * p[3] - b * q[3]) / 255 if i < 3 else abs(a - b)
                        for p, q in zip(ours.getdata(), pillow.getdata()) for i, (a, b) in enumerate(zip(p, q))]
            self.assertLess(sum(diff) / len(diff), 1.0)
            self.assertLessEqual(max(diff), 8)
            # C# saves into a folder Python already mapped another person in.
            fmface.save_portrait(graphics, "7", fitted.read_bytes(), newgen=None, replace=True)
            results = self.run_cases([
                {"action": "save", "directory": str(graphics), "personId": "5", "input": str(fitted)},
                {"action": "save", "directory": str(graphics), "personId": "5", "input": str(fitted)},
                {"action": "current", "directory": str(graphics), "personId": "5"},
                {"action": "current", "directory": str(graphics), "personId": "7"},
                {"action": "save", "directory": str(graphics), "personId": "8", "input": str(source.with_name("missing.png"))},
            ])
            # Each face gets a new file name, so FM26 loads it instead of the one it drew before.
            first, second = (Path(result["path"]) for result in results[:2])
            for path in (first, second):
                self.assertEqual(path.parent, graphics)
                self.assertRegex(path.name, r"^5-[0-9a-f]{16}\.png$")
            self.assertNotEqual(first, second)
            self.assertFalse(first.exists())  # replaced by the second
            self.assertEqual(results[2:4], [{"found": True}, {"found": True}])
            self.assertIn("error", results[4])
            _, maps = fmface.load_graphics_maps(graphics)
            self.assertEqual(sorted((r.get("from"), r.get("to")) for r in maps), [
                (second.stem, "graphics/pictures/person/5/portrait"), (second.stem, "graphics/pictures/person/r-5/portrait"),
                ("7", "graphics/pictures/person/7/portrait"), ("7", "graphics/pictures/person/r-7/portrait"),
            ])
            self.assertEqual(second.read_bytes(), fitted.read_bytes())
            # A face saved under the old name ("7.png", here by Python) is replaced and removed.
            again = self.run_cases([{"action": "save", "directory": str(graphics), "personId": "7", "input": str(fitted)}])
            self.assertRegex(Path(again[0]["path"]).name, r"^7-[0-9a-f]{16}\.png$")
            self.assertFalse((graphics / "7.png").exists())
            (graphics / "config.xml").write_text("<record><list id='other'/></record>")
            refused = self.run_cases([{"action": "save", "directory": str(graphics), "personId": "9", "input": str(fitted)}])
            self.assertEqual(refused, [{"error": "Existing config.xml has no maps list; refusing to alter it."}])
            self.assertEqual(list(graphics.glob("9*.png")), [])

    def test_image_requests_match_python(self):
        requests, image, base = self.requests, self.image, self.base
        with tempfile.TemporaryDirectory() as tmp:
            reference = Path(tmp) / "reference.png"
            reference_png = fmface.fit_portrait(image)
            reference.write_bytes(reference_png)
            out = Path(tmp) / "out.png"
            results = self.run_cases([
                {"action": "generate", "apiKey": "sk-test", "quality": "low", "baseUrl": base,
                 "prompt": "A face", "output": str(out)},
                {"action": "edit", "apiKey": "sk-test", "baseUrl": base, "prompt": "Update",
                 "input": str(reference), "output": str(out)},
                {"action": "generate", "apiKey": "sk-bad-key", "baseUrl": base, "prompt": "A face", "output": str(out)},
                {"action": "generate", "apiKey": "", "baseUrl": base, "prompt": "A face", "output": str(out)},
                {"action": "generate", "apiKey": "sk-test", "quality": "best", "baseUrl": base,
                 "prompt": "A face", "output": str(out)},
            ])
            with Image.open(out) as saved:
                self.assertEqual(saved.size, (256, 256))
        self.assertEqual(results[:2], [{}, {}])
        self.assertEqual(results[2], {"error": "OpenAI image API returned HTTP 401: Incorrect API key provided: [REDACTED]."})
        self.assertEqual(results[3], {"error": "No OpenAI API key is saved yet."})
        self.assertIn("Quality", results[4]["error"])
        self.assertEqual(len(requests), 3)
        path, auth, content_type, body = requests[0]
        self.assertEqual((path, auth, content_type.split(";")[0]), ("/v1/images/generations", "Bearer sk-test", "application/json"))
        _, settings = fmface._image_settings({"OPENAI_API_KEY": "sk-test", "OPENAI_IMAGE_QUALITY": "low"})
        self.assertEqual(json.loads(body), dict(settings, prompt="A face"))
        path, auth, content_type, body = requests[1]
        self.assertEqual((path, content_type.split(";")[0]), ("/v1/images/edits", "multipart/form-data"))
        for name in ("model", "size", "quality", "background", "output_format", "prompt"):
            self.assertIn(f'name={name}'.encode(), body.replace(b'"', b""))
        self.assertIn(b'name="image[]"', body.replace(b"name=image[]", b'name="image[]"'))
        self.assertIn(reference_png, body)


    def test_key_is_checked_for_free_and_kept_outside_the_game_folder(self):
        good = "sk-good-key-0123456789abcdef"
        with tempfile.TemporaryDirectory() as tmp:
            path = str(Path(tmp) / "FM Face" / "openai-key")
            results = self.run_cases([
                {"action": "loadkey", "path": path},
                {"action": "checkkey", "baseUrl": self.base, "apiKey": good},
                {"action": "checkkey", "baseUrl": self.base, "apiKey": "sk-wrong-key-0123456789abcdef"},
                {"action": "savekey", "path": path, "apiKey": f"  {good}\n"},
                {"action": "loadkey", "path": path},
                {"action": "savekey", "path": path, "apiKey": "my key"},
                {"action": "savekey", "path": path, "apiKey": ""},
                {"action": "loadkey", "path": path},
            ])
            # Off Windows the file is marked unencrypted; on Windows it is DPAPI-encrypted.
            marker = b"FMFK1\n" if sys.platform == "win32" else b"FMFK0\n"
            self.assertTrue(Path(path).read_bytes().startswith(marker))
        self.assertEqual(results[0], {"apiKey": None})
        self.assertEqual(results[1], {"problem": None})
        self.assertEqual(results[2], {"problem": "OpenAI didn't accept this key. Check that you copied all of it."})
        self.assertEqual(results[3:5], [{}, {"apiKey": good}])
        self.assertIn("starts with sk-", results[5]["error"])
        self.assertEqual(results[6], {"error": "Paste your OpenAI API key first."})
        self.assertEqual(results[7], {"apiKey": good})
        self.assertEqual([request[0] for request in self.requests], ["/v1/models", "/v1/models"])

    def test_in_game_requests_update_the_saved_face_and_keep_it_on_failure(self):
        staff = {"type": "staff", "name": "Alex Morgan", "age": 45, "nationality": "Wales",
                 "jobTitle": "Assistant Manager", "biographyHeadline": "Morgan started coaching in 2021."}
        player = {"name": "Lionel Messi", "age": 38, "club": "Inter Miami"}
        with tempfile.TemporaryDirectory() as tmp:
            graphics = Path(tmp) / "graphics"
            shown = Path(tmp) / "shown.png"
            shown_png = fmface.fit_portrait(_png(512))
            shown.write_bytes(shown_png)
            ok = {"apiKey": "sk-test", "baseUrl": self.base, "directory": str(graphics)}
            results = self.run_cases([
                dict(ok, action="portrait", mode="update", personId="7458500", profile=player),
                dict(ok, action="portrait", mode="update", personId="7458500", profile=player, input=str(shown)),
                dict(ok, action="portrait", mode="update", personId="7458500", profile=player, input=str(shown)),
                dict(ok, action="portrait", personId="5118334", profile=staff),
            ])
            saved = Path(results[2]["path"]).read_bytes()
            staff_path = Path(results[3]["path"])
            staff_before = staff_path.read_bytes()
            failed = self.run_cases([
                dict(ok, action="portrait", personId="5118334", profile=staff, apiKey="sk-bad-key"),
                dict(ok, action="portrait", personId="5118334", profile=dict(staff, age=3)),
            ])
            self.assertEqual(staff_path.read_bytes(), staff_before)
            self.assertEqual(sorted(graphics.glob("*.png")), sorted([Path(results[2]["path"]), staff_path]))
        self.assertEqual(results[0], {"error": "This person has no face to update yet. Use Generate face instead."})
        for result, person in zip(results[1:], ("7458500", "7458500", "5118334")):
            self.assertRegex(Path(result["path"]).name, rf"^{person}-[0-9a-f]{{16}}\.png$")
        self.assertIn("401", failed[0]["error"])
        self.assertEqual(failed[1], {"error": "Staff age is missing or invalid."})
        paths = [request[0] for request in self.requests]
        self.assertEqual(paths, ["/v1/images/edits", "/v1/images/edits", "/v1/images/generations", "/v1/images/generations"])
        # The first update starts from the game's picture, the second from FM Face's saved face.
        self.assertIn(shown_png, self.requests[0][3])
        self.assertNotIn(shown_png, self.requests[1][3])
        self.assertIn(saved, self.requests[1][3])
        self.assertIn("at age 38", self.requests[0][3].decode("latin-1"))
        self.assertIn("Morgan started coaching in 2021.", json.loads(self.requests[2][3])["prompt"])


if __name__ == "__main__":
    unittest.main()
