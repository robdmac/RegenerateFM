# FM24 player, staff and squad faces (experimental)

FM Face for FM24 works inside the game through the [native DLL](native_menu/README.md), which is pinned to the tested FM24 24.4.2 executable on Steam/Proton.
- A player or staff profile's **Overview** tab menu ends with **Generate new face** and **Update face**, with any skin. With the optional FM Face skin, clicking the portrait gives FM24's own popup with the same two items.
- The Squad screen's **Overview** menu ends with **Generate missing squad faces…**. A reserve or youth team's tab menu in the Development Center (and another club's Players menu) ends with it too.

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
- **Person:** the person comes from the screen FM24 has open, in game memory. The header's title, the canonical person record and the contract must agree. If **Show screen IDs in Title Bar** is on, the header's `(ID:n)` must match as well; the setting isn't needed.
- **Repeat read:** everything is read twice to catch navigation during capture.

If anything can't be verified, the request is refused before any image is requested. If the in-game reload fails after a face is saved, the image is kept; use Preferences > Reload Skin to load it.

## Setup

`setup_fm24.py` does the one-time setup, and the packaged companion runs it by itself:
- **Finds FM24** in your Steam libraries (`libraryfolders.vdf`), and its **user-data** folder (`Documents/Sports Interactive/Football Manager 2024`; under Proton, inside the game's `compatdata/2252570` prefix). That's the folder with `skins` and `graphics`, not the game folder.
- **Builds the optional FM Face skin** from the player and staff panels in FM24's own `data/game/panels.fmf`, read directly, so there's no Resource Archiver step. This repository does not include Sports Interactive's panels.
- **Creates the private token** the DLL and companion share (`.fm24-auth` beside FM Face). If FM24 already has an FM Face token, for example after moving the companion to a new folder, it reuses that one.
- **Installs the [native DLL](native_menu/README.md)** and adds the portrait popups to the FM Face skin's panels.

Run it with FM24 closed. Running it again does nothing unless something changed, such as a new DLL in an FM Face update; `--remove` uninstalls the DLL and restores the panels. `--game-dir` and `--user-data-dir` override the search.

Afterwards FM Face works with whatever skin you use. For the portrait popup too, choose the **FM Face** skin in FM24's **Preferences > Interface**. It is FM24's default skin plus that popup, so it can't be combined with another custom skin. Without it, single-face progress appears in an FM Face dialog instead of on the portrait; closing that dialog doesn't stop the face.

## macOS (Apple Silicon, experimental)

FM24 for Mac is its own program, so FM Face uses a Mac library instead of the DLL: `libfmface24.dylib`, from [native_menu/fmface24_macos.c](native_menu/fmface24_macos.c), pinned to FM24 24.4.2's arm64 build. Its in-game features are the same: the Overview tab rows, the portrait popup, the Squad menu item, FM24's dialogs and the skin reload. macOS doesn't let another program read FM24's memory, so the library also serves the companion's reads.

1. Install Xcode's command-line tools (`xcode-select --install`) and the companion's Python packages (`pip install -r fm24/requirements.txt`).
2. Build the library: `python3 fm24/native_menu/build_macos.py`.
3. Close FM24 and run `python3 fm24/setup_fm24.py`. It finds FM24 in `~/Library/Application Support/Steam`, builds the skin in `~/Library/Application Support/Sports Interactive/Football Manager 2024/skins/fmface`, and puts `libfmface24.dylib`, `run_fmface24.sh` and `fmface24.ini` beside `fm.app`.
4. Set FM24's Steam launch option to the line it prints: `"<the game folder>/run_fmface24.sh" %command%`.
5. Start the companion, `python3 fm24/companion.py`. The first time you choose a face command, FM24 asks for your OpenAI key in a prompt on its window (paste with Cmd+V; the key isn't shown). The companion checks it with a free OpenAI request, keeps it in `~/Library/Application Support/FM Face`, readable only by you, and asks again in the game if OpenAI later rejects it. Optionally, choose the FM Face skin in FM24's **Preferences > Interface** for the portrait popup. On the Mac, **Show screen IDs in Title Bar** isn't needed: the library reports the person from the screen's object browser, and the companion finds them by their handle in FM24's person table.

`fm.app` is never changed. `python3 fm24/setup_fm24.py --remove` removes the library and launcher and restores the panels; then clear the launch option. On an Intel Mac, `run_fmface24.sh` starts FM24 without FM Face. The memory layouts are in [layout.py](layout.py) and described in [native_menu/README.md](native_menu/README.md#macos).

## Packaged companion for Windows

`fmface-fm24.exe` (the release ZIP's `FM24` folder) is the companion packaged with Python, Pillow, zstandard and the FM24 DLL, so players need no Python or compiler.

**Using it:**
1. Copy the `FM24` folder somewhere of your own, not into the game folder.
2. Close FM24 and double-click `fmface-fm24.exe`. The first time (and after an FM Face update) it runs the setup above.
3. It asks for your OpenAI key in its window. Paste it (right-click or Ctrl+V) or type it; it isn't shown. It checks the key with a free OpenAI request and saves it encrypted for your Windows user in `%APPDATA%\FM Face\openai-key`, the same file the FM26 plugin uses. Delete that folder to forget the key. On later starts it checks the saved key the same free way and asks again only if OpenAI rejects it. A `.env` beside the exe with `OPENAI_API_KEY` takes priority.
4. Start FM24 (choosing the FM Face skin is optional, as above). Keep the window open while you play.

**Defaults:** it reads `.fm24-auth` from its own folder and finds `Documents\Sports Interactive\Football Manager 2024\graphics\fmface` itself. `fmface-fm24.exe setup [--game-dir ...] [--remove]` runs only the setup. The companion options below (`--graphics-dir`, `--preview-only` and so on) still work. If it stops with an error, the window stays open until you press Enter.

**Building it:** build the DLL first (see [native_menu/README.md](native_menu/README.md); it needs MinGW), then run `fm24/build_exe.py` with a Windows Python 3.11 that has `fm24/requirements-exe.txt` installed. On Linux, a Windows Python under Wine works:

```sh
wine python.exe -m pip install -r fm24/requirements-exe.txt
wine python.exe fm24/build_exe.py
```

The output is the folder `dist/fmface-fm24/`, about 30 MB, which `make_release.py` adds to the release ZIP. It's a folder rather than one file because one-file packages unpack themselves on every start, which is slower and more often flagged by antivirus software.

**Note on antivirus:** a program that reads another program's memory can still be flagged. Signing the exe would reduce that.

**Testing it under Proton:** the exe has been run under Wine in FM24's Proton prefix. Start it with `WINEFSYNC=1` (Proton's wineserver uses fsync) and pipe its output (`| cat`); under Wine, redirecting it straight to a file fails to set up its output streams.

## Running from source (Linux)

1. Create a virtual environment and install the companion's dependencies:

   ```sh
   python3 -m venv .venv-fm24
   .venv-fm24/bin/pip install -r fm24/requirements.txt
   ```

2. With FM24 closed, run the setup: `.venv-fm24/bin/python fm24/setup_fm24.py`. It needs the DLL from `fm24/native_menu/build.py` (MinGW) in `fm24/native_menu/build/`, or take `FM24/_internal/fm24-dll/winmm.dll` from a release ZIP and put it there.
3. Set FM24's Steam launch option to `WINEDLLOVERRIDES="winmm=n,b" %command%`.
4. Copy `.env.example` to `.env` and set `OPENAI_API_KEY` there. Keep the file private (`chmod 600 .env`). Each generated or updated face is one billed image API request. If you don't have a key yet, follow [Get an OpenAI API key](../README.md#1-get-an-openai-api-key).
5. Start FM24 and the companion:

   ```sh
   .venv-fm24/bin/python fm24/companion.py \
     --graphics-dir '/path/to/Football Manager 2024/graphics/fmface'
   ```

   `--auth-file` defaults to `.fm24-auth` in the repository. `fm24/build_skin.py` still builds the skin from panels you extracted yourself, if you prefer.

## Use

Leave the companion running while you play; the menus work only while it runs.

- **Generate new face:** open a player or staff profile and choose it from the **Overview** tab menu (below **Follow**), or, with the FM Face skin, click the portrait and choose it from FM24's popup. The new face loads automatically, and choosing it again replaces the portrait.
- **Update face:** refreshes the face the person already has, asking for the same person at their current age in their current club's kit. The starting face can be:
  - FM Face's own portrait
  - a facepack picture from your graphics folder
  - FM24's bundled squad photo, read from `data/game/graphics.fmf` with its mask as transparency

  For a person with no face, **Update face** is greyed out in both menus (the companion answers the DLL's check as the menu opens). If the companion doesn't answer within 30 ms the item stays enabled, and the companion refuses before making any request. OpenAI refuses to edit real photos of very famous people (Lionel Messi was refused); lesser-known players' photos and generated faces work.
- **Generate missing squad faces…:** on the Squad screen, choose it from the **Overview** menu. For a reserve or youth team, open **Development Center**, then choose it from that team's tab menu (for example **Inter Miami II**); opening the menu shows the team's Overview, whose squad list it reads.
  - It reads every row from memory, including rows FM24 hasn't drawn, and asks in an FM24 dialog before any request. The dialog names the team most of the players play for.
  - Players FM24 lists as **Scouting Required** with no age or ability (players of leagues it hasn't fully loaded, such as Inter Miami II's 11 of 23 and all of the IMCF Academy on the test save) are left out and counted in the dialog: FM24 keeps only a brief record for them, without the age, nationality or attributes a prompt needs.
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
