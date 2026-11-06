# Third-party notices

Pirsa is licensed under the GNU General Public License v3.0 or later (see
`LICENSE`). It bundles or links the components below. This file must ship with
every distributed build (portable ZIP, MSIX, and Store package).

Generated from `dotnet list Pirsa/Pirsa.csproj package --include-transitive`.
Keep it in sync when dependencies change.

## Bundled or linked components

| Component | Version | License | Role |
| --- | --- | --- | --- |
| libvlc / VLC | 3.0.24 | LGPL-2.1-or-later | Media engine (`libvlc.dll`, `libvlccore.dll`, `plugins/`) |
| libmpv / mpv | 0.41.0 | LGPL-2.1-or-later | Optional media engine (`libmpv-2.dll`) |
| LibVLCSharp | 3.10.2 | LGPL-2.1-or-later | .NET bindings for libvlc |
| LibVLCSharp.WinUI | 3.10.2 | LGPL-2.1-or-later | WinUI video surface |
| CommunityToolkit.Mvvm | 8.4.2 | MIT | MVVM source generators |
| Microsoft Windows App SDK | 2.6.4 | Microsoft Software License Terms (distributable code) | WinUI 3, Mica, app lifecycle |
| .NET Runtime | 10.0 | MIT | Self-contained runtime |
| Windows SDK Build Tools | 10.0.28000.2957 | Microsoft Software License Terms | Build-time only, not redistributed |

### libvlc / VLC

The native media engine is distributed as separate DLLs and a `plugins`
directory. Its license requires that:

1. This notice and the full LGPL-2.1 text are distributed with the binaries.
2. The corresponding source code is made available.
3. Users can replace the library with a modified version.

**Full LGPL-2.1 text:** <https://www.gnu.org/licenses/old-licenses/lgpl-2.1.html>

**libvlc 3.0.24 source:**
<https://download.videolan.org/pub/videolan/vlc/3.0.24/>

To satisfy point 3, Pirsa is also published as a **portable ZIP**. A signed
MSIX package cannot be modified in place, so the portable build is the
supported way to substitute a rebuilt `libvlc.dll` / `libvlccore.dll` /
`plugins/`. Pirsa does not modify any libvlc binary; the DLLs are loaded
unmodified from the application directory.

### libmpv / mpv

The optional mpv backend is distributed as a separate `libmpv-2.dll` in the
`libmpv/win-x64` or `libmpv/win-arm64` folder. The package used is
`Endpne.LibMPV.Windows` 0.41.0, which builds mpv under
LGPL-2.1-or-later. The same LGPL obligations apply: this notice and the full
license text ship with the binary, the source is available, and the DLL can be
replaced in the portable build.

**Full LGPL-2.1 text:** <https://www.gnu.org/licenses/old-licenses/lgpl-2.1.html>

**mpv source:** <https://github.com/mpv-player/mpv>

**Windows libmpv package:** <https://www.nuget.org/packages/Endpne.LibMPV.Windows>

The libvlc build also contains FFmpeg and other codec modules, each under its
own license. The `plugins` folder is shipped byte-for-byte as published by the
VideoLAN project.

If you distribute a modified libvlc, you must publish your changes under the
LGPL and say so in this file.

### Microsoft Windows App SDK

Redistributed under the Microsoft Software License Terms supplied with the
NuGet package. The full terms are at
<https://github.com/microsoft/WindowsAppSDK> and in the package's
`license.txt`.

## Remote services (not bundled)

Pirsa ships **no** content, catalog, or addon. It talks only to services you
configure yourself. No data is sent anywhere else.

| Service | Default? | Purpose |
| --- | --- | --- |
| Cinemeta (`v3-cinemeta.strem.io`) | Yes | Catalogs and metadata |
| OpenSubtitles v3 (`opensubtitles-v3.strem.io`) | Yes | Subtitle search |
| Stremio addons you add | No | Catalogs, metadata, streams, subtitles |
| IMDb (`www.imdb.com`) | No | "Open on IMDb" link only |

Pirsa is not affiliated with any of these services. Their terms and privacy
practices apply to the requests you make to them.

## Artwork

The Pirsa icon, tiles, and splash screen are original works created for this
project from `assets/icon.svg`. No third-party artwork, poster, backdrop, or
font file is bundled. Fonts and theme brushes come from Windows itself.

## Sample notices for the About screen

```
Pirsa — GPL-3.0-or-later
Media engines: libvlc 3.0.24, LGPL-2.1-or-later (videolan.org)
               libmpv 0.41.0, LGPL-2.1-or-later (mpv.io)
Bindings: LibVLCSharp, LGPL-2.1-or-later
Windows App SDK © Microsoft Corporation
Icons and UI original to the Pirsa project
Full notices: THIRD-PARTY-NOTICES.md
```
