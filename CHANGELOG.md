# Changelog

## 1.1.0

### Face detection & matching

- Replace the bundled LBP cascade face detector with a YOLOv8 model (deepghs/anime_face_detection)
  - a real, measured recall win, especially on close-up multi-person photos.
- Switch face matching from a general-purpose CLIP model to CCIP (contrastively trained to tell
  anime characters apart), with nearest-neighbor scoring for well-populated people and an averaged
  centroid fallback for small/noisy reference sets.
- Add per-photo elimination to Suggest Faces (a person already confirmed or claimed elsewhere in
  the same photo is excluded as a candidate for its other faces) and fix a stale-suggestion bug it
  exposed.
- Fix face-tag identity merging around VRCX's empty-string "unresolved player" sentinel, which had
  been silently merging unrelated unresolved people.

### Tag Faces

- The person picker now shows a friend/self indicator glyph, and ranks "in this instance"
  candidates by live match score against the face being tagged.
- Split the old combined "All tagged" button into independent "Confirm N faces" (accepts pending
  suggestions) and "Remove N untagged faces" (clears bad detections) actions.
- Add sort-by-suggestion-confidence and sort-by-tagging-value options to the main grid.

### Metadata & gamelog

- Add a "Traveled together" section to Photo Metadata: friends whose departure from your previous
  instance and arrival into this one both fall within a configurable window of your own
  transition - a portal/invite-hop detector, in other words.

### General

- Persist the main window's filters and sort order across restarts, the same way window size
  already was.
- Split the Settings window into tabs (Models / Upload & Sharing / General) now that it had grown
  past a single comfortable column.
- Add a Gist-published photo index, VRCDN quota display, and rework crop/upload tracking.
- Add CODE_SIGNING.md and PRIVACY.md ahead of a SignPath Foundation application.

## 1.0.1

- Add an installer and automatic background updates via [Velopack](https://velopack.io) -
  releases now publish to GitHub Releases via CI on a version tag push. See the README's
  Installing/Releasing sections.

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
