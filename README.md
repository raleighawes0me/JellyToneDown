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
* **A page your users can set their own level on**, at `/JellyToneDown/MySettings`, without
  dashboard access. Plugin settings normally live in the admin dashboard, which ordinary users
  cannot reach; this is a plain page served by the plugin that reuses the Jellyfin session
  already in their browser.
* Optionally, a slider inside the web client itself that appears while a theme is playing.
  That one needs script injection, which does not work on every install — see below.

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

**Let users set their own level** — enables `/JellyToneDown/MySettings` and the in-browser
slider. The config page shows the exact address to hand out. Turn this off to hold everyone to
the server default.

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

**The script tag usually cannot be written, and that is fine.** On a package install the web
client lives at `/usr/share/jellyfin-web` owned by root while Jellyfin runs as the `jellyfin`
user, so the patch fails and the config page says so plainly. Nothing is degraded by this:
server-side gain still covers the web client, and users still get their own level from
`/JellyToneDown/MySettings`. The only thing lost is the slider appearing *inside* the web
client while a theme plays. If you want that too, make `index.html` writable by the server's
user — but note a jellyfin-web update replaces the file and you will have to do it again.

**First play of a theme has to wait for ffmpeg.** Usually a fraction of a second for a short
mp3, and only once per theme per level. Run **Dashboard → Scheduled Tasks → Pre-render theme
audio** after changing the volume to do the whole library up front.

**The audio is re-encoded.** Applying gain means decoding and re-encoding, in the source's own
container and at the lower of the source's own bitrate and a per-codec ceiling — so a 128 kbps
theme comes back 128 kbps rather than being inflated. For background theme music this is
inaudible, but it is not bit-identical. Use the in-browser mode for the web client if that
matters to you.

Because the encoder settings are part of the cache key, changing the volume, replacing a
theme file, or upgrading to a version that encodes differently all produce a fresh cache entry
by themselves. Old entries are dropped once the cache passes its size limit, or immediately
via **Clear the adjusted-audio cache** on the config page.

**HLS is not intercepted.** Theme media is never delivered as a segmented stream in practice,
and rewriting one from middleware is not safe, so those routes are left alone.

**Native clients that cache aggressively** may keep playing a previously downloaded theme at
the old volume until their cache turns over.

## Releasing

**Add a section to [CHANGELOG.md](CHANGELOG.md) for the new version first.** Those notes are
what Jellyfin shows beside the version in its plugin catalogue, and the release fails if the
section is missing — otherwise every version silently inherits the previous one's notes,
which is exactly how 1.0.0 and 1.0.1 both ended up captioned "Initial release".

Then tag and push:

```bash
git tag v1.0.2 && git push origin v1.0.2
```

The release workflow stamps the version, builds with JPRM, attaches the zip to a GitHub
release, and adds the version to `manifest.json` on `main` so servers pointed at this
repository are offered the update.

The catalogue icon is `image.png` at the repo root. JPRM picks it up by name and bundles it,
and the workflow points the manifest's `imageUrl` at it. It is not hand-drawn or pasted in:
[tools/make_image.py](tools/make_image.py) draws it, the release workflow runs that script
before packaging, and the result is committed back to `main` with the manifest. So the icon
in the catalogue always matches the script — change the script, cut a release, and the
picture follows. To preview a change without releasing, `pip install pillow && python
tools/make_image.py` and look at the file it writes.

## License

MIT. See [LICENSE](LICENSE).
