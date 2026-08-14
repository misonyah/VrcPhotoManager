# VRC Photo Manager

[![ko-fi](https://ko-fi.com/img/githubbutton_sm.svg)](https://ko-fi.com/misonyah)

See [CHANGELOG.md](CHANGELOG.md) for release history.

A Windows desktop tool for curating and uploading VRChat photos to [VRCDN](https://vrcdn.live)
object storage — browse a thumbnail grid, pick what goes up and what doesn't, and track
upload status per photo, instead of blanket-uploading a whole library by filter alone.

> **⚠️ Not affiliated with VRChat Inc. or VRCDN.** This is an independent, unofficial,
> community-made tool. It is not endorsed by, sponsored by, or connected to either in any way.
> It reads photos and data from your own local VRChat/VRCX installation, and talks to VRCDN's
> panel via an API discovered through normal browser inspection of the public web UI — not a
> published or supported integration — so either could change or break it at any time without
> notice. "VRChat" is a trademark of VRChat Inc.; "VRCDN" is a trademark of its respective
> owner. This project's icon is an original design and does not use either party's logo or
> branding.

## Features

- **Thumbnail grid** — adjustable size, hover preview (after a brief pause so scrolling
  doesn't spam previews), scrolls smoothly through large libraries via real UI virtualization.
  Click a thumbnail (or its checkbox) to select it.
- **Filter & sort** — by rating, upload status, detected-face count, VRCX-recorded world
  occupancy, and face-match suggestion confidence. The player filter is a fuzzy, Unicode-
  tolerant autocomplete search (matches stylized VRChat names and recorded aliases, not just
  exact substrings) rather than a plain dropdown. Sort by filename, capture date (newest/oldest
  first), remaining untagged faces, or busiest instance.
- **Per-photo badges** — upload status (Not Uploaded / Uploading / Uploaded / Failed), content
  rating, detected-face count, and whether VRCX metadata was found for that photo.
- **Local nudity/content classifier** — runs the open [WD14 tagger](https://huggingface.co/SmilingWolf)
  model in-process via ONNX Runtime (DirectML-accelerated), entirely on-device. No photos or
  classification results leave your machine for this step.
- **Local avatar-base classifier** — **Classify Avatars** identifies which VRChat avatar base
  (e.g. "Manuka", "Rusk") is worn in each photo, via a locally-run ONNX model trained on
  Booth.pm avatar listings (see [Local model setup](#local-model-setup)). Shown as a thumbnail
  badge and filterable via the Avatar dropdown; below-confidence results are tracked separately
  from never-classified ones so a later, more complete model can be re-run against just those.
- **Local anime-face detection** — **Detect Faces** finds anime-style faces (an LBP cascade
  trained for stylized/rendered faces, since detectors trained on real photos miss VRChat
  avatars) and shows a per-photo face count. Ships bundled with the app, no separate setup.
- **Face tagging (Tag Faces)** — click a detected face box to tag who it is, or click-drag on
  empty space to add a box the detector missed. Search by name across registered people, VRCX
  friends, and everyone else VRCX has recorded (friends and gamelog history alike), tolerant of
  stylized Unicode display names. People can be linked to a real VRC account or created as a
  plain name with no linked account.
  - **Previous names (aliases)** — VRCX rename history is captured automatically, and you can
    add your own; searching an old name still finds the right person, shown in parentheses only
    when that's what matched.
  - **Duplicate-person merge prompts** — if someone was tagged by typed name before their VRC
    account was found, Tag Faces flags the resulting duplicate and offers to merge it into the
    real account with one click, combining their tags.
  - **Suggest Faces** — suggests who's in each untagged face using CLIP embedding similarity
    against a person's confirmed reference photos. Suggestions are never applied automatically;
    review and confirm (or correct) them in Tag Faces.
  - **Cross-reference Gamelog** — a fallback for photos with no VRCX player data at all (e.g.
    taken by someone else nearby): infers who was likely present by matching the photo's
    capture time against your own VRCX gamelog. Also backfills world name when missing, using
    the same gamelog data.
  - **Sync VRC Players** — refreshes the local player cache and name-history aliases from
    VRCX's friends list and gamelog. Tag Faces' search reads this cache instead of querying
    VRCX live (which gets slow on a long play history), so run this occasionally to pick up
    new friends, renames, or people you've recently played with.
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
  happens inside the app. If the session expires, the app automatically retries in the
  background (reusing the still-valid underlying Patreon login) before falling back to asking
  you to log in again — see the Login button's tooltip/red highlight if it can't recover.
- **Sync Metadata** — reconciles the local index against what's actually on VRCDN (matching by
  filename, with a fallback for objects uploaded under a reformatted name by an older tool), so
  a fresh install doesn't risk re-uploading photos that already made it up some other way.
  Safe to re-run any time.
- **Upload Selected / Remove from VRCDN** — upload resizes to fit VRChat's image-loader limits
  before going up, with an optional per-photo crop (see [Keyboard shortcuts](#keyboard-shortcuts)
  below); Remove deletes selected, already-uploaded photos from VRCDN's storage (confirmed first
  — there's no undo except re-uploading). Uploads are re-encoded as JPEG by default (Settings'
  "Upload Image Format" section also offers lossless PNG), regardless of the local file's own
  format, and carry no metadata — no VRCX author/world/player data, no EXIF/XMP; none of that is
  attached to what actually goes to VRCDN. Your original file on disk is never modified. **Note:**
  VRCDN's own API reports every object's URL as ending in `.png` regardless of what was actually
  uploaded (confirmed live - this app's own JPEG uploads all come back as `.png` URLs too), so the
  URL's extension is not a reliable way to tell what format a photo was really uploaded as; the
  cloud badge's gray-vs-cyan color reflects what this app actually sent instead.
- **VRCDN photo index for Udon** — **Update VRCDN Index** publishes a list of every currently-
  uploaded photo's URL (and pixel dimensions) to a GitHub Gist, for a Udon world script to
  randomly pick one from (e.g. a photo-frame prop). Needs a GitHub token scoped to `gist` only
  (Settings has a one-click link to generate one correctly scoped) - no repository is created or
  touched. The gist's URL never changes across updates (unlike VRCDN itself, which mints a new
  URL on every upload), so your world's reference to it is set once. Default format is CSV
  (`url,width,height` per line, no header) - the easiest shape to parse in Udon
  (`VRCStringDownloader.LoadUrl` → `string.Split`), while still letting your script size a
  display quad to the right aspect ratio before the image loads; `txt` (URL-only) and `json`
  are also available in Settings.
- **Settings screen** — configure where each local model's files live, with a one-click
  download straight from Hugging Face that checks the current version first and skips
  re-downloading if you already have it, and shows whether/when each model was last downloaded
  (see [Local model setup](#local-model-setup)).

## Keyboard shortcuts

| Keys | Action |
| --- | --- |
| `Ctrl+F` | Open the standalone Filter window |
| Arrow keys, while hovering a photo | Nudge where its upload crop sits within the photo (see [Upload crop](#upload-crop-position--per-photo-ratio) below) |
| `[` / `]`, while hovering a photo | Cycle that photo's own crop-ratio preset backward/forward |

### Upload crop position & per-photo ratio

There's no batch-wide crop setting - every photo uploads at its own original resolution
(uncropped) unless you give it a crop individually:

- **Hover a photo** to see white crop lines showing what will actually be cut (or, for an
  already-uploaded photo, what was actually cut) - a small badge in the corner names the ratio
  currently in effect for it.
- **`[` / `]`** cycle that photo through the crop-ratio presets (1:1, 3:4, 4:3, 9:16, 16:9,
  Original) - see Settings' "Upload Crop Presets" panel for the exact ratios and example
  resolutions. Cycling past the last preset (or before the first) passes through "no crop" so
  you can always get back to the default without counting presses.
- **Arrow keys** nudge the crop's position within the photo (left/right and up/down), instead of
  always centering it.
- Adjusting an already-**Uploaded** photo reverts it back to Not Uploaded first (its live VRCDN
  copy isn't touched) so the new crop can actually be uploaded - select it and click **Upload
  Selected** again when ready.

Both the position and the per-photo ratio are remembered (persisted locally, and NOT reset after
upload) so you can adjust several photos before hitting **Upload Selected**, and the preview
keeps reflecting the real crop even after it's live.

## Requirements

- Windows 10/11
- [.NET 10 SDK](https://dotnet.microsoft.com/download) (or the runtime, if only running a
  published build)
- [WebView2 Runtime](https://developer.microsoft.com/microsoft-edge/webview2/) (ships with
  Windows 11 by default; Windows 10 may need it installed separately)
- A VRCDN account with panel access
- [VRCX](https://github.com/vrcx-team/VRCX) installed and used to play, for player/friend
  search, VRCX metadata capture, and the gamelog-based features — everything else works without
  it, just with less player-identification data available

## Installing

Download the latest installer from the [Releases page](https://github.com/misonyah/VrcPhotoManager/releases)
(`VrcPhotoManager-win-Setup.exe`) and run it - it installs to your user profile (no admin rights
needed) and adds Start Menu/Desktop shortcuts. **The installer and updates are currently
unsigned**, so Windows SmartScreen will show an "unknown publisher" warning the first time you
run it — click "More info" → "Run anyway". This is normal for small independently-published
Windows apps without a paid code-signing certificate. See [CODE_SIGNING.md](CODE_SIGNING.md) for
this project's **Code signing policy** (applying for [SignPath Foundation](https://signpath.org/)
- not yet active) and [PRIVACY.md](PRIVACY.md) for what data this app does and doesn't transmit.

The app checks for updates in the background on startup and downloads them automatically;
a downloaded update is applied the next time you restart the app - no separate updater step.

## Building & running from source

```powershell
dotnet build
dotnet run
```

On first run, click **Login** to authenticate via the embedded browser, then **Scan Library**
to index a folder of photos.

### Releasing

CI ([`.github/workflows/build.yml`](.github/workflows/build.yml)) compiles every push/PR as a
fast sanity check. An actual release only happens when a `v*.*.*` tag is pushed
([`.github/workflows/release.yml`](.github/workflows/release.yml)), which publishes a
self-contained win-x64 build, packages it with [Velopack](https://velopack.io) (installer +
delta-update feed), and uploads it to a GitHub Release.

To cut a release:

1. Bump `<Version>` in `VrcPhotoManager.csproj` and add an entry to `CHANGELOG.md`.
2. Commit that change.
3. Tag it and push the tag: `git tag vX.Y.Z && git push origin vX.Y.Z` (also push the commit
   itself if it isn't already on the remote).
4. The Release workflow does the rest - watch the Actions tab, then check the Releases page.

To add code signing later (see the CHANGELOG/commit history for why it's currently skipped):
add a signing step to `release.yml` before the `vpk pack` step (or pass `--signParams`/
`--azureTrustedSignFile` directly to `vpk pack`, per [Velopack's signing docs](https://docs.velopack.io/packaging/signing)) -
nothing else in this workflow needs to change.

## Local model setup

Three optional local ONNX models add features; the app works without any of them, just with
those specific buttons disabled at startup (with a status-bar message saying so). The
anime-face detector needs no setup at all — it's bundled with the app.

If a model folder isn't configured, it defaults to
`%LOCALAPPDATA%\VrcPhotoManager\Models\<WD14|CLIP|Avatar>\` — stable across app updates and any
future packaged release, unlike a path tied to the exe's own install location. (WD14 also
still checks a `wd14-model` folder next to the exe first, for backward compatibility with
existing installs that already rely on it.) Clearing a model folder's path and clicking
**Save** resets it back to this default next time Settings opens.

In **Settings**, each model section shows whether it's already downloaded and when. Clicking
**Download** checks Hugging Face's current version first (a cheap request, not a re-download)
and reports "Already up to date" instead of re-fetching if nothing changed. **Restart the app
after a real download for it to actually load the new model** — models are loaded once at
startup, so Settings downloading a file doesn't hot-swap it into the running session.

### WD14 (nudity/content classifier)

Needed by **Classify Photos**. Two files from
[SmilingWolf/wd-vit-tagger-v3](https://huggingface.co/SmilingWolf/wd-vit-tagger-v3) (or a
compatible WD14-family model):

```
model.onnx           (~378 MB)
selected_tags.csv    (~300 KB)
```

### CLIP (face-match suggestions)

Needed by **Suggest Faces**. One file from
[laion/CLIP-ViT-L-14-laion2B-s32B-b82K](https://huggingface.co/laion/CLIP-ViT-L-14-laion2B-s32B-b82K)
(image encoder only, ~1.2 GB):

```
model.onnx           (~1.2 GB)
```

### Avatar-base classifier

Needed by **Classify Avatars**. Two files from
[misonyah/vrc-avatar-classifier](https://huggingface.co/misonyah/vrc-avatar-classifier),
trained on Booth.pm avatar-listing images (scraper tool lives outside this repo — training
images are never published, only the resulting model weights):

```
model.onnx
labels.txt
```

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
