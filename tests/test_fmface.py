import tempfile
import base64
import io
import json
import os
import re
import threading
import unittest
import urllib.error
import urllib.request
import urllib.parse
import xml.etree.ElementTree as ET
from pathlib import Path
from unittest.mock import patch
import fmface

from http.server import ThreadingHTTPServer

from fmface import FaceHandler, PNG_SIGNATURE, generate_png, load_env, profile_prompt, save_portrait, validate_id
from PIL import Image


def real_png(size=1024, colour=(200, 120, 90, 255)):
    output = io.BytesIO()
    image = Image.new("RGBA", (size, size), (0, 0, 0, 0))
    image.paste(colour, (size // 4, size // 4, 3 * size // 4, 3 * size // 4))
    image.save(output, "PNG")
    return output.getvalue()


class PortraitTests(unittest.TestCase):
    def test_profile_prompt_includes_identity_all_physical_and_extreme_other_stats(self):
        profile = {
            "name": "Jak Stewart", "age": 21, "nationality": "English",
            "position": "Goalkeeper", "club": "Example FC", "height": "190 cm",
            "physical": {
                "Acceleration": 10, "Agility": 15, "Balance": 12,
                "Jumping Reach": 16, "Natural Fitness": 14, "Pace": 9,
                "Stamina": 11, "Strength": 13,
            },
            "technical": {"Passing": 12, "First Touch": 7},
            "mental": {"Decisions": 14, "Bravery": 5},
            "goalkeeping": {"Reflexes": 18, "Handling": 17, "One On Ones": 4},
        }
        prompt = profile_prompt(profile)
        self.assertIn("Jak Stewart, a 21-year-old Goalkeeper who plays for Example FC. Nationality: English", prompt)
        self.assertIn("Jak is 190 cm tall", prompt)
        for stat, score in profile["physical"].items():
            self.assertIn(f"{stat} {score}/20", prompt)
        for stat in ("Reflexes 18/20", "Handling 17/20", "Decisions 14/20",
                     "One On Ones 4/20", "Bravery 5/20", "First Touch 7/20"):
            self.assertIn(stat, prompt)

    def test_profile_prompt_accepts_visible_scouting_ranges(self):
        profile = {
            "name": "Jak Stewart", "age": 21, "nationality": "England",
            "position": "Goalkeeper", "club": "MK Dons", "height": "6'0\"",
            "physical": dict.fromkeys(("Acceleration", "Agility", "Balance", "Jumping Reach",
                                           "Natural Fitness", "Pace", "Stamina", "Strength"), 10),
            "technical": {"Passing": "9-11", "First Touch": 1, "Technique": 1},
            "mental": {"Determination": "14-17", "Bravery": 8, "Vision": "4-6"},
        }
        profile["physical"]["Natural Fitness"] = "12-14"
        prompt = profile_prompt(profile)
        self.assertIn("Nationality: England", prompt)
        self.assertIn("Natural Fitness 12–14/20", prompt)
        self.assertIn("Determination 14–17/20", prompt)

    def test_staff_prompt_uses_identity_role_and_optional_biography(self):
        profile = {
            "type": "staff", "name": "Alex Morgan", "age": 45,
            "nationality": "Welsh", "jobTitle": "Assistant Manager",
            "club": "Example FC",
            "biographyHeadline": "Morgan began coaching after a long playing career.\n\nMore history follows.",
        }
        prompt = profile_prompt(profile)
        self.assertIn("Alex Morgan, age 45, nationality Welsh, job title Assistant Manager at Example FC", prompt)
        self.assertIn("Morgan began coaching after a long playing career", prompt)
        self.assertNotIn("More history follows", prompt)
        self.assertIn("staff portrait", prompt)
        self.assertNotIn("Physical stats", prompt)

    def test_player_prompt_includes_optional_biography(self):
        profile = {
            "name": "Jak Stewart", "age": 21, "nationality": "English",
            "position": "Goalkeeper", "club": "Example FC", "height": "190 cm",
            "physical": dict.fromkeys(("Acceleration", "Agility", "Balance", "Jumping Reach",
                                           "Natural Fitness", "Pace", "Stamina", "Strength"), 10),
            "technical": {"Passing": 10, "First Touch": 10, "Dribbling": 10},
            "mental": {"Decisions": 10, "Bravery": 10, "Vision": 10},
            "biographyHeadline": "A promising keeper who joined the club in 2024.",
        }
        self.assertIn("Biography headline: A promising keeper who joined the club in 2024.",
                      profile_prompt(profile))
        profile["biographyHeadline"] = None
        self.assertNotIn("Biography headline:", profile_prompt(profile))

    def test_staff_prompt_rejects_missing_job_title(self):
        with self.assertRaisesRegex(ValueError, "Staff jobTitle"):
            profile_prompt({"type": "staff", "name": "Alex Morgan", "age": 45,
                            "nationality": "Welsh"})

    def test_server_rejects_nonlocal_host_before_generation(self):
        with tempfile.TemporaryDirectory() as tmp:
            prior_env, prior_reload = FaceHandler.env, FaceHandler.reload_env
            FaceHandler.env = {"FM_GRAPHICS_DIR": tmp}
            FaceHandler.reload_env = False
            server = ThreadingHTTPServer(("127.0.0.1", 0), FaceHandler)
            worker = threading.Thread(target=server.serve_forever, daemon=True)
            worker.start()
            try:
                url = f"http://127.0.0.1:{server.server_port}"
                form = urllib.parse.urlencode({"player_id": "5118334", "description": "short hair",
                                               "csrf_token": FaceHandler.form_token}).encode()
                request = urllib.request.Request(url + "/generate", data=form)
                request.add_header("Host", f"attacker.example:{server.server_port}")
                with patch("fmface.generate_png") as generate:
                    with self.assertRaises(urllib.error.HTTPError) as rejected:
                        urllib.request.urlopen(request)
                    self.assertEqual(rejected.exception.code, 403)
                    generate.assert_not_called()
                get = urllib.request.Request(url + "/", headers={"Host": f"attacker.example:{server.server_port}"})
                with self.assertRaises(urllib.error.HTTPError) as rejected_get:
                    urllib.request.urlopen(get)
                self.assertEqual(rejected_get.exception.code, 403)
                with urllib.request.urlopen(f"http://localhost:{server.server_port}/") as response:
                    self.assertEqual(response.status, 200)
            finally:
                server.shutdown()
                server.server_close()
                worker.join(timeout=3)
                FaceHandler.env, FaceHandler.reload_env = prior_env, prior_reload

    def test_dotenv_key_is_used_when_process_key_is_empty(self):
        with tempfile.TemporaryDirectory() as tmp:
            env_file = Path(tmp) / ".env"
            env_file.write_text("OPENAI_API_KEY=test-key\n")
            with patch.dict(os.environ, {"OPENAI_API_KEY": ""}):
                self.assertEqual(load_env(env_file)["OPENAI_API_KEY"], "test-key")

    def test_server_picks_up_key_added_after_start(self):
        with tempfile.TemporaryDirectory() as tmp:
            env_file = Path(tmp) / ".env"
            env_file.write_text("OPENAI_API_KEY=\n")
            handler = object.__new__(FaceHandler)
            handler.reload_env = True
            # Never the key this computer has saved.
            with patch("fmface.load_env", side_effect=lambda: load_env(env_file)), \
                    patch("keystore.default_path", return_value=None):
                self.assertFalse(handler._current_env().get("OPENAI_API_KEY"))
                env_file.write_text("OPENAI_API_KEY=test-key\n")
                self.assertEqual(handler._current_env()["OPENAI_API_KEY"], "test-key")

    def test_mapping_preserves_other_players_and_prevents_accidental_replace(self):
        with tempfile.TemporaryDirectory() as tmp:
            folder = Path(tmp)
            png = PNG_SIGNATURE + b"test bytes"
            save_portrait(folder, "123", png, newgen=False, replace=False)
            save_portrait(folder, "456", png, newgen=True, replace=False)
            with self.assertRaises(FileExistsError):
                save_portrait(folder, "123", png, newgen=True, replace=False)
            save_portrait(folder, "123", png, newgen=True, replace=True)
            root = ET.parse(folder / "config.xml").getroot()
            records = {r.get("from"): r.get("to") for r in root.find("./list[@id='maps']")}
            self.assertEqual(records, {
                "123": "graphics/pictures/person/r-123/portrait",
                "456": "graphics/pictures/person/r-456/portrait",
            })
            self.assertEqual((folder / "123.png").read_bytes(), png)

    def test_failed_config_write_restores_previous_portrait(self):
        with tempfile.TemporaryDirectory() as tmp:
            folder = Path(tmp)
            old = PNG_SIGNATURE + b"old image"
            save_portrait(folder, "123", old, newgen=False, replace=False)
            old_config = (folder / "config.xml").read_bytes()
            original_write = fmface._atomic_write

            def fail_config(path, content):
                if path.name == "config.xml":
                    raise OSError("simulated config write failure")
                original_write(path, content)

            with patch("fmface._atomic_write", side_effect=fail_config):
                with self.assertRaisesRegex(OSError, "simulated config write failure"):
                    save_portrait(folder, "123", PNG_SIGNATURE + b"replacement", newgen=None, replace=True)
            self.assertEqual((folder / "123.png").read_bytes(), old)
            self.assertEqual((folder / "config.xml").read_bytes(), old_config)

            with patch("fmface._atomic_write", side_effect=fail_config):
                with self.assertRaisesRegex(OSError, "simulated config write failure"):
                    save_portrait(folder, "456", PNG_SIGNATURE + b"new image", newgen=None, replace=True)
            self.assertFalse((folder / "456.png").exists())

    def test_invalid_ids_and_missing_key_do_not_make_requests(self):
        with self.assertRaises(ValueError):
            validate_id("123/../../secret")
        with patch("urllib.request.urlopen") as open_url:
            with self.assertRaisesRegex(ValueError, "OPENAI_API_KEY"):
                generate_png({}, "dark hair")
            open_url.assert_not_called()

    def test_image_quality_is_sent_and_usage_can_be_collected(self):
        png = real_png()
        usage = {"input_tokens": 100, "output_tokens": 200, "total_tokens": 300}
        body = {"data": [{"b64_json": base64.b64encode(png).decode()}], "usage": usage}
        for configured, expected in ((None, "medium"), ("low", "low")):
            env = {"OPENAI_API_KEY": "test-only"}
            if configured is not None:
                env["OPENAI_IMAGE_QUALITY"] = configured
            metadata = {}
            with patch("fmface.urllib.request.urlopen", return_value=io.BytesIO(json.dumps(body).encode())) as request:
                saved = generate_png(env, "test portrait", metadata=metadata)
            # Stored at FM's portrait size, with the transparency kept.
            with Image.open(io.BytesIO(saved)) as image:
                self.assertEqual((image.size, image.mode), ((256, 256), "RGBA"))
                self.assertEqual(image.getpixel((0, 0))[3], 0)
                self.assertEqual(image.getpixel((128, 128)), (200, 120, 90, 255))
            payload = json.loads(request.call_args.args[0].data)
            self.assertEqual(payload["quality"], expected)
            self.assertEqual(payload["size"], "1024x1024")
            self.assertEqual(metadata["usage"], usage)
            self.assertEqual(metadata["quality"], expected)
            self.assertNotIn("data", metadata)

    def test_invalid_quality_is_refused_before_a_billed_request(self):
        with patch("fmface.urllib.request.urlopen") as request:
            with self.assertRaisesRegex(ValueError, "OPENAI_IMAGE_QUALITY"):
                generate_png({"OPENAI_API_KEY": "test-only", "OPENAI_IMAGE_QUALITY": "typo"}, "portrait")
            request.assert_not_called()

    def test_update_prompt_keeps_the_person_and_changes_age_and_kit(self):
        prompt = fmface.update_prompt({"name": "Lionel Messi", "age": 38, "club": "Inter Miami",
                                       "position": "Striker"})
        self.assertIn("Lionel Messi's current portrait", prompt)
        self.assertIn("at age 38", prompt)
        self.assertIn("home kit and colours of Inter Miami", prompt)
        self.assertIn("updated version of the supplied picture of Lionel Messi", prompt)
        self.assertIn("transparent background", prompt)
        # New-face variety instructions would fight the supplied likeness.
        self.assertNotIn("distinctive facial proportions", prompt)
        staff = fmface.update_prompt({"type": "staff", "name": "Alex Morgan", "age": 61})
        self.assertIn("unbranded dark jacket", staff)
        with self.assertRaisesRegex(ValueError, "club"):
            fmface.update_prompt({"name": "Lionel Messi", "age": 38})
        with self.assertRaisesRegex(ValueError, "age"):
            fmface.update_prompt({"name": "Lionel Messi", "age": 3, "club": "Inter Miami"})

    def test_edit_request_sends_existing_portrait_and_settings(self):
        reference = PNG_SIGNATURE + b"current face"
        png = real_png(256)
        body = {"data": [{"b64_json": base64.b64encode(png).decode()}]}
        env = {"OPENAI_API_KEY": "test-only", "OPENAI_IMAGE_QUALITY": "low"}
        with patch("fmface.urllib.request.urlopen", return_value=io.BytesIO(json.dumps(body).encode())) as request:
            self.assertEqual(fmface.edit_png(env, "update this portrait", reference), png)
        sent = request.call_args.args[0]
        self.assertEqual(sent.full_url, "https://api.openai.com/v1/images/edits")
        self.assertTrue(sent.headers["Content-type"].startswith("multipart/form-data; boundary=fmface-"))
        self.assertIn(b'name="image[]"; filename="portrait.png"', sent.data)
        self.assertIn(reference, sent.data)
        self.assertIn(b'name="quality"\r\n\r\nlow\r\n', sent.data)
        self.assertIn(b'name="background"\r\n\r\ntransparent\r\n', sent.data)
        self.assertIn(b"update this portrait", sent.data)
        with patch("fmface.urllib.request.urlopen") as request:
            with self.assertRaisesRegex(ValueError, "PNG"):
                fmface.edit_png(env, "update", b"GIF89a")
            request.assert_not_called()

    def test_web_page_renders_without_api_key(self):
        from io import BytesIO

        handler = object.__new__(FaceHandler)
        handler.wfile = BytesIO()
        handler.send_response = lambda status: None
        handler.send_header = lambda name, value: None
        handler.end_headers = lambda: None
        handler._page(player_id="123", message="Ready")
        body = handler.wfile.getvalue().decode("utf-8")
        self.assertIn('value="123"', body)
        self.assertIn("Ready", body)

    def test_actionlink_event_to_generated_mapping(self):
        with tempfile.TemporaryDirectory() as tmp:
            prior_env, prior_id, prior_port = FaceHandler.env, FaceHandler.selected_id, FaceHandler.port
            FaceHandler.env = {"FM_GRAPHICS_DIR": tmp}
            FaceHandler.selected_id = ""
            server = ThreadingHTTPServer(("127.0.0.1", 0), FaceHandler)
            FaceHandler.port = server.server_port
            worker = threading.Thread(target=server.serve_forever, daemon=True)
            worker.start()
            try:
                base = f"http://127.0.0.1:{server.server_port}"
                event = json.dumps({"event": "button.click", "buttonId": "fmface", "playerId": 200123}).encode()
                with patch("fmface.webbrowser.open"):
                    with urllib.request.urlopen(urllib.request.Request(base + "/fm-events", data=event, headers={"Content-Type": "application/json"})) as response:
                        self.assertEqual(response.status, 204)
                with urllib.request.urlopen(base + "/") as response:
                    page = response.read().decode()
                    self.assertIn('value="200123"', page)
                    token = re.search(r'name="csrf_token" value="([^"]+)"', page).group(1)
                fields = {"player_id": "200123", "description": "short dark hair", "newgen": "on"}
                with patch("fmface.generate_png", return_value=PNG_SIGNATURE + b"test bytes") as generate:
                    with self.assertRaises(urllib.error.HTTPError) as rejected:
                        urllib.request.urlopen(urllib.request.Request(base + "/generate", data=urllib.parse.urlencode(fields).encode()))
                    self.assertEqual(rejected.exception.code, 403)
                    generate.assert_not_called()
                    fields["csrf_token"] = token
                    with urllib.request.urlopen(urllib.request.Request(base + "/generate", data=urllib.parse.urlencode(fields).encode())) as response:
                        self.assertIn(b"Portrait saved", response.read())
                record = ET.parse(Path(tmp) / "config.xml").getroot().find("./list[@id='maps']/record")
                self.assertEqual(record.get("to"), "graphics/pictures/person/r-200123/portrait")
            finally:
                server.shutdown()
                server.server_close()
                worker.join(timeout=3)
                FaceHandler.env, FaceHandler.selected_id, FaceHandler.port = prior_env, prior_id, prior_port


if __name__ == "__main__":
    unittest.main()
