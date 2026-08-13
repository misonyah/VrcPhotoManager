# Privacy Policy

VRC Photo Manager does not collect analytics or telemetry, and does not transfer any
information to networked systems except as described below.

## Automatic (no explicit per-use request)

- **Update check**: on startup, the app asks GitHub's public Releases API whether a newer
  version exists, and downloads it in the background if so (applied on your next restart - see
  [CHANGELOG.md](CHANGELOG.md) and [CODE_SIGNING.md](CODE_SIGNING.md)). No information about you
  or your files is sent - only a standard HTTP request for the latest release metadata.

## User-initiated (only when you take the corresponding action)

- **VRCDN login/upload/sync/remove**: logging in, uploading photos, syncing metadata, or
  removing photos from VRCDN sends your session cookie and (for uploads) the photo itself to
  [VRCDN](https://vrcdn.live)'s servers. See VRCDN's own privacy policy for how they handle it.
- **Local model downloads**: clicking Download in Settings fetches model files from
  [Hugging Face](https://huggingface.co) (misonyah/vrc-avatar-classifier, SmilingWolf/wd-vit-tagger-v3,
  laion/CLIP-ViT-L-14-laion2B-s32B-b82K).

## Local-only (never leaves your machine)

- Your photo library, VRCX's own local database (read-only), face-tagging/classification
  results, ratings, and app settings are stored entirely in a local SQLite database under
  `%LOCALAPPDATA%\VrcdnManager\` (the pre-rename folder name, kept stable so existing installs
  keep their data). Nothing here is transmitted anywhere.
- All ML classification (nudity/content rating, avatar-base detection, face detection,
  face-match suggestions) runs on-device via ONNX Runtime - no photo or classification result is
  sent anywhere for this.
