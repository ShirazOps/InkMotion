# InkMotion

InkMotion is a native Windows video editor in early development, built with C#, .NET 10, WinUI 3, and the Windows App SDK.

## First milestone

- Provide separate Video Editor, Audio Editor, and Ink Editor workspaces with shared project and transport controls.
- Build native x86 and x64 Windows targets, and create an x86/x64 MSIX upload bundle for the Microsoft Store after Partner Center identity association.
- Use the Video Editor for video preview and playback, the Audio Editor for multitrack clip placement, waveform previews, mixing, and tag editing, and the Ink Editor for pen-first drawing and cel animation.
- Add typed text overlays centred in animation cels, with selectable font family, size, and brush colour, separate from pressure-sensitive freehand ink.
- Select an export profile for 720p, 1080p, 1440p, or 4K UHD while retaining the project's supported 12, 24, 30, or 60 fps setting.
- Open local MP4, MOV, M4V, WMV, AVI, AV1, MKV, or WebM video files in the preview. AV1 playback depends on an AV1 decoder being available on Windows.
- Draw with a mouse, pen, or touch directly over the preview.
- Preserve Windows pen pressure in ink strokes, including through project save/load, undo, and in-between animation.
- Render the ink overlay through Win2D/Direct2D on the GPU; Windows' media playback stack uses hardware video decoding when the installed codec and driver support it.
- Set the ink brush colour and size for new strokes; existing strokes keep their saved appearance.
- Create independent ink layers, switch the active drawing layer, control visibility and opacity, and preserve each layer through save/load and undo/redo.
- Keep independent ink drawings on timeline frames, move between them, and duplicate a drawing to a new frame.
- Delete selected frames while maintaining a valid, sequentially numbered timeline and at least one frame per project.
- Use optional onion-skin guides for the previous and next drawings, with adjustable opacity.
- Mark frames as keyframes to define the endpoints for in-between animation.
- Choose linear, ease-in, ease-out, or ease-in/ease-out tweening from each keyframe to the next.
- Set each cel's exposure length in output frames; playback, video seeking, audio timing, and tween spacing follow the exposure sheet.
- Play and scrub to synchronise the drawing exposure with video playback at 12, 24, 30, or 60 fps. Playback extends the ink timeline as needed.
- Add audio clips to separate tracks (MP3, WAV, M4A, AAC, WMA, or FLAC). Clips start at the playhead and can be repositioned to it later; each track has independent volume and mute controls.
- Edit audio clips non-destructively: trim the start or end at the playhead, split a clip at the playhead (the second part moves to a new track below), set fade-in and fade-out lengths, and normalise a clip's peak to -1 dBFS or reset its gain. Waveform lanes show the trimmed range and fade envelope, and edits support undo/redo.
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

The new workspaces organise the current capabilities; they do not yet provide a full nonlinear editing or audio workstation. The Audio Editor displays decoded per-clip waveforms, and supports trim, split, fades, normalisation, tempo, pitch, and mix layouts up to 12.1. Multichannel output to surround devices and surround file export remain planned. Image tracks, video clip trim/split operations, and final media encoding are also planned in separate increments. The export profile selector currently configures requested output dimensions (720p, 1080p, 1440p, or 4K UHD); actual video encoding is not yet available. Export targets are intended to use supported project rates of 12, 24, 30, or 60 fps. Codec, channel-layout, and encoding support will depend on a verified Windows encoding pipeline.

Project files store paths to the original video and audio rather than embedding the media. Keep the sources available or relink them with **Open video** and **Add audio** after opening the project.

## Windows builds and Microsoft Store packaging

Build either desktop architecture with `dotnet build InkMotion.sln -c Release -p:Platform=x86` or `-p:Platform=x64`. The Windows CI workflow builds and tests both configurations.

To make a Store upload, reserve the app in Partner Center and use Visual Studio's **Package and Publish → Associate App with the Store** to obtain its exact package identity. Replace both the `Identity` name and publisher in `src/InkMotion/Package.appxmanifest` with the values assigned by Partner Center, and increment its four-part version for each release. Then run the **Microsoft Store package** workflow manually or push a `v*` tag; it creates an unsigned x86/x64 `.msixupload` bundle for Partner Center, where Microsoft signs Store submissions. Store packaging restores both runtime identifiers and retains the required `runFullTrust` capability in the upload manifest. The checked-in name and publisher are deliberately placeholders, and the workflow refuses to package until both are replaced. Do not commit signing certificates.

## Build

Requirements: Windows 11, the .NET 10 SDK, and the Windows 10/11 SDK (10.0.19041 or newer). The solution supports x86 and x64 builds.

```powershell
dotnet restore InkMotion.sln
dotnet build InkMotion.sln -c Debug -p:Platform=x64
dotnet test InkMotion.sln -c Debug -p:Platform=x64
dotnet run --project src/InkMotion/InkMotion.csproj
```

Native media capabilities depend on the codecs installed on Windows. GitHub Actions builds and tests both x86 and x64 on every branch push and on pull requests targeting `main` or `master`.
