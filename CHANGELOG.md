# Changelog

The release workflow reads the section matching the version being released and puts it in
`manifest.json`, which is what Jellyfin shows in its plugin catalogue. **A release fails if
there is no section for its version**, so this file cannot silently fall out of date.

Headings may be written as `## 1.2.3` or `## 1.2.3.0`; both match the same release.

## 1.1.0

- The in-browser volume slider no longer needs a writable web client. The script tag is now
  added to `index.html` as the page is served, by middleware, instead of being written into
  the file on disk. That was the one part of the plugin that did not work on a normal package
  install, where the web client is owned by root and Jellyfin runs as its own user - and it
  also means a Jellyfin upgrade can no longer undo it.
- The old on-disk patch is still selectable under **Web client -> How the script gets in**, and
  switching away from it removes anything it previously wrote.
- The config page now reports how many page loads the script has actually been added to, rather
  than only whether it is switched on.
- The catalogue icon is no longer a square that Jellyfin blew up to fill the card.

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
