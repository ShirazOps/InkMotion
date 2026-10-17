# InkMotion

InkMotion is a native Windows video editor in early development, built with C#, .NET 10, WinUI 3, and the Windows App SDK.

## First milestone

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
- Play and scrub to synchronise the drawing exposure with video playback at 12, 24, 30, or 60 fps. Playback extends the ink timeline as needed.
- Add audio clips to separate tracks (MP3, WAV, M4A, AAC, WMA, or FLAC). Clips start at the playhead and can be repositioned to it later; each track has independent volume and mute controls.
- Edit audio title, artist, album, genre, and year tags for the selected source. Write supported metadata back to the source only with **Write tags to audio file**; project comments remain project-only.
- Play and scrub audio alongside video, or play an audio-only timeline.
- Generate in-between frames by interpolating corresponding keyframe strokes, resampling point paths when their point counts differ. Edit an in-between frame to make it a new keyframe.
- View Video, Ink, multiple Audio, and Effects track lanes, with each audio lane showing its clip and start frame.
- Save and reopen `.inkmotion` project files, including linked video and audio paths, audio track settings and tags, frame drawings, ink layers, and keyframe tween settings. Versions 1, 2, and 3 are migrated when opened.
- Preserve the chosen project frame rate (12, 24, 30, or 60 fps) in the project file.
- Undo and redo completed ink strokes, erasures, frame additions, duplication and deletion, keyframe changes, and ink-layer visibility/opacity changes.

Project files store paths to the original video and audio rather than embedding the media. Keep the sources available or relink them with **Open video** and **Add audio** after opening the project.

## Build

Requirements: Windows 11, the .NET 10 SDK, and the Windows 10/11 SDK (10.0.19041 or newer).

```powershell
dotnet restore InkMotion.sln
dotnet build InkMotion.sln -c Debug -p:Platform=x64
dotnet test InkMotion.sln -c Debug -p:Platform=x64
dotnet run --project src/InkMotion/InkMotion.csproj
```

The app currently targets x64. Native media capabilities depend on the codecs installed on Windows.

GitHub Actions builds the x64 solution and runs its tests on Windows for every branch push and pull requests targeting `main` or `master`.
