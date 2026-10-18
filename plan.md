# InkMotion workspace redesign

## Goal

Reshape InkMotion into dedicated Video Editor, Audio Editor, and Ink Editor workspaces, with a clear export workflow, while preserving current projects and existing editing capabilities.

## Delivery phases

1. **Workspace shell and Video Editor**
   - Replace the crowded single-screen layout with clear workspace navigation and shared project, playback, undo/redo, and status controls.
   - Make Video Editor the initial workspace; organise preview, timeline, track management, and clip tools into predictable regions.
   - Model timeline lanes explicitly for video, audio, ink, images, and text; only expose operations that are wired to real behaviour.
   - Preserve current video playback, cel/ink animation, audio clips, audio tags, project save/load, and undo/redo.
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
   - Produce a Store-upload MSIX bundle for both architectures after the Partner Center package identity is associated.

## Important decisions and constraints

- Deliver in small, buildable increments; do not present non-functional controls as implemented features.
- Treat text overlays as typed text with selectable fonts, separate from freehand handwriting/brush presets.
- Use NAudio for waveform decoding and future Windows audio-processing work rather than introducing custom codec interop.
- Keep editing non-destructive where practical and retain backward compatibility for `.inkmotion` projects.
- Support formats and channel layouts according to available Windows codecs and verified processing capabilities; do not claim unsupported 12.1 processing or export.
- Keep Store identity values tied to Partner Center; never guess or commit signing certificates.
- Add focused tests for project models, timeline operations, migrations, and export-profile validation as each phase lands.

## Initial implementation increment

Implement the workspace shell and reorganise the existing editor into Video Editor, Audio Editor, and Ink Editor views. Reuse the current controls and handlers, keep the current feature set operational, and establish the layout/selection structure for later track and export work without adding placeholder features that imply functionality that does not exist yet. The user-facing editing shell is organised into the three areas; deeper clip, waveform, DSP, and export capabilities remain subsequent increments.

## Validation

- Build `InkMotion.sln` as x86 and x64 Release on Windows and run the full test suite for both architectures.
- Launch the application and verify each workspace is navigable, the current editing interactions still function, and the window remains responsive.
- Run `git diff --check` and add/update project documentation for the actual delivered capabilities.
