"""Appearance consistency, persistence and age boundaries; no image API requests."""

import json
import tempfile
import unittest
from pathlib import Path
from unittest.mock import patch

import regeneratefm
import appearance
from appearance import appearance_traits


class AppearanceTests(unittest.TestCase):
    def profile(self, **changes):
        return {"type": "staff", "name": "Alex Morgan", "age": 45,
                "nationality": "Welsh", "jobTitle": "Coach", **changes}

    def test_id_controls_appearance_independently_of_name_or_club(self):
        original = regeneratefm.profile_prompt(self.profile(), person_id="123")
        renamed = regeneratefm.profile_prompt(self.profile(name="Alex Jones", club="Other FC"), person_id="123")
        appearance = lambda text: text.split("Individual appearance: ")[1].split("Keep these features")[0]
        self.assertEqual(appearance(original), appearance(renamed))
        self.assertNotEqual(appearance(original), appearance(regeneratefm.profile_prompt(self.profile(), person_id="456")))

    def test_saved_choices_survive_age_and_generator_changes(self):
        with tempfile.TemporaryDirectory() as folder:
            directory = Path(folder)
            # A white Northern European keeps the saved hair colour as well.
            white = {"ethnicity": "NorthernEuropean"}
            original = regeneratefm.profile_prompt(self.profile(**white), person_id="123", appearance_dir=directory)
            path = directory / ".regeneratefm-appearances/123.json"
            saved = path.read_bytes()
            with patch("regeneratefm.appearance_traits", side_effect=AssertionError("Must reuse saved choices")):
                older = regeneratefm.profile_prompt(self.profile(age=46, **white), person_id="123", appearance_dir=directory)
            self.assertEqual(path.read_bytes(), saved)
            self.assertIn(json.loads(saved)["traits"]["hair"], original)
            self.assertIn(json.loads(saved)["traits"]["hair"], older)

    def test_preview_builds_same_choices_without_writing_files(self):
        with tempfile.TemporaryDirectory() as folder:
            preview = regeneratefm.profile_prompt(self.profile(), person_id="123")
            self.assertEqual(list(Path(folder).iterdir()), [])
            saved = regeneratefm.profile_prompt(self.profile(), person_id="123", appearance_dir=Path(folder))
            self.assertEqual(preview, saved)

    def test_invalid_saved_identity_or_traits_are_refused(self):
        with tempfile.TemporaryDirectory() as folder:
            directory = Path(folder)
            regeneratefm.profile_prompt(self.profile(), person_id="123", appearance_dir=directory)
            path = directory / ".regeneratefm-appearances/123.json"
            saved = json.loads(path.read_text())
            for change in ({"personId": "456"}, {"traits": {"hair": "short hair"}}, {"traits": None}):
                path.write_text(json.dumps({**saved, **change}))
                with self.assertRaises(ValueError):
                    regeneratefm.profile_prompt(self.profile(), person_id="123", appearance_dir=directory)

    def test_minor_profiles_are_clean_shaven_and_population_has_variety(self):
        young = [appearance_traits(str(i), 16, "male") for i in range(100)]
        self.assertTrue(all(p["facialHair"] == "clean-shaven" for p in young))
        self.assertGreater(len({p["face"] for p in young}), 50)
        self.assertGreater(len({p["hair"] for p in young}), 30)
        self.assertGreater(len({p["expression"] for p in young}), 3)

    def test_only_men_get_facial_hair(self):
        for age in (18, 25, 40, 60):
            women = [appearance_traits(str(i), age, "female") for i in range(200)]
            unknown = [appearance_traits(str(i), age) for i in range(200)]
            self.assertEqual({p["facialHair"] for p in women + unknown}, {"no facial hair"})
            # Women get their own hairstyles, never the men's cuts.
            self.assertFalse([p["hair"] for p in women if "buzz cut" in p["hair"] or "receding" in p["hair"]
                              or "shaved" in p["hair"]])
            self.assertGreater(len({p["hair"].split(";")[0] for p in women}), 6)
        men = [appearance_traits(str(i), 30, "male") for i in range(200)]
        self.assertGreater(len({p["facialHair"] for p in men}), 3)

    def test_prompts_say_who_is_a_woman_and_rule_out_facial_hair(self):
        for kind in ("staff", "player"):
            profile = self.profile(gender="female") if kind == "staff" else dict(PLAYER, gender="female")
            for prompt in (regeneratefm.profile_prompt(profile, person_id="5"), regeneratefm.update_prompt(profile)):
                self.assertIn(" is a woman.", prompt)
                self.assertNotIn("age-appropriate facial hair", prompt)
                for word in ("moustache", "beard", "stubble", "clean-shaven"):
                    self.assertNotIn(word, prompt)
            self.assertIn("no facial hair", regeneratefm.profile_prompt(profile, person_id="5"))
            self.assertIn("Do not add any facial hair.", regeneratefm.update_prompt(profile))
        man = regeneratefm.profile_prompt(self.profile(gender="male"), person_id="5")
        self.assertIn("Alex is a man.", man.replace("Alex Morgan is", "Alex is"))
        self.assertIn("age-appropriate facial hair", man)
        with self.assertRaisesRegex(ValueError, "Gender"):
            regeneratefm.profile_prompt(self.profile(gender="other"))

    def test_saved_traits_from_before_gender_was_known_are_chosen_again(self):
        with tempfile.TemporaryDirectory() as folder:
            directory = Path(folder)
            path = directory / ".regeneratefm-appearances/7.json"
            path.parent.mkdir()
            # As saved before gender was read: a man's facial hair and haircut.
            path.write_text(json.dumps({"schemaVersion": 1, "personId": "7", "traits": dict(
                appearance_traits("7", 40, "male"), facialHair="a modest moustache", hair="a close buzz cut")}))
            unknown = regeneratefm.profile_prompt(self.profile(), person_id="7", appearance_dir=directory)
            self.assertNotIn("moustache", unknown)  # not known: facial hair is dropped, the rest kept
            self.assertIn("buzz cut", unknown)
            woman = regeneratefm.profile_prompt(self.profile(gender="female"), person_id="7", appearance_dir=directory)
            self.assertNotIn("moustache", woman)
            self.assertNotIn("buzz cut", woman)
            saved = json.loads(path.read_text())
            self.assertEqual((saved["gender"], saved["traits"]), ("female", appearance_traits("7", 45, "female")))
            # Read again for her, the saved traits are kept.
            self.assertEqual(regeneratefm.profile_prompt(self.profile(gender="female", age=50), person_id="7",
                                                         appearance_dir=directory).split("Individual appearance")[1].split(".")[0],
                             woman.split("Individual appearance")[1].split(".")[0])


PLAYER = {"type": "player", "name": "Ana Silva", "age": 24, "nationality": "Brazilian", "position": "Striker",
          "club": "Example FC", "height": "5'7\"", "physical": {"Pace": 15},
          "technical": {"Finishing": 14, "Dribbling": 13, "Passing": 11},
          "mental": {"Composure": 12, "Vision": 10, "Work Rate": 15}}



class FamilyTests(unittest.TestCase):
    def test_relatives_share_some_traits_and_keep_their_own(self):
        father = appearance.appearance_traits("1001", 45, "male", "family:1001")
        son = appearance.appearance_traits("1002", 18, "male", "family:1001")
        alone = appearance.appearance_traits("1002", 18, "male")
        self.assertEqual(father["face"].split(",")[0], son["face"].split(",")[0])
        self.assertEqual(father["features"], son["features"])
        self.assertNotEqual(son, alone)
        # Jaw, cheeks, hairstyle and expression stay the person's own.
        self.assertEqual(son["expression"], alone["expression"])
        self.assertEqual(son["hair"].split(";")[0], alone["hair"].split(";")[0])

    def test_only_family_groups_count_closest_first(self):
        profile = {"relatives": [{"relation": "Team-mates", "personId": "5"}, {"relation": "Cousins", "personId": "40"},
                                 {"relation": "Former Coaching Staff", "personId": "6"},
                                 {"relation": " Twin  Brothers ", "personId": "30"}, {"relation": "Children", "personId": "41"}]}
        self.assertEqual(regeneratefm.family_relatives(profile),
                         [("twin brother", "30"), ("child", "41"), ("cousin", "40")])
        self.assertEqual(regeneratefm.family_seed("100", regeneratefm.family_relatives(profile)), "family:30")
        self.assertIsNone(regeneratefm.family_seed("100", []))
        with self.assertRaisesRegex(ValueError, "digits"):
            regeneratefm.family_relatives({"relatives": [{"relation": "Father", "personId": "1; rm"}]})

    def test_a_relatives_prompt_asks_for_only_a_slight_resemblance(self):
        prompt = regeneratefm.relative_prompt(dict(PLAYER, relatives=[{"relation": "Father", "personId": "7"}]), "father")
        self.assertIn("The supplied image is a portrait of", prompt)
        self.assertIn("only a slight family resemblance", prompt)
        with self.assertRaisesRegex(ValueError, "family relations"):
            regeneratefm.relative_prompt(PLAYER, "team-mate")


if __name__ == "__main__":
    unittest.main()
