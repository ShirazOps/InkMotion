# InkMotion privacy policy

**Last updated: 24 October 2026**

InkMotion is a Windows video, audio and ink-animation editor provided by Shiraz Ops. This policy describes how the application handles your information.

## Local processing, not a cloud service

InkMotion processes your projects, video, images, audio, text and drawings on your computer. The application does not require an account and does not operate an online storage service.

InkMotion does not send your media, projects or recordings to Shiraz Ops. It does not include advertising, behavioural tracking or application analytics, and does not automatically send crash reports or usage information to us. We do not sell your information or share it for advertising.

## Files and information used by the application

InkMotion accesses the media and project files you choose to open, import, relink or export. Projects can contain text, drawings, edit settings, media metadata and paths identifying files on your computer. Source media normally remains in its original location rather than being copied into the project.

The application also stores working data locally, including:

- Editor layout preferences.
- Microphone recordings and generated video proxies.
- Export job history, saved project snapshots, output and linked-source paths, progress and error details.
- Temporary rendering, audio-mixing and encoder-probe files, and completed video segments used to recover interrupted optional FFmpeg exports.

These files can contain personal information if your content, file names or paths contain it. Export job snapshots can include project text and drawings even after you close the editor.

Local working data is generally stored under `%LOCALAPPDATA%\InkMotion`; temporary files can also be stored in your Windows temporary directory or beside a chosen export destination. Windows packaging may redirect application storage. Saved projects and exported files are stored where you choose.

## Microphone, camera and input

Microphone recording starts when you use the recording controls and stops when you stop recording or close the recording session. Recordings are saved locally and are not uploaded by InkMotion. You can manage microphone access through Windows privacy settings.

InkMotion uses mouse, touch and pen input for editing and drawing. It does not use your camera, contacts or location.

## Background exports and optional tools

Export jobs run in separate local processes and can continue after you close the editor. They use saved project snapshots and access linked media to render the output. Closing the window alone does not cancel an export; use the export cancellation controls to stop a job.

If you choose to use a locally installed FFmpeg executable, InkMotion passes it media data and local file paths needed for encoding. FFmpeg is optional and is not bundled with or automatically downloaded by InkMotion. Use a build you trust; independently installed tools have their own licensing and privacy practices.

## Windows, the Microsoft Store and other services

Microsoft may process information relating to Store downloads, purchases, updates, permissions or Windows diagnostics under the [Microsoft Privacy Statement](https://privacy.microsoft.com/privacystatement). Such processing is controlled by Microsoft and your Windows settings, not by InkMotion.

If you choose files on a network share or in a cloud-synchronised folder, Windows or your storage provider may transfer those files as part of that service. InkMotion does not control those services or their retention policies.

## Retention and your choices

You control your saved projects, recordings and exports and can delete them using Windows file-management tools. Local export history and snapshots remain until removed; interrupted or cancelled FFmpeg video jobs may retain completed segments for retry. Successful FFmpeg video exports remove their segment files, but retain job history.

Before removing working data, stop recording, cancel or finish active exports and close InkMotion. Back up anything you need: deleting working folders may remove recordings, proxies, saved job snapshots or recovery data. Uninstalling the application may not remove all files created by the desktop app. Windows backups, cloud providers and independently installed tools can retain their own copies.

## Questions and policy changes

For privacy questions, contact Shiraz Ops through the [InkMotion GitHub issue tracker](https://github.com/ShirazOps/InkMotion/issues). Issues are public: do not post private media, credentials or personal information. We receive only information you voluntarily submit there; GitHub handles that information under its own privacy statement.

This policy will be updated if the application's data practices change. The date above identifies the latest revision.
