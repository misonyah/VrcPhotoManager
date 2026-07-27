# VRCDN Manager

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
- **Per-photo selection** — pick exactly what gets uploaded, with a status badge (Not
  Uploaded / Uploading / Uploaded / Failed) and a rating badge (see below) on every thumbnail.
- **Local nudity/content classifier** — runs the open [WD14 tagger](https://huggingface.co/SmilingWolf)
  model in-process via ONNX Runtime (DirectML-accelerated), entirely on-device. No photos or
  classification results leave your machine for this step.
- **SQLite-backed, EF Core 10** — tracks local file metadata, ratings, and per-photo upload
  status, with automatic migrations on startup.
- **Session login via embedded WebView2** — no external browser required; the login flow
  happens inside the app.
- **Sync Metadata** — reconciles the local index against what's actually on VRCDN, so a
  fresh install doesn't risk re-uploading photos that already made it up some other way.

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

The nudity/content classifier expects the WD14 ONNX model files in a `wd14-model` folder next
to the built exe:

```
wd14-model\model.onnx
wd14-model\selected_tags.csv
```

(from [SmilingWolf/wd-vit-tagger-v3](https://huggingface.co/SmilingWolf/wd-vit-tagger-v3) or
a compatible WD14-family model). If that folder doesn't exist, **Classify Photos** is disabled;
everything else still works.

**Import Ratings** is a shortcut for anyone already running a separate WD14 tagging pipeline
that produces a SQLite db with a `photos(path, rating)` table — point it at `wd14-index.db`
next to the exe. Most people won't have this; **Classify Photos** covers the same need
standalone.

## How the VRCDN integration works

There's no published VRCDN API. This app replicates the same request flow the browser makes
to VRCDN's panel — request a presigned upload URL, then PUT the file directly — reverse
engineered by inspecting the panel's own network traffic. It is not guaranteed to keep
working if VRCDN changes their panel implementation.

## License

MIT — see [LICENSE](LICENSE).
