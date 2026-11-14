# UABE Avalonia — Android port

UABE Avalonia is the Android-focused port of UABEA (Unity Assets Bundle Extractor Avalonia). It reuses the upstream UABEA/AssetsTools.NET asset-reading code and provides a phone-oriented Avalonia interface, Android file access, and mobile asset previews.

**This repository is a port of UABEA, not a new implementation claiming ownership of the original program.** The original authors retain their copyrights and the upstream license.

## Project identity

- Android application name: **UABE Avalonia**
- Repository: https://github.com/g5-j/UABEA-Mobile
- Upstream UABEA: https://github.com/nesrak1/UABEA
- Original UABE: https://github.com/SeriousCache/UABE
- Android project: `UABEA.Android`
- Android package ID: `com.g5j.uabeamobile` (kept stable so new APKs can update existing installations)
- Minimum Android version: Android 12 / API 31
- Supported ABI: ARM64 (`arm64-v8a`)

## Features

- Open UnityFS, UnityRaw, and UnityWeb AssetBundles.
- Open serialized Unity `.assets` files and browse their contents.
- Search entries and assets, filter by asset type, and load large lists in pages.
- Inspect asset names, class types, Path IDs, byte sizes, and file offsets.
- Export and open supported AssetBundle entries.
- Preview supported Texture2D, Sprite, Font, TextAsset, AudioClip, Mesh, Material, Cubemap, and GameObject/Prefab hierarchy data.
- Import matching external Unity streams such as `.resource` and `.resS` when a requested preview needs them.
- Rotate and zoom 3D previews, open fullscreen viewers, and inspect Cubemap faces.
- Display Material saved shader properties and render a referenced Texture2D on the Material preview sphere when the reference and data are available.

Feature availability depends on Unity version, serialized type-tree data, asset layout, texture format, and external resource files. Unsupported or incomplete data must be reported instead of being silently replaced with unrelated data.

## What this port is not

The Android app is currently a read-only browser and preview tool. It does not provide full desktop UABEA parity. Editing serialized values, saving modified assets, all desktop plugin workflows, Cpp2IL, dependency editing, and multi-window desktop workflows are not included in this Android host.

## How to use the app

1. Install the signed `UABE-Avalonia-<version>.apk` from the GitHub Releases page.
2. Launch **UABE Avalonia** and press **Open Unity file**.
3. Select a Unity bundle or serialized `.assets` file using Android's file picker.
4. Browse the file entries or assets. Use Search and the type filter to narrow the list.
5. Tap an asset to view its metadata and any supported preview.
6. If a preview requires an external stream, the preview card identifies the expected filename. Press its resource button and select the matching `.resource` or `.resS` file. The app should not open the file picker merely because you entered an asset details page.
7. Use the preview controls to rotate, zoom, open fullscreen, toggle wireframe, or inspect Cubemap directions where available.
8. Use the in-app Back button or Android Back to return.

### External resource files

Some Unity assets store image, audio, vertex, or index data outside the serialized `.assets` file. Their metadata specifies a path, offset, and size. The exact referenced resource is required; an unrelated file with a sufficiently large byte range is not a valid substitute. When the required stream is missing, select the matching sidecar file from the preview card.

## Build from source

Requirements:

- .NET 8 SDK
- Android SDK and a supported Java toolchain
- .NET Android workload
- Android NDK `27.2.12479018` and CMake `3.22.1` for the native AssetStudio texture decoder

Install the workload:

```bash
dotnet workload install android
```

Publish an unsigned local ARM64 APK:

```bash
dotnet publish UABEA.Android/UABEA.Android.csproj \
  -c Release \
  -f net8.0-android31.0 \
  -r android-arm64 \
  -p:AndroidPackageFormat=apk \
  -p:PublishTrimmed=false \
  -p:RunAOTCompilation=false
```

The release workflow builds and signs the distributable APK. It requires these repository Actions secrets:

- `ANDROID_KEYSTORE_BASE64`
- `ANDROID_KEYSTORE_PASSWORD`
- `ANDROID_KEY_ALIAS`
- `ANDROID_KEY_PASSWORD`

Keep the signing keystore and passwords private and backed up. Every update distributed to existing users must be signed with the same keystore identity or Android will not install it as an update.

## Versioning and releases

Each successful Android release workflow run generates a version number, creates a matching `v<version>` Git tag, and publishes a GitHub Release. The Release attaches a plain signed ARM64 `.apk` file. The workflow also uploads a separately compressed Ultra 7z archive as a GitHub Actions artifact; the APK attached to the Release is not compressed.

## Android dependencies and third-party notices

The Android application directly uses the following libraries and components:

| Component | Use in this port | License / notice |
|---|---|---|
| Avalonia and Avalonia.Android | Android UI and platform host | MIT — https://github.com/AvaloniaUI/Avalonia |
| Material.Icons.Avalonia | UI icons | MIT — https://github.com/AvaloniaUI/Material.Icons |
| AssetsTools.NET and AssetsTools.NET.Texture | Unity serialized asset parsing and texture structures | MIT — https://github.com/nesrak1/AssetsTools.NET |
| AssetRipper.TextureDecoder | Managed texture decoding | MIT — https://github.com/AssetRipper/TextureDecoder |
| Fmod5Sharp | FMOD bank and sample decoding for AudioClip preview | MIT — https://github.com/SamboyCoding/Fmod5Sharp |
| AssetStudio Texture2DDecoderNative | Native ARM64 block-compressed texture decoding | MIT; retain `Texture2DDecoderNative/ASSETSTUDIO-LICENSE.txt` — https://github.com/Perfare/AssetStudio |

The Android app also uses Android platform APIs supplied by the Android SDK. NuGet packages may bring transitive dependencies with their own licenses; their upstream notices and terms continue to apply. Desktop-only dependencies listed by upstream UABEA are not claimed as Android app dependencies unless they are actually referenced by `UABEA.Android`.

## Copyright and license

UABEA and this port retain the upstream MIT license and copyright notices. The repository's root `license` file is preserved. Third-party notices distributed with this project, including the AssetStudio native decoder notice, must remain available in source distributions and derived builds. Components with separate license terms remain subject to those terms.

This software is provided "as is", without warranty. Unity is a trademark of Unity Technologies; Unity names are used only to identify supported file formats and technologies. This project is not affiliated with or endorsed by Unity Technologies.