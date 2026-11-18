# UABE Avalonia — Android application

This directory contains the Android host and phone-specific interface for the UABEA port. The desktop Avalonia application remains a separate project. The Android app reuses UABEA/AssetsTools.NET asset-reading code and adds Android file access, mobile preview controls, and ARM64 packaging.

## Android application scope

- .NET 8 Android with Avalonia 11.0.1.
- ARM64 APK targeting Android 12 / API 31 and newer.
- Android Storage Access Framework for opening Unity files and importing matching sidecar resources.
- UnityFS, UnityRaw, and UnityWeb AssetBundle browsing.
- Serialized `.assets` browsing, asset metadata, search, type filtering, and paged lists.
- Supported previews for Texture2D, Sprite, Font, TextAsset, AudioClip, Mesh, Material, Cubemap, and GameObject/Prefab hierarchy data.
- Mobile 3D rotation, pinch zoom, fullscreen viewing, Mesh wireframe, and Cubemap face navigation.
- External `.resource` and `.resS` streams are requested from the preview card when the user asks to view the affected asset.

Preview support depends on the asset's Unity version, serialized type tree, payload layout, texture format, and availability of its exact external resource. A successful APK build is not proof that every possible Unity asset layout is supported.

## App identity

- Display name: **UABE Avalonia**
- Package ID: `com.g5j.uabeamobile` (kept stable for in-place updates)
- Launcher icon: original UABEA Avalonia icon from `UABEAvalonia/Assets/uabeavalonia64.png`

## Build

```bash
dotnet workload install android
dotnet publish UABEA.Android/UABEA.Android.csproj -c Release -f net8.0-android31.0 -r android-arm64 -p:AndroidPackageFormat=apk -p:PublishTrimmed=false -p:RunAOTCompilation=false
```

The release workflow signs the APK using the persistent repository Actions keystore secrets. Do not replace the signing keystore between releases.

See the repository root `readme.md` for the port overview, supported workflows, usage instructions, limitations, dependencies, and license notices.