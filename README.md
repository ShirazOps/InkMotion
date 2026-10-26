# InkMotion

A native Windows video, audio and animation editor built with C#, .NET 10, WinUI 3 and Win2D. Combine multitrack footage, sound, titles and hand-drawn animation in one project.

**InkMotion is actively developed and is not yet at full professional-editor feature parity.** See the [roadmap](plan.md#expanded-editor-delivery-sequence) for implementation details and remaining work.

## Get started

Download an x64 or x86 ZIP from [Releases](https://github.com/ShirazOps/InkMotion/releases), extract it and run `InkMotion.exe`. Release builds are self-contained; no separate .NET or Windows App SDK installation is needed. Builds are currently unsigned.

Switch between **Video Editor**, **Audio Editor**, **Ink Editor** and **Export** at the top of the window. Each workspace has its own toolbar, inspector and timeline, with shared playback and undo/redo controls. Use **View** to resize, hide or restore panels; layouts are remembered per workspace.

The red playhead tracks playback across the track area in every editor. Click or drag the timeline ruler to pause and choose a frame for **Split**, trims, range boundaries and other playhead-based edits. Audio clips share one project-time scale, including start offsets and tempo changes: click a waveform to seek, or drag it to select a range. The Ink ruler seeks within cel exposures and follows playback as the strip scrolls. Switching editors keeps the same position.

Open and save `.inkmotion` projects with **Ctrl+O** and **Ctrl+S**. Media and LUT files remain externally linked, so keep them available. The project media bin identifies missing sources and can search folders for unique filename matches before relinking.

## Features

### Video and titles

- Multitrack video, image and title compositing, with source In/Out marks, insert/overwrite, snapping, markers, copy/paste, split, trim, ripple, roll, slide and cross-dissolves.
- In **Select** mode, drag a video, image or title clip's body to move it, or either highlighted edge to shorten or extend its duration. Video extensions use available source footage; still images and titles can be extended without changing playback speed. Track locks and neighbouring clips remain protected.
- Clip position, scale, rotation, crop, flips, opacity, fades, playback speed and reverse; keyframed transforms and supported effects.
- Exposure, contrast, white balance, saturation, gamma, RGB curves and 3D `.cube` LUTs; chroma key with spill suppression, blur, sharpening, vignette and animated masks.
- Multicamera switching, local proxy generation, stabilisation and coarse rolling-shutter correction. RGB histogram, luma waveform and vectorscope for source grading.
- Double-click a title clip, title track header or title in the viewer to edit its text. In paused **Modified** Video preview, drag titles with mouse or touch, resize proportionally using corner handles and rotate with the round handle.
- Title font and size, with matching text/background colour pickers and independent opacity controls. Viewer edits respect track locks, support undo/redo and update transform keyframes on animated titles.
- Animate titles using **Set title motion keyframe**: mark the first pose, move the playhead and drag/rotate/resize the next pose. Subsequent pose edits automatically create or update keys; playback and export interpolate between them. Use **Previous/Next pose** or the title clip's blue motion markers to navigate.
- Right-click video/image/title headers for track properties or clips for their respective inspector. Audio lanes/headers and the ink timeline/layer selector also provide property menus.

**Original / Modified** compares the active video source at the playhead with the edited composition. Original is a source comparison, not a second unedited timeline.

### Audio

- Multitrack waveforms, sample-level source trimming, split/join, range copy/cut/silence, fades, peak normalisation, independent tempo/pitch and metadata editing.
- Volume, mute, solo, left/right balance and mono-source spatial panning. Project layouts from mono and stereo through surround, 7.1.4 and 12.1, with stereo monitoring.
- Non-destructive EQ and filters, noise gate, compression, sample-peak limiting, profile-based noise reduction, reverb and delay.
- Embedded video-audio volume and balance automation, timeline curves and matching export processing.
- Selected-track and live preview master peak/RMS meters; complete-export-mix analysis with peak/RMS and integrated LUFS. Source spectrum and spectrogram analysis.
- Record a selected Windows audio input to WAV and place the take on the timeline. Audio/video clip linking and source relinking are also supported.

### Ink and animation

- Mouse, touch and pressure-sensitive pen drawing, textured brushes, lines, rectangles, ellipses, solid shapes and enclosed-area bucket fill.
- Partial/whole-stroke erasing and lasso selection, drawing transforms, independent layers with visibility/opacity/locks, layer duplication, merging and flips.
- Cel exposures, keyframes, eased in-between animation, configurable onion skinning, named cel groups, range editing and independent or linked drawing cycles. Ink-only animations can play without adding video or audio.
- Per-layer **Follow object**: draw on a cel, select a textured object box in Modified preview, then track its position, rotation and size until the chosen end time. Other layers remain untracked. Loss stops and hides that layer until you correct the box at the flagged time and resume; motion is saved and shared by preview/export.
- Cel text overlays with character-range styling; drawing-only or full-preview colour sampling.
- Canvas zoom, pan, rotation, grids and output-pixel rulers. Import footage or images as references beneath the artwork.

### Export

- H.264 or HEVC MP4 at 720p, 1080p, 1440p or 4K UHD, with independent output frame rates from 24-120 fps.
- Software encoding or a hardware preference with reported software fallback. Preview and export share the composition and effect pipeline.
- Separate PCM/float WAV, MP3 and AAC/M4A mixes. Multichannel WAV supports the project layout; MP4 carries up to 5.1 AAC, with a companion full-layout WAV when needed.
- Optional local FFmpeg adds 16/24-bit FLAC mixes up to 384 kHz and 8 channels, 48 kHz mono/stereo Opus, and Matroska video with AAC, PCM, FLAC or Opus audio.
- Optional FFmpeg also exports WMA audio (`.wma`, wmav2, 44.1/48 kHz stereo/mono), Windows Media Video (`.wmv`/ASF, wmv2 with WMA audio), and AV1 in MP4 or Matroska. WMV uses software encoding; AV1 uses libaom-av1 software or an explicitly selected NVENC/QSV/AMF AV1 encoder when supported. Use **Check video encoder profiles** to test the selected codec/device/profile before a long job; no codec or hardware fallback is hidden.
- FFmpeg video offers explicit CPU, NVIDIA NVENC, Intel Quick Sync and AMD AMF encoders with no automatic hardware-to-software substitution. Selected-profile probes report unsupported devices/codecs.
- Persistent background export jobs with progress, cancellation and retries. Jobs use saved project snapshots and continue independently of the editor.

For optional formats, choose a trusted local `ffmpeg.exe` under **Export > Optional FFmpeg** and check its capabilities. InkMotion does not download, bundle or redistribute FFmpeg; available encoders and licensing depend on your build. Existing Windows exports need no FFmpeg. Saved jobs retain the executable path.

**WMA, WMV and AV1 import:** normal audio/video import uses installed Windows decoders. If those codecs are missing, check the selected FFmpeg executable and enable **Use FFmpeg compatibility imports for WMA, WMV and AV1** on Export before importing or relinking. This applies to audio import, timeline video import, source monitor and media relinking. AV1 inside MP4/MOV/M4V/Matroska/WebM is detected with `ffprobe.exe` beside FFmpeg; non-AV1 files in those containers remain unchanged. Raw `.av1` streams are also accepted using FFmpeg's source timing.

Compatibility imports decode WMA to float WAV and transcode WMV/AV1 to high-quality H.264/AAC editing copies (first video/audio streams, odd dimensions padded to even pixels). Video conversion is lossy, not a bit-exact AV1/WMV editing path. Originals are not modified. Copies live in `%LOCALAPPDATA%\InkMotion\ImportedMedia`, are reused until the source size/timestamp changes, and are linked in the saved project for preview, waveforms, effects and export. They are **not temporary proxies**: keep or move/relink these files with the project, and only remove them when no project needs them. Reopening a converted project needs no FFmpeg unless importing/converting more media.

### Screen recording

Use **File > Screen recorder** to record one or more displays, or add regions using global physical-pixel coordinates (including negative monitor positions). Selected areas retain their relative placement on one canvas; gaps are black. Choose a 1920/3840-pixel longest-side limit, 15/24/30 fps, and whether to include the pointer.

**Draw box** or **Draw freeform** selects directly over the live desktop with a mouse or pen, including across monitors. Outside the selection is fogged while drawing. Release to place the outline: the fog clears immediately and applications remain usable inside and outside it, so you can rearrange windows before recording. Drag the box edges/corners to resize; freeform selections have a resizable bounding box that scales their closed shape. Left/Top/Width/Height update live. Repeat to add more areas; outlines disappear before recording begins and are not included in the video. Before placing, **Reset entire screen** (R) selects the full display and **Cancel** (Esc) discards the drawing. The recording is cropped to the selected areas' bounding box, with pixels outside freeform shapes and gaps between areas black. **Reset to entire screen** in the recorder replaces all regions with the selected full display.

15 fps capture opens on a 30 fps editing timeline without changing its duration; 24/30 fps captures use matching project rates.

System output audio, microphone and webcam are explicitly selected and off by default. Each becomes a separate editable source; the webcam can appear picture-in-picture in the resulting project. Use headphones to avoid microphone echo. Windows camera/microphone privacy settings still apply.

Under **Recording workflow**, choose a **0, 3, 5 or 10 second countdown**. **Cancel countdown** cancels before capture starts. **Stop after** sets a timed recording limit in minutes (fractions allowed down to one second, maximum 1,440 minutes); **0** keeps manual stopping. The limit excludes the countdown and starts with capture, including optional-device startup. Timed stops finalise and save like manual stops, with remaining time shown during recording.

**Minimise this recorder when capture starts** gets the recorder out of the way after the countdown; it does not minimise the main editor or exclude any InkMotion windows from capture. Arrange the editor yourself if it should not appear. Return through the taskbar or **File > Screen recorder** to stop manually. Turn off **Open the saved recording in the editor** to keep completed takes in the library without replacing the editor's current project, ready to record another take.

Enable **Ctrl+Shift+F10 stops and saves from any application** before starting to stop without returning to InkMotion. The shortcut works while the recorder is minimised and cancels the countdown if capture has not begun. It is reserved only during the countdown/take and released when stopping or finalising. If Windows cannot reserve the combination (for example, another app owns it), InkMotion reports the conflict before capture starts; disable the option or free the combination. The shortcut cannot start a recording and is not a keyboard logger.

**Stop and save** creates a durable `.inkmotion` project before optionally opening it. Screen segments, separate audio/webcam files and recovery metadata remain under `%LOCALAPPDATA%\InkMotion\ScreenRecordings`. The recorder's library shows storage use and opens saved/interrupted takes; removal requires explicit confirmation and is blocked while the editor uses those sources. No automatic cleanup removes recordings.

Capture is best-effort desktop SDR capture, not protected-content, secure-desktop or exclusive-fullscreen capture. Slow frames repeat to preserve elapsed time; disconnected/rearranged displays stop the take. Five-second screen checkpoints survive crashes, but the unfinished MP4/device files may need external recovery. Review sync on your devices; separate tracks allow timing correction. Do not change device formats or displays mid-take.

## Privacy

Media is processed locally, without application analytics or automatic uploads. See the [privacy policy](PRIVACY.md) for microphone use, local working files, background exports and optional tools.

## Current limits

Windows codecs and hardware determine playback and encoding availability; HEVC needs the HEVC Video Extensions. Encoder probes are local checks, not device certification. Reverse video preview mutes embedded audio; export can reverse it or leave it muted.

Stabilisation and noise reduction need review on real material. Audio effect tails stop at clip boundaries, and metering/limiting is sample-peak rather than true-peak. Recording does not include live input monitoring. FFmpeg video retries reuse completed five-second segments when linked media/settings are unchanged; the current segment and audio/mux stages restart. Windows and audio-only exports restart from the beginning. Failed/cancelled FFmpeg video jobs retain checkpoints locally until a successful retry.

Project files automatically migrate older formats. Export snapshots, linked paths, recordings and proxies are stored locally; keep source files unchanged while jobs run.

Object following is local feature tracking for textured, approximately rigid objects, not semantic AI or deformable-surface tracking. Review the result for drift. Empty cels hold the drawing at the reference time; existing cel animation still plays, so changing cel timing can change that reference drawing. Cel-global text is not attached. Disable following to edit original strokes; new/duplicated layers do not inherit motion, and tracked layers must have tracking cleared before merging. Source-clip property changes require clearing and recalculating tracking; replacing a source file in place also requires recalculation.

## Build from source

Requires Windows 11, the .NET 10 SDK and Windows SDK 10.0.19041 or newer. Supported build targets are x64 and x86.

```powershell
dotnet restore InkMotion.sln -p:Platform=x64
dotnet build InkMotion.sln -c Release -p:Platform=x64 --no-restore
dotnet test InkMotion.sln -c Release -p:Platform=x64 --no-build --no-restore
& .\src\InkMotion\bin\x64\Release\net10.0-windows10.0.19041.0\InkMotion.exe
```

For x86, use `Platform=x86` and the corresponding output directory. [Windows CI](.github/workflows/windows-ci.yml) builds and tests both architectures and publishes portable releases from `main`. [Store packaging](.github/workflows/windows-store-package.yml) uses repository secrets for the Partner Centre identity; never commit credentials or signing certificates.

### Microsoft Store updates

Each successful `main` release automatically includes an x86/x64 `.msixupload` package when the Store identity secrets are configured. Download it from [GitHub Releases](https://github.com/ShirazOps/InkMotion/releases), create an update in [Partner Center](https://partner.microsoft.com/dashboard), upload it under **Packages**, review the submission and send it for certification. Release notes and the Actions summary include these steps. Store submission remains manual; no Partner Center API credentials are required.

Package versions use the CI build number (`1.0.<build>.0` with the current manifest). Keep manually submitted versions below subsequent generated versions; if you change the major/minor version in Partner Center, update the manifest base version accordingly. Wait for an existing submission to finish before submitting another update.

<details>
<summary>Workspace screenshots (empty-project layout examples)</summary>

![Video Editor](docs/screenshots/video-editor.png)
![Audio Editor](docs/screenshots/audio-editor.png)
![Ink Editor](docs/screenshots/ink-editor.png)
![Export](docs/screenshots/export.png)

</details>
