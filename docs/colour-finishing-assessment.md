# Colour-finishing feasibility and licensing assessment

This records why InkMotion's HSL qualifiers, shot matching, ACES import and camera-RAW development were implemented as they are, what was rejected, and the licences involved. It reflects package 1.0.53.0 (project schema 96).

## Existing colour pipeline

SDR projects composite on 8-bit BGRA Win2D surfaces. Primary grading and 3D `.cube` LUTs are evaluated per clip, then spatial effects and compositing follow. Preview, scopes and the export renderer share the same clip-processing code. HDR projects use a separate half-float path for PQ/HLG sources and exports.

Any new processing therefore had to:

- run inside the shared per-clip path, so preview and export cannot diverge;
- be saved in the project with defaults that leave older projects unchanged;
- avoid claiming scene-linear or wide-gamut precision that the 8-bit SDR compositor cannot retain.

## Decisions

| Feature | Implementation | Rationale | Third-party code |
| --- | --- | --- | --- |
| HSL qualifiers | Hue/saturation/lightness selection with feathering, baked into a 33-point RGB table applied after primary grading/LUT | Fits the existing LUT stage, so preview, scopes and export share it exactly | None |
| Shot matching | RGB mean/standard-deviation transfer from local 128×72 graded samples, gains limited to 0.25–4 | Deterministic, local and explainable; no AI model or download required | None |
| ACES | User-selected OpenImageIO `oiiotool` with OpenColorIO applies the official ACES 1.3 `ACES 1.0 - SDR Video` view of the built-in `cg-config-v2.2.0_aces-v1.3_ocio-v2.4` config to an explicitly labelled ACEScg or ACES2065-1 EXR still, producing a 16-bit PNG editing copy | Genuine ACES output transform at import. Re-implementing the RRT/ODT inside the 8-bit compositor would not be a faithful ACES pipeline | Invoked, not linked or bundled |
| Camera RAW | User-selected LibRaw `dcraw_emu` performs sensor demosaicing (AHD), white balance and exposure into a 16-bit sRGB TIFF editing copy | Real RAW decoding with broad stills coverage; no embedded-thumbnail substitution | Invoked, not linked or bundled |

## Licensing

| Component | Licence | Use by InkMotion |
| --- | --- | --- |
| [OpenImageIO](https://github.com/AcademySoftwareFoundation/OpenImageIO/blob/main/LICENSE.md) | Apache 2.0 (current releases; releases before 2.5 used BSD-3-Clause) | Optional external executable chosen by the user |
| [OpenColorIO](https://github.com/AcademySoftwareFoundation/OpenColorIO/blob/main/LICENSE) | BSD-3-Clause | Used through the user's OpenImageIO build |
| [OpenColorIO-Config-ACES](https://github.com/AcademySoftwareFoundation/OpenColorIO-Config-ACES/blob/main/LICENSE) | BSD-3-Clause | Built into OpenColorIO 2.4+; referenced by its `ocio://` name only |
| [LibRaw](https://github.com/LibRaw/LibRaw/blob/master/COPYRIGHT) | LGPL 2.1 or CDDL 1.0, at the user's choice; includes BSD-style DCB/FBDD/X3F code and MIT-licensed Adobe DNG SDK fragments | Optional external executable chosen by the user |

InkMotion does not link, bundle, redistribute or download any of these components, configurations or any AI model. Because the tools run as separate processes chosen by the user, InkMotion takes on no LGPL relinking or source-offer obligations and needs no third-party notice for them. If a future release bundles or links any of them, their licence texts and notices must be shipped, and LibRaw must remain dynamically linked and replaceable (LGPL) or be used under the CDDL. Users should check the licence of the specific binaries they install, including any optional codecs compiled into them.

Linking the libraries in-process was rejected for this release because it would add native dependency builds for both x64 and x86, LibRaw licence obligations, and a larger attack surface for untrusted RAW and EXR parsing in the editor process. External processes run with cancellation, a five-minute timeout, 2 GiB source and 64 Mi-pixel output limits.

## Blockers and unsupported formats

- **Scene-linear ACES timeline and ACES/HDR export:** blocked by the 8-bit SDR compositor. Supporting it requires a floating-point SDR compositing path and colour-managed working space across all effects, which is a separate architectural change. ACES is offered only as a display transform at import, and is labelled as such.
- **EXR inputs:** RGB/RGBA stills with an explicitly chosen ACEScg or ACES2065-1 interpretation only. No colour-space inference, sequences, deep or multilayer EXR.
- **Camera RAW:** stills only. `.dng`, `.cr2`, `.cr3`, `.nef`, `.arw`, `.raf`, `.rw2`, `.orf` and `.pef` are offered as candidates, but decoding succeeds only for cameras and compression schemes supported by the user's LibRaw build. Automated verification covers an uncompressed Bayer DNG, not every camera or compression variant. Development settings are baked at import; changing them requires reimporting.
- **Motion RAW (Blackmagic RAW, REDCODE R3D, ARRIRAW, Canon Cinema RAW Light, ProRes RAW):** not supported. Decoding requires vendor SDKs or platform decoders under proprietary licence terms, and none is integrated into this project. No such support is claimed.
- **HDR projects:** HSL qualifiers and shot matching are SDR-only and must be cleared before switching a project to HDR.
