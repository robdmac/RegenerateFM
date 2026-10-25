# FM26 portrait, Actions, and squad commands

This BepInEx plugin lets you left-click a player or staff portrait on FM26's Overview and choose **Generate new face** from a native menu beneath it. **Actions > Generate face** remains available. Both commands read the visible profile, build the prompt, call OpenAI's image API directly from inside FM26, show an animation over the portrait, save the face and reload FM26 graphics. No separate server is needed. Staff requests use name, age, nationality, and job title. For both players and staff, the prompt includes the headline paragraph from the Career biography when that tab has been shown during the current game session. Choosing the command again regenerates and replaces that person's RegenerateFM portrait. It makes one image API request per successful selection. If the API returns an error, the previous portrait remains; the portrait area shows a failure state and the detail appears in its tooltip and `BepInEx/LogOutput.log`.

**Update face** sits below **Generate new face** in the portrait menu and below **Actions > Generate face**. It is enabled only when the portrait is not FM26's `person_default` silhouette, so the person already has a face, generated or from the game. The plugin reads the displayed texture, encodes it as a PNG and sends it with the profile. The plugin uses its own saved file instead when the face is a RegenerateFM portrait. The prompt keeps the person, sets their current age and club kit, and asks for a realistic, updated photograph of the supplied picture. When the picture is FM26's own 3D face for a newgen (its portrait address starts with `newgen_`) and RegenerateFM has no face for them yet, the prompt instead turns the render into a photograph of a real person: it keeps the skin tone, eye colour, hair colour and hairstyle, and gives the face the person's saved appearance traits (face shape, nose and eyebrows, facial hair, expression), the same ones Generate face uses. The request goes to the image edit endpoint and replaces the RegenerateFM portrait like a normal generation.

On **Squad > Overview** (in the FM26 Demo, **Squad > First Team**: its Overview is a tile dashboard, and its Squad button opens that, so between faces the batch goes on to First Team), open the top **Squad** dropdown and choose **Generate faces…** below it. The plugin scans the roster, identifies players whose portrait asset is `person_default`, and requires the number scanned to match FM's displayed player count. It then asks for confirmation showing the number of missing portraits and billed image requests. Each confirmed player is opened in sequence; the plugin checks the visible name and age, captures the same profile details as individual generation, saves a face and reloads graphics before moving on. **Stop after current face** stops before the next request, with a five-second pause between completed players. A failed capture or request stops the batch without sending later requests. The squad command does not replace existing portraits and is currently FM26-only.

The plugin is pinned to the checked FM26 26.3.2 game binaries and those of the FM26 Demo, a separate 26.3.2 build. It disables its buttons if those binaries change. It was tested with [BepInEx 6.0.0-be.760 Unity IL2CPP Windows x64](https://builds.bepinex.dev/projects/bepinex_be/), which the release ZIP's `FM26` folder includes unmodified (see `licenses/THIRD_PARTY_NOTICES.md`). It no longer needs [FM ActionLink](https://sortitoutsi.net/content/77820/fm-actionlink-free-plugin-for-modders-connect-fm-screens-to-external-tools), created by @marceloouw / @FMSeasonHub, and works beside it. It uses FM26's local-asset registration and panel reload methods, identified with help from the MIT-licensed [FM Graphics Reload source](https://github.com/dev-junos/FM-Graphics-Reload).

**Installing:** copy the release ZIP's `FM26` folder's contents into the `Football Manager 26` folder (or `Football Manager 26 Demo`) (on Linux, also set the launch option `WINEDLLOVERRIDES="winhttp=n,b" %command%`). The first start takes a few minutes while BepInEx generates its game bindings.

**Building it yourself** needs the .NET 10 SDK and a game folder where BepInEx has started once (the build references its generated `BepInEx/interop` assemblies). The plugin targets .NET 6, which BepInEx runs, and compiles `src/RegenerateFM.Core`'s files in, so it is one DLL:

```bash
dotnet build src/RegenerateFM.FM26 -c Release -p:GameDir="/path/to/Football Manager 26"
```

Copy `src/RegenerateFM.FM26/bin/Release/net6.0/RegenerateFM.dll` into `Football Manager 26/BepInEx/plugins/RegenerateFM/` and restart FM26. (Builds before 1.0.0 were `RegenerateFMHotReload.dll` in `BepInEx/plugins/RegenerateFMHotReload`; the installers remove that folder, and if you copy by hand, delete it so the two don't both load.)

## Credits or OpenAI key

The first command without a saved key opens RegenerateFM's key dialog. It offers both ways to pay (see [Choose how to pay](../README.md#1-choose-how-to-pay)).
- **Credits:** **Buy credits / Sign in** starts a sign-in with the credits service (`[Credits] ServiceUrl`) and opens its page in the browser. The player signs in with their email and connects the game, and the plugin polls until then and saves the credits token (`rfm_…`) where a key goes. Requests with a token go to the credits service, which takes one credit a face. Running out of credits, or a sign-in that's no longer valid, opens the dialog again.
- **Getting a key:** a button opens OpenAI's API keys page, and a masked field takes the key, pasted or typed. While the dialog is open the plugin switches on FM26's "exclusive input", as the game's own text boxes do, so letters go to the field and not to FM26's keyboard shortcuts.
- **Checking it:** **Save key** checks the key with a free `GET /v1/models` request. Only then is it saved, and the chosen command continues.
- **Where it's kept:** the key or token, in `%APPDATA%\RegenerateFM\openai-key`, outside the game folder so it isn't zipped up with a BepInEx folder shared for support. It's encrypted with DPAPI (`CryptProtectData`) for the current Windows user; under Proton that's Wine's DPAPI inside the game's prefix.
- **Rejected keys:** an HTTP 401 from the image API opens the dialog again.
- **Forgetting the key:** delete the file.

## Settings

The plugin reads `settings.ini` beside the saved key (`%APPDATA%\RegenerateFM\settings.ini`; `~/Library/Application Support/RegenerateFM/settings.ini` on macOS) before each request, and writes it with the defaults and what each one does the first time. FM24's companion reads the same file:

```ini
[OpenAI]
Model = gpt-image-2.5-sunburst
Quality = medium
ApiBaseUrl = https://api.openai.com/v1

[Credits]
ServiceUrl = https://credits.regenerate.fm
```

- **Quality:** `low`, `medium`, `high`, `xhigh`, `max` or `auto`.
- **`ApiBaseUrl` and `ServiceUrl`:** for testing against a stand-in server only. They must be `https` addresses, or `http` on this computer.

## Where faces are saved

Faces are saved as `<person ID>-<16 hex digits>.png`, a new name for each face (FM26 keeps a staff portrait it has drawn, so a face saved over the same file wouldn't appear until a restart; the person's older file is removed), in FM26's user-data folder, `Documents/Sports Interactive/Football Manager 26/graphics/regeneratefm` (the Demo's is `Football Manager 26 Demo`; on macOS, `~/Library/Application Support/Sports Interactive/Football Manager 26/graphics/regeneratefm`).
- **Person ID:** read from the profile header's data binding, where FM26 maps `objectID` to `Person.UniqueId`, so no skin change or preference is needed. A header that shows `(ID:n)` text is used if that binding isn't found.
- **Mapping:** `config.xml` maps each face to the person's ordinary and generated-player IDs.
- **Appearance traits:** these are kept under `.regeneratefm-appearances/`, so a regenerated face keeps the same traits.

The generator is `src/RegenerateFM.Core`, which FM24's companion uses too; `tests/RegenerateFM.Tests` checks its prompts against the ones the earlier Python generator made. The plugin's own files are by area: `Plugin.cs` (loading), `GameBuild.cs` (the checked FM26 builds), `Generation.cs`, `Profiles.cs`, `Ethnicity.cs`, `SquadBatch.cs`, `KeyDialog.cs`, `PortraitMenu.cs` and `Patches.cs`. Request and graphics-reload timeouts release their locks, so a stalled reload doesn't need an FM26 restart before trying again. Removing the DLL and restarting turns the controls off.
