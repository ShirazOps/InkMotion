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
