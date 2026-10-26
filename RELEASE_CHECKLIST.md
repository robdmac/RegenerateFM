# FM24 native integration: in-game release checklist

Automated tests and the Wine bridge harness cover protocol, installer, and
binding logic. The checks below need the real game. Run them on the pinned
FM24 24.4.2 executable at 1854×1011 with screen IDs enabled. Record the date,
result, and `regeneratefm24.log` excerpt for each.

Steps marked **billed** make one image API request each.

## Install and upgrade

1. With FM24 closed, run `regeneratefm-fm24 setup` (on Linux, the release's
   `install-linux.sh fm24`) over the existing installation. Expect a version 2 `regeneratefm24-install.json` listing the two
   portrait panels, and (for an install that had it) the message that the
   Squad screen Faces button was removed.
2. Start FM24 with `WINEDLLOVERRIDES="winmm=n,b"`. Expect
   `REGENERATEFM24_NATIVE_MENU_HOOK_INSTALLED`, `REGENERATEFM24_SQUAD_MENU_INSTALLED`,
   `REGENERATEFM24_BRIDGE_READY` and `REGENERATEFM24_RELOAD_READY` in `regeneratefm24.log`.

## Preview mode (`--preview-only`)

3. Player portrait → Generate new face: the prompt for the correct ID appears.
4. Staff portrait → Generate new face: the same check for a staff profile.
5. Navigation race: choose Generate new face, then immediately open another
   profile. Expect a refusal naming both IDs, and no preview for the second
   person. Delayed delivery: pause the companion (`kill -STOP`), choose Generate
   new face, open another profile, then resume it (`kill -CONT`) after about
   one second. Expect FM24's "received this click too late" dialog, a companion
   line saying the command "arrived … after the click; refused", and no preview.
   A pause exceeding the DLL's reply timeout may instead show "companion is
   unavailable". Also test a pause below 0.75 seconds using the next-person
   arrow: expect "Generate new face was chosen for person X, but FM24 now
   shows person Y", with X the clicked person, and no preview. `regeneratefm24.log`
   and the companion line must name the clicked person's ID.
5b. On a player and a staff profile, open the **Overview** tab menu: it ends
   with Generate new face and Update face. Each sends the preview for the
   person shown. On a person with no face, Update face is greyed out in both
   the portrait popup and the Overview menu, and clicking it does nothing.
6. Squad → **Overview** menu → Generate missing squad faces… (last item): the
   scan runs and the confirmation shows the missing count. Cancel.
7. The same item anywhere else FM24 shows **Export Team for Versus
   Competition** (if any): the command must refuse with "no longer on the
   Squad screen". **Export Team for Versus Competition** must still work.

## Bridge recovery

8. Stop the companion, then choose Generate new face. After about 2 s, expect the
   "companion is unavailable" dialog.
9. Restart the companion without restarting FM24 and click again. Expect it to
   be accepted.
10. Click twice quickly. Expect the second click to show the "already
    processing" dialog.

## Billed generation

11. **billed** Player with no portrait: the portrait is saved and reloads in game.
12. **billed** Regenerate the same player: the new image replaces the old one
    and the previous versioned file is removed.
13. **billed** Staff member.
14. **billed** Squad batch of two missing players via the Squad menu item,
    including **Stop after current face** on a second run.
15. Failure path: set an invalid `OPENAI_API_KEY`. Expect an error dialog, no
    graphics change, and the old portrait intact.

## Restart, update, uninstall

16. Restart FM24 and the companion. Repeat step 3.
17. Re-run the installer (idempotent reinstall). Expect panels and the manifest
    to be unchanged apart from the DLL.
18. `install.py --remove`. Expect the original RegenerateFM panels to be restored,
    the squad panel and the empty `panels/team` directory to be removed, and
    the game to start without the hook once the launch option is cleared.

## C# port and 1.0.0 names: 2026-10-25

The FM24 companion, its setup and the Linux installer are now C#
(`src/RegenerateFM.Desktop`), sharing the generator with the FM26 plugin
(`src/RegenerateFM.Core`). The plugin is `BepInEx/plugins/RegenerateFM/RegenerateFM.dll`
(plugin ID `fm.regenerate`); both games read `settings.ini` beside the key
instead of `regeneratefm.hotreload.cfg` and `.env`; the token lives only in
`regeneratefm24.ini`; the Python web form and `import` are gone. Entries below
this one name the Python files of the time.

- [x] `dotnet test tests/RegenerateFM.Tests`: 98 tests, including all 180
  prompt cases and 270 appearance-trait cases recorded from the Python
  generator, and `python3 -m unittest discover -s tests/tools`.
- [x] `tools/make_release.py` published the companion (13 MB) and the Linux
  installer (15 MB) with no trimming warnings.
- [x] Linux, Steam/Proton (Proton 9): `install-linux.sh` refused with Steam
  running, then updated FM26, the FM26 Demo and FM24 from the Python build
  (the old plugin folder removed; FM24's DLL, token and skin kept, "already set
  up"; the companion replaced). Steam's launch options were unchanged.
- [x] FM26: the plugin loaded as `RegenerateFM 1.0.0`; Generate face through
  the stand-in (with `settings.ini`'s test addresses), then one **billed** face
  with the default `settings.ini` it wrote.
- [x] FM24: the DLL started the C# companion under Proton; with no key, its
  Win32 key window opened over FM24 and saved a key (DPAPI, `RFMK1`);
  Update face for Messi from FM24's bundled JPEG and mask (stand-in); Update
  face greyed for a person with no face; the Squad scan (29 players, all with
  faces); Inter Miami II's confirmation (11 of 12 missing, 11 brief records)
  and a stand-in batch stopped with Stop after this face; one **billed**
  Generate (Lucas Meek) and one **billed** Update from a bundled photo (Ryan
  Sailor).
- [x] Windows rehearsed under Wine (Proton 10, a scratch prefix): `dotnet test`
  with the Windows .NET 10.0.402 SDK passed 92 and skipped the 7 tests of the
  Linux installer and launch options. The packaged companion, started through
  the Windows shell, found FM24 in a second Steam library through the
  registry's `SteamPath` and `libraryfolders.vdf`, refused until FM24's
  Documents folder existed, then installed the DLL, UTF-16 ini and skin,
  took a hidden key (checked by the stand-in, saved `RFMK1`) and waited for
  Enter; run again it only said it was set up, and `setup --remove` undid it.
  This found the console printing € as `?` (now UTF-8).
- [ ] Windows: the companion's double-click setup and terminal key prompt,
  FM24 starting it, a face and a squad batch; FM26's plugin (copied by hand:
  delete `BepInEx/plugins/RegenerateFMHotReload` first).
- [ ] FM26 staff faces on Windows/Proton (checked on the Mac, 2026-10-26, with
  the image stand-in): **Generate faces…** on an All Staff list, each tick box
  (counting the other list through Club Site), and a batch covering both.
- [ ] FM24 staff faces on Windows/Proton (checked on the Mac, 2026-10-26): the
  DLL's **Generate missing staff faces…** in the Staff screen's Overview
  menu, the three-button choice (each answer, and No closing it), and the
  Windows layout's guesses: a contract's job at +0x1C, a club object's index
  at +8, the screen's club reference (type 3), and the record's woman flag
  (0x10 in byte +0x0D: Karren Brady's prompt says she is a woman).
- [ ] macOS: `dotnet publish -r osx-arm64`, `macos/make_installer.py` (the
  companion is signed with the allow-jit entitlement), the installer's FM24
  and FM26 install, update from the Python build and uninstall, the companion
  reading through the Mac library, the in-game key prompt and a face.
- [ ] Stop at the stand-in's speed took effect three faces late (the skin
  reload closes the progress dialog before its answer is read); the Python
  companion had the same logic. Check that Stop is prompt at real speed.

## Independent review: 2026-10-14

**Release status: not ready.** Tests below used FM24 24.4.2, Proton 9, the
RegenerateFM skin, English UI, enabled screen IDs and a 1854×1011 game window.
All companion runs were preview-only; no image API request was made. The
production native DLL is installed, FM24 is running, and the companion is
left in preview mode. Temporary diagnostic DLLs were removed before final
testing. The FM26 plugin was built, but not installed or tested in FM26.

### Remaining findings

1. **High: delayed commands can target another person.** Clicked Generate on
   Franco Negri (14080606), with the companion temporarily paused; opened
   Drake Callender (72056318), then resumed the companion. It successfully
   prepared Drake's prompt instead of refusing. The DLL packet has no person
   ID, and `native_listener.py` captures the screen only after receipt.
   The current expected-ID check protects navigation after that capture, not
   before it. Bind the original selection in the native callback and carry it
   in the authenticated command; add delayed-delivery coverage.
2. **Medium: native biography selection can use a later paragraph.** The
   Jorge Walter Theiler staff preview used "Theiler's life-long love affair
   with Newell's…". The visible headline starts "Club Atlético Newell's Old
   Boys icon Jorge Walter Theiler…". `native_profile.read_biography` chooses
   the longest candidate beginning with a person link, which does not prove
   the paragraph's position in the displayed biography. Player previews for
   Drake Callender and Franco Negri did match their visible headlines.
3. **Medium: full squad scan did not finish.** The native Faces command
   scanned ten players, then stopped while returning from Franco Negri's
   profile: "Could not return to the same FM24 squad; scan stopped. FM24 is
   no longer on the Squad screen; no image request was made." The profile
   remained open. `_return_to_squad` sends one click, then polls for up to
   75 seconds without retrying the transition. A complete roster scan,
   confirmation and batch generation remain unverified.

### Implementation changes before the follow-up (2026-10-14)

1. **Delayed commands (finding 1).** Native protocol version 2: the DLL
   stamps each command with its click time, and the companion refuses one that
   arrives more than 0.75 s later, before any screen capture. FM24 shows
   "received this click too late"; a command queued while the companion was
   paused gets the same refusal once it resumes. Wine harness reproduction of
   the review case (companion stopped during the click, then resumed): refused,
   no request; later clicks accepted/busy as before. This bounds the click-to-
   capture window to about a second rather than proving the original person;
   reading the person in the DLL would need further reverse engineering.
2. **Staff biography (finding 2).** Candidate paragraphs now include strings
   whose first paragraph links to the person anywhere, not only at the start
   (Theiler's headline starts with the club link). The companion captures the
   screen between its two native reads and uses the candidate whose opening
   matches the on-screen Biography box. With no unique match it uses the
   on-screen text; with none readable, no headline.
3. **Squad return (finding 3).** Back is pressed again, up to three times,
   when the exact profile title is still shown 8 s after the previous press.
   Once the title changes, Back is never repeated, so a slow but accepted Back
   cannot become a double navigation.

Verification: 61 tests (6 new), pyflakes, DLL build with `-Werror`, Wine
harness (no companion, dropped packet, stale ACK, busy, silent companion,
stale refusal dialog, paused-then-resumed real listener). The rebuilt DLL is
installed (in-place upgrade; panels unchanged). Checklist steps 4–6 need
repeating in game.

### Fixes made during this review

- A request-worker startup failure now releases the bridge's busy lock and
  reports an error, keeping the listener available for subsequent clicks.
- Installer rollback restores a removed directory's permissions.
- Atomic installer writes use unique temporary files, preserving unrelated
  files with the old predictable temporary name.
- Three regression tests cover these failures. The existing binding test was
  renamed to describe receipt-time binding accurately.

### Results

| Check | Result |
| --- | --- |
| Automated suite | Original 52 passed; 55 passed after fixes |
| pyflakes | Passed for `fm24` and `regeneratefm.py` |
| FM24 native DLL build | Passed with `-Werror`; 189 forwarding exports |
| Wine harness, current native C source | Passed normal ACK, dropped first packet, stale ACK, busy reply and no-reply cases |
| FM26 plugin Release build | Passed with .NET SDK 6.0.428; zero warnings/errors |
| Release archive | 44 explicitly selected public files, including this checklist; no private config, research or backups; extracted copy passed all 55 tests |
| Checklist 1, v1 upgrade | Passed on real installation; version 2, three panels |
| Checklist 2, startup | Passed hook-installed and bridge-ready log markers |
| Checklist 3, player preview | Passed correct ID, identity, physical/extreme stats and biography |
| Checklist 4, staff preview | Correct ID/name/age/nationality/role; biography failed as above |
| Checklist 5, navigation | Failed delayed-receipt case as above |
| Checklist 6, squad scan/confirmation | Menu and dispatch passed; full scan failed as above; confirmation not reached |
| Checklist 7, non-Squad guard | Not exercised in game |
| Checklist 8, companion unavailable | Passed in FM24; visible native dialog and unavailable log |
| Checklist 9, companion restart | Passed without game restart; subsequent player preview accepted |
| Checklist 10, busy click | Passed unit/Wine harness; not separately exercised in FM24 |
| Checklist 11–15, generation/failure/batch | Not exercised; no billed requests |
| Checklist 16, restart | Passed repeated game/companion startup and profile preview |
| Checklist 17, reinstall | Passed on real files; DLL/config/manifest/skin/backups byte-identical |
| Checklist 18, uninstall | Passed; original panels byte-identical, squad directory removed, unhooked game started |

Session evidence is under `/tmp/regeneratefm-review-*`: final test/build/harness
logs, preview logs, screenshots, and a private pre-install backup. These are
local review artifacts, not release contents. The release archive produced
during review is a test artifact, not an approved release.

## Follow-up in-game retest: 2026-10-14

**Release status: not ready; original-person binding remains unresolved.**
The rebuilt native protocol v2 DLL was already installed when this retest
started. Its checksum matched the installation manifest, and startup recorded
both hook-installed and bridge-ready markers. FM24 used the same build, skin,
language, screen IDs, Proton version and resolution as the first review.

| Check | Follow-up result |
| --- | --- |
| Automated tests and pyflakes | 61 tests passed independently before further changes; 62 passed after the screenshot-identity fix below; pyflakes passed |
| Final release archive | All 62 tests passed from the extracted 44-file archive; private configuration and research excluded |
| DLL build | Passed `-Werror`, 189 forwarding exports |
| Native C harness | Passed under Proton's Wine 9: protocol v2, normal ACK, dropped packet/retry, older ACK ignored, busy, stale, and unavailable paths |
| Staff biography | Passed in FM24: Theiler's prompt starts with the visible club-link headline and includes its complete opening paragraph; repeated successfully after the screenshot-identity fix |
| Player after screenshot-identity fix | Passed in FM24: verified Franco Negri's ID before dispatch; final preview retained identity, height, physical/extreme stats and opening biography |
| Stale-command refusal | Passed in FM24: paused companion, chose Generate on Theiler, opened Drake, resumed; command arrived 1.43 seconds after the click, native stale dialog shown, no new preview |
| Short-delay navigation | **Failed in FM24:** Generate chosen for Facundo Farías (14240979), next-person arrow opened Ian Fray (2000045865), companion resumed after a 0.532-second pause; accepted command prepared Ian's prompt |
| Navigation after screenshot | A separate fast-navigation attempt captured the Squad title before C.J. dos Santos opened; expected-ID mismatch refused it without a request |
| Dropped return click | Passed with actual FM24 UI and an intentionally suppressed first click: returned to Squad with exactly one retry |
| Full native squad scan | Passed: all 29 players scanned, zero missing; zero-missing info dialog reached; retries recovered multiple dropped return clicks, including Franco Negri where the first review stopped |
| Graphics writes | File set and checksums unchanged throughout preview testing |
| Billed generation/reload/batch | Not exercised; no API requests made |

The timestamp cutoff mitigates long delays but does not fix the original-person
race. The successful short-delay reproduction uses FM24's normal next-person
arrow, not a fabricated native command. The original ID must be bound in the
native callback and verified before billing. Lowering the cutoff alone is not
proof of correct selection.

### Additional biography fix during retest

Matching native reads before and after a screenshot do not prove that the
screenshot belongs to that person: navigation away and back can leave both
reads unchanged. A controlled test using the real Theiler screenshot between
two mocked Drake native reads reproduced Theiler's biography being attached to
Drake. This was a snapshot-injection reproduction, not a claimed live
out-and-back reproduction.

`read_prompt_profile` now verifies the person ID in that exact screenshot before
using its visible biography. A mismatch or unreadable ID refuses the request.
When no biography can be read, the unused native biography scan is skipped.
One new regression test covers both player and staff identity mismatches; the
same real-screenshot reproduction now refuses before candidate selection.
Theiler's normal native menu preview still passed with this guard enabled.

One additional limit observed: Ian Fray's native profile reader found no
validated record and used OCR fallback; its prompt spelled his first name
"lan". This retest does not establish native profile coverage for every person.

Evidence is in `/tmp/regeneratefm-retest-*`, including original/final preview logs,
screenshots, test/build/harness logs and graphics checksums. The test dialogs
were dismissed, and the updated companion was restarted in preview mode for
the running game window. Nothing was billed. This section supersedes the
earlier pending status for the three in-game retests; missing-face confirmation,
paid generation, automatic reload and batch stop remain outside this run.

## Click-time person binding (protocol 3): implementation before live retest

Addresses the short-delay wrong-person race (Facundo Farías → Ian Fray).

- **Where the person comes from.** A read-only search of live FM24 memory
  found exactly one static path to the open profile's person: the static
  stack FM24 uses to draw bound UI objects, at RVA `0x6374408` (begin, cursor,
  end). Its bottom entry is the open screen's bound object. Live checks in
  preview mode, using the next-person arrow and Back: Franco Negri, Federico
  Redondo, a third player, and Redondo again all matched the title ID once
  settled. The entry was unchanged with the portrait menu open and the
  pointer over **Generate new face**. While a new profile loaded it briefly
  held other objects (none, a club, another person). No staff profile has been
  checked.
- **DLL.** The menu callback reads that entry on FM24's UI thread with
  `ReadProcessMemory`, before returning to the game. FM24 cannot navigate
  during the callback. It accepts only a person record with a duplicated ID
  and sends that ID in the command, with 0 if none was found. Click, time and
  person are handed to the worker together under one lock.
- **Companion.** A profile command with person 0 is refused. Otherwise the
  screenshot taken on receipt must show the same ID ("chosen for person X, but
  FM24 now shows person Y"). Generation is then requested with
  `expected_id` = the clicked person, which the companion checks again against
  the native profile before billing. The 0.75 s late-command refusal stays as
  an extra check. Protocol 2 DLLs are ignored.
- **Verification.** 64 tests (the retest's short-delay case is a regression
  test), pyflakes, DLL build with `-Werror`. Wine harness with FM24's stack
  emulated: matching person → request bound to it; mismatched person
  (14240979 vs. screen 14008037) → refused, no request; no stack (unmapped
  address) → person 0, refused, no crash. Fake-companion retry, busy and
  unavailable cases still pass.
- **Not yet done.** The new DLL is built in `fm24/native_menu/build/` but not
  installed, because FM24 was running. Close FM24, run the installer, start
  FM24 and restart the companion; a protocol 2 DLL and the new companion
  ignore each other. Then repeat step 5, including the sub-0.75 s
  next-person case, plus step 4 for a staff profile. Check that
  `regeneratefm24.log` shows no crash and that the refusal names the clicked ID.

## Protocol 3 independent live retest: 2026-10-14

**Release status: not ready. The original short-delay case now refuses
correctly, but the native ID reader does not cover staff and all players.**

The game was closed, the current source rebuilt, and the new DLL installed
over protocol 2. All skin XML files remained byte-identical. A private backup
is at `/tmp/regeneratefm-binding-preinstall.tgz`. FM24 and the companion were then
restarted with protocol 3 in preview mode, on the same validated build/skin,
English UI, screen IDs and 1854×1011 window.

### Remaining finding: first drawing-stack entry is not always the person

`bound_person_id` reads only the first entry at `boundObjectStackRva`. These
live menu selections returned person 0 and the companion refused them:

- **Jorge Walter Theiler (14008037):** first entry held Inter Miami
  (72052048), with runtime type `TEAM@db`; the second entry held Theiler's
  person record, with the expected duplicated ID and person tag `0x58`.
- **Tyler Hall (2000251984):** first entry held nation 390, with runtime type
  `NATION@db`; the second entry held Tyler's person record with the expected
  duplicated ID and person tag `0x58`.

The type names were verified through FM24's runtime type information, not
inferred from IDs alone. The field described as the stack cursor equaled its
begin pointer during the settled reads. Merely scanning additional allocated
entries therefore needs separate proof of current selection; it should not
be adopted as an unvalidated fallback. The observed non-person entries were
on settled profiles, so this is not limited to navigation/loading transitions.
The current refusal prevents billing but blocks previously working staff and
some player menu commands. Resolve coverage before treating this path as a
general click-time selection mechanism.

### Results

| Check | Result |
| --- | --- |
| Installation/startup | Passed; rebuilt protocol 3 installed, skin XML unchanged, hook-installed and bridge-ready markers logged |
| Automated suite | Initial run exposed an asynchronous test failure; after the test fix below, all 64 passed |
| pyflakes | Passed for public Python scripts, FM24 source and tests |
| Native DLL build | Passed `-Werror`, 189 forwarded WINMM exports |
| Current C/Wine harness | Passed matching ID, mismatched ID, unreadable address, invalid record tag/duplicated ID, retries, old ACK ignored, busy, stale and unavailable paths |
| Extracted release | All 64 tests passed from the 44-file archive; private configuration and research excluded |
| Franco Negri normal menu | Passed native bound-ID preview for 14080606 |
| Facundo Farías normal menu | Passed native bound-ID preview for 14240979 |
| Original Farías → Ian Fray race | **Passed:** verified Farías's menu was open, chose Generate, used next-person arrow with companion paused 0.533 seconds; refused with "chosen for person 14240979, but FM24 now shows person 2000045865"; no new preview/request |
| Late command | Passed in FM24: command arriving 1.18 seconds after click was refused, with native stale dialog and log marker |
| Theiler staff menu | **Failed functionally:** reported 0, refused before any request; game remained running |
| Tyler Hall player menu | **Failed functionally:** reported 0, refused before any request; game remained running |
| Recovery | Franco preview succeeded after the two ID-0 refusals without restarting the game or companion |
| Graphics/API | Graphics file set and checksums unchanged from previous review; no billed requests |
| Paid generation/reload/batch | Not exercised in this retest |

### Test fix

`test_profile_without_reported_person_is_refused` checked the recovery request's
mock immediately after receiving its ACK. The bridge sends the ACK before
starting the worker, so `request.call_args` could still be `None`. The test
now asserts no request for person 0, then waits for the valid request worker
before inspecting its bound ID. Product behavior was unchanged by this fix.

Evidence is under `/tmp/regeneratefm-binding-*`: preview/test/build logs, screenshots,
graphics checksums and the harness's results. Temporary harness memory was
simulated in its own process; the live game memory inspection was read-only.
The test dialogs were dismissed. The protocol 3 DLL remains installed and the
companion remains in preview mode. This section supersedes the earlier
"awaiting in-game retest" status; it does not establish general person-reader
coverage or approve a release.

## Header-memory binding (protocol 5): passed in-game preview retest

Replaces protocol 3's drawing-stack reader, which refused Theiler and Tyler
Hall. A screenshot-based alternative (unreleased "protocol 4") was tried and
withdrawn, because the goal is binding from game memory. The tree is back on
the protocol 3 sources from `dist/regeneratefm-0.7.0.zip`, with only the changes
below.

- **How the ID is found.** In the menu callback, on FM24's UI thread:
  1. Start at the event's source, which must be RegenerateFM's `fmnP`/`fmnS`
     button and belong to the callback's UI manager. With FM24 paused at a
     real click, gdb showed the source at event+0x20.
  2. Walk up the parents to the screen's unique `head` widget.
  3. Find its unique `subs` subtitle widget.
  4. Parse the trailing "(ID:n)" from its text.
  The widget layout (widget part at +0x38: manager +0x10, parent +0x18, skin ID
  +0x24; children vector at +0x100; text at widget part +0x158) was mapped
  read-only in live FM24. Anything unexpected sends ID 0, which is refused.
- **Live model check** (Python model reading live memory, compared with the
  title ID read by OCR for checking only): all 12 matched.
  - Players: Tyler Hall 2000251984, 71006535, 7458500, 2000273878, Franco
    Negri 14080606, Federico Redondo 14253394, 72056129.
  - Staff: Theiler 14008037, 72049827, 2000214125, 2000218221, 2000218222.
  - Search size on Tyler Hall: 10 ancestors and 138 header widgets.
- **Companion.** The profile request is bound to the DLL's ID. Before billing,
  the companion reads the open profile from memory and refuses if it differs.
  The receipt screenshot used for binding by protocols 2–3 is removed. The
  0.75 s late-command refusal stays. Protocols 1–3 are ignored.
- **Verification.**
  - 62 tests, pyflakes, and the DLL build with `-Werror` pass.
  - Wine test of the C reader on a model widget tree:
    - Correct player and staff IDs.
    - 0 for: no ID, leading zero, overflow, non-digits, wrong manager, wrong
      source, two headers, bad pointer, null event.
  - Wine transport harness: ID 0 refused; squad accepted then busy; no
    companion gives "unavailable".
- **Not yet done.** Install the new DLL (FM24 closed), restart FM24 and the
  companion, then repeat checklist steps 3–5 on Theiler, Tyler Hall, Franco
  and Farías. Include Farías → Ian Fray with the companion paused for under
  0.75 s: expect "now person 2000045865, not the requested person 14240979".
  Check `person=` and `read_ms=` in `regeneratefm24.log` for each click.

### In-game retest: 2026-10-14

The protocol 5 DLL was installed over protocol 3. The three skin panels were
byte-identical before and after. FM24 started with hook-installed and
bridge-ready log markers. The companion ran in `--preview-only`; no image
request was made and graphics were not written.

| Check | Result |
| --- | --- |
| Players, Generate new face | **Passed, 8 of 8.** `regeneratefm24.log` `person=` matched the on-screen ID and the preview was for that ID: Tyler Hall 2000251984, Franco Negri 14080606, Facundo Farías 14240979, Ian Fray 2000045865, Nicolás Freire 14036721, Diego Gómez 2000036372, Julian Gressel 72048236. Tyler Hall was refused by protocol 3. |
| Staff, Generate new face | **Passed, 5 of 5:** Jorge Theiler 14008037 (refused by protocol 3), Damian Silvero 72049827, Erik Marquis 2000214125, Tom Jobe 2000218221 (twice). Theiler's preview used the correct "Club Atlético Newell's Old Boys icon…" headline. |
| Read time | `read_ms=0` on every click, i.e. below the 15 ms tick resolution |
| Farías → Ian Fray, binding check alone | **Passed.** Test-only companion with the late-click limit raised to 30 s; companion paused 1.9 s; next-person arrow opened Ian Fray. DLL sent `person=14240979`; companion refused: "The open FM24 profile is now person 2000045865, not the requested person 14240979; no image request was made." |
| Farías → Ian Fray, normal companion | Refused as late (1.28 s), with the clicked person still logged as 14240979 |

**Observation (not a binding fault).** FM24 fires the menu event only after
the menu closes. A click elsewhere before that cancels the selection, so
navigation cannot precede the event. While a DLL dialog (for example the
"too late" message) is open, the DLL worker waits. Clicks made meanwhile are
merged and handled when it closes, which then refuses them as late. One such
click was logged 41 s late and refused. This is safe but can be confusing.
The cause (a blocking message box) is inferred, not confirmed.

Remaining: paid generation, reload and squad batch (steps 11–15) are not
exercised. The prompt's profile data still falls back to the screen when the
memory reader cannot validate a person: Tyler Hall, Ian Fray and Diego Gómez
here.

## Protocol 5 independent retest: 2026-10-15

**The previously observed Theiler/Tyler binding regression and the original
Farías-to-Ian navigation case passed this independent retest.** This is still
the pinned FM24 24.4.2 build, English RegenerateFM skin, enabled screen IDs,
1854×1011 X11 window and Proton 9. Broader builds/layouts are not established.

FM24 was closed when this run started. The source was independently rebuilt
and reinstalled, with all skin XML files unchanged, and the game restarted.
The old companion was replaced with one targeting the new window.

### Verification results

| Check | Result |
| --- | --- |
| Automated suite / pyflakes | All 62 tests passed independently; pyflakes passed |
| Native DLL build | Passed `-Werror`, 189 forwarding exports |
| Current C reader under Wine | All 12 model-tree cases passed: player/staff IDs, malformed ID, wrong manager/source, duplicate headers, invalid pointer and null event |
| Current C transport under Wine | Passed normal ACK, dropped packet/retry, earlier ACK ignored, busy, stale and unavailable paths |
| Extracted release archive | All 62 tests passed from the 44-file archive; private settings, research, backups and temporary test fixtures excluded |
| Theiler normal native menu | Passed: DLL logged 14008037, preview matched, correct club-link biography headline |
| Tyler Hall normal native menu | Passed: DLL logged 2000251984, preview matched; profile details used verified OCR fallback |
| Original Farías → Ian race | **Passed with the normal 0.75-second cutoff:** verified menu open, selected Generate, next-person arrow opened Ian, companion paused 0.533 seconds; DLL logged 14240979; endpoint refused current 2000045865 versus requested 14240979; no image request. No test-only cutoff override was needed. |
| Native squad entry / Cancel | Passed: scan reached four players; closing its progress window invoked Cancel, scan reported cancellation without requests |
| Graphics restoration | Original file set, checksums and file metadata restored; restored staff portrait pixels matched the original screenshot |

Every independently tested click logged `read_ms=0`, at the clock's available
resolution. This is not a high-resolution performance benchmark.

### Save/reload checks without an image API call

A temporary server fixture outside the repository replaced `generate_png`
with existing local portrait bytes and supplied a dummy key; it never called
the image provider. The normal native menu, authenticated request, profile
validation, progress, versioned save and automatic reload paths remained in
use. A private backup of all original graphics was taken first.

- **Player save and visible reload:** generated for Tyler using Theiler's
  existing portrait as deliberately different test data. The different face
  appeared automatically on Tyler's profile. The old versioned file was
  removed after success.
- **Player regeneration:** a second native menu selection returned Tyler's
  original portrait bytes. The original-looking face appeared automatically,
  and the first test version was removed.
- **Provider failure:** the fixture raised a provider error. The error was
  reported and the complete graphics file set/checksums remained unchanged.
  This exercises the error path, not a real provider authentication response.
- **Staff save and visible reload:** Theiler's menu returned Tyler's existing
  portrait bytes. The visibly different face appeared automatically on
  Theiler's profile. This also showed recovery after the provider error without
  restarting the game or fixture server.

The fixture server was then stopped. All original graphics files, names,
permissions and mtimes were restored from backup, and the game skin reloaded.
Portrait-crop comparisons showed mean RGB differences of 23.232 for the
player test face and 19.265 for the staff test face; player regeneration and
staff restoration both compared at 0.0 against their originals.

### Current limits and final state

No image API call was made and nothing was billed. Actual provider generation,
authentication failures, a complete missing-face squad batch and
stop-after-current-face remain unverified by this run. The save/reload checks
above do establish visible in-game refresh for player generation, regeneration
and staff generation using controlled local image bytes.

Profile details can still use OCR when native records cannot be validated;
the clicked ID itself comes from the DLL's in-memory header reader. Screen IDs
must remain enabled. The DLL worker's reply-dialog behavior noted above is
unchanged; this retest does not establish broader dialog/navigation behavior.

The normal companion is running in preview mode with the production cutoff
unchanged. Original graphics are restored. Evidence, fixtures and private
backups are under `/tmp/regeneratefm-p5-*` and are excluded from the release archive.
No product source changes were needed during this retest.


## Native menu as default; automatic window detection: 2026-10-15

The companion now uses the native menu unless `--direct-clicks` is given, and
finds the visible FM24 game window itself when `--window-id` is omitted. With
no mode or window flag, it selected window 0x4a00005 and a native Generate new
face on Ryan Sailor previewed person 2000155624 (DLL `read_ms=0`). Preview
only; nothing was billed. In native mode the companion draws no menus: the
remaining companion windows are the portrait progress indicator, the squad
progress panel and zenity dialogs. Reload and squad navigation still use X11
input.

## In-process skin reload and biography fix: 2026-10-15

**In-process reload (DLL):** the companion sends `REGENERATEFM24R 1 <serial> <token>`
to the bridge port + 1, and the DLL repeats FM24's own reload-current-skin
command on the UI thread (details in the native menu README). Live results on
the pinned build:

| Check | Result |
| --- | --- |
| Reload request with no screen change | Done in 4.3 s; log `RELOAD_REQUESTED`, `POSTED`, `DONE` |
| Changed portrait picked up | Tyler Hall's mapping pointed at Theiler's existing image; reload took 3.8 s and Theiler's face appeared on Tyler's open profile without leaving it |
| Restore | Original graphics restored byte-identical from backup; reload showed Tyler's original portrait, pixel-identical (mean difference 0.0) |

Two defects were found and fixed during the test. The stage counter rests at 5
(not only -1) after the game's initial skin load, which first refused the
request. The posted `GChS` is handled before the post returns, so the
"running" state is now set before posting. No image request was made. The
companion's end-to-end use of this reload through a native menu selection
(generation, then reload) has not yet been run.

**Biography:** a headline also kept in memory joined to the following
paragraphs made two candidates match the on-screen opening, so the prompt fell
back to OCR text, which read the club-link icon as "!" ("!InterMiami"). The
shortest match is now used when every other match is that headline followed by
more sentences, and the OCR fallback drops a "!" glued to the start of a word.
Ryan Sailor's prompt now uses the memory headline.

**Incident:** while a debugger was attached to FM24 for menu research,
Xwayland stopped responding and GNOME restarted it at 10:05, closing FM24 and
the Steam client. No project files or graphics were affected.

## Squad menu item replaces the Faces button: 2026-10-15

The DLL extends the provider object that adds **Export Team for Versus
Competition** to the Squad screen's **Overview** menu (vtable entries pinned
in `supported_build.json`). Found by locating that item's translation ID
(335045) in code, then confirming with breakpoints which of two identical
providers the Squad screen uses.

| Check | Result |
| --- | --- |
| Menu | "Generate missing squad faces..." is the last item, below Export Team for Versus Competition |
| Selection | Log `REGENERATEFM24_SQUAD_CLICK`, `CLICK_ACCEPTED`; companion started the squad scan (preview; stopped by restarting the companion) |
| Upgrade | Reinstall removed the created `team squad.xml`; manifest lists two panels; the Faces button is gone from the Squad screen |
| Portrait menu after upgrade | Drake Callender previewed as 72056318, `read_ms=0` |

**Export Team for Versus Competition** still opens FM24's Save dialog (cancelled
without saving). Not yet checked: whether the item appears on other screens
that use the same provider.

## In-game squad command (memory roster, prompts by ID): 2026-10-15

The native Squad menu command no longer opens profiles or uses OCR. Results
on the pinned build, preview mode only (nothing billed):

| Check | Result |
| --- | --- |
| Own squad via Squad menu | 29 of 29 rows read with display names; 0 missing (all indexed); "All 29 squad players already have portraits" |
| Roster completeness | Table row count (29) and visible range drive scrolling; reads opened mid-table and at the bottom both completed |
| Another club (LA Galaxy, run directly) | 22 of 22 rows; 4 not indexed; prompts built by ID for all 4 (e.g. Mauricio Cuevas, 19, American, Defender (Right), LA Galaxy) |
| Prompt fields | All 29 own-squad players produced valid prompts, including loanee Freire (club Inter Miami) and previously OCR-only Tyler Hall |
| Stale index | With Tyler's RegenerateFM mapping removed and the skin reloaded, his profile showed the silhouette but his key stayed indexed: treated as present (safe direction). Mapping restored, graphics byte-identical |
| Timing | About 7 s for the first pass (cells, portrait index, date), about 5 s per extra pass after a scroll; 22-33 s overall |

Also fixed: alternate-name records for first names of any length (Tyler,
Lawson, Robbie, Benjamin), type-5 players (Diego Gómez), and the 0x40 record
marker flag. Ian Fray's alternate-name record is only sometimes resident, so
his open-profile read can still fall back to OCR; the squad command uses the
table's display name instead.

Not yet run: a billed squad batch through this path, and Stop after current
face with it.

## Update face: 2026-10-15

**Update face** sits below the generate command in each menu. It sends the
person's current face to the image edit endpoint (`/v1/images/edits`, the
same model, size, quality and transparent background) with a prompt that keeps
the person and changes only their age and kit. No billed request has been made
through it yet.

- [x] FM24 skin popup shows **Generate new face** and **Update face**. A gdb
      capture showed both rows post `FMFG`; the event's `slct` property holds
      `FMFG` or `FMFU`. The DLL logs `item=generate|update`.
- [x] FM24 preview, Messi (7458500): DLL `item=update`. The companion built the
      update prompt (age 35, Inter Miami kit) and found the bundled photo
      `pictures/players/normal/usa/inter miami/lionel messi.jpg` with its mask.
- [x] FM24 preview, Edison Azcona (2000045863): the existing RegenerateFM portrait
      was found. **Generate new face** still sent the full profile prompt.
- [x] Lookup: 12,295 bundled person mappings in `graphics.fmf`, about 0.4 s
      for the first lookup and then cached. RegenerateFM's folder wins, then the
      user's other graphics, then the bundled photos.
- [x] FM26 (stand-in server, no API): **Update face** appears below
      **Generate face** in Actions and below **Generate new face** in the
      portrait menu. Max Crocombe's photo was captured (191×250 RGBA,
      correct orientation and colours, transparent background), sent, saved
      and reloaded in game. For Ryan Kelly (silhouette) both items were
      greyed out and clicking them sent nothing. The FM26 graphics folder was
      restored byte-identical afterwards.
- [x] FM26: `ImageConversion.EncodeToPNG` fails in the IL2CPP interop layer
      ("Instances of abstract classes cannot be created"), so the plugin
      reads the pixels back and encodes the PNG itself.
- [x] FM24's Update face row is now greyed out for a person with no face
      (see "FM24 profile menus" below). Without the companion's answer the
      row stays enabled and the companion refuses before any request.
- [ ] Billed update requests in both games, checking likeness and that the
      kit and age change.

## In-game dialogs and progress (native DLL): 2026-10-15

FM24 itself now shows RegenerateFM's progress, confirmations, results and errors
(see the native README). All checks were in preview mode with no API
requests. Each was live on the Inter Miami save, plus LA Galaxy's squad
table, which has 4 players with no face.

- [x] RE: FM24's quit question is opened by `0xbdef100` (via `0x10bfcc0`)
      with record keys titl/text/acte/noet/widt/heig and the manager as
      owner. A gdb trace showed No posts `NOEX` to the manager.
- [x] Yes/No question: Yes and No each returned to the companion
      (`REGENERATEFM24_DIALOG_YES`/`_NO`).
- [x] OK message: the No button is hidden and Yes is relabelled OK. The squad
      command on Inter Miami showed "All 29 squad players already have
      portraits." in FM24.
- [x] Squad batch on LA Galaxy, run in-process with the companion's code
      (the Squad-menu item exists only on your own squad):
  - [x] **No** at "Preview faces?" made no requests.
  - [x] **Yes** showed a progress dialog, updated in place for all 4
        players, which then became "Previewed 4 squad faces." with OK.
  - [x] **Stop after this face**, chosen during face 2, finished that face
        and stopped before face 3.
- [x] Text updates fail safely after a skin reload: the dialog is gone, so
      `PROGRESS_NOT_SET` is logged; no crash.
- [x] Portrait progress on Messi: Generating…, Loading…, Failed, then
      cleared, all inside the RegenerateFM popup. When no profile was open, the
      companion fell back to the X11 overlay.
- [x] Companion error, from Update face on another club's player, shown in
      an FM24 OK dialog.
- [x] A DLL notice with the companion stopped ("companion unavailable")
      appeared as an FM24 dialog, not a Windows message box.
- [ ] Billed batch: a dialog per face after each reload, and Stop between
      faces.

## Billed native squad batch and Update face read: 2026-10-15

- [x] Billed batch on LA Galaxy (4 of 22 missing), run through the native squad
      code with the companion's `generate_for_profile`. The FM24 Yes/No
      confirmation was answered Yes. Mauricio Cuevas (20049243) and Julián Aude
      (14223129) were generated (24 s and 19 s), saved and reloaded in game,
      both in LA Galaxy's white home kit. The progress dialog reopened for face
      2 after face 1's skin reload. Stop during face 2 finished that face, and
      no third request was made.
- [x] Fixed: a stopped batch now ends with "Stopped after n of m squad faces."
      Before, nothing was shown once the reload had closed the progress dialog.
- [x] Update face reads only the verified name, age and club from memory (read
      twice to catch navigation), so other clubs' players with partly known
      attributes work. Preview: Brad Guzan (980785, Atlanta United), age 38,
      Atlanta United kit, bundled photo `usa/atlanta/brad guzan.jpg`.
- [ ] Billed Update face in either game.

## No X11 in the native path: 2026-10-15

The native mode no longer scrolls the Squad table, takes screenshots, runs
OCR, looks up the game window or activates it. Evidence comes from a preview
companion whose X11 entry points (`Xlib.display.Display`, `capture_window`,
`find_game_window`, `_activate_game`, `_click`, `_scroll`, `reload_skin`) all
raise; it started without a window and logged no X11 call.

- [x] Squad rows from the table's row model (table+0x6e8 → +0x18) and FM24's
      person table (found from drawn rows, base 0x1c690040 on the test save):
      Inter Miami 29 of 29 with rows 1–5 undrawn (visible 6–29), in about 1 s
      beyond the existing scan. Through FM24's Squad menu with rows 1–2 and
      28–29 undrawn: "0 missing of 29", shown in FM24's dialog.
- [x] LAFC (24 rows, 11 missing) through the native squad code with the X11
      helpers raising: confirmation Yes, 11 previews (Hugo Lloris, age 36,
      LAFC, …), "Previewed 11 squad faces."; X11 calls: none.
- [x] Generate preview, Messi (7458500): biography headline from the `biot`
      widget ("Barcelona legend Lionel Messi is truly one of world soccer's
      global superstars…"), identical to what FM24 shows.
- [x] Generate preview, Jorge Theiler (14008037, staff): biography headline
      "Club Atlético Newell's Old Boys icon Jorge Walter Theiler was born…".
- [x] Update face preview for Messi (bundled photo) and Theiler (RegenerateFM
      portrait), name/age/club from memory.
- [ ] "All Players" on another club's Players screen includes a row the squad
      reader refuses ("does not show a validated FM24 player"); unchanged.
- [ ] A billed run through the X11-free path.

## Billed single face through the X11-free path: 2026-10-15

- [x] Gabriel Pec (19397595, LA Galaxy, no face before): a billed **Generate
      new face** through FM24's portrait popup. The companion ran with every X11
      entry point patched to raise; it logged 0 X11 calls. The face was saved and
      reloaded in game 37 s after the click.
- [x] Fixed: some name records carry a third string, the common name
      ("Gabriel" "Fortes Chaves" "Gabriel Pec"). The decoder rejected these, and
      the first attempt was refused safely with "found 0" and no request. It now
      accepts the common name and uses "first surname".

## X11 code removed from FM24: 2026-10-15

The FM24 companion now requires the native DLL and has no X11 code.
- **Removed modules:** `overlay.py`, `squad_progress.py`, `menu_overlay.py`,
  `portrait_menu.py`, `click_listener.py`, `read_profile.py`,
  `capture_profile.py`, `capture_staff.py`, `refresh.py`, `native_portrait.py`
  and `research/probe_server.py`.
- **Removed modes and fallbacks:** the `--direct-clicks`, `--window-id` and
  menu-overlay options, the scrolling squad reader, the OCR biography
  matching, the screen-profile enrichment, and the zenity dialogs.
- **When FM24 can't show a dialog:** the message goes to the companion
  terminal, and an unanswered squad confirmation counts as Cancel.
- **When the in-game reload fails:** the face stays saved, and the result
  says to use Preferences > Reload Skin.
- **Dependencies:** `fm24/requirements.txt` is now Pillow and zstandard.
  `rapidocr`, `onnxruntime`, `python-xlib`, Tkinter and zenity are no longer
  needed. The release file list was updated.
- [x] 90 tests pass; pyflakes is clean.
- [x] Live re-check in preview with the slimmed companion. Its process maps no
      X11 or ONNX libraries.
      - **Generate** on Gabriel Pec: the prompt includes the biography shown
        ("Gabriel Pec is a name that will probably be more familiar…").
      - **Update face:** used his new RegenerateFM portrait.
      - **Generate missing squad faces…** from FM24's Squad menu: 0 of 29
        missing, shown in FM24's dialog.

## Billed Update face: 2026-10-15

- [x] **FM24, Edison Azcona (generated face):** the same face, hair and features
      came back in Inter Miami's pink home kit, reloaded in game. He is on loan at
      Las Vegas, but the club comes from his contract, so the kit is the parent
      club's.
- [x] **FM24, Drake Callender (bundled photo):** a close likeness, now in a green
      goalkeeper kit, reloaded in game.
- [x] **FM26, Max Crocombe (game photo copied from memory, 191×250):** a close
      likeness in a green goalkeeper kit, saved by `regeneratefm.service` and reloaded
      in game. This also ran the final plugin build in game for the first time.
- [x] **Refused, FM24, Lionel Messi (bundled photo):** OpenAI returned "HTTP 400:
      Your request was rejected by the safety system." The error showed in an
      FM24 dialog, and nothing was saved. Edits of very famous people's real
      photos are refused; lesser-known players' photos were accepted.
- [ ] **FM26 Actions menu timing:** opened about 5 s after the profile, the menu
      had no RegenerateFM items (the header ID was not yet readable); reopening it
      showed them.

## Loan club kit (FM24): 2026-10-15

- [x] Player prompts use the club the player plays for now (`record-0x154`): the
      loan club during a loan, else the contract club. Staff keep the contract
      club, and the header check still uses the contract club, which the title
      shows.
- [x] Preview of Edison Azcona (on loan from Inter Miami): Generate says "plays
      for Las Vegas" with Las Vegas's kit, and Update face uses Las Vegas's kit.
      The squad reader gives Las Vegas for him and Inter Miami for Messi.

## Loan club kit (FM26): 2026-10-15

- [x] The header's `PlayingTeam` is the contract club; for a loaned player the
      header also shows the loan club in `TeamAwayFromOrTo`, which the plugin now
      uses when it is visible. Found by logging the header's element tree.
- [x] With a stand-in server that records the prompt and refuses (no API call,
      nothing saved): Kane Thompson-Sommers (MK Dons, on loan at Bristol Rovers)
      gets "plays for Bristol Rovers" and Bristol Rovers's kit. Max Crocombe
      keeps MK Dons.
- [ ] `hotreload/bin` still holds an older plugin build; rebuild it before a
      release.

## Windows memory backend and platform check: 2026-10-15

- [x] `process_memory.py` chooses a Linux backend (`/proc`, `process_vm_readv`)
      or a Windows backend (`ReadProcessMemory`, `VirtualQueryEx`, a Toolhelp
      process snapshot, and `EnumProcessModulesEx` for the fm.exe base).
      Process lookups in `live_memory`, `existing_portrait` and the installer
      go through it.
- [x] Windows Python 3.11.9 (the python.org embeddable build) ran under
      Proton's Wine in FM24's prefix, alongside the live game:
      - found fm.exe (pid 292), the image base 0x140000000 with "MZ", and
        1,214 MB of writable memory in 256 regions
      - read Federico Redondo's full profile (6.8 s) and the shown biography
      - read all 29 Squad rows (8.9 s)
- [x] The companion refuses to start on macOS with an explanation. The
      installer gives the Proton launch option only off Windows.
- [ ] Run on real Windows: the DLL, the installer and the companion.

## Reserve and youth players; All Players view: 2026-10-16

- [x] **Record types:** person records of type 0 or 1 belong to staff and to
      reserve or youth players. Player versus staff is now decided by the
      record's position ratings (players 1–20 with a 20, staff all zeros), not
      by the type alone. Before, type-0 players would have got a staff-style
      prompt, and type-1 players were refused.
- [x] **Header club:** a reserve player's header names the team they play in
      ("LA Galaxy II"), so the header check accepts the contract club or the
      playing club.
- [x] **Missing names:** when a record has no name string and no
      first/surname record, the name comes from FM24's
      "object: <id> - <name>" texts, which exist for people just viewed, but
      only when exactly one name is found. Nolan Anderson (16, LA Galaxy II
      goalkeeper, type 1) now reads correctly.
- [x] **All Players view (LA Galaxy, 37 rows):** 34 players read and 13 lack
      faces. 3 undrawn players whose names FM24 hasn't loaded are left out, and
      the confirmation says so instead of refusing the whole command.

## FM26 Actions menu timing: 2026-10-16

- [x] The RegenerateFM items are added whenever the profile header exists. The
      person ID is read when an item is chosen (and must match the ID shown
      when the menu opened, if one was shown); a profile still loading gets
      "The profile is still loading. Choose it again." Update face is greyed
      out only once the portrait is known to be the silhouette.
- [x] Live, with the recording stand-in (no API call, nothing saved): Actions
      opened 0.6 s after Craig MacGillivray's profile showed both items, and
      Update face sent the update prompt (age 32, MK Dons kit) with his
      173×250 photo.

## FM24 profile menus: 2026-10-16

All checks ran live in FM24 24.4.2 with a `--preview-only` companion. No API
requests were made.

- [x] **Update face greyed out:** when a menu opens (`meop`), the DLL asks the
      companion `REGENERATEFM24F` whether the person has a face and sets the row's
      `disa` property. Portrait popup: Edison Azcona (2000045863) and Ryan
      Sailor (2000155624) showed `face=yes` with the row enabled. Federico
      Higuaín (956737, staff, silhouette) showed `face=no` with the row greyed
      out, and clicking it sent nothing.
- [x] **Overview tab menu:** Generate new face and Update face are appended
      below Follow, on the first opening. Azcona: Generate and Update sent
      the profile and update previews (the update preview named his RegenerateFM
      portrait). Jorge Theiler (14008037, staff): Generate sent the staff
      preview. Higuaín: Update face greyed out and ignored. Clicking the tab
      label itself sends nothing. `regeneratefm24.log` names the person for each.
- [x] New pinned layout: popup root item `+0x4f8` with rows at `+0x38`, and
      the Overview tab class vtable `0x5679168`
      (`profileMenu.overviewTabVtableRva`).
- [ ] Higuaín's Generate is refused: "Invalid FM24 club object." He is
      contracted to the reserve team (Inter Miami II). This predates the menus.
- [x] Fixed: after an FM24 restart, the first clicks could be ignored or
      answered from the previous run. Wine reused the Windows process ID, so
      the companion read serial 1 of the new run as a replay. Its cached
      "accepted" was sent without starting anything (Azcona at 01:37), or a
      lower serial was dropped and FM24 said the companion was unavailable
      (01:48). Click packets now name the run by the DLL's start time in
      milliseconds instead of the process ID.
- [x] Face checks took 1–38 ms. One check timed out (102 ms, the row stayed
      enabled): it was the first lookup needing FM24's bundled-picture index,
      which takes about half a second to build. That index is now built in the
      background, and the check sends no answer until it is ready. After a
      companion restart with FM24 running, Higuaín's check answered `no` in
      2 ms. Theiler's popup Update face sent the update preview.
