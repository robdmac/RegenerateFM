# FM26 portrait, Actions, and squad commands

This BepInEx plugin lets you left-click a player or staff portrait on FM26's Overview and choose **Generate new face** from a native menu beneath it. **Actions > Generate face** remains available. Both commands read the visible profile, build the prompt, call OpenAI's image API directly from inside FM26, show an animation over the portrait, save the face and reload FM26 graphics. No separate server is needed. Staff requests use name, age, nationality, and job title. For both players and staff, the prompt includes the headline paragraph from the Career biography when that tab has been shown during the current game session. Choosing the command again regenerates and replaces that person's FM Face portrait. It makes one image API request per successful selection. If the API returns an error, the previous portrait remains; the portrait area shows a failure state and the detail appears in its tooltip and `BepInEx/LogOutput.log`.

**Update face** sits below **Generate new face** in the portrait menu and below **Actions > Generate face**. It is enabled only when the portrait is not FM26's `person_default` silhouette, so the person already has a face, generated or from the game. The plugin reads the displayed texture, encodes it as a PNG and sends it with the profile. The plugin uses its own saved file instead when the face is an FM Face portrait. The prompt keeps the person, sets their current age and club kit, and asks for an updated version of the supplied picture. The request goes to the image edit endpoint and replaces the FM Face portrait like a normal generation.

On **Squad > Overview**, open the top **Squad** dropdown and choose **Generate faces…** below it. The plugin scans the roster, identifies players whose portrait asset is `person_default`, and requires the number scanned to match FM's displayed player count. It then asks for confirmation showing the number of missing portraits and billed image requests. Each confirmed player is opened in sequence; the plugin checks the visible name and age, captures the same profile details as individual generation, saves a face and reloads graphics before moving on. **Stop after current face** stops before the next request, with a five-second pause between completed players. A failed capture or request stops the batch without sending later requests. The squad command does not replace existing portraits and is currently FM26-only.

The plugin is pinned to the checked FM26 26.3.2 game binaries. It disables its buttons if those binaries change. It was tested with [BepInEx 6.0.0-be.760 Unity IL2CPP Windows x64](https://builds.bepinex.dev/projects/bepinex_be/) and [FM ActionLink 1.0.0](https://sortitoutsi.net/content/77820/fm-actionlink-free-plugin-for-modders-connect-fm-screens-to-external-tools), created by @marceloouw / @FMSeasonHub. It uses FM26's local-asset registration and panel reload methods, identified with help from the MIT-licensed [FM Graphics Reload source](https://github.com/dev-junos/FM-Graphics-Reload). It does not include these projects' binaries or injection code.

Build with the .NET 6 SDK after installing BepInEx IL2CPP and FM ActionLink:

```bash
dotnet build hotreload/FMFaceHotReload.csproj -c Release -p:GameDir="/path/to/Football Manager 26"
```

Copy `hotreload/bin/Release/net6.0/FMFaceHotReload.dll` into `Football Manager 26/BepInEx/plugins/FMFaceHotReload/`, set FM ActionLink's `playerProfile.buttons` to `[]`, and restart FM26 once.

## OpenAI key

The first command without a saved key opens FM Face's key dialog.
- **Getting a key:** a button opens OpenAI's API keys page, and a masked field takes the pasted key.
- **Checking it:** **Save key** checks the key with a free `GET /v1/models` request. Only then is it saved, and the chosen command continues.
- **Where it's kept:** `%APPDATA%\FM Face\openai-key`, outside the game folder so it isn't zipped up with a BepInEx folder shared for support. It's encrypted with DPAPI (`CryptProtectData`) for the current Windows user; under Proton that's Wine's DPAPI inside the game's prefix.
- **Rejected keys:** an HTTP 401 from the image API opens the dialog again.
- **Forgetting the key:** delete the file.

## Settings

The first start creates `BepInEx/config/fmface.hotreload.cfg`, which is reread before each request:

```ini
[OpenAI]
Model = gpt-image-2.5-sunburst
Quality = medium
ApiBaseUrl = https://api.openai.com/v1
```

- **Quality:** `low`, `medium`, `high`, `xhigh`, `max` or `auto`.
- **`ApiBaseUrl`:** for testing against a stand-in server only.

## Where faces are saved

Faces are saved as `<person ID>.png` in FM26's user-data folder, `Documents/Sports Interactive/Football Manager 26/graphics/fmface`.
- **Mapping:** `config.xml` maps each face to the person's ordinary and generated-player IDs.
- **Appearance traits:** these are kept under `.fmface-appearances/`, so a regenerated face keeps the same traits.

The generator is `FaceGenerator.cs`. It builds the same prompts as `fmface.py`, and `tests/test_fm26_generator.py` checks that they stay identical (it needs the .NET 6 SDK). Request and graphics-reload timeouts release their locks, so a stalled reload doesn't need an FM26 restart before trying again. Removing the DLL and restarting turns the controls off.
