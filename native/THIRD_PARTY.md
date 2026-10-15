# Native playback components

Nightingale Desktop uses libmpv, which incorporates FFmpeg and other third-party libraries in this Windows binary distribution.

| Item | Pinned value |
| --- | --- |
| Build provider | https://github.com/shinchiro/mpv-winbuild-cmake |
| Release | https://github.com/shinchiro/mpv-winbuild-cmake/releases/tag/20261013 |
| Asset | `mpv-dev-x86_64-20261013-git-9d144e57b6.7z` |
| SHA-256 (GitHub release asset digest, also checked locally) | `10860837bc04491862c1e66a95dc1af1d7886d69757ce447a09e1e49ae993b52` |
| mpv source revision | https://github.com/mpv-player/mpv/tree/9d144e57b6 |
| Original DLL name | `libmpv-2.dll` (distributed here as `mpv-2.dll`) |

mpv is GPL v2 or later by default; certain builds can be LGPL v2.1 or later when all relevant GPL components are excluded. This project does **not** claim that the pinned third-party build is an LGPL-only build. The bundled copyright statement and both upstream license texts are preserved in this directory. Full attribution and exact component licenses remain in the corresponding upstream source trees and build definitions. Relevant projects include:

- mpv: https://github.com/mpv-player/mpv — GPL v2+ / LGPL v2.1+ according to build configuration.
- FFmpeg: https://ffmpeg.org/legal.html — LGPL v2.1+ or GPL depending on enabled components; the provider's build configuration determines the applicable license.
- libass: https://github.com/libass/libass — ISC.
- libplacebo: https://code.videolan.org/videolan/libplacebo — LGPL v2.1+.

The downloaded development archive contains only headers, an import library, and the DLL; it does not contain a complete dependency license bundle or corresponding source archive. The present download script is for reproducible local development. Before public redistribution of a package containing this binary, establish the applicable combined license and provide the corresponding-source and complete attribution material required by that license. The release source recipe is maintained by the build provider linked above.
