# InkMotion workspace redesign

## Goal

Reshape InkMotion into dedicated Video Editor, Audio Editor, and Ink Editor workspaces, with a clear export workflow, while preserving current projects and existing editing capabilities.

## Delivery phases

1. **Workspace shell and Video Editor**
   - Replace the crowded single-screen layout with clear workspace navigation and shared project, playback, undo/redo, and status controls.
   - Make Video Editor the initial workspace; organise preview, timeline, track management, and clip tools into predictable regions.
   - Model timeline lanes explicitly for video, audio, ink, images, and text; only expose operations that are wired to real behaviour.
   - Preserve current video playback, cel/ink animation, audio clips, audio tags, project save/load, and undo/redo.
   - Done: four top-level pages (Video Editor, Audio Editor, Ink Editor, Export) switched from buttons in a custom dark title bar with File and Edit menus; each page shares the preview and transport bar and shows its own inspector and timeline.
   - Done: project version 9 stores ordered video, image, and title tracks with clips (source in/out, fades, opacity, volume, position, scale, title styling); version 8 projects migrate their video into a clip. The timeline supports drag, trim, split, ripple delete, and track hide/lock/mute/reorder with undo/redo. Preview shows the topmost visible video clip; multi-video blending is deferred to the export compositor.

2. **Ink Editor**
   - Give drawing and animation a dedicated workspace with the preview, ink tracks/layers, pen/eraser, colour, size, brush presets, onion skin, tween, and cel-exposure tools.
   - Add typed text as a separate overlay tool with font and styling controls; keep typed text distinct from freehand strokes.
   - Link ink tracks to video timeline content and preserve drawing data through project save/load and history.

3. **Audio Editor**
   - Show decoded waveform previews for audio clips; the first waveform increment uses NAudio and reports decoding failures in the track UI.
   - Establish a non-destructive multitrack audio model with clip selection, trim, split, and fades. Done: clips store source in/out points, fades, and gain; an NAudio playback pipeline applies them, and normalisation targets a -1 dBFS peak.
   - Add normalisation and audio metadata editing/writing, with explicit source-file write actions.
   - Add mono, stereo, and supported surround layouts up to 12.1; provide tempo and pitch processing controls only when a real processing implementation is available. Done: per-clip WSOLA tempo (50-200%) and pitch (+/-12 semitones), project mix layouts from mono to 12.1 with ITU-style mixing matrices, and stereo fold-down monitoring; surround export follows with real encoding.
   - Preserve existing audio playback and project compatibility through versioned migrations.

4. **Export and finishing**
   - Add export profiles for 720p, 1080p, 1440p, and 4K UHD, using the current project frame rates (12, 24, 30, or 60 fps).
   - Implement export through a Windows-supported encoding pipeline, with clear validation for unsupported codecs, dimensions, or frame-rate combinations.
   - Verify exported media dimensions, frame rate, audio handling, and project-state preservation.
   - Done: Media Foundation MP4 export (H.264/HEVC, hardware encoder with software fallback) of a Win2D timeline-order composite with ink, a 48 kHz mix into stereo or 5.1 AAC plus a 24-bit multichannel WAV stem for larger layouts, progress, cancellation, and partial-file clean-up; round-trip tests verify dimensions, frame count, channels, and ink pixels.

5. **Windows distribution**
   - Build and test x86 and x64 configurations on Windows runners.
   - Produce a Store-upload MSIX bundle for both architectures after the Partner Centre package identity is associated.
   - Done: the Store workflow applies the Partner Centre identity from repository secrets, then builds and verifies the x86/x64 `.msixupload`; the app declares Per-Monitor V2 DPI awareness for certification.

## Important decisions and constraints

- Deliver in small, buildable increments; do not present non-functional controls as implemented features.
- Treat text overlays as typed text with selectable fonts, separate from freehand handwriting/brush presets.
- Use NAudio for waveform decoding and future Windows audio-processing work rather than introducing custom codec interop.
- Keep editing non-destructive where practical and retain backward compatibility for `.inkmotion` projects.
- Support formats and channel layouts according to available Windows codecs and verified processing capabilities; do not claim unsupported 12.1 processing or export.
- Keep Store identity values in repository secrets, tied to Partner Centre; never guess them or commit them or signing certificates.
- Add focused tests for project models, timeline operations, migrations, and export-profile validation as each phase lands.

## Expanded editor delivery sequence

The expanded design uses one shell: four page buttons in the first row; relevant actions in the second row; a shared video/audio viewer on the left with Original/Modified comparison; page-specific tools and options on the right; and the corresponding timeline below. Deliver real processing and project persistence with each tool, not placeholder controls.

1. **Shared layout and preview comparison**
   - Implemented: page-specific action row and the shared viewer in Audio Editor as well as Video Editor, Ink Editor and Export.
   - Implemented: Modified shows image/title/ink overlays across pages. Only Ink Editor accepts drawing input and shows onion-skin guides.
   - Implemented: Original compares the active visible video at the same source-mapped timeline position, bypassing opacity, clip volume/mute, overlays and added audio. A mode change pauses playback and does not change project data or export settings. Where there is no active video source, show an explicit empty state.
   - Still required for full preview/export parity: real-time multi-video compositing and all future effects. Original is source comparison, not a separately maintained unedited project.
2. **Export controls and encoding**
   - Implemented: independent 24-120 fps MP4 output (including decimal rates), time-mapped ink exposures, software encoding or hardware preference with reported software fallback, and a target-bit-rate quality/file-size slider with a video-only size estimate.
   - Implemented: separate audio-mix exports as integer PCM WAV (16/24/32 bits), float WAV (32 bits), MP3 and AAC/M4A. WAV supports the listed 44.1-384 kHz rates, project channel layouts, resampling and integer dither. Lossy output is restricted to 44.1/48 kHz, mono/stereo and requested 128-320 kbps. Missing encoders report errors. MP4 has independent embedded AAC settings for 44.1/48 kHz and 128/160/192 kbps, including sample-rate-correct timestamps and surround stems.
   - Added: on-demand Windows MP3/AAC encoder-format discovery for separate audio files, showing rate/channel/bit-rate combinations. Lossy exports require an exact native output type and reject unsupported choices before touching the output; 160 kbps is also selectable. Video sink-writer capability probing remains separate.
   - Added: independent headless export processes for video/audio, immutable local request snapshots, atomic job progress/results, cross-process cancellation, startup discovery/reattachment, active output/stem reservations and exclusive worker leases. Failed/cancelled/interrupted jobs can restart from their saved snapshot at the same output path. Editor shutdown/crash does not stop the worker; worker/OS failure is explicitly interrupted, with no mid-file resume. Job history remains local. Scheduled queues, automatic retry policies and resumable rendering remain outstanding.
   - Verified: round trips of every offered WAV rate/depth, advertised Windows MP3/AAC encoder combinations, embedded AAC rate/bit-rate combinations, cancellation, and 120 fps video with time-correct ink exposures. Still required: additional embedded audio codecs/containers, video capability discovery, FLAC/Opus support and broader hardware/resolution certification.
   - Keep resolution separate from frame rate. Add independent output frame rates from 24 to 120 fps, including common fractional rates, with time-based ink exposure resampling and synchronised audio.
   - Add explicit GPU encoding on/off, report actual encoder use, and reject unsupported combinations clearly. Never silently ignore a hardware selection.
   - Add a low-quality/small-file to high-quality/large-file slider mapped to verified encoder quality/bit-rate settings; label file-size estimates as estimates.
   - Add codec/container capability discovery and selectable audio codecs. Candidate formats: AAC in MP4, PCM WAV, FLAC, MP3 and Opus in appropriate containers. Availability, licensing and channel limits must be verified before enabling each format.
   - Support selectable 44.1, 48, 88.2, 96, 176.4, 192, 352.8 and 384 kHz audio where supported, and 16/24-bit PCM or 32-bit float where meaningful. Lossy codecs use bit rate rather than pretending to preserve a selectable PCM bit depth. Add real resampling, dither when reducing PCM depth, format probing and round-trip tests.
3. **Video tools inspired by DaVinci Resolve Studio**
   - Existing: trim, split, move, copy/paste/duplicate, ripple delete, clip-edge snapping, source slipping, individual video/image source relinking, track ordering/visibility/locking/mute, opacity, fades, volume, images and styled titles.
   - Added: version 14 named timeline markers with add/rename/move/remove, previous/next/selected navigation, amber timeline guides, clip snapping and time-preserving frame-rate conversion; save/load and undo/redo.
   - Added: one-project-frame rolling end trims and three-clip slide edits on adjacent unlocked video clips, preserving sequence length and centre source range for slides. Source handle, minimum-duration, lock, gap/overlap and fade checks reject invalid edits without mutation; saved clip properties drive preview/audio/export and undo/redo. No schema change.
   - Next: media bins and batch relinking; source in/out; select/blade/interactive trim modes; insert/overwrite; linked audio/video; ripple trim and drag roll/slide tools; transitions; crop/rotate/transform keyframes; speed changes and reverse.
   - Then: colour controls (exposure, contrast, white balance, saturation, curves, LUTs), scopes, stabilisation, masks/tracking, chroma key, compositing and effects. Multicam, proxy/optimised media and noise reduction need separate verified increments; this is not a claim of full Resolve parity.
4. **Audio tools inspired by Audacity and ocenaudio**
   - Existing: waveform lanes, multitrack placement, trim/split, whole-clip copy/paste/duplicate/remove, frame-based range selection/copy/cut/delete/silence (paste at playhead), volume/mute/solo, left/right speaker balance, fades, normalisation, tempo/pitch, channel layout and tag editing. Version 10 persists solo/balance and migrates previous projects to neutral defaults.
   - Added: version 11 saved/bypassable high/low-pass filters, three-band EQ and linked peak compression with shared preview/export processing; selected-track monitor peak/RMS and latched clipping indication.
   - Added: bounded source-range spectrum snapshots with a Hann-windowed FFT, logarithmic frequency plot, calibrated amplitude, peak/RMS and strongest non-DC bin; asynchronous analysis is cancelled when selection changes.
   - Added: non-destructive joins of consecutive same-source segments on adjacent audio lanes, retaining outer fades and tags and rejecting gaps, overlaps, internal fades or processing mismatches; undo restores both parts.
   - Next: direct waveform range dragging and sample-level selection; spatial panning; master metering; recording; sample-level waveform zoom; spectrogram and whole-range spectral averaging.
   - Added: version 13 linked noise gate with attack/hold/release and 3 dB hysteresis, plus instant sample-peak limiting with a configurable ceiling/release, persisted and shared by preview/export. Neither is spectral noise reduction or true-peak mastering.
   - Then: extend non-destructive effect chains with noise reduction, reverb/delay. Plug-in hosting is a later compatibility project, not an immediate placeholder.
5. **Ink tools inspired by FlipaClip**
   - Existing: pressure-sensitive pen with optional stabilisation, whole/partial-stroke eraser with adjustable radius, palette/custom colour and transparency/size, drag-to-draw lines/rectangles/ellipses, whole-stroke lasso/all selection with copy/paste/delete and offset/scale/rotation, layers/opacity/reordering, cel add/copy/paste/duplicate/delete/exposure (including text and layer content), keyframes/tweening, onion skin and text.
   - Added: saved drawing locks protecting strokes, opacity and layer ordering, with copy/selection and visibility still available; whole-cel/timing/text operations are explicitly outside the drawing lock.
   - Added: layer renaming, independent all-cel drawing duplication (including base/locked sources) and undoable non-base layer removal with lock/last-visible protection. Named round-brush presets set width, alpha and stabilisation while preserving hue; no texture or special highlighter blend mode.
   - Next: canvas colour sampling; fill; finer partial-stroke and text selection; rulers; canvas pan/zoom/rotation; layer merge; texture brushes.
   - Then: frame ranges, copy/move/group/loop, richer onion-skin controls and imported reference media. Draw over video non-destructively and verify those strokes and text render into exported video, not just the preview.

Reference products define the intended workflows, not a licence to copy their UI assets or proprietary implementations: [DaVinci Resolve editing](https://www.blackmagicdesign.com/products/davinciresolve/edit), [Audacity](https://www.audacityteam.org/), [ocenaudio](https://www.ocenaudio.com/), and [FlipaClip](https://www.flipaclip.com/).

## Initial implementation increment

Implement the workspace shell and reorganise the existing editor into Video Editor, Audio Editor, and Ink Editor views. Reuse the current controls and handlers, keep the current feature set operational, and establish the layout/selection structure for later track and export work without adding placeholder features that imply functionality that does not exist yet. The user-facing editing shell is organised into the three areas; deeper clip, waveform, DSP, and export capabilities remain subsequent increments.

## Validation

- Build `InkMotion.sln` as x86 and x64 Release on Windows and run the full test suite for both architectures.
- Launch the application and verify each workspace is navigable, the current editing interactions still function, and the window remains responsive.
- Run `git diff --check` and add/update project documentation for the actual delivered capabilities.
