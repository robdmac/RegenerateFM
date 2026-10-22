# RegenerateFM

RegenerateFM makes new portraits for Football Manager players and staff who don't have one, and updates the faces they already have. You choose **Generate new face** or **Update face** from the person's portrait menu in the game. The face is created from their profile and appears in the game a few seconds later. A whole squad can be done in one go.

Regen face packs give a player a stock photo picked from thousands by nationality or ethnicity. RegenerateFM paints a portrait of that particular person from what the game knows about them:

- **Faces that match the attributes.** The prompt includes the player's age, position, height, known physical attributes (Strength, Pace, Stamina and so on) and their best and weakest technical and mental attributes, plus the headline of their biography when there is one. So a 6'4" centre-back with 18 Strength is drawn as one, and a slight, quick 17-year-old winger looks nothing like the centre-back. Attributes hidden by scouting are left out rather than guessed. Ethnicity is never inferred from nationality.
- **In their club's kit.** Players wear their current club's home kit and colours, goalkeepers the club's goalkeeper kit, free agents a plain shirt, and staff a plain dark jacket or training top. No badges, sponsors or numbers are drawn.
- **Update face keeps up with your save.** A face pack photo never changes during a save. **Update face** takes the face the person already has (a RegenerateFM portrait or the game's own photo) and redraws the same person at their current age, in their current club's kit. Use it after a transfer, or a few seasons on, to age a regen you've watched grow up. Their face shape, features, skin tone, eyes, hair and marks stay the same; only the natural signs of age and the kit change.
- **The same person every time.** A person's face shape, features and hairstyle are chosen once from their game ID and kept, so generating a face again gives a fresh picture of the same-looking person, not someone new.

- **Football Manager 26** (26.3.2, and the FM26 Demo) through a BepInEx plugin.
- **Football Manager 2024** (24.4.2) through a small DLL and a companion program.

The images come from OpenAI's image API. Pay for them with RegenerateFM credits (one credit a face; sign in with your email from the game's key dialog, in FM26 and FM24), or with your own OpenAI API key, where each face is one request on your OpenAI account. RegenerateFM is independent of Sports Interactive, SEGA and the BepInEx project.

| | Windows | Linux (Steam/Proton) | macOS (Apple Silicon) |
|---|---|---|---|
| **FM26** 26.3.2 | Supported | Supported | Supported (runs under Rosetta 2) |
| **FM26 Demo** 26.3.2 | Supported | Supported | Supported (runs under Rosetta 2) |
| **FM24** 24.4.2 | Supported | Supported | Supported |

Each supported combination has been checked in game; see [Platform support](#platform-support). RegenerateFM is pinned to these builds and turns itself off on any other version until it is checked.

## 1. Get an OpenAI API key

You need an OpenAI **API** account. A ChatGPT subscription (Plus, Pro and so on) is billed separately and doesn't give API access.

1. Sign up or log in at [platform.openai.com](https://platform.openai.com/).
2. Add credit. Go to **Settings > Billing**, add a payment method and buy some credit; a few dollars is enough to start. If you leave **auto recharge** off, OpenAI can never charge more than the credit you bought.
3. Verify your organisation if asked. OpenAI may require **Settings > Organization > General > Verify Organization** before its image models work. Without it, the first face fails with an error saying your organisation must be verified. Verification can take a few minutes to apply after it's done.
4. Create the key. Open [API keys](https://platform.openai.com/api-keys) and choose **Create new secret key**. Copy it now: OpenAI shows it only once. It starts with `sk-`.

Treat the key like a password. Anyone who has it can spend your credit. RegenerateFM keeps it only on your computer and sends it only to OpenAI.

**What it costs:** each generated or updated face is one image request, typically a few cents or less at the default `medium` quality. `low` quality is cheaper. Check the [Usage](https://platform.openai.com/usage) page after your first few faces to see your real cost. Previews (`--preview-only` for FM24) and imports of existing PNGs make no request and cost nothing.

## 2. Set up FM26

The plugin calls OpenAI itself from inside FM26, so nothing else needs to run while you play. Setup is copying one folder (on macOS, an installer does it).

1. **Copy the files.** Close FM26. Find its game folder (in Steam: right-click Football Manager 26 > **Manage > Browse local files**). Copy everything inside the release ZIP's `FM26` folder into it. The FM26 Demo works the same way, in its own `Football Manager 26 Demo` folder. That adds [BepInEx 6.0.0-be.760](https://builds.bepinex.dev/projects/bepinex_be/), the mod loader, and the RegenerateFM plugin. If you already use BepInEx for other mods, copy only `BepInEx/plugins/RegenerateFMHotReload`.
2. **Linux only:** set FM26's Steam launch option to `WINEDLLOVERRIDES="winhttp=n,b" %command%` (or quit Steam and run `python3 steam_launch.py fm26`, or `fm26demo` for the Demo).

   **macOS:** use the installer instead of steps 1 and 2. Open `RegenerateFM-macOS.dmg` from the release and run **RegenerateFM Installer**. It finds FM26 (and the Demo) in your Steam libraries, or you choose the game folder, and **Install** copies the files beside `fm.app` and sets FM26's Steam launch option, so Play in Steam starts RegenerateFM. Steam must be closed for the launch option: if it's running, the installer copies the files and asks you to quit Steam and click Install again (or shows the launch option to set yourself). **Update** installs a newer release the same way, and **Uninstall** removes it and clears the launch option; your faces are kept. FM26 then runs under Rosetta 2 (BepInEx's macOS runtime is Intel-only); the installer offers to install Rosetta if it's missing. `fm.app` itself is never changed. Without the installer, copy the release ZIP's `FM26-macOS` folder's contents beside `fm.app`, quit Steam and run `python3 steam_launch.py fm26`, or set the launch option yourself: the path of `run_regeneratefm.sh` in that folder, in quotes, followed by `%command%`. See [hotreload/macos/README.md](hotreload/macos/README.md).
3. **Start FM26.** The first start takes a few minutes longer while BepInEx prepares itself.
4. **Add your key in the game.** The first time you choose **Generate new face** (or a squad's **Generate faces…**), RegenerateFM asks for your key.
   - It has a button that opens OpenAI's API keys page.
   - Paste or type the key and choose **Save key**. RegenerateFM checks it with OpenAI using a free request, then carries on with what you chose.
   - **Where it's kept:** outside the game folder, in `%APPDATA%\RegenerateFM`, encrypted for your Windows user. Under Proton that is inside the game's Wine prefix. On macOS it is `~/Library/Application Support/RegenerateFM`, readable only by you.
   - **Changing it:** if OpenAI later rejects the key, the dialog opens again. To forget the key, delete that folder.

**Removing it:** delete `BepInEx`, `dotnet`, `winhttp.dll`, `doorstop_config.ini` and `.doorstop_version` from the game folder (or only `BepInEx/plugins/RegenerateFMHotReload` if other mods use BepInEx). On macOS, use the installer's **Uninstall**, or delete `BepInEx`, `dotnet`, `Data`, `libdoorstop.dylib`, `.doorstop_version` and `run_regeneratefm.sh`, and clear the launch option (`python3 steam_launch.py fm26 --remove` with Steam closed). To build the plugin yourself, see [hotreload/README.md](hotreload/README.md).

### Use it in FM26

- **Generate new face:** open a player or staff **Overview**, left-click the portrait and choose **Generate new face**, or use **Actions > Generate face**. The new face loads automatically. Choosing it again replaces that RegenerateFM portrait; if generation fails, the old one stays. A player with half or more of their attributes hidden (shown as "-") needs scouting first, and RegenerateFM says so; with fewer hidden, the known ones are used (a range such as 11-14 counts as known).
- **Update face:** in the same menus. It refreshes the face the person already has (a RegenerateFM portrait or the game's own photo) at their current age, in their current club's kit. It is greyed out when the person has no face.
- **Squad:** open **Squad > Overview**, or a team's own page (**First Team**, a reserve or academy team; in the FM26 Demo, **Squad > First Team**), open the top **Squad** dropdown and choose **Generate faces…**. RegenerateFM counts the players with no portrait and asks before making any request. It then does one face at a time. **Stop after current face** ends the batch after the face in progress. Players who already have a portrait are never replaced. Players with half or more of their attributes unknown to your scouts are skipped, and the batch says how many.
- **Biographies:** for a profile with a biography, visit its **Career** tab once before generating, then return to **Overview**. The biography's headline paragraph is then included.

Faces are saved in FM26's user-data folder, `Documents/Sports Interactive/Football Manager 26/graphics/regeneratefm` (the Demo's is `Football Manager 26 Demo`; on macOS, `~/Library/Application Support/Sports Interactive/Football Manager 26/graphics/regeneratefm`). Errors appear in the portrait's tooltip and in `BepInEx/LogOutput.log`. The plugin turns its buttons off if FM26 updates to an untested build.

## 3. Set up FM24

FM24 needs a companion program that runs while you play; FM24 starts it and stops it for you. It reads the open profile from FM24's memory and makes the image requests. It also sets FM24 up the first time: a small DLL in the game folder that adds RegenerateFM's menu items, and an optional RegenerateFM skin built from FM24's own files.

**Windows**
1. Copy the release ZIP's `FM24` folder somewhere of your own, such as `Documents\RegenerateFM for FM24`, not into the game folder.
2. Close FM24 and double-click `regeneratefm-fm24.exe`. The first time, it finds FM24 through Steam, sets it up and asks for your OpenAI key in its window (paste or type it; it is checked for free and saved encrypted in `%APPDATA%\RegenerateFM`, the same file the FM26 plugin uses).
3. Start FM24. Any skin works; to also get the menu on the portrait, choose the **RegenerateFM** skin in **Preferences > Interface**.
4. That's all: from now on FM24 starts RegenerateFM by itself and stops it when you quit. Run `regeneratefm-fm24.exe` again only after a RegenerateFM update or if you move its folder.

It needs no Python. If FM24 isn't in a Steam library it can find, run `regeneratefm-fm24.exe setup --game-dir "<path to Football Manager 2024>"` from a command prompt. `regeneratefm-fm24.exe setup --remove` uninstalls the DLL.

**Linux (Steam/Proton)** uses the same `regeneratefm-fm24.exe`, which FM24 runs inside its Proton prefix. Copy the release's `FM24` and `source` folders somewhere of your own, side by side. Close FM24, quit Steam (Steam > Exit), then run `FM24/install-linux.sh`; it needs only the system's `python3`, and sets FM24's Steam launch option for you. Start Steam and play: the first time, a small RegenerateFM window asks for your OpenAI key (paste it with Ctrl+V). The full steps are in [fm24/README.md](fm24/README.md#packaged-companion-windows-and-linux).

**macOS (Apple Silicon)** also runs the companion with Python from this source. Build the Mac library with `python3 fm24/native_menu/build_macos.py` (it needs Xcode's command-line tools), then run `python3 fm24/setup_fm24.py` with FM24 closed. It installs `libregeneratefm24.dylib` and `run_regeneratefm24.sh` beside `fm.app` and, if Steam is closed, sets FM24's Steam launch option to `"<that folder>/run_regeneratefm24.sh" %command%` (otherwise it prints it to set yourself). `fm.app` itself is never changed. FM24 asks for your key in the game the first time you choose a face command, like FM26, and the companion saves it in `~/Library/Application Support/RegenerateFM`, the file the FM26 plugin uses too. On the Mac, **Show screen IDs in Title Bar** isn't needed. See [fm24/README.md](fm24/README.md).

In FM24, the profile's **Overview** tab menu has **Generate new face** and **Update face**, with any skin; with the RegenerateFM skin, so does a popup on the portrait. The Squad screen's **Overview** menu has **Generate missing squad faces…**. Progress, confirmations and errors appear in FM24's own dialogs.

## Settings

**FM26** asks for the key in the game (see above). The other settings are in `Football Manager 26/BepInEx/config/regeneratefm.hotreload.cfg`, section `[OpenAI]`, which FM26 creates on its first start with the plugin. It is reread before every request.

| Setting | Meaning |
|---|---|
| `Model` | The image model. The default is `gpt-image-2.5-sunburst`. |
| `Quality` | `low`, `medium` (default), `high`, `xhigh`, `max` or `auto`. Higher quality costs more per face. |
| `ApiBaseUrl` | Leave as `https://api.openai.com/v1`; for testing only. |

**FM24 and the web app** read `.env`, beside `regeneratefm.py` or `regeneratefm-fm24.exe`. On Windows, when `.env` has no `OPENAI_API_KEY`, they use the key saved in `%APPDATA%\RegenerateFM`. It is reread for every request, so changes apply without a restart. Never share or commit `.env`; on Linux and macOS, `chmod 600 .env` keeps it private.

| Setting | Meaning |
|---|---|
| `OPENAI_API_KEY` | Your OpenAI API key, or a RegenerateFM credits token (`rfm_…`, saved by **Buy credits / Sign in**). Required. |
| `REGENERATEFM_CREDITS_URL` | The credits service, for testing. The default is `https://credits.regenerate.fm`. |
| `OPENAI_IMAGE_MODEL` | The image model. The default is `gpt-image-2.5-sunburst`. |
| `OPENAI_IMAGE_QUALITY` | As `Quality` above. |
| `FM_GRAPHICS_DIR` | Where the web app and `import` save portraits. FM24's companion takes its folder on the command line instead. |

## Web app and import

These are optional tools for portraits you describe or already have. They need Python 3.9 or newer, Pillow (`python3 -m pip install Pillow`) and a `.env` file.

`python3 regeneratefm.py serve` starts a web form at <http://127.0.0.1:8767/> that makes a portrait from a description you type. On Linux, `python3 install_service.py` starts it automatically at login instead; check it with `systemctl --user status regeneratefm.service`. After saving a portrait from the form, restart FM26 to load it. The form's **Generated player** option saves it for a generated (newgen) player's ID.

To install a PNG you already have, with no API request:

```bash
python3 regeneratefm.py import --player-id 123456 --png /path/to/portrait.png --newgen
```

Add `--replace` to overwrite a portrait RegenerateFM already created for that ID. For FM24, import into FM24's own user-data `graphics/regeneratefm` folder and reload the skin in Preferences.

## How faces are made

- **Player prompts** use the player's name, age, nationality, position, club, height, all eight physical attributes, and their three strongest and three weakest other attributes.
- **Staff prompts** use name, age, nationality, job title and club.
- **Biography:** both kinds include the biography's headline paragraph when the game has shown it.
- **Appearance traits:** each person gets five seeded appearance traits: facial proportions, features, hair, facial hair and expression. They're saved by person ID under `.regeneratefm-appearances/` in the graphics folder and reused when you regenerate, so keep that folder when moving portraits. A new image can still change the face itself.
- **Size:** the API returns 1024×1024 images, which are scaled to FM's 256×256 portrait size.
- **Update face** sends the current face to OpenAI's image edit endpoint, which adds input-image cost to the request. OpenAI refuses to edit real photos of very famous people (Lionel Messi was refused, and nothing is saved). Lesser-known players' photos and generated faces work.

The FM26 plugin's generator (`hotreload/FaceGenerator.cs`) and `regeneratefm.py` build identical prompts; `tests/test_fm26_generator.py` checks this.

## Platform support

Everything has been tested on Linux with Steam/Proton. Status elsewhere:

| Part | Windows | macOS |
|---|---|---|
| FM26 plugin | Its native target: the build check pins the Windows game binaries that Proton also runs. On Windows 10, with FM26 26.3.2 and the FM26 Demo, the plugin loads and the portrait, Actions and squad commands work: billed single faces, Update face (on a RegenerateFM portrait and a game photo), staff faces, a biography, and squad batches with Stop (the Demo's returns to First Team between faces). On 2026-10-21, with the image stand-in: the scouting message for a player with half or more of their attributes hidden (after its 5 s check, closed by Escape or by leaving the page), a partly scouted player's face, and squad batches on First Team and a reserve team's page that skip players needing scouting and keep their result after Stop. | Through the `FM26-macOS` folder: BepInEx be.789 under Rosetta 2, with a rebuilt hooking library and a patched Il2CppInterop ([hotreload/macos/README.md](hotreload/macos/README.md)). On FM26 26.3.2 for Mac (2026-10-21): Generate and Update for players and staff (a staff face shows at once), the scouting message, squad batches on First Team and a reserve team's page, and a billed face. The FM26 Demo for Mac (2026-10-21, with the image stand-in): a 21-face First Team batch, and Generate and Update for a staff member, each shown at once. |
| FM24 DLL | The same Windows DLL; Windows loads it from the game folder without a launch option. On Windows 10 with FM24 24.4.2: the profile Overview menu's Generate new face and Update face (greyed for a person with no face), and the Squad menu's Generate missing squad faces…. On 2026-10-21, with the image stand-in: the same command on another club's Players tab (Inter Miami, 10 faces) and a Development Center team (Atlanta United 2), and Stop. | A Mac library (`fm24/native_menu/regeneratefm24_macos.c`) for FM24 24.4.2's Apple Silicon build, loaded by `run_regeneratefm24.sh`. On the Mac it adds the same menus and dialogs, reloads the skin, serves the companion's memory reads and starts the companion. Checked in game on 2026-10-21: profiles, staff, squad batches (the first team, a Development Centre team and another club's), and billed Generate and Update. |
| FM24 companion | Has a Windows memory reader (`ReadProcessMemory`, `VirtualQueryEx`). On Windows 10, the packaged `regeneratefm-fm24.exe` sets FM24 up, FM24 starts and stops it, and it reads the live game: profiles, staff, squad scans and batches, and a billed face. Every player and staff member of a surveyed save reads (45,940; `fm24/research/survey_people.py`). It also runs under Wine in the game's Proton prefix. | Runs from source with Python (`.venv-fm24`), reading memory through the Mac library; FM24's launcher starts it. Every player and staff member of a surveyed save reads. |
| Web app and import (`regeneratefm.py`) | Standard-library Python plus Pillow. On Windows 10 (2026-10-21, with the image stand-in): the web form generates, saves and previews a portrait with the key saved in `%APPDATA%\RegenerateFM`; `import` installs a PNG, refuses to overwrite one and replaces it with `--replace`. The test suite passes on Windows. `install_service.py` is Linux-only (systemd). | Should run; untested. |

## Release scope

- **FM26:** tested on 26.3.2 with the vanilla skin and BepInEx 6.0.0-be.760. FM ActionLink is no longer needed; the plugin also works beside it.
  - Its squad scanner matched all 49 players in the test save.
  - A three-face billed batch generated and reloaded every missing portrait; a fresh scan afterwards found none missing. That run used the earlier separate server; the plugin now makes the request itself.
  - Its game-binary checks disable the buttons on other FM26 builds.
- **Windows 10:** FM26 26.3.2 (a 23-player squad scan and a 10-face batch) and FM24 24.4.2 (a 28-player squad scan and a 9-face batch), each with one billed face; the other faces used a stand-in for the image API.
- **FM26 Demo:** tested on Windows 10 with the vanilla skin and the same BepInEx. Its squad pages differ from the full game's: Overview is a dashboard, so the squad command is on **First Team**, where the scanner matched all 33 players. On Linux (2026-10-21, with the image stand-in): BepInEx's first start, a First Team scan (8 of 33 missing) and an 8-face batch, and Update face for a player, each shown at once.
- **FM24:** tested on 24.4.2 with its vanilla-based skin through the native DLL: previews of every command, a billed single face and a billed squad batch with Stop.
- **Not yet checked:** other skins, resolutions, languages and game builds.

To make a release ZIP, build the plugin and the FM24 companion (`fm24/build_exe.py`, which includes the DLL from `fm24/native_menu/build.py`), then run `python3 make_release.py`. It downloads the pinned BepInEx build once and checks its SHA-256. The ZIP holds three folders: `FM26` (BepInEx and the plugin, to copy into the game folder), `FM24` (the companion) and `source`. It leaves out `.env`, tokens, portraits and the translated third-party research guide. RegenerateFM's own code and documentation are licensed under [MIT](LICENSE); the bundled third-party software is listed in [licenses/THIRD_PARTY_NOTICES.md](licenses/THIRD_PARTY_NOTICES.md).
