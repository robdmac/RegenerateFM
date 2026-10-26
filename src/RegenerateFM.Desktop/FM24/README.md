# FM24 player, staff and squad faces

RegenerateFM for FM24 works inside the game through the [native DLL](../../../native/fm24/README.md), which is pinned to the tested FM24 24.4.2 executable on Steam/Proton.
- A player or staff profile's **Overview** tab menu ends with **Generate new face** and **Update face**, with any skin. With the optional RegenerateFM skin, clicking the portrait gives FM24's own popup with the same two items.
- The Squad screen's **Overview** menu ends with **Generate missing squad faces…**. A reserve or youth team's tab menu in the Development Center (and another club's Players menu) ends with it too.
- The Staff screen's **Overview** menu (another club's **Staff** menu) ends with **Generate missing staff faces…**.

The companion, `regeneratefm-fm24` (this folder: `src/RegenerateFM.Desktop`), reads everything from FM24's memory: the open profile, the biography FM24 is showing, every Squad row, and a club's staff and players. It makes the image request with the generator FM26's plugin uses (`src/RegenerateFM.Core`), writes a versioned portrait and reloads the skin inside the game. Progress, the squad confirmation, results and errors appear in FM24's own dialogs. There is no X11 code, no screenshots or OCR, and no browser.

Versioned filenames matter: FM24 kept showing the old portrait when the same filename was overwritten.

## What it reads

From read-only process memory (`Memory/`), the companion reads:
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

`regeneratefm-fm24 setup` does the one-time setup (`Setup/`), and the companion runs it by itself the first time it's double-clicked:
- **Finds FM24** in your Steam libraries (`libraryfolders.vdf`), and its **user-data** folder (`Documents/Sports Interactive/Football Manager 2024`; under Proton, inside the game's `compatdata/2252570` prefix). That's the folder with `skins` and `graphics`, not the game folder.
- **Builds the optional RegenerateFM skin** from the player and staff panels in FM24's own `data/game/panels.fmf`, read directly. This repository does not include Sports Interactive's panels.
- **Makes the private token** the DLL and companion share, kept in `regeneratefm24.ini` beside `fm.exe`. If FM24 already has a RegenerateFM token, for example after moving the companion to a new folder, it keeps that one.
- **Installs the [native DLL](../../../native/fm24/README.md)**, which the companion carries inside itself, and adds the portrait popups to the RegenerateFM skin's panels. Every file change is journalled, so a failed install or removal is undone.

Run it with FM24 closed. Running it again does nothing unless something changed, such as a new DLL in a RegenerateFM update; `--remove` uninstalls the DLL and restores the panels. `--game-dir` and `--user-data-dir` override the search.

Afterwards RegenerateFM works with whatever skin you use. For the portrait popup too, choose the **RegenerateFM** skin in FM24's **Preferences > Interface**. It is FM24's default skin plus that popup, so it can't be combined with another custom skin. Without it, single-face progress appears in a RegenerateFM dialog instead of on the portrait; closing that dialog doesn't stop the face.

## The companion on Windows and Linux

`regeneratefm-fm24.exe` (the release ZIP's `FM24` folder) is one self-contained .NET program carrying the FM24 DLL, so players need nothing else installed. It is a Windows program; on Linux it runs inside FM24's Proton prefix, so the same file serves both.

**FM24 starts it.** Once RegenerateFM is set up, the DLL starts `regeneratefm-fm24.exe` whenever FM24 starts (its path is in `regeneratefm24.ini` beside `fm.exe`), and it stops when FM24 does. It has no window: it writes to `regeneratefm-fm24.log` beside itself. There is nothing to start or keep open.

**Windows:**
1. Copy the `FM24` folder somewhere of your own, not into the game folder.
2. Close FM24 and double-click `regeneratefm-fm24.exe`. The first time (and after a RegenerateFM update, or after moving it) it runs the setup above.
3. It asks in its window how you want to pay. Type `credits` to sign in to RegenerateFM credits in your browser (see [Choose how to pay](../../../README.md#1-choose-how-to-pay)); or paste your OpenAI key (right-click or Ctrl+V) or type it, and it's checked with a free OpenAI request. Either is saved encrypted for your Windows user in `%APPDATA%\RegenerateFM\openai-key`, the same file the FM26 plugin uses. Delete that folder to forget it.
4. Start FM24 (choosing the RegenerateFM skin is optional, as above).

**Linux (Steam/Proton):**
1. Unpack the release ZIP anywhere.
2. Close FM24, **quit Steam** (Steam > Exit), and run `./install-linux.sh fm24` in the unpacked folder (without `fm24` it sets up FM26 and the FM26 Demo too). It needs nothing else installed. It copies the companion to `~/.local/share/RegenerateFM/FM24` (so the unpacked release can be deleted), installs the DLL and the RegenerateFM skin, points the DLL at that `regeneratefm-fm24.exe`, and sets FM24's Steam launch option to `WINEDLLOVERRIDES="winmm=n,b" %command%`. It refuses while Steam is running, because Steam overwrites launch options changed while it's open.
3. Start Steam and play FM24. The first time, a small **RegenerateFM: credits or OpenAI key** window opens. Choose **Buy credits / Sign in** to use credits, or paste your key with Ctrl+V (or type it; it shows as dots) and choose **Save key**; a key is checked with a free OpenAI request. Either is saved encrypted in FM24's Proton prefix.

**When a key is needed later** (none was saved, OpenAI rejects the saved key, your credits run out or your sign-in expires), choosing a face command opens the key window again, and FM24 says so. Closing it saves nothing.

**Options:** `regeneratefm-fm24.exe setup [--game-dir ...] [--remove]` runs only the setup; `./install-linux.sh fm24 --remove` uninstalls on Linux (with Steam closed). Double-clicked while FM24 is running, it runs as the companion in its window. `--preview-only` prints the prompt instead of making a request; `--graphics-dir` names FM24's user-data `graphics/regeneratefm` folder. If it stops with an error, the window stays open until you press Enter. Other settings (the image model and quality, and a stand-in image API for testing) are in `settings.ini` beside the key; see [Settings](../../../README.md#settings).

**Building it:** build the DLL first (see [native/fm24/README.md](../../../native/fm24/README.md); it needs MinGW), then publish:

```sh
dotnet publish src/RegenerateFM.Desktop -c Release -r win-x64 -o dist/publish/win-x64
```

The output is the one file `dist/publish/win-x64/regeneratefm-fm24.exe`, about 13 MB, which `tools/make_release.py` adds to the release ZIP. It can be built on Linux, macOS or Windows. .NET runs it from the file itself, so nothing is unpacked when it starts.

**Note on antivirus:** a program that reads another program's memory can still be flagged. Signing the exe would reduce that.

**Testing it under Proton:** checked on 2026-10-25: `install-linux.sh` with Steam open (refused) and closed, then Play in Steam: the DLL started the companion, the key window saved a key, a face was generated and loaded, Update face started from FM24's bundled photo, and a squad batch stopped when asked; the companion stopped with FM24. To start the exe by hand in FM24's prefix, use `WINEFSYNC=1` (Proton's wineserver uses fsync) and pipe its output (`| cat`).

## macOS (Apple Silicon)

FM24 for Mac is its own program, so RegenerateFM uses a Mac library instead of the DLL: `libregeneratefm24.dylib`, from [native/fm24/regeneratefm24_macos.c](../../../native/fm24/regeneratefm24_macos.c), pinned to FM24 24.4.2's arm64 build. Its in-game features are the same: the Overview tab rows, the portrait popup, the Squad menu item, FM24's dialogs and the skin reload. macOS doesn't let another program read FM24's memory, so the library also serves the companion's reads.

**With the installer:** the release's `RegenerateFM-macOS.dmg` holds RegenerateFM Installer, which sets FM24 up with the companion built for the Mac (carrying the library and launcher): it copies the companion to `<game folder>/RegenerateFM/regeneratefm-fm24`, runs its `setup --no-launch-option` and then sets the launch option itself. `run_regeneratefm24.sh` starts that companion, as `regeneratefm24.ini`'s `companion=` names it. It logs to `regeneratefm-fm24.log` beside itself.

**From source:**

1. Install Xcode's command-line tools (`xcode-select --install`) and the .NET 10 SDK.
2. Build the library: `python3 native/fm24/build_macos.py`.
3. Publish the companion: `dotnet publish src/RegenerateFM.Desktop -c Release -r osx-arm64 -o dist/publish/osx-arm64`. It carries the library and launcher.
4. Close FM24 and run `dist/publish/osx-arm64/regeneratefm-fm24 setup`. It finds FM24 in `~/Library/Application Support/Steam`, builds the skin in `~/Library/Application Support/Sports Interactive/Football Manager 2024/skins/regeneratefm`, and puts `libregeneratefm24.dylib`, `run_regeneratefm24.sh` and `regeneratefm24.ini` beside `fm.app`.
5. If Steam was running, quit it and run the setup again, or set FM24's Steam launch option to the line it prints: `"<the game folder>/run_regeneratefm24.sh" %command%`.
6. Play FM24 from Steam: the launcher starts the companion and it stops when FM24 does. The first time you choose a face command, FM24 asks in a prompt on its window how you want to pay: **Buy credits / Sign in** opens the credits page in your browser, or paste your OpenAI key (Cmd+V; it isn't shown), which the companion checks with a free OpenAI request. Either is kept in `~/Library/Application Support/RegenerateFM`, readable only by you, and FM24 asks again if OpenAI rejects the key, your credits run out or your sign-in expires. On the Mac, **Show screen IDs in Title Bar** isn't needed: the library reports the person from the screen's object browser, and the companion finds them by their handle in FM24's person table.

`fm.app` is never changed. `regeneratefm-fm24 setup --remove` removes the library and launcher and restores the panels; then clear the launch option. On an Intel Mac, `run_regeneratefm24.sh` starts FM24 without RegenerateFM. The memory layouts are in [Layout.cs](Layout.cs) and described in [native/fm24/README.md](../../../native/fm24/README.md#macos). The C# companion hasn't been run on a Mac yet; the Python companion before it was checked there on 2026-10-21 and 2026-10-22.

## Running from source on Linux

The packaged companion above is simpler. From source, `dotnet run` builds the companion for Linux itself, which reads FM24's Proton process from outside the prefix (`process_vm_readv`) and keeps its key in `~/.config/RegenerateFM`. Linux lets a program read only its own children's memory unless `kernel.yama.ptrace_scope` is 0 (`sudo sysctl kernel.yama.ptrace_scope=0` until the next restart); the companion FM24 starts inside Proton needs nothing of the kind:

1. With FM24 closed and Steam quit, set it up with `dotnet run --project src/RegenerateFM.Desktop -- setup`. It needs the DLL from `native/fm24/build.py` (MinGW) in `native/fm24/build/`, or take it from a release. Set up this way, FM24 starts no companion of its own (the release's installer names the packaged one, which would take the bridge port).
2. Start FM24 and the companion: `dotnet run --project src/RegenerateFM.Desktop -- --preview-only`. It finds FM24's `graphics/regeneratefm` folder inside the Proton prefix, and asks for a key in the terminal (typing `credits` signs in to RegenerateFM credits instead). Each face without `--preview-only` is one billed image request.

## Use

The packaged companion runs whenever FM24 does. From source, leave the companion running while you play; the menus work only while it runs.

- **Generate new face:** open a player or staff profile and choose it from the **Overview** tab menu (below **Follow**), or, with the RegenerateFM skin, click the portrait and choose it from FM24's popup. The new face loads automatically, and choosing it again replaces the portrait.
- **Update face:** refreshes the face the person already has, asking for the same person at their current age in their current club's kit. The starting face can be:
  - RegenerateFM's own portrait
  - a facepack picture from your graphics folder
  - FM24's bundled squad photo, read from `data/game/graphics.fmf` with its mask as transparency

  For a person with no face, **Update face** is greyed out in both menus (the companion answers the DLL's check as the menu opens). If the companion doesn't answer within 30 ms the item stays enabled, and the companion refuses before making any request. OpenAI refuses to edit real photos of very famous people (Lionel Messi was refused); lesser-known players' photos and generated faces work.
- **Generate missing squad faces…:** on the Squad screen, choose it from the **Overview** menu. For a reserve or youth team, open **Development Center**, then choose it from that team's tab menu (for example **Inter Miami II**); opening the menu shows the team's Overview, whose squad list it reads.
  - It reads every row from memory, including rows FM24 hasn't drawn, and asks in an FM24 dialog before any request. The dialog names the team most of the players play for.
  - Players FM24 lists as **Scouting Required** with no age or ability (players of leagues it hasn't fully loaded, such as Inter Miami II's 11 of 23 and all of the IMCF Academy on the test save) are left out and counted in the dialog: FM24 keeps only a brief record for them, without the age, nationality or attributes a prompt needs.
  - Player-coaches are left out and counted too: FM24 keeps their player details outside the record the list reads, so make theirs from their own profile. A list with no players RegenerateFM can read (a Development Center overview that lists staff) says so and makes no request.
  - If some of the club's staff have no face either, the dialog has three buttons: **Generate N faces** for the squad, **Include staff: N faces** for the squad and the staff, and **No**.
  - It then generates and reloads one face at a time. Choose **Stop after this face** to end after the current one.
- **Generate missing staff faces…:** on your club's **Staff** screen, choose it from the **Overview** menu; for another club, from its **Staff** menu. It makes faces for everyone contracted to the club's teams as staff (board, coaches, medical and recruitment staff, the Under 18s' staff; player-coaches too), with the job FM24 lists for them, and offers **Include players: N faces** in the same way for the club's players (its youth teams' and those out on loan included). Your own manager is left out.
  - The club's people come from FM24's person table (everyone at the club, in about 0.3 s on the Mac), so the staff list needn't be open.

If FM24 can't show one of its dialogs, the message goes to the companion's log instead, and an unanswerable squad confirmation counts as Cancel.

To see the prompt with **no image API request or portrait write**, start the companion with `--preview-only`. Previews go to its log or terminal; for **Update face**, the preview also names the existing portrait that would be sent.

## Validation and limits

Live checks on one Steam/Proton installation and save:
- **Previews:** Generate, Update face and the squad command, run with every X11 entry point set to fail. No X11 calls were made.
- **Staff (Mac, 2026-10-26, preview mode):** MK Dons' 30 staff (jobs from Chairperson to Sports Scientist, the Under 18s' too) and, with **Include players**, its 20 players without a face: 50 prompts. The squad command offering the 30 staff, and Chelsea's staff (86, every job named). The three-button dialog's answers, and No (FM24's Cancel), on the Mac.
- **Billed single face:** a Generate through the portrait popup (Gabriel Pec, LA Galaxy); with the C# companion, a Generate (Lucas Meek, Inter Miami II) and an Update from FM24's bundled photo (Ryan Sailor).
- **Billed squad batch:** two LA Galaxy faces, with Stop ending the run after the second face; with the C# companion and a stand-in image API, an Inter Miami II batch with Stop.

Other staff roles, languages, skins, clubs and game builds need separate validation. The DLL refuses to hook any other FM24 executable.

The memory layouts were derived for the tested build using only original code and Linux interfaces. The C# readers are tested against fake game memory laid out as the game's (`tests/RegenerateFM.Tests`). Historical feasibility probes are in [native/fm24/research](../../../native/fm24/research), and the Python research scripts in the git history before the C# port.
