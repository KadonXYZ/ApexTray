# ApexTray

A small Windows tray app for the AJAZZ AJ179 APEX mouse and its 2.4G screen dock. It replaces the official AJAZZ Driver(R) window for everyday use.

## Features

- Battery level in the tray icon, with a low-battery notification at a level you choose
- Remap the mouse buttons: mouse clicks, keyboard shortcuts, media keys, DPI functions or disabled
- DPI stages: edit up to 8 stages, pick the active one, and follow the DPI button on the mouse live
- Polling rate (125 to 8000 Hz), lift-off distance, angle snapping and ripple control
- Upload pictures and animated GIFs to the dock screen, and sync the dock clock
- Firmware check against the AJAZZ update server
- Light and dark theme, English, German, French, Spanish and Chinese
- Starts with Windows if you want it to

## Requirements

- Windows 10 or 11 (64-bit)
- The [Microsoft Visual C++ Redistributable (x64)](https://aka.ms/vs/17/release/vc_redist.x64.exe). Most PCs already have it.
- For the small download only: the [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0) (x64)
- An internet connection the first time ApexTray starts, unless the official AJAZZ Driver(R) is installed

ApexTray talks to the mouse through `iot_driver`, AJAZZ's own background service. It does not ship that file. If the official AJAZZ Driver(R) is installed, ApexTray uses its copy. Otherwise it downloads the current version once from the same AJAZZ/RongYuan update server the official app uses, checks that it is signed by Shenzhen RongYuan Technology, and keeps it in `%LOCALAPPDATA%\ApexTray\iot_driver`.

## Installation

1. If the official AJAZZ Driver(R) is installed, close it from its tray icon (right-click, Exit). It must not run at the same time as ApexTray, because only one app can talk to the dock. You don't need to install it.
2. Download ApexTray from the [latest release](../../releases/latest). There are two versions:
   - `ApexTray-<version>-win-x64.exe` (about 0.5 MB) needs the .NET 10 Desktop Runtime.
   - `ApexTray-<version>-win-x64-standalone.exe` (larger) runs without installing anything else.
3. Put the exe in a folder of your choice, for example `%LOCALAPPDATA%\Programs\ApexTray`, and start it. On the first start without the AJAZZ app, the status line shows "Downloading the AJAZZ driver service..." for a few seconds.
4. Windows SmartScreen may warn about an unknown publisher because the exe is not signed. Click "More info", then "Run anyway".
5. Optional: turn on "Start with Windows" on the Overview tab. ApexTray then starts in the tray when you sign in.

To update, quit ApexTray from its tray icon and replace the exe with the new one. Settings are kept.

## Uninstalling

1. Turn off "Start with Windows" and quit ApexTray from the tray icon.
2. Delete the exe and the folder `%LOCALAPPDATA%\ApexTray`.
3. Optional: delete the settings key `HKEY_CURRENT_USER\Software\ApexTray` with the Registry Editor.

## Notes

- Battery: the dock does not always report a charge for the mouse. When it doesn't, ApexTray shows "-" and keeps checking. The official app gets the same blank value in that case.
- Firmware: flashing runs over the USB cable through the official app, so updating firmware needs the AJAZZ Driver(R) installed. ApexTray closes and opens the official app for this, so the two never use the mouse at the same time.
- Low-battery notifications use normal Windows notifications. If the Test button shows nothing, turn on notifications for ApexTray in Settings > System > Notifications.

## Building from source

Needs the .NET 10 SDK.

```
dotnet build ApexTray.sln
dotnet test ApexTray.sln
dotnet publish src\ApexTray -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o publish
```

## Disclaimer

ApexTray is an independent project. It is not affiliated with or endorsed by AJAZZ. It only sends commands the official app also sends, but you use it at your own risk.

## License

MIT
