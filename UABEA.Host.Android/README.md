# UABEA Original Android Host

This is a native Android host shell for the upstream UABEA desktop application. It is intentionally separate from the previous Avalonia mobile editor.

## Implemented in this stage

- Native Android activity and touch-friendly controls.
- Android Storage Access Framework picker; selected files are copied into private app storage.
- Installer for the original Linux x64 UABEA publish payload embedded in the APK.
- WebView display surface prepared for a local noVNC endpoint.
- Runtime preflight that explicitly reports missing components rather than pretending to launch.

## Not implemented yet

- Box64 executable built and packaged for Android ARM64.
- PRoot and a Linux root filesystem.
- Linux x64 .NET 8 runtime in the root filesystem.
- X server + VNC/noVNC service startup and lifecycle.
- SAF-to-Linux shared-file bridge and save-back to the selected document URI.
- End-to-end open/edit/replace/save/reopen testing on a physical Android device.

The app must not be described as a working UABEA emulator until those components are integrated and tested. The original UABEA binaries are unmodified; this host does not reimplement its UI or asset engine.
