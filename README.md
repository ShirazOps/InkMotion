# InkMotion

InkMotion is a native Windows video editor in early development, built with C#, .NET 10, WinUI 3, and the Windows App SDK.

## First milestone

- Switch between four pages with the **Video Editor**, **Audio Editor**, **Ink Editor**, and **Export** buttons at the top of the window. A second row provides page-specific actions. All four pages share the video/audio viewer on the left, a page-specific inspector on the right, and a timeline across the bottom, with a shared transport bar (frame readout, previous/play/next, undo/redo) under the preview.
- Use **Original / Modified** above the viewer to compare the active video source at the playhead with the edited timeline. Original bypasses video opacity, clip volume/mute, image/title/ink overlays and added audio; it keeps the timeline's source-in mapping to compare the same source frame. Switching pauses playback and never changes the project or export. Original is not a second timeline of the entire unedited project; it is blank where no visible video clip is active. Modified shows ink on every page, with drawing input and optional onion-skin guides restricted to Ink Editor.
- Use the **File** menu in the title bar to open (Ctrl+O) and save (Ctrl+S) projects, add video, image, title, and audio clips, and go to the Export page; the **Edit** menu provides undo and redo.
- Build native x86 and x64 Windows targets, and create an x86/x64 MSIX upload bundle for the Microsoft Store with the Partner Centre identity supplied from repository secrets.
- Use the Video Editor for video preview and playback, the Audio Editor for multitrack clip placement, waveform previews, mixing, and tag editing, and the Ink Editor for pen-first drawing and cel animation.
- Add typed text overlays centred in animation cels, with selectable font family, size, and brush colour, separate from pressure-sensitive freehand ink.
- On the Export page, select 720p, 1080p, 1440p, or 4K UHD independently of the output frame rate (24-120 fps, including decimal rates). Ink exposure timing follows project time rather than the output frame count, so a higher output rate does not speed up the drawings.
- Choose software video encoding or prefer GPU encoding with a reported software fallback. The video quality/file-size slider changes the target bit rate, with an approximate video-only MB/min estimate; actual size depends on content and the encoder. H.264/HEVC availability and supported high-rate/resolution combinations depend on Windows codecs and hardware.
- Export a separate audio mix as PCM WAV (16/24/32-bit integer), 32-bit float WAV, MP3 or AAC/M4A. WAV rates are 44.1, 48, 88.2, 96, 176.4, 192, 352.8 and 384 kHz; the mixer runs at the selected rate and resamples sources only when needed. Increasing the rate does not restore detail absent from a source. WAV preserves the project channel layout and uses dither for integer quantisation; files beyond the 4 GB RIFF limit are rejected. MP3 and AAC use Windows encoders at 44.1/48 kHz and 128-320 kbps, with stereo downmix for surround projects and explicit errors for missing encoders. MP4's embedded audio remains 48 kHz AAC.
- Export the timeline to an MP4 file with H.264 or HEVC video at 720p, 1080p, 1440p, or 4K UHD and the selected output frame rate. Every visible video, image, and title track is composited in timeline order on the GPU with Win2D, ink drawings and typed overlays are drawn on top at their exposures, and frames are encoded through Windows Media Foundation. GPU encoding is a hardware preference with software fallback; switching it off selects software encoding (Win2D composition still uses the GPU). Audio tracks (with tempo, pitch, fades, and gain) and unmuted video-clip audio are mixed at 48 kHz into AAC (stereo, or up to 5.1 for surround layouts); if Windows cannot encode 5.1 AAC, the MP4 falls back to stereo. Whenever the MP4 carries fewer channels than a surround mix, the full mix is also written to a 24-bit multichannel WAV beside the video. Export shows progress, can be cancelled, and removes partial files. HEVC requires the HEVC Video Extensions.
- Build a multitrack video timeline with video, image (PNG, JPEG, BMP, GIF, TIFF, or WebP), and title tracks. Add clips at the playhead, drag them in time or onto another track of the same kind, and trim the start or end, split, move to the playhead, delete, or ripple delete. Tracks can be added, renamed, reordered, hidden, locked, and (for video) muted; clip opacity, fades, volume, still duration, position, scale, and title font, size, and colour are editable, all with undo/redo.
- Use local MP4, MOV, M4V, WMV, AVI, AV1, MKV, or WebM video files as timeline clips. AV1 playback depends on an AV1 decoder being available on Windows. Projects from earlier versions open with their video as a single clip on "Video 1".
- Draw with a mouse, pen, or touch directly over the preview.
- Preserve Windows pen pressure in ink strokes, including through project save/load, undo, and in-between animation.
- Render the ink overlay through Win2D/Direct2D on the GPU; Windows' media playback stack uses hardware video decoding when the installed codec and driver support it.
- Set the ink brush colour and size for new strokes; existing strokes keep their saved appearance.
- Choose whole-stroke or partial-stroke erasing on the active ink layer, with an adjustable radius. Partial erasing splits stroke paths at the erased region and preserves pressure and brush styling on the remaining portions. Optional drawing stabilisation smooths new pointer samples; stronger smoothing adds pen lag, and releasing the pointer anchors the final point. These are session tool preferences; the resulting edited strokes are saved and exported.
- Create independent ink layers, switch the active drawing layer, control visibility and opacity, and preserve each layer through save/load and undo/redo.
- Keep independent ink drawings on timeline frames, move between them, and duplicate a drawing to a new frame.
- Copy and paste complete ink cels after the selection, preserving text, all layer strokes, exposure and animation settings. Duplicate cels also retain text. Raise or lower the active ink layer in the bottom-to-top rendering stack, with undo/redo and saved ordering.
- Copy video, image or title clips and paste at the playhead, or duplicate immediately after the source clip. Copies retain source ranges and editable properties but receive new identities. Placement avoids locked tracks and preserves overlaps on another compatible track.
- Copy audio clips and paste at the playhead, duplicate after their timeline duration, or remove them from the project without deleting source files. Copies retain trimming, fades, tempo/pitch, volume and tags, are placed on separate tracks and support undo/redo. The internal editing clipboard is cleared when another project opens.
- Delete selected frames while maintaining a valid, sequentially numbered timeline and at least one frame per project.
- Use optional onion-skin guides for the previous and next drawings, with adjustable opacity.
- Mark frames as keyframes to define the endpoints for in-between animation.
- Choose linear, ease-in, ease-out, or ease-in/ease-out tweening from each keyframe to the next.
- Set each cel's exposure length in output frames; playback, video seeking, audio timing, and tween spacing follow the exposure sheet.
- Play and scrub to synchronise the drawing exposure with video playback at 12, 24, 30, or 60 fps. Playback extends the ink timeline as needed.
- Add audio clips to separate tracks (MP3, WAV, M4A, AAC, WMA, or FLAC). Clips start at the playhead and can be repositioned to it later; each track has independent volume and mute controls.
- Edit audio clips non-destructively: trim the start or end at the playhead, split a clip at the playhead (the second part moves to a new track below), set fade-in and fade-out lengths, and normalise a clip's peak to -1 dBFS or reset its gain. Waveform lanes show the trimmed range and fade envelope, and edits support undo/redo.
- Select an audio range using whole timeline frame numbers or start/end-at-playhead buttons; the end frame is excluded. Copy/cut to the audio clipboard, delete leaving a gap, or silence the selection as a muted segment. The range is highlighted on its clip waveform. Surrounding pieces keep their timeline positions on separate tracks; outer fades, tags and tempo/pitch are retained, with internal split fades removed. All range edits support undo/redo. Muted audio segments retain their duration in separate audio exports, including a completely silent mix.
- Change a clip's tempo (50-200%) without changing its pitch, or shift its pitch by up to 12 semitones either way without changing its length, using a built-in WSOLA time-stretch with preview-quality resampling. Timeline length, trim, split, and fades follow the tempo.
- Choose a project mix layout: mono, stereo, quad, 5.1, 7.1, 7.1.4, or 12.1 (7.1.4 plus back centre). Sources are mapped into the mix with standard ITU-style downmix and upmix rules, and monitoring folds the mix down to the stereo output device.
- Edit audio title, artist, album, genre, and year tags for the selected source. Write supported metadata back to the source only with **Write tags to audio file**; project comments remain project-only.
- Play and scrub audio alongside video, or play an audio-only timeline.
- Generate in-between frames by interpolating corresponding keyframe strokes, resampling point paths when their point counts differ. Edit an in-between frame to make it a new keyframe.
- View Video, Ink, multiple Audio, and Effects track lanes, with each audio lane showing its clip and start frame.
- Save and reopen `.inkmotion` project files, including linked video and audio paths, audio track settings and tags, frame drawings, typed overlays, ink layers, exposure lengths, and keyframe tween settings. Versions 1-7 are migrated when opened.
- Preserve the chosen project frame rate (12, 24, 30, or 60 fps) in the project file.
- Undo and redo completed ink strokes, erasures, frame additions, duplication and deletion, keyframe changes, and ink-layer visibility/opacity changes.

## Planned editing milestones

The shared layout and expanded export controls are implemented. The full Resolve/Audacity/ocenaudio/FlipaClip tool expansion is not yet implemented. The remaining video, audio and ink tool inventories and delivery sequence are recorded in [the workspace roadmap](plan.md#expanded-editor-delivery-sequence).

The new workspaces organise the current capabilities; they do not yet provide a full nonlinear editing or audio workstation. The Audio Editor displays decoded per-clip waveforms, and supports trim, split, fades, normalisation, tempo, pitch, and mix layouts up to 12.1. The video timeline preview plays the topmost visible video clip at the playhead, with image and title overlays composited on the GPU; real-time blending of several overlapping video tracks is planned, so the preview can differ from export, which composites every visible track in timeline order. Multichannel output to surround devices is planned; MP4 audio is limited to 5.1 AAC, with larger layouts delivered as a separate WAV.

Project files store paths to the original video and audio rather than embedding the media. Keep the sources available or relink them with **File > Add video clip…** and **File > Add audio clip…** after opening the project.

## Windows builds and Microsoft Store packaging

Build either desktop architecture with `dotnet build InkMotion.sln -c Release -p:Platform=x86` or `-p:Platform=x64`. The Windows CI workflow builds and tests both configurations. After both pass on a push to `main`, it publishes self-contained x86 and x64 builds and creates a GitHub release tagged `build-<run number>` with `InkMotion-build-<n>-win-x64.zip` and `InkMotion-build-<n>-win-x86.zip`. Download the latest from the repository's **Releases** page, extract it, and run `InkMotion.exe`; no separate .NET or Windows App SDK installation is needed. These builds are unsigned, so Windows SmartScreen may ask for confirmation on first run. Each release also includes `InkMotion-build-<n>-x86-x64.msixupload`, the MSIX bundle for Partner Centre, whose package version is `<major>.<minor>.<run number>.0` from the manifest so that every upload is higher than the last. It is built only when the Store secrets below are set; otherwise the release contains just the zips.

Microsoft Store details are kept out of source control. `src/InkMotion/Package.appxmanifest` holds placeholder identity values, and the **Microsoft Store package** workflow applies the Partner Centre identity from these repository secrets (**Settings → Secrets and variables → Actions**) with `build/Set-StoreIdentity.ps1`:

| Secret | Partner Centre value |
| --- | --- |
| `STORE_PACKAGE_NAME` | Package/Identity/Name |
| `STORE_PUBLISHER` | Package/Identity/Publisher (the full `CN=...` value) |
| `STORE_PUBLISHER_DISPLAY_NAME` | Package/Properties/PublisherDisplayName |
| `STORE_DISPLAY_NAME` | The reserved app name |
| `STORE_PACKAGE_FAMILY_NAME` | Package Family Name (PFN), for reference |
| `STORE_PACKAGE_SID` | Package SID, for reference |
| `STORE_APP_ID` | Store ID, for future automatic Store submission |

Increment the manifest's four-part version for each release (the fourth part must stay 0), then run the workflow manually or push a `v*` tag. It creates an unsigned x86/x64 `.msixupload` bundle for Partner Centre, where Microsoft signs Store submissions, after checking that both packages carry the identity from the secrets and keep the required `runFullTrust` capability. For a local Store build, set the same names as environment variables, run `build/Set-StoreIdentity.ps1`, build with the workflow's MSBuild arguments, and then run `git checkout -- src/InkMotion/Package.appxmanifest` so the identity is never committed. Do not commit signing certificates.

## Build

Requirements: Windows 11, the .NET 10 SDK, and the Windows 10/11 SDK (10.0.19041 or newer). The solution supports x86 and x64 builds.

```powershell
dotnet restore InkMotion.sln
dotnet build InkMotion.sln -c Debug -p:Platform=x64
dotnet test InkMotion.sln -c Debug -p:Platform=x64
dotnet run --project src/InkMotion/InkMotion.csproj
```

Native media capabilities depend on the codecs installed on Windows. GitHub Actions builds and tests both x86 and x64 on every branch push and on pull requests targeting `main` or `master`.
