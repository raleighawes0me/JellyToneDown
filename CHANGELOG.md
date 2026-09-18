# Changelog

The release workflow reads the section matching the version being released and puts it in
`manifest.json`, which is what Jellyfin shows in its plugin catalogue. **A release fails if
there is no section for its version**, so this file cannot silently fall out of date.

Headings may be written as `## 1.2.3` or `## 1.2.3.0`; both match the same release.

## 1.0.2

- Fixed the per-user settings page always reporting an expired session. Jellyfin 12 ships a
  migration that disables legacy authorization, which switches off the `X-Emby-Token` header
  the page was authenticating with. It now uses the standard `Authorization: MediaBrowser`
  scheme.
- Added a plugin icon.
- Per-version release notes: every version used to read "Initial release" in the catalogue.

## 1.0.1

- Added a per-user settings page at `/JellyToneDown/MySettings`, so users can set their own
  theme volume without dashboard access. (Shipped broken - fixed in 1.0.2.)
- Adjusted copies are now encoded at the lower of the source bitrate and a per-codec ceiling
  rather than always around 190 kbps, so a 128 kbps theme is no longer inflated.
- The encoder settings are part of the cache key, so changing them invalidates the cache by
  itself.
- Fixed the config page status panel printing the same sentence twice.

## 1.0.0

- Initial release.
