# Project packages

**File > Package project with linked media** writes an `.inkpkg` ZIP containing a
validated InkMotion project, linked video, images, audio, proxies and colour LUTs,
including those inside compound timelines and noise-profile source references.
Repeated references share one copy; identically named files from different folders
remain distinct. Originals and the current project are unchanged. Missing files
stop packaging: relink them first. VST3 inserts must be removed from a project copy
or rendered first; executable plug-ins and their licences are not redistributed.
Embedded artwork, subtitles, meshes and timeline settings remain in the project.

Use **Open project** to open a package and choose an extraction parent folder.
InkMotion creates a unique new subfolder and never extracts over an existing
project. The extracted `project.inkmotion` uses relative media links, so the whole
folder can be moved and reopened. Saving retains relative links within its folder;
outside links remain absolute. Keep the folder and media together.

Package version 1 has `manifest.json` (`Version`, `Media` entries with `Name`,
`Length`, `Sha256`), `project.inkmotion` and `media/000001.ext` files. Archive paths
must match this closed manifest; external/traversing project references, duplicate
entries, missing files, bad checksums, unsupported versions and malformed projects
are rejected. Extraction does not use ZIP filesystem metadata or arbitrary paths.
Failed extraction removes only its newly created subfolder. Limits are 10,000
entries, 64 MiB per JSON document and 1 TiB total uncompressed media; sufficient
free disk space is required. Media bytes are copied, not transcoded.

## Picture timeline interchange

The File menu imports/exports **Final Cut Pro 7 XML** (`xmeml` v5 export,
v4/v5 import), not modern Apple FCPXML. The supported subset is one sequence,
directly under `xmeml` or inside `project/children`, containing progressive,
square-pixel picture cuts on multiple video tracks. Local Windows file URLs,
source in/out frames, timeline start/end frames, gaps, file-ID references,
source bounds and fixed canvas dimensions are retained. The sequence must start
at timecode zero, with no trailing empty duration. Source and sequence frame rates
and dimensions must match. Rates are **24, 30 and 60 fps integral NDF**, matching
the editor's supported rates; 12 fps interchange, 25/48/50 fps, fractional NTSC,
drop-frame, VFR and mixed-rate editing are not supported. Windows supplies source
rate/dimensions during export; use constant-frame-rate sources.

**CMX 3600 EDL** supports one picture track, cuts (`V C`), up to 999 export events,
24 or 30 fps NDF, with source and record timecodes shorter than 24 hours.
Choose the actual frame rate at import: EDL does not encode it. A missing FCM
header is interpreted as NDF; explicit drop-frame headers are rejected.
Source timecodes are interpreted as offsets from the start of each file, **not**
camera/tape timecode. Record timecodes are retained as absolute timeline offsets;
a one-hour record start therefore produces a one-hour leading gap.
`FROM FILE` / `FROM CLIP` path comments and `FROM CLIP NAME` filename comments
resolve media (filename-only links are relative to the EDL folder).
Reel-only events cannot resolve files and are rejected. Export uses `AX` reels
plus media comments. EDL does not retain canvas dimensions or full source duration;
the imported source bound initially ends at the last referenced out point.
EDL export uses ASCII paths; XML supports Unicode paths.

These are deliberately **picture-only** operations. Export explicitly confirms
the omission of embedded audio; imported video clips have embedded audio muted.
Proxy selection, clip/track names, locks, reel names and editor metadata do not
transfer. Audio tracks, ink, titles, subtitles, compounds, markers, multicamera,
HDR management, effects, transforms, scaling, retiming, fades, transitions,
disabled picture tracks/clips and overlaps are rejected, not silently baked or
dropped. Export reports the non-default unsupported clip settings by name.
Subframe trims/durations are rejected rather than rounded. Missing imported media
remains offline and can be relinked in the media bin. Import replaces the project
only after parsing/validation and confirmation; save existing edits first.

### Specification and interoperability evidence

- [Apple's archived xmeml v5 specification](https://developer.apple.com/library/archive/documentation/AppleApplications/Reference/FinalCutPro_XML/Elements/Elements.html):
  `start/end` place a clip in sequence frames; `in/out` select its source range.
- [ASWF CMX 3600 adapter](https://github.com/OpenTimelineIO/otio-cmx3600-adapter):
  edit events, source/record timecodes and common media-path comments.
- Actual InkMotion exports were read using OpenTimelineIO 0.18.1 with the
  `otio-fcp-adapter` and `otio-cmx3600-adapter` packages. Both readers preserved
  source starts 48/24, lengths 72/48, record starts 0/120 and 24 fps, including a
  gap and a media path containing spaces and `&`. Their XML/EDL writer outputs
  were then imported back by InkMotion's tests with those frame values intact.
  This is parser-level evidence, **not** a claim of tested Premiere, Resolve,
  Avid or Final Cut Pro application interoperability.

To reproduce, install those optional Python packages outside the app, set
`INKMOTION_INTERCHANGE_FIXTURES` to an empty scratch folder, run
`PictureInterchangeTests`, run `tests\interop\verify_picture.py <folder>`, then
rerun `PictureInterchangeTests` to import the external-writer outputs. Ordinary
unit tests need no Python installation. These tools are not bundled or downloaded
by InkMotion.
