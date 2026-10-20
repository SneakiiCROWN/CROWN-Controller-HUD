# CROWN Controller HUD

A Windows app for keeping an eye on your game controllers and their battery status.

**Current version: Windows test build.** It's a real downloadable program, but controller compatibility still needs testing on more PCs.

## Download and open it

1. [Download the Windows ZIP](https://github.com/SneakiiCROWN/CROWN-Controller-HUD/releases/download/hud-test-2/CROWN-Controller-HUD-Windows-Test.zip).
2. Right-click the ZIP and choose **Extract All**.
3. Open the extracted folder and double-click **CrownControllerHUD.exe**.
4. Connect your controller and see what the app detects.

That's it. No coding tools or .NET SDK needed for this download. It's for **64-bit Windows 10/11**. Only run downloads from a source you trust.

**Don't use GitHub's green "Code → Download ZIP" button** if you just want the app. That's the source code.

## What's working, and what isn't proven yet?

- **Built successfully:** the Windows app and its required runtime files are packaged together.
- **Included in the app:** a controller dashboard, tray companion, connection monitoring and battery-related features.
- **Still needs real testing:** detection across controller brands and connection types, accurate battery readings, notifications and how it behaves during games.

I haven't confirmed a specific outstanding bug for every controller model. If something doesn't work, please report it rather than assuming it should.

## What's next?

Better compatibility checks, more reliable battery reporting, and further settings and interface polish. See [development notes](DEVELOPMENT_STATUS.md) for the detailed list.

## Known issues and updates

No specific controller model is confirmed broken in this release, but real-device checks are still outstanding. Battery percentages, Bluetooth detection and pop-up alerts may behave differently on different hardware. The next goal is to verify those and fix any issues we find.

**Updating:** check [Releases](https://github.com/SneakiiCROWN/CROWN-Controller-HUD/releases), download the newer Windows ZIP and extract it into its own folder. Keep the previous version until you've checked the new one. There's no automatic updater yet.

## Found a problem?

[Open an issue](https://github.com/SneakiiCROWN/CROWN-Controller-HUD/issues) and tell me which controller you're using, how it's connected (USB, Bluetooth or adapter), what you expected and what happened.

## For developers only

This is a .NET 10 Windows/WPF project. With the .NET 10 SDK installed, build it using:

```powershell
dotnet build CrownControllerHUD/CrownControllerHUD.csproj -c Release
```

The **Releases** page is for users; the repository files are for people working on the code.
