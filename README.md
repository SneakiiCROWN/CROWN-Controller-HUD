# CROWN Controller HUD 🎮

A Windows companion app for gaming controllers, made by SneakiiCROWN. It's designed to show connected controllers, battery information and connection alerts without taking over your game.

## Want to try it?

**Windows 10/11 only.** Check [Releases](https://github.com/SneakiiCROWN/CROWN-Controller-HUD/releases) for a portable download.

**There isn't a ready-to-download release attached yet.** The files here are the *source code*, not an installer. If you're helping test, ask me for a tested build instead of expecting the green **Code → Download ZIP** to run.

When a build is posted: download the Windows ZIP, **extract the whole folder**, then open `CrownControllerHUD.exe`.

## What does it do?

- Controller dashboard and tray companion.
- Connection and battery-related features.
- Real hardware compatibility and battery reporting still need testing.

If your controller isn't detected or its battery looks wrong, tell me its model and whether you're using USB, Bluetooth or a wireless adapter.

## Building it yourself (optional)

Install the **.NET 10 SDK** on Windows, then run:

```powershell
dotnet build CrownControllerHUD/CrownControllerHUD.csproj -c Release
```

The executable appears inside that project's `bin/Release` folders. Building isn't the same as packaging a friend-ready release.

See [DEVELOPMENT_STATUS.md](DEVELOPMENT_STATUS.md) for technical details and features that still need checking.

**Status:** In development. Actual controller detection and notifications need more real-device testing.
