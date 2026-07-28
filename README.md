# VRC Photo Manager

A Windows desktop tool for curating and uploading VRChat photos to [VRCDN](https://vrcdn.live)
object storage — browse a thumbnail grid, pick what goes up and what doesn't, and track
upload status per photo, instead of blanket-uploading a whole library by filter alone.

> **⚠️ Not affiliated with VRCDN.** This is an independent, unofficial, community-made tool.
> It is not endorsed by, sponsored by, or connected to VRCDN in any way. It talks to VRCDN's
> panel via an API discovered through normal browser inspection of the public web UI — not a
> published or supported integration — and VRCDN could change or break it at any time without
> notice. "VRCDN" is a trademark of its respective owner; this project's icon is an original
> design and does not use VRCDN's logo or branding.

## Features

- **Thumbnail grid** — adjustable size, hover preview (after a brief pause so scrolling
  doesn't spam previews), scrolls smoothly through large libraries via real UI virtualization.
  Click a thumbnail (or its checkbox) to select it.
- **Filter & sort** — by rating, upload status, VRCX player/author name (substring match), and
  sort by filename or capture date (newest/oldest first).
- **Per-photo badges** — upload status (Not Uploaded / Uploading / Uploaded / Failed), content
  rating, detected-face count, and whether VRCX metadata was found for that photo.
- **Local nudity/content classifier** — runs the open [WD14 tagger](https://huggingface.co/SmilingWolf)
  model in-process via ONNX Runtime (DirectML-accelerated), entirely on-device. No photos or
  classification results leave your machine for this step.
- **Local anime-face detection** — **Scan Faces** finds anime-style faces (an LBP cascade
  trained for stylized/rendered faces, since detectors trained on real photos miss VRChat
  avatars) and shows a per-photo face count. Ships bundled with the app, no separate setup.
- **VRCX metadata capture** — Scan Library reads the author, world, and player list VRCX embeds
  directly into each screenshot's PNG metadata at capture time (both display names and stable
  VRChat user IDs), when VRCX was running to record it. Right-click a photo → **View
  Metadata...** to see everything captured for it.
- **Crop Print Borders** — detects photos in VRChat's in-game "Print" format (padded to
  2048×1440) and crops the white border back to the real 1920×1080 content, without touching
  the original file.
- **SQLite-backed, EF Core 10** — tracks local file metadata, ratings, and per-photo upload
  status, with automatic migrations on startup.
- **Session login via embedded WebView2** — no external browser required; the login flow
  happens inside the app.
- **Sync Metadata** — reconciles the local index against what's actually on VRCDN (matching by
  filename, with a fallback for objects uploaded under a reformatted name by an older tool), so
  a fresh install doesn't risk re-uploading photos that already made it up some other way.
  Safe to re-run any time.
- **Upload Selected / Remove from VRCDN** — upload picks resize to fit VRChat's image-loader
  limits before going up; Remove deletes selected, already-uploaded photos from VRCDN's storage
  (confirmed first — there's no undo except re-uploading).
- **Settings screen** — configure where the WD14 model files live, with a one-click download
  straight from Hugging Face (see [Local classifier setup](#local-classifier-setup)).

## Requirements

- Windows 10/11
- [.NET 10 SDK](https://dotnet.microsoft.com/download) (or the runtime, if only running a
  published build)
- [WebView2 Runtime](https://developer.microsoft.com/microsoft-edge/webview2/) (ships with
  Windows 11 by default; Windows 10 may need it installed separately)
- A VRCDN account with panel access

## Building & running

```powershell
dotnet build
dotnet run
```

On first run, click **Login** to authenticate via the embedded browser, then **Scan Library**
to index a folder of photos.

## Local classifier setup

The nudity/content classifier (**Classify Photos**) needs two files from the
[SmilingWolf/wd-vit-tagger-v3](https://huggingface.co/SmilingWolf/wd-vit-tagger-v3) model
(or a compatible WD14-family model):

```
model.onnx           (~378 MB)
selected_tags.csv    (~300 KB)
```

**Easiest way:** open **Settings** in the app, browse to (or type) a folder for the model
files, and click **Download Model Files** — it fetches both directly from Hugging Face and
saves the folder for next time. Restart the app afterward for it to pick up the change.

**Manual alternative:** download both files yourself into a `wd14-model` folder next to the
built exe:

```
wd14-model\model.onnx
wd14-model\selected_tags.csv
```

If neither is set up, **Classify Photos** is simply disabled at startup (with a status-bar
message saying so) — everything else works normally, including the anime-face detector, which
is bundled with the app and needs no separate download.

## Planned work

- **Import/export lists** — a general mechanism to import and export the ratings list and the
  registered-people/tag list (currently only a per-machine SQLite table), so this data isn't
  locked to one machine's database. Replaces the old narrow "Import Ratings" feature (removed -
  it only read from one specific external WD14-pipeline `index.db` format).

## How the VRCDN integration works

There's no published VRCDN API. This app replicates the same request flow the browser makes
to VRCDN's panel — request a presigned upload URL, then PUT the file directly — reverse
engineered by inspecting the panel's own network traffic. It is not guaranteed to keep
working if VRCDN changes their panel implementation.

## License

MIT — see [LICENSE](LICENSE).
