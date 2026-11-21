# UABEA Original Android Host

This is a native Android host for the upstream UABEA desktop application. The desktop UI and asset engine remain the original Linux x64 UABEA build; this project does not replace them with a rewritten editor.

## Runtime architecture

- Android ARM64 host activity.
- Debian Linux x64 root filesystem with .NET 8, Xvfb and x11vnc.
- Android ARM64 Box64 and statically linked Android PRoot/loader.
- Runtime startup sequence: Xvfb, localhost-only x11vnc, then original UABEA.
- Connect from an external VNC Viewer to `127.0.0.1:5900`. The app does not use WebView, noVNC or websockify.
- The Linux home directory is mounted at `/home/uabea`; it is not redirected to `/workspace`.

## Import and output

1. Use **Open file** in the Android host to copy a Unity file into the private Linux home directory.
2. In the original UABEA window, open `/home/uabea/<filename>`.
3. Save from UABEA using its normal save operation.
4. Select the output directory with **Output folder**. Changed files under `/home/uabea` are synchronized automatically to that directory.

Output synchronization waits for files to become stable and obtains an exclusive read handle before copying. It writes to a temporary document, verifies the copied content with SHA-256, and only then renames it into place. If the selected Android document provider cannot rename documents, the old output is preserved and synchronization reports an error instead of deleting or truncating the existing file. Keep the app open until the sync status reports completion.

The self-test data under `.uabea-selftest` is excluded from output synchronization.

## Installation, diagnostics and limitations

The first launch extracts the original UABEA payload and Linux x64 rootfs into private app storage. Installation is considered successful only after required files and native Android binaries are verified. A failed installation is reported as failed and Start will not attempt to launch a partial runtime.

Runtime diagnostics are stored at:

```
/data/user/0/com.g5j.uabeahost/files/runtime/logs/runtime.log
```

Individual process logs (for example `Xvfb.log`, `x11vnc.log` and `UABEA.log`) are stored in the same directory.

GitHub Actions builds the APK and runs an Android emulator integration smoke test. CI success does not prove compatibility with every physical Android device or every Android document provider. PRoot ptrace behavior, linker namespaces, Box64 performance and VNC connectivity still require testing on the target phone. The original UABEA binaries are not modified.
