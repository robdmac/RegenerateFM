# FM24 player, staff and squad faces (experimental)

FM Face for FM24 works inside the game through the [native DLL](native_menu/README.md), which is pinned to the tested FM24 24.4.2 executable on Steam/Proton.
- Click a player or staff portrait on their Overview to get FM24's own popup with **Generate new face** and **Update face**. The same two items are at the end of the profile's **Overview** tab menu.
- The Squad screen's **Overview** menu ends with **Generate missing squad faces…**.

The companion reads everything from FM24's memory: the open profile, the biography FM24 is showing, and every Squad row. It makes the image request, writes a versioned portrait and reloads the skin inside the game. Progress, the squad confirmation, results and errors appear in FM24's own dialogs. There is no X11 code, no screenshots or OCR, and no browser.

Versioned filenames matter: FM24 kept showing the old portrait when the same filename was overwritten.

## What it reads

From read-only process memory, the companion reads:
- the open person's ID, name and age
- nationality and club
- job title or position, and height
- player attributes
- the first paragraph of the Biography box FM24 is displaying

**Checks before any request**
- **Person:** the title, the canonical person record and the contract must agree.
- **Header:** it must show the same `(ID:n)`, so enable **Show screen IDs in Title Bar to assist skinning** in FM24 Preferences.
- **Repeat read:** everything is read twice to catch navigation during capture.

If anything can't be verified, the request is refused before any image is requested. If the in-game reload fails after a face is saved, the image is kept; use Preferences > Reload Skin to load it.

## Setup

1. Create a virtual environment and install the companion's dependencies:

   ```sh
   python3 -m venv .venv-fm24
   .venv-fm24/bin/pip install -r fm24/requirements.txt
   ```

2. Extract your own FM24 `panels.fmf` with the FM24 Resource Archiver. Locate `panels/player/player overview panel.xml` and `panels/non_player/non player profile.xml` in that extraction. This repository does not include Sports Interactive's panels.
3. Find your FM24 **user-data** directory, which contains `skins` and `graphics`, and create its `graphics/fmface` directory. For Steam Proton this is normally inside the game's `compatdata/2252570/pfx/drive_c/users/steamuser/Documents/Sports Interactive/Football Manager 2024/`. It is not the game installation directory.
4. Build the skin, supplying your extracted panel and user-data paths:

   ```sh
   python3 fm24/build_skin.py \
     --panel-source '/path/to/extracted/panels/player/player overview panel.xml' \
     --staff-panel-source '/path/to/extracted/panels/non_player/non player profile.xml' \
     --skin-dir '/path/to/Football Manager 2024/skins/fmface' \
     --auth-file .fm24-auth
   ```

   The builder creates a private local token and refuses to overwrite an existing skin. Keep `.fm24-auth` private.
5. Copy `.env.example` to `.env` and set `OPENAI_API_KEY` there. Keep the file private (`chmod 600 .env`). Each generated or updated face is one billed image API request. The FM24 companion uses the graphics path passed on its command line; `FM_GRAPHICS_DIR` in `.env` is for the main web app and FM26.
6. Install the [native DLL](native_menu/README.md) with FM24 closed and add its Steam launch option. In FM24 Preferences, select **FM Face** and enable screen IDs in the title. Then run:

   ```sh
   .venv-fm24/bin/python fm24/companion.py \
     --auth-file .fm24-auth \
     --graphics-dir '/path/to/Football Manager 2024/graphics/fmface'
   ```

## Use

Leave the companion running while you play; the menus work only while it runs.

- **Generate new face:** open a player or staff Overview, click the portrait and choose it from FM24's popup, or choose it from the profile's **Overview** tab menu (below **Follow**). The new face loads automatically, and choosing it again replaces the portrait.
- **Update face:** refreshes the face the person already has, asking for the same person at their current age in their current club's kit. The starting face can be:
  - FM Face's own portrait
  - a facepack picture from your graphics folder
  - FM24's bundled squad photo, read from `data/game/graphics.fmf` with its mask as transparency

  For a person with no face, **Update face** is greyed out in both menus (the companion answers the DLL's check as the menu opens). If the companion doesn't answer within 100 ms the item stays enabled, and the companion refuses before making any request. OpenAI refuses to edit real photos of very famous people (Lionel Messi was refused); lesser-known players' photos and generated faces work.
- **Generate missing squad faces…:** on the Squad screen, choose it from the **Overview** menu.
  - It reads every row from memory, including rows FM24 hasn't drawn, and asks in an FM24 dialog before any request.
  - It then generates and reloads one face at a time. Choose **Stop after this face** to end after the current one.

If FM24 can't show one of its dialogs, the message goes to the companion terminal instead, and an unanswerable squad confirmation counts as Cancel.

To see the prompt with **no image API request or portrait write**, add `--preview-only` to the companion command. Previews print to the companion terminal; for **Update face**, the preview also names the existing portrait that would be sent.

## Validation and limits

Live checks on one Steam/Proton installation and save:
- **Previews:** Generate, Update face and the squad command, run with every X11 entry point set to fail. No X11 calls were made.
- **Billed single face:** a Generate through the portrait popup (Gabriel Pec, LA Galaxy).
- **Billed squad batch:** two LA Galaxy faces, with Stop ending the run after the second face.

Other staff roles, languages, skins, clubs and game builds need separate validation. The DLL refuses to hook any other FM24 executable.

The memory layouts were derived for the tested build using only original code and Linux interfaces. Run `.venv-fm24/bin/python fm24/memory_probe.py --pid <FM24_PID>` to check process-memory access without generating an image. Historical feasibility probes are kept in `research/` and are not part of the release.
