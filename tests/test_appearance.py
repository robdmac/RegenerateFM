"""Appearance consistency, persistence and age boundaries; no image API requests."""

import json
import tempfile
import unittest
from pathlib import Path
from unittest.mock import patch

import fmface
from appearance import appearance_traits


class AppearanceTests(unittest.TestCase):
    def profile(self, **changes):
        return {"type": "staff", "name": "Alex Morgan", "age": 45,
                "nationality": "Welsh", "jobTitle": "Coach", **changes}

    def test_id_controls_appearance_independently_of_name_or_club(self):
        original = fmface.profile_prompt(self.profile(), person_id="123")
        renamed = fmface.profile_prompt(self.profile(name="Alex Jones", club="Other FC"), person_id="123")
        appearance = lambda text: text.split("Individual appearance: ")[1].split("Keep these features")[0]
        self.assertEqual(appearance(original), appearance(renamed))
        self.assertNotEqual(appearance(original), appearance(fmface.profile_prompt(self.profile(), person_id="456")))

    def test_saved_choices_survive_age_and_generator_changes(self):
        with tempfile.TemporaryDirectory() as folder:
            directory = Path(folder)
            original = fmface.profile_prompt(self.profile(), person_id="123", appearance_dir=directory)
            path = directory / ".fmface-appearances/123.json"
            saved = path.read_bytes()
            with patch("fmface.appearance_traits", side_effect=AssertionError("Must reuse saved choices")):
                older = fmface.profile_prompt(self.profile(age=46), person_id="123", appearance_dir=directory)
            self.assertEqual(path.read_bytes(), saved)
            self.assertIn(json.loads(saved)["traits"]["hair"], original)
            self.assertIn(json.loads(saved)["traits"]["hair"], older)

    def test_preview_builds_same_choices_without_writing_files(self):
        with tempfile.TemporaryDirectory() as folder:
            preview = fmface.profile_prompt(self.profile(), person_id="123")
            self.assertEqual(list(Path(folder).iterdir()), [])
            saved = fmface.profile_prompt(self.profile(), person_id="123", appearance_dir=Path(folder))
            self.assertEqual(preview, saved)

    def test_invalid_saved_identity_or_traits_are_refused(self):
        with tempfile.TemporaryDirectory() as folder:
            directory = Path(folder)
            fmface.profile_prompt(self.profile(), person_id="123", appearance_dir=directory)
            path = directory / ".fmface-appearances/123.json"
            saved = json.loads(path.read_text())
            for change in ({"personId": "456"}, {"traits": {"hair": "short hair"}}, {"traits": None}):
                path.write_text(json.dumps({**saved, **change}))
                with self.assertRaises(ValueError):
                    fmface.profile_prompt(self.profile(), person_id="123", appearance_dir=directory)

    def test_minor_profiles_are_clean_shaven_and_population_has_variety(self):
        young = [appearance_traits(str(i), 16) for i in range(100)]
        self.assertTrue(all(p["facialHair"] == "clean-shaven" for p in young))
        self.assertGreater(len({p["face"] for p in young}), 50)
        self.assertGreater(len({p["hair"] for p in young}), 30)
        self.assertGreater(len({p["expression"] for p in young}), 3)


if __name__ == "__main__":
    unittest.main()
