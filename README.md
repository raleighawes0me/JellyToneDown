# JellyToneDown

Turn down Jellyfin's theme music. That's the whole plugin.

Jellyfin plays `theme.mp3` in the background when you open a series or a movie, and there is
no built-in way to change how loud it is — [jellyfin-web#3086](https://github.com/jellyfin/jellyfin-web/issues/3086)
has been open for years. The plugins that do offer a volume control bundle it with theme-song
downloading, assignment and library management. JellyToneDown does the one thing: a volume
level for theme media, and nothing else.

Built for **Jellyfin 12.0 and newer**.

## What it does

* A server-wide default theme volume, plus an optional personal level per user.
* Applies to theme songs and to the audio track of theme videos. Nothing else in your library
  is affected — normal playback volume is untouched.
* **Works on clients that have no theme volume setting of their own.** The adjustment happens
  on the server, so Swiftfin, Findroid, the Android TV app, Kodi and Infuse all receive audio
  that is already quiet. No client-side support required.
* **Never modifies your files.** Adjusted copies are written into Jellyfin's cache directory,
  keyed by the source file and the level applied. Delete the cache at any time; your
  `theme.mp3` files are never read-write opened, let alone rewritten.
* An optional slider in the web client that appears while a theme is playing, so users can set
  their own level without going anywhere near the dashboard.

## What it deliberately does not do

Download theme songs. Assign them. Rename them. Scan for them. Manage them in any way. If you
want those, [Themerr](https://github.com/LizardByte/themerr-jellyfin) and
[jellyfin-plugin-themesongs](https://github.com/danieladov/jellyfin-plugin-themesongs) exist and
JellyToneDown sits happily alongside either.

## Installing

### From the plugin repository (recommended)

In Jellyfin: **Dashboard → Plugins → Repositories → Add**, then:

| | |
|---|---|
| Repository name | `JellyToneDown` |
| Repository URL | `https://raw.githubusercontent.com/raleighawes0me/JellyToneDown/main/manifest.json` |

Then **Catalog → JellyToneDown → Install**, and restart Jellyfin.

### Manually

Download the zip from [Releases](https://github.com/raleighawes0me/JellyToneDown/releases),
unpack it into a folder inside your Jellyfin plugin directory, and restart. On a native Linux
install that is usually:

```bash
sudo -u jellyfin mkdir -p /var/lib/jellyfin/plugins/JellyToneDown_1.0.0.0
sudo -u jellyfin unzip jellytonedown_1.0.0.0.zip -d /var/lib/jellyfin/plugins/JellyToneDown_1.0.0.0
sudo systemctl restart jellyfin
```

Check **Dashboard → Plugins** afterwards: the plugin should be listed as Active. If it shows as
Malfunctioned, the server log will say why.

### Building it yourself

Needs the .NET 10 SDK.

```bash
dotnet publish Jellyfin.Plugin.JellyToneDown -c Release -o ./out
# then copy ./out/Jellyfin.Plugin.JellyToneDown.dll into a plugin folder as above
```

If your server is on the 12.1 line and you would rather build against those packages:

```bash
dotnet publish Jellyfin.Plugin.JellyToneDown -c Release -o ./out -p:JellyfinVersion=12.1.*
```

## Settings

**Dashboard → Plugins → JellyToneDown.**

**Default theme volume** — applies to everyone without a personal level. 40% is a reasonable
starting point; theme songs are usually mastered far louder than the show itself.

**Volume curve** — `Cubic` matches the curve the Jellyfin player uses for its own volume
slider, so 40% here sounds about like 40% there. If that feels too aggressive at the low end,
`Linear` maps the percentage straight onto amplitude and is considerably louder at the same
number.

**Adjust theme songs / theme videos** — either can be switched off. For theme videos only the
audio is re-encoded; the video stream is copied through.

**Let users set their own level** — adds the in-browser slider. Turn it off to hold everyone
to the server default.

**How the web client gets quieter themes** — see below.

**Leave these clients alone** — a comma-separated list matched loosely against the client name,
for example `Kodi, Infuse`. Useful if an app already has a theme volume control you would
rather use.

**Cache size limit** — least-recently-used adjusted copies are dropped past this.

**Per-user levels** — set a number for a user, or leave it blank to have them follow the
default.

## How it works

Two mechanisms, and you can see both in the source:

**Server-side gain (the main one).** A piece of middleware sits at the front of Jellyfin's
request pipeline and watches for `/Audio/{id}/universal`, `/Audio/{id}/stream[.container]` and
`/Videos/{id}/stream[.container]`. When the item behind one of those requests is a theme song
or theme video, the plugin works out which volume applies to the requesting user, has ffmpeg
produce a gain-adjusted copy in the same container, caches it, and serves that instead —
with proper range support, so seeking and `Content-Length` behave normally.

The cache key includes the source file's size and modification time, so replacing a
`theme.mp3` produces a new key automatically, and each volume level gets its own file rather
than invalidating anything. Anything the plugin cannot confidently handle — an unfamiliar
container, a missing ffmpeg, a client asking for something we cannot produce — is passed
straight back to Jellyfin untouched. The theme still plays; it just plays at its original
volume.

**The browser script (optional).** Jellyfin has no supported way for a plugin to add a script
to the web client, so, like every other plugin that needs one, JellyToneDown edits
`index.html` to add a `<script>` tag. The script is what draws the slider that appears while
a theme is playing. Unticking the setting removes the tag cleanly, and the edit is re-applied
at each server start because upgrading Jellyfin replaces that file.

The web client can get its reduction from either mechanism:

* **Server-side** (default) — the web client is treated like every other client. Sturdy, and
  identical in behaviour to your phone and TV.
* **In the browser** — the server sends the web client untouched audio and the script scales
  the volume in the page. Lossless and instant, but it depends on web client internals and
  needs injection to be working. The two are mutually exclusive by design, so the reduction
  is never applied twice.

## Known limitations

**The script tag may not be writable.** On a package install, the web client usually lives at
`/usr/share/jellyfin-web` owned by root, while Jellyfin runs as the `jellyfin` user. The patch
then fails and the config page says so. This is not fatal — server-side gain covers the web
client too, and all you lose is the in-browser slider. If you want it, make `index.html`
writable by the server's user, or use the default server-side mode and set levels from the
dashboard.

**First play of a theme has to wait for ffmpeg.** Usually a fraction of a second for a short
mp3, and only once per theme per level. Run **Dashboard → Scheduled Tasks → Pre-render theme
audio** after changing the volume to do the whole library up front.

**The audio is re-encoded.** Applying gain means decoding and re-encoding, at a high quality
setting in the source's own container. For background theme music this is inaudible, but it
is not bit-identical. Use the in-browser mode for the web client if that matters to you.

**HLS is not intercepted.** Theme media is never delivered as a segmented stream in practice,
and rewriting one from middleware is not safe, so those routes are left alone.

**Native clients that cache aggressively** may keep playing a previously downloaded theme at
the old volume until their cache turns over.

## Releasing

Tag a commit and push:

```bash
git tag v1.0.1 && git push origin v1.0.1
```

The release workflow stamps the version, builds with JPRM, attaches the zip to a GitHub
release, and adds the version to `manifest.json` on `main` so servers pointed at this
repository are offered the update.

## License

MIT. See [LICENSE](LICENSE).
