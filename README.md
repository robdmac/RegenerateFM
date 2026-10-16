# FM Face

FM Face makes new portraits for Football Manager players and staff who don't have one, and updates the faces they already have. You choose **Generate new face** or **Update face** from the person's portrait menu in the game. The face is created from their profile and appears in the game a few seconds later. A whole squad can be done in one go.

- **Football Manager 26** (26.3.2) through a BepInEx plugin.
- **Football Manager 2024** (24.4.2), experimental, through a small DLL and a companion program.

The images come from OpenAI's image API, using your own API key. Each face costs one request on your OpenAI account. FM Face is independent of Sports Interactive, SEGA and FM ActionLink. It has been tested on Linux with Steam/Proton; Windows should work but is untested (see [Platform support](#platform-support)).

## 1. Get an OpenAI API key

You need an OpenAI **API** account. A ChatGPT subscription (Plus, Pro and so on) is billed separately and doesn't give API access.

1. Sign up or log in at [platform.openai.com](https://platform.openai.com/).
2. Add credit. Go to **Settings > Billing**, add a payment method and buy some credit; a few dollars is enough to start. If you leave **auto recharge** off, OpenAI can never charge more than the credit you bought.
3. Verify your organisation if asked. OpenAI may require **Settings > Organization > General > Verify Organization** before its image models work. Without it, the first face fails with an error saying your organisation must be verified. Verification can take a few minutes to apply after it's done.
4. Create the key. Open [API keys](https://platform.openai.com/api-keys) and choose **Create new secret key**. Copy it now: OpenAI shows it only once. It starts with `sk-`.

Treat the key like a password. Anyone who has it can spend your credit. FM Face only keeps it in the `.env` file on your computer and sends it only to OpenAI.

**What it costs:** each generated or updated face is one image request, typically a few cents or less at the default `medium` quality. `low` quality is cheaper. Check the [Usage](https://platform.openai.com/usage) page after your first few faces to see your real cost. Previews (`--preview-only` for FM24) and imports of existing PNGs make no request and cost nothing.

## 2. Install FM Face

1. Install **Python 3.9 or newer**.
2. Download this repository and open a terminal in its folder.
3. Install Pillow: `python3 -m pip install Pillow` (or `python3-pil` on Debian and Ubuntu).
4. Copy `.env.example` to `.env` and paste your key after `OPENAI_API_KEY=`:

   ```sh
   cp .env.example .env
   chmod 600 .env    # Linux and macOS: only your user can read it
   ```

   Never share or commit `.env`. The other settings are explained in [Settings](#settings).

Then set up your game below.

## 3a. Set up FM26

1. **Find the graphics folder.** Find FM26's **user-data** folder (the one containing `graphics` and `skins`, not the Steam installation folder) and create `graphics/fmface` inside it. Set `FM_GRAPHICS_DIR` in `.env` to that `graphics/fmface` folder.
2. **Install BepInEx and FM ActionLink.** Download [BepInEx 6.0.0-be.760 Unity IL2CPP Windows x64](https://builds.bepinex.dev/projects/bepinex_be/) and [FM ActionLink 1.0.0](https://sortitoutsi.net/content/77820/fm-actionlink-free-plugin-for-modders-connect-fm-screens-to-external-tools). The installer checks both ZIPs and does a dry run first:

   ```sh
   python3 install_fm26.py --game "/path/to/Football Manager 26" \
     --loader-zip "/path/to/bepinex.zip" --actionlink-zip "/path/to/actionlink.zip"
   ```

   If the dry run looks right, run it again with `--apply`. It refuses to overwrite existing files.
3. **Linux only:** set FM26's Steam launch option to `WINEDLLOVERRIDES="winhttp=n,b" %command%`.
4. **Build and install the plugin** with the .NET 6 SDK, as described in [hotreload/README.md](hotreload/README.md).
5. **Start the server:** `python3 fmface.py serve`. On Linux you can start it automatically at login instead (see [Run the server in the background](#run-the-server-in-the-background)).

### Use it in FM26

Keep the server running while you play.

- **Generate new face:** open a player or staff **Overview**, left-click the portrait and choose **Generate new face**, or use **Actions > Generate face**. The new face loads automatically. Choosing it again replaces that FM Face portrait; if generation fails, the old one stays.
- **Update face:** in the same menus. It refreshes the face the person already has (an FM Face portrait or the game's own photo) at their current age, in their current club's kit. It is greyed out when the person has no face.
- **Squad:** open **Squad > Overview**, open the top **Squad** dropdown and choose **Generate faces…**. FM Face counts the players with no portrait and asks before making any request. It then does one face at a time. **Stop after current face** ends the batch after the face in progress. Players who already have a portrait are never replaced.
- **Biographies:** for a profile with a biography, visit its **Career** tab once before generating, then return to **Overview**. The biography's headline paragraph is then included.

Errors appear in the portrait's tooltip and in `BepInEx/LogOutput.log`. The plugin turns its buttons off if FM26 updates to an untested build.

## 3b. Set up FM24 (experimental)

FM24 needs a few more steps:
- a Python virtual environment;
- a small skin built from your own copy of FM24's panels;
- a native DLL;
- a companion program that runs while you play.

The full steps are in [fm24/README.md](fm24/README.md). It uses the same `.env` key from step 2.

In FM24, the portrait popup and the profile's **Overview** tab menu have **Generate new face** and **Update face**. The Squad screen's **Overview** menu has **Generate missing squad faces…**. Progress, confirmations and errors appear in FM24's own dialogs.

## Settings

All settings are in `.env`. The server rereads it for every request, so changes apply without a restart.

| Setting | Meaning |
|---|---|
| `OPENAI_API_KEY` | Your OpenAI API key (required). |
| `OPENAI_IMAGE_MODEL` | The image model. The default is `gpt-image-2.5-sunburst`. |
| `OPENAI_IMAGE_QUALITY` | `low`, `medium` (default), `high`, `xhigh`, `max` or `auto`. Higher quality costs more per face. |
| `FM_GRAPHICS_DIR` | FM26's user-data `graphics/fmface` folder. Required for the in-game buttons; the plugin and server check they agree before any request. FM24's companion takes its folder on the command line instead. |

## Run the server in the background

On Linux, `python3 install_service.py` creates and starts a user service for FM26's server, so it's running whenever you log in. Check it with `systemctl --user status fmface.service`. The service reads the key from `.env`; the key isn't copied into the service file.

## Web app and import

The web form at <http://127.0.0.1:8767/> (while the server runs) makes a portrait from a description you type. After saving through the form or importing a PNG, restart FM26 to load it. The form's **Generated player** option saves it for a generated (newgen) player's ID.

To install a PNG you already have, with no API request:

```bash
python3 fmface.py import --player-id 123456 --png /path/to/portrait.png --newgen
```

Add `--replace` to overwrite a portrait FM Face already created for that ID. For FM24, import into FM24's own user-data `graphics/fmface` folder and reload the skin in Preferences.

## How faces are made

- **Player prompts** use the player's name, age, nationality, position, club, height, all eight physical attributes, and their three strongest and three weakest other attributes.
- **Staff prompts** use name, age, nationality, job title and club.
- **Biography:** both kinds include the biography's headline paragraph when the game has shown it.
- **Appearance traits:** each person gets five seeded appearance traits: facial proportions, features, hair, facial hair and expression. They're saved by person ID under `.fmface-appearances/` in the graphics folder and reused when you regenerate, so keep that folder when moving portraits. A new image can still change the face itself.
- **Size:** the API returns 1024×1024 images, which are scaled to FM's 256×256 portrait size.
- **Update face** sends the current face to OpenAI's image edit endpoint, which adds input-image cost to the request. OpenAI refuses to edit real photos of very famous people (Lionel Messi was refused, and nothing is saved). Lesser-known players' photos and generated faces work.

## Platform support

Everything has been tested on Linux with Steam/Proton. Status elsewhere:

| Part | Windows | macOS |
|---|---|---|
| Local server (`fmface.py`) | Standard-library Python plus Pillow, so it should run; untested. `install_service.py` is Linux-only (systemd). | Should run; untested. |
| FM26 plugin | Its native target: the build check pins the Windows game binaries that Proton also runs. Untested on Windows itself. | Not supported. The build check pins the Windows binaries, and BepInEx IL2CPP on macOS needs a community Il2CppInterop patch for plugins that register their own types, as this one does. |
| FM24 DLL | The same Windows DLL; Windows loads it from the game folder without a launch option. Untested on Windows itself. | Not possible: FM24 for macOS is a native Mac program, not `fm.exe`. |
| FM24 companion | Has a Windows memory reader (`ReadProcessMemory`, `VirtualQueryEx`). Run with Windows Python under Wine against the live game, it found FM24, read a full profile, the shown biography and all 29 Squad rows. Native Windows is untested. | Refuses to start. |

## Release scope

- **FM26:** tested on 26.3.2 with the vanilla skin, BepInEx 6.0.0-be.760 and FM ActionLink 1.0.0.
  - Its squad scanner matched all 49 players in the test save.
  - A three-face billed batch generated and reloaded every missing portrait; a fresh scan afterwards found none missing.
  - Its game-binary checks disable the buttons on other FM26 builds.
- **FM24:** tested on 24.4.2 with its vanilla-based skin through the native DLL: previews of every command, a billed single face and a billed squad batch with Stop.
- **Not yet checked:** other skins, resolutions, languages and game builds.

Run `python3 make_release.py` after building the plugin to create a ZIP containing only the public source, docs, license, tests and plugin DLL. It leaves out `.env`, portraits, the translated third-party research guide and dependency binaries. FM Face's own code and documentation are licensed under [MIT](LICENSE).
