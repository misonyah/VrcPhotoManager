# Changelog

## 1.0.0

First versioned release.

### Highlights

- Thumbnail grid with filtering (rating, upload status, face count, world occupancy, face-match
  confidence, avatar type, player, upload crop) and sorting, adjustable thumbnail size, and a
  hover preview pop-up.
- VRCDN session login via embedded WebView2, with automatic background re-login when the session
  expires (reusing the persisted Patreon session) and a red Login-button highlight plus a
  10-minute keep-alive ping when it can't recover on its own.
- Upload / Sync Metadata / Remove from VRCDN, with robust filename matching against VRCDN's
  server-side filename reformatting.
- Per-photo upload cropping: hover a photo and press `[` / `]` to cycle it through crop-ratio
  presets, and arrow keys to nudge the crop's position within the photo - both remembered until
  the photo is actually uploaded. A live crop-line preview shows exactly what will be cut (or, for
  an already-uploaded photo, what was actually cut). See the README's Keyboard Shortcuts section.
- Local, on-device ML: WD14 nudity/content classifier, an avatar-base classifier trained on
  Booth.pm listings, anime-style face detection, and CLIP-based face-match suggestions.
- Face tagging with alias tracking, duplicate-person merge prompts, and gamelog cross-referencing
  for photos with no VRCX player data.
- VRCX metadata capture (author/world/players) embedded in each photo's PNG at capture time.
- Crop Print Borders for VRChat's in-game Print format.
- Session-state persistence (window size/position, thumbnail size, scroll position) and a
  single-instance guard to prevent concurrent SQLite writers.
