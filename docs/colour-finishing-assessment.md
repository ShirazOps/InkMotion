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

## HDR10+ and Dolby Vision

Assessed for package 1.0.66.0.

### HDR10+ (implemented)

HDR10+ is SMPTE ST 2094-40 Application 4 dynamic metadata, carried in HEVC as ITU-T T.35 user-data SEI messages. x265 inserts those messages from an "LLC" JSON file passed through its `dhdr10-info` option, reading `SceneInfo` entries strictly in array order from the first frame of each encoder instance. InkMotion therefore generates the metadata itself and hands x265 one slice per resumable segment, indexed from zero:

- **Measurement.** A first render pass measures every final output frame from the same 16-bit PQ Rec.2020 frames sent to the encoder. Each pixel's maxRGB code is counted in a 65,536-bin histogram, so percentiles are exact for the 16-bit signal. Values are decoded through the ST 2084 EOTF and written in the ST 2094-40 0.00001 (0.1 cd/m²) step, clamped to 0–100,000.
- **Scenes.** A scene starts where the 64-bin normalised PQ maxRGB distribution moves by an L1 distance above 0.5 from the previous frame. Every frame in a scene carries that scene's MaxSCL (per-channel maximum), average linear maxRGB and the 1, 5, 10, 25, 50, 75, 90, 95 and 99 % maxRGB levels, as the standard intends metadata to be stable within a scene.
- **Profile.** Profile A only: a targeted system display maximum luminance of 0 and no Bézier tone-mapping curve. Profile B curves are creative tone-mapping decisions that cannot be derived deterministically from measurement, so none are fabricated.
- **Alignment.** The second pass encodes, re-measures every rendered frame and compares it with the first-pass signature (per-channel maxima, maxRGB sum and histogram hash). Any mismatch stops the export without writing a file. Each generated slice is also validated for consecutive frame indices before encoding.
- **Static metadata.** MaxCLL and MaxFALL are set from the measured frames (brightest component, and highest frame-average maxRGB) instead of the mastering peak and an unknown MaxFALL. The mastering display colour volume is unchanged.
- **Interoperability.** Verified with a libx265 FFmpeg build and the independent `hdr10plus_tool` parser: metadata extracted from the exported HEVC matches the generated values for every frame. x265 always writes one processing window, application version 1, a zero fraction of bright pixels and no actual-peak or colour-saturation data, so those fields are not signalled.

Limitations: statistics describe the signal before the encoder's 10-bit 4:2:0 conversion; export takes roughly twice as long because the timeline is rendered twice; hardware encoders, AV1 and HLG projects are not supported, because their FFmpeg paths cannot carry the metadata from a sidecar. ST 2094-40 and the x265 option are openly documented. InkMotion makes no HDR10+ certification or logo claim; that requires the HDR10+ Technologies licensing and certification programme.

User-supplied HDR10+ JSON sidecars remain supported and are validated as before; the two modes are mutually exclusive.

### Dolby Vision (blocked)

Dolby Vision is not implemented. A conformant stream needs reference processing unit (RPU) metadata produced by Dolby's proprietary content-mapping analysis, available only through licensed Dolby software such as the Dolby Encoding Engine or Dolby Vision professional tools, and distributing Dolby Vision content and branding is subject to Dolby's licence terms. x265 and open tools can only carry or edit RPUs created elsewhere; generating RPUs without Dolby's analysis would produce metadata that does not represent a Dolby Vision grade, and InkMotion will not fabricate it.

InkMotion does not include, download, distribute or enable any Dolby encoder or SDK. Support could be revisited only if the user explicitly approves and holds a suitable Dolby licence and a locally installed licensed encoder, which InkMotion would then invoke as an external process.
