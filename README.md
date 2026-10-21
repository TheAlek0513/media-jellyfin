# Mediarium Requests for Jellyfin

A Jellyfin plugin for [Mediarium](https://github.com/TheAlek0513/Media): people
find and request movies and shows without leaving Jellyfin, and hear in Jellyfin
when what they asked for is ready. Requests go through Mediarium's own rules
(approval, limits per day or week, 4K copies, duplicate checks).

Built for Jellyfin **10.11**. Needs a Mediarium with the plugin API
(`/plugin/v1/`, see `docs/jellyfin-plugin.md` in the Mediarium repo).

## What it adds

- **Request page in Jellyfin Web**: a *Request* entry in the side menu and a
  *Request something* row on Home. Search, trending and popular rows with each
  title's state, a title panel with **Request**, **Request in 4K too** and
  **Cancel request**, a season list for shows (pick the seasons to ask for, or
  ask for more seasons of a show that's already in the library; each season
  shows whether it's requested, approved, partly in or in the library), a 4-step status (Requested, Approved, Downloading, Ready),
  how many requests are left, and *Your requests*. The same page is at
  `http://<jellyfin>/MediariumRequests/Page` on its own.
- **Jellyfin's own search** (Jellyfin Web): under the library's results there's
  a **Not in your library** row of matching titles from Mediarium, with their
  state. Open one and press **Request**. A Discover title opened in Jellyfin Web
  shows **Request** where the Play button would be.
- **Discover library** for every app (phones, TVs): trending and popular titles
  that aren't in the library yet, each marked "Not in library, favourite to
  request." Favouriting one requests it as that person, and they get a message
  saying how it went.
- **Ready to watch**: when a requested title is in the library, the person who
  asked gets "*Title* is ready to watch" on their open Jellyfin sessions. In
  Jellyfin Web it's a pop-up with **Play**. The title also goes into their own
  private **Ready for you** playlist.

Jellyfin's apps other than Jellyfin Web don't run plugin scripts, so there the
Discover library and the messages are what people see.

## Install

1. In Jellyfin, open **Dashboard > Plugins > Repositories**, add
   `https://raw.githubusercontent.com/TheAlek0513/media-jellyfin/main/manifest.json`,
   then install **Mediarium Requests** from the catalogue and restart Jellyfin.
2. In Mediarium, open **Settings > Other apps > Jellyfin plugin**, press
   **Switch on and make a key**, and copy the address and key.
3. In Jellyfin, open the plugin's settings, paste the address and key, press
   **Test**, then **Save**. Saving tells Mediarium where to send events.
4. In Mediarium, link Jellyfin users to Mediarium accounts, or switch on
   accounts for Jellyfin users nobody linked.

### This Jellyfin's address

Mediarium sends a signed webhook (`POST /MediariumRequests/Event`, HMAC-SHA256
in `X-Mediarium-Signature`) the moment a title is ready. Put the address
Mediarium reaches Jellyfin at in **This Jellyfin's address** (for Docker on the
same network, something like `http://jellyfin:8096`). If Mediarium can't reach
Jellyfin, leave it empty: the plugin then reads `GET /plugin/v1/events` every
20 seconds instead. With a webhook it still reads them every 2 minutes, in case
one got lost. Events are handled once, whichever way they come.

### How the Request page gets into Jellyfin Web

The plugin adds one `<script>` tag to Jellyfin Web's `index.html` as it is
served. No file on disk changes, so nothing is left behind when the plugin is
removed, and it survives Jellyfin updates. Switch it off with **Add the Request
page to Jellyfin Web** in the settings.

## Build

```sh
./build.sh            # artifacts/mediarium-requests_<version>.zip and its MD5
```

Uses a local .NET 9 SDK, or the `mcr.microsoft.com/dotnet/sdk:9.0` image when
there is none. Pushing a `v<version>` tag builds the zip and makes a GitHub
release; then put its MD5 (in the release notes) and URL into `manifest.json`.

### Try it in a container

```sh
./build.sh
mkdir -p test/config/plugins/MediariumRequests_0.1.0.0 test/cache test/media
cp artifacts/publish/Jellyfin.Plugin.Mediarium.dll test/config/plugins/MediariumRequests_0.1.0.0/
docker run -d --name mdrm-jellyfin -p 18096:8096 --add-host host.docker.internal:host-gateway \
  -v "$PWD/test/config":/config -v "$PWD/test/cache":/cache -v "$PWD/test/media":/media \
  jellyfin/jellyfin:10.11.11
```

A Mediarium running on the same computer is then `http://host.docker.internal:<port>`
from inside the container.

## License

GPL-3.0, like Jellyfin. See [LICENSE](LICENSE).
