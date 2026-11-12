# HEIC/HEIF support (fork)

This fork lets ShareX open iPhone photos (`.heic`, `.heif`, `.hif`) everywhere it opens images:
image viewer, image editor, main window and history thumbnails, tools (resizer, converter, watermark,
combiner, splitter, thumbnailer, OCR, QR code, background remover, icon converter, ...) and file upload.

Decoding uses [libheif](https://github.com/strukturag/libheif) + libde265 (LGPL), loaded at runtime
from `heif.dll` next to `ShareX.exe`. Microsoft's HEIF/HEVC Store extensions are not needed.
If `heif.dll` is missing, ShareX works as before and HEIC files simply fail to open.

## Build

1. Build libheif once (needs Visual Studio C++ build tools and git):

   ```powershell
   .\Scripts\Build-LibHeif.ps1              # x64
   .\Scripts\Build-LibHeif.ps1 -Platform ARM64
   ```

   This puts `heif.dll` and `libde265.dll` in `Lib\libheif\win-x64` (ignored by git).

2. Build ShareX as usual (`ShareX.sln`, Release, x64). The DLLs are copied to the output folder.

Or run the **Build ShareX (HEIC)** workflow from the Actions tab of your fork; it produces a ready-to-run
folder as an artifact.

## Behaviour

- Rotation/mirroring stored in the HEIF file is applied; Display P3 photos are converted to sRGB.
- ShareX cannot write HEIC, so an edited HEIC never overwrites the original: *Save* writes a copy next to it
  with the configured image format (e.g. `IMG_0001.png`), *Save as* suggests a `.png` name.
- Uploading a HEIC file with an image uploader uploads a JPEG copy (quality from Task settings > Image),
  because most image hosts reject HEIC. File uploaders still receive the original file.
- `heic`, `heif`, `hif` are added once to *Task settings > Advanced > ImageExtensions* of existing settings.
