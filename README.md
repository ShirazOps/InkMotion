# InkMotion

InkMotion is a native Windows video editor in early development, built with C#, .NET 10, WinUI 3, and the Windows App SDK.

## First milestone

- Open a local MP4, MOV, or M4V video in the preview.
- Draw with a mouse, pen, or touch directly over the preview.
- Keep independent ink drawings on timeline frames, move between them, and duplicate a drawing to a new frame.
- Mark frames as keyframes. Keyframe interpolation is planned for a later milestone.
- View the initial Video, Ink, Audio, and Effects track lanes.
- Save and reopen `.inkmotion` project files, including the linked video path, frame drawings, and keyframe markers.
- Undo and redo completed ink strokes, erasures, frame additions, frame duplications, and keyframe changes.

Project files store a path to the original video rather than embedding the media. Keep the source video available or relink it with **Open video** after opening the project.

## Build

Requirements: Windows 11, the .NET 10 SDK, and the Windows 10/11 SDK (10.0.19041 or newer).

```powershell
dotnet restore InkMotion.sln
dotnet build InkMotion.sln -c Debug -p:Platform=x64
dotnet test InkMotion.sln -c Debug -p:Platform=x64
dotnet run --project src/InkMotion/InkMotion.csproj
```

The app currently targets x64. Native media capabilities depend on the codecs installed on Windows.
