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

## AAF linked-WAV audio interchange

The File menu imports/exports **genuine binary AAF structured-storage files**
through the supplied `Tools\aaf_bridge.py` script and an explicitly selected,
trusted **Python 3 `python.exe`** environment with **pyaaf2** installed.
InkMotion never installs Python, downloads packages, embeds WAV essence, or
substitutes JSON/XML for AAF. The intermediate JSON is private temporary IPC
data, removed when conversion finishes. Output is written to a temporary file
and replaces the selected destination only after successful conversion and a
binary-header check. Conversion has a two-minute limit and bounded diagnostics.

The supported subset is one CompositionMob with zero-origin TimelineMobSlots at
**24, 30 or 60 fps integral NDF**, containing Sound SourceClips and gap Fillers.
Each imported clip becomes an independent InkMotion audio track; each exported
clip gets a composition audio slot. SourceClip chains through MasterMobs and
SourceMobs (including single-component source Sequences) resolve local WAV
NetworkLocators. Source slots may use a different rational edit rate, including
the audio sample rate: offsets are converted without rounding and must ultimately
align to project frames. Both PCMDescriptor and WAVEDescriptor are accepted for
**mono PCM WAV** media. Descriptor/file bounds, source references and cycles are
checked. A missing WAV can remain offline for relinking. Keep linked WAVs with
the AAF and relink them in the receiving tool if moved.

Not supported: picture, embedded essence, stereo/multichannel or compressed
audio, effects/OperationGroups, fades (including SourceClip fades), automation,
transitions, retiming, non-zero slot/timecode origins, drop-frame, sample-based
composition slots, nested compositions, ambiguous/multiple locators, non-local
URLs, subframe edits, empty/trailing-filler-only duration, or more than 10,000
clips / 24 hours. Explicit source-channel selections are also rejected.
Import is limited to 256 MiB. Export requires neutral audio
mix/effect settings and no picture/artwork; unsupported settings are named rather
than silently discarded. Empty editor tracks/cels are harmless. Names, file tags,
canvas size and editor metadata do not transfer, as stated in the confirmation.
This is not a general-purpose DAW round trip or an Avid-certified AAF profile.

### Tools, licensing, architecture and evidence

- The [AAF Object Specification](https://www.aafassociation.org/html/specs/AAFSpecDR4.pdf)
  defines TimelineMobSlot edit units, SourceClip references and essence
  descriptors. The implementation uses the documented
  [pyaaf2 mob API](https://github.com/markreidvfx/pyaaf2/blob/main/src/aaf2/mobs.py) and
  [ASWF adapter](https://github.com/OpenTimelineIO/otio-aaf-adapter) as executable
  reference points for source/record timing and descriptor structures.
- The native AAF SDK has its own
  [AAF SDK Public Source Licence v2.0](https://github.com/dneg/aaf/blob/master/LEGAL/AAFSDKPSL.TXT),
  including additional intellectual-property notices. It is not linked,
  copied or redistributed here. No claim is made about patent clearance.
- [pyaaf2](https://pypi.org/project/pyaaf2/) is pure Python, has zero dependencies
  and does not use the AAF SDK. Its
  [MIT licence](https://github.com/markreidvfx/pyaaf2/blob/main/LICENSE) permits
  use; the user installs it in their selected environment. No third-party Python
  code or native AAF library is bundled with InkMotion. This avoids an in-process
  x86/x64 ABI dependency; both app architectures invoke the selected interpreter
  as a separate process. Python must support the user's Windows installation.
- Tested with pyaaf2 **1.7.1**, Python **3.15 x64**, OpenTimelineIO **0.18.1** and
  `otio-aaf-adapter` **2.0.0**. Both x64 and x86 app tests invoked that interpreter.
  The external ASWF AAF adapter independently read the binary exports with record
  starts 48/144, source starts 24/48 and lengths 48/24 at 24 fps and retained WAV
  URLs. A separate hand-built binary fixture uses WAVEDescriptor plus 48 kHz
  source/master slots; both readers agree on its three-second source offset and
  four-second record offset. Picture, stereo and cyclic-reference binary
  fixtures are rejected. No commercial NLE/DAW application was tested.

For optional end-to-end tests, install `pyaaf2` and the three ASWF OTIO adapters
(`otio-fcp-adapter` **1.0.0**, `otio-cmx3600-adapter` **1.0.0**,
`otio-aaf-adapter` **2.0.0**) in a trusted environment. Set
`INKMOTION_TEST_AAF_PYTHON` to its absolute `python.exe` path and
`INKMOTION_INTERCHANGE_FIXTURES` to a scratch folder. Run `AafInterchangeTests`,
then `tests\interop\verify_aaf.py <folder>`, then rerun the tests to check the
independently built/rejection fixtures. The Python bridge itself only needs
pyaaf2, not OpenTimelineIO. These checks do not imply broader application
interoperability or legal certification.
