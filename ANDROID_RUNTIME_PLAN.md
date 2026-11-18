# Original UABEA Android Runtime

This branch deliberately starts from the upstream `nesrak1/UABEA` source tree. The Android application must host the **unmodified UABEAvalonia desktop application**, not replace its UI or reimplement its editing engine.

## Runtime architecture

1. Build the upstream `UABEAvalonia` project for Linux x86-64.
2. Run its x86-64 .NET 8 runtime through Box64 on ARM64 Android.
3. Provide a Linux userspace (proot/rootfs), X11 display server, and a local display bridge inside the Android app.
4. Embed or provide the UABEA publish payload, class database, and plugin directory.
5. Grant access to user-selected Android files through Android's Storage Access Framework and expose them to the Linux runtime without silently copying edited files to a different location.
6. Verify open, edit, replace, save, and reopen against real Unity assets and bundles.

## Current implementation stage

The workflow in `.github/workflows/uabea-original-linux-runtime.yml` builds and packages the upstream desktop application for Linux x64. `runtime/launch-uabea.sh` is the Linux-side Box64 launcher.

This is a runtime-validation stage, **not yet a finished Android APK**. The Android host, rootfs bootstrap, display bridge, SAF file bridge, and end-to-end device tests remain required. Do not label a Linux payload artifact as an Android release.

## Why Box64 is used

The upstream desktop target is Linux x64 and uses Avalonia Desktop. Box64 provides x86-64 user-space instruction translation on ARM64; it does not itself provide Linux libraries, .NET, an X server, Android storage integration, or touch input. Those pieces must be packaged and tested separately.

## Acceptance checks

- Original upstream Avalonia UI starts on an ARM64 Android device.
- Touch input maps correctly to pointer and keyboard events.
- Android file picker can open supported files.
- Replace operations update the in-memory workspace and preview.
- Save writes the selected original file or an explicitly chosen output file.
- Saved files reopen successfully in the original UABEA build.
- Large files do not cause silent data loss.
