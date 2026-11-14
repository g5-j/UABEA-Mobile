# Android Port Status

Branch: `android-port`

Application name: **UABE Avalonia**

## Current Android implementation

- Separate Avalonia Android host in `UABEA.Android/`, using .NET 8 and Avalonia 11.0.1.
- ARM64 APK target, minimum Android 12 / API 31.
- Android Storage Access Framework for Unity files and external resource streams.
- UnityFS, UnityRaw, and UnityWeb bundle browsing.
- Serialized `.assets` browsing, asset metadata, search, type filters, and paged lists.
- Bundle entry export/open and read-only asset inspection.
- Texture2D, Sprite, Font, TextAsset, AudioClip, Mesh, Material, Cubemap, and GameObject/Prefab hierarchy previews, subject to format and resource availability.
- Optional import prompts for external `.resource` and `.resS` files instead of opening the picker automatically when an asset details page is opened.
- Touch rotation and zoom for 3D previews, optional linked Material texture mapping, fullscreen preview, and spherical Cubemap navigation.
- App identity uses the display name **UABE Avalonia** and the original UABEA Avalonia launcher icon. Package ID remains `com.g5j.uabeamobile` for upgrade compatibility.

## Release pipeline

- GitHub Actions builds and signs the ARM64 APK.
- Every successful release workflow run assigns a version, creates a matching `v<version>` tag, and publishes a GitHub Release with the plain `.apk` file attached.
- The same run separately uploads an Ultra-compressed `.7z` artifact whose name includes the version and commit SHA.
- Release signing requires the persistent Android keystore secrets documented in `readme.md`.

## Limitations

This Android app is a read-only browser and preview port, not full desktop UABEA feature parity. Editing and saving serialized values, all desktop plugin workflows, Cpp2IL, dependency editing, and desktop multi-window features are not included. Preview support remains dependent on Unity serialization layouts, type-tree data, texture formats, mesh UV channels, and the exact external streams referenced by the asset.

CI validates compilation, native decoder builds, the available Texture2D/Sprite fixture checks, APK signing, and archive integrity. Physical-device preview behavior still requires testing with representative Unity assets.