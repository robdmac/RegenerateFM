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

Treat the key like a password. Anyone who has it can spend your credit. FM Face keeps it only on your computer and sends it only to OpenAI.

**What it costs:** each generated or updated face is one image request, typically a few cents or less at the default `medium` quality. `low` quality is cheaper. Check the [Usage](https://platform.openai.com/usage) page after your first few faces to see your real cost. Previews (`--preview-only` for FM24) and imports of existing PNGs make no request and cost nothing.

## 2. Set up FM26

The plugin calls OpenAI itself from inside FM26, so nothing else needs to run while you play.

1. **Install BepInEx and FM ActionLink.** Download [BepInEx 6.0.0-be.760 Unity IL2CPP Windows x64](https://builds.bepinex.dev/projects/bepinex_be/) and [FM ActionLink 1.0.0](https://sortitoutsi.net/content/77820/fm-actionlink-free-plugin-for-modders-connect-fm-screens-to-external-tools). The installer (it needs **Python 3.9 or newer**) checks both ZIPs and does a dry run first:

   ```sh
   python3 install_fm26.py --game "/path/to/Football Manager 26" \
     --loader-zip "/path/to/bepinex.zip" --actionlink-zip "/path/to/actionlink.zip"
   ```

   If the dry run looks right, run it again with `--apply`. It refuses to overwrite existing files.
2. **Linux only:** set FM26's Steam launch option to `WINEDLLOVERRIDES="winhttp=n,b" %command%`.
3. **Build and install the plugin** with the .NET 6 SDK, as described in [hotreload/README.md](hotreload/README.md).
4. **Add your key in the game.** The first time you choose **Generate new face** (or a squad's **Generate faces…**), FM Face asks for your key.
   - It has a button that opens OpenAI's API keys page.
   - Paste the key and choose **Save key**. FM Face checks it with OpenAI using a free request, then carries on with what you chose.
   - **Where it's kept:** outside the game folder, in `%APPDATA%\FM Face`, encrypted for your Windows user. Under Proton that is inside the game's Wine prefix.
   - **Changing it:** if OpenAI later rejects the key, the dialog opens again. To forget the key, delete that folder.

### Use it in FM26

- **Generate new face:** open a player or staff **Overview**, left-click the portrait and choose **Generate new face**, or use **Actions > Generate face**. The new face loads automatically. Choosing it again replaces that FM Face portrait; if generation fails, the old one stays.
- **Update face:** in the same menus. It refreshes the face the person already has (an FM Face portrait or the game's own photo) at their current age, in their current club's kit. It is greyed out when the person has no face.
- **Squad:** open **Squad > Overview**, open the top **Squad** dropdown and choose **Generate faces…**. FM Face counts the players with no portrait and asks before making any request. It then does one face at a time. **Stop after current face** ends the batch after the face in progress. Players who already have a portrait are never replaced.
- **Biographies:** for a profile with a biography, visit its **Career** tab once before generating, then return to **Overview**. The biography's headline paragraph is then included.

Faces are saved in FM26's user-data folder, `Documents/Sports Interactive/Football Manager 26/graphics/fmface`. Errors appear in the portrait's tooltip and in `BepInEx/LogOutput.log`. The plugin turns its buttons off if FM26 updates to an untested build.

## 3. Set up FM24 (experimental)

FM24 needs some one-time setup:
- a small skin built from your own copy of FM24's panels;
- a native DLL installed beside the game.

It also needs a companion program that runs while you play. It reads the open profile from FM24's memory and makes the image requests. You can run it two ways:
- **Windows:** `fmface-fm24.exe`, from a release ZIP or built with `fm24/build_exe.py`. It needs no Python install. Put a `.env` file beside it (copy `.env.example` and paste your key after `OPENAI_API_KEY=`) together with the `.fm24-auth` file from the skin setup, then double-click it while FM24 is running. It finds FM24's graphics folder in your Documents by itself.
- **Linux, or from source:** with Python, as described in the FM24 guide.

The full steps are in [fm24/README.md](fm24/README.md).

In FM24, the portrait popup and the profile's **Overview** tab menu have **Generate new face** and **Update face**. The Squad screen's **Overview** menu has **Generate missing squad faces…**. Progress, confirmations and errors appear in FM24's own dialogs.

## Settings

**FM26** asks for the key in the game (see above). The other settings are in `Football Manager 26/BepInEx/config/fmface.hotreload.cfg`, section `[OpenAI]`, which FM26 creates on its first start with the plugin. It is reread before every request.

| Setting | Meaning |
|---|---|
| `Model` | The image model. The default is `gpt-image-2.5-sunburst`. |
| `Quality` | `low`, `medium` (default), `high`, `xhigh`, `max` or `auto`. Higher quality costs more per face. |
| `ApiBaseUrl` | Leave as `https://api.openai.com/v1`; for testing only. |

**FM24 and the web app** read `.env`, beside `fmface.py` or `fmface-fm24.exe`. It is reread for every request, so changes apply without a restart. Never share or commit `.env`; on Linux and macOS, `chmod 600 .env` keeps it private.

| Setting | Meaning |
|---|---|
| `OPENAI_API_KEY` | Your OpenAI API key (required). |
| `OPENAI_IMAGE_MODEL` | The image model. The default is `gpt-image-2.5-sunburst`. |
| `OPENAI_IMAGE_QUALITY` | As `Quality` above. |
| `FM_GRAPHICS_DIR` | Where the web app and `import` save portraits. FM24's companion takes its folder on the command line instead. |

## Web app and import

These are optional tools for portraits you describe or already have. They need Python 3.9 or newer, Pillow (`python3 -m pip install Pillow`) and a `.env` file.

`python3 fmface.py serve` starts a web form at <http://127.0.0.1:8767/> that makes a portrait from a description you type. On Linux, `python3 install_service.py` starts it automatically at login instead; check it with `systemctl --user status fmface.service`. After saving a portrait from the form, restart FM26 to load it. The form's **Generated player** option saves it for a generated (newgen) player's ID.

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

The FM26 plugin's generator (`hotreload/FaceGenerator.cs`) and `fmface.py` build identical prompts; `tests/test_fm26_generator.py` checks this.

## Platform support

Everything has been tested on Linux with Steam/Proton. Status elsewhere:

| Part | Windows | macOS |
|---|---|---|
| FM26 plugin | Its native target: the build check pins the Windows game binaries that Proton also runs. Untested on Windows itself. | Not supported. The build check pins the Windows binaries, and BepInEx IL2CPP on macOS needs a community Il2CppInterop patch for plugins that register their own types, as this one does. |
| FM24 DLL | The same Windows DLL; Windows loads it from the game folder without a launch option. Untested on Windows itself. | Not possible: FM24 for macOS is a native Mac program, not `fm.exe`. |
| FM24 companion | Has a Windows memory reader (`ReadProcessMemory`, `VirtualQueryEx`). Run under Wine in the game's Proton prefix, both Windows Python and the packaged `fmface-fm24.exe` read the live game: a full profile, the shown biography, squad rows and portrait checks. Native Windows is untested. | Refuses to start. |
| Web app and import (`fmface.py`) | Standard-library Python plus Pillow, so it should run; untested. `install_service.py` is Linux-only (systemd). | Should run; untested. |

## Release scope

- **FM26:** tested on 26.3.2 with the vanilla skin, BepInEx 6.0.0-be.760 and FM ActionLink 1.0.0.
  - Its squad scanner matched all 49 players in the test save.
  - A three-face billed batch generated and reloaded every missing portrait; a fresh scan afterwards found none missing. That run used the earlier separate server; the plugin now makes the request itself.
  - Its game-binary checks disable the buttons on other FM26 builds.
- **FM24:** tested on 24.4.2 with its vanilla-based skin through the native DLL: previews of every command, a billed single face and a billed squad batch with Stop.
- **Not yet checked:** other skins, resolutions, languages and game builds.

To make a release ZIP, run `python3 make_release.py` after building the plugin, and optionally `fm24/build_exe.py`. The ZIP contains only the public source, docs, license, tests, the plugin DLL and, if built, the FM24 companion. It leaves out `.env`, portraits, the translated third-party research guide and dependency binaries. FM Face's own code and documentation are licensed under [MIT](LICENSE).
