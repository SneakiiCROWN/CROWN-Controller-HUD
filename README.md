# CROWN-Controller-HUD

A Windows controller dashboard and tray companion for connection state, battery information and alerts.

**Current prerelease: V4 battery and USB/Bluetooth handoff test.**

## Download and play

1. [Download the Windows ZIP](https://github.com/SneakiiCROWN/CROWN-Controller-HUD/releases/download/hud-v4-public-test-20261020/CROWN-Controller-HUD-Windows-V4-Test.zip).
2. Right-click it, choose **Extract All**, then open the extracted folder.
3. Double-click **CrownControllerHUD.exe** and keep its supporting files together.

No coding tools are needed. GitHub’s **Code → Download ZIP** contains source, not the playable program.

## What is in this version?

The current local V4 source includes live connection monitoring, battery backoff and warning policies, USB/Bluetooth handoff handling, settings and the CROWN notification renderer.

## Checked, and still unfinished

61 automated tests passed. A fresh self-contained Windows x64 build launched and responded; its existing settings hash stayed unchanged.

Physical controller detection, battery accuracy, connection handoff and notifications during real games still need hardware testing. A successful launch does not establish controller compatibility.

## Updating without losing progress

Extract each Windows version into a new folder and keep the old folder for rollback. Keep your existing settings and save folders; do not reset or delete app data to update.

## What is next?

Next work follows the existing development notes: verify more controller/connection combinations, then address battery and notification issues found on hardware.

## Requirements and credits

64-bit Windows 10/11. The .NET runtime is included; no SDK or separate .NET install is needed. Kenney assets are CC0, Oxanium uses SIL OFL, Lucide uses its bundled licence, and software dependency notices are in ThirdPartyNotices.

Original project material is source-available with the permissions in LICENSE.txt; public availability is not a blanket open-source or asset-reuse licence. Third-party licence rights remain intact.

## Found a bug?

[Open an issue](https://github.com/SneakiiCROWN/CROWN-Controller-HUD/issues) with the release version, Windows/Android version, relevant controller or phone model, steps to reproduce and what happened. Share screenshots or a short recording if useful; leave personal saves, account details and private logs out of public reports.
