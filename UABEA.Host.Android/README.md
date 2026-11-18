# UABEA Original Android Host

This is a native Android host for the upstream UABEA desktop application. The desktop UI and asset engine remain the original Linux x64 UABEA build; this project does not replace them with a rewritten editor.

## Runtime included by the current integration

- Android ARM64 host activity and touch-enabled WebView.
- Android Storage Access Framework import and explicit Save copy export.
- Debian Linux x64 root filesystem with .NET 8, Xvfb, x11vnc, noVNC and websockify.
- Android ARM64 Box64 and statically linked Android PRoot/loader.
- Runtime startup sequence: Xvfb, VNC bridge, then original UABEA.
- Imported files are exposed to the Linux session at `/workspace`.

## Important limitations

The CI build verifies compilation and Linux-side runtime components, but it cannot prove that PRoot ptrace, Android linker namespace access, Box64, and the VNC bridge work on every physical Android device. The Android runtime must be tested on-device before describing the app as fully working.

Open a file with the Android picker, then use the original UABEA File > Open dialog and select `/workspace/<filename>`. After saving in UABEA, use **Save copy** to export the modified workspace file through Android's document picker.

The first launch extracts the Linux rootfs into private app storage and can take several minutes. It also requires substantial free storage. The original UABEA binaries are not modified.
