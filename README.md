# ChannelFlow-Jellyfin

Thin [Jellyfin](https://jellyfin.org) 12 plugin that connects to [ChannelFlow Server](https://github.com/FinTVProject/FinTV).

This is the canonical plugin repository. GUID: `f4e8a2b1-3c5d-4e6f-9a8b-7c6d5e4f3a2b`

## Install

Dashboard → Plugins → Repositories → +:

- Repository: `ChannelFlow-Jellyfin`
- URL: `https://raw.githubusercontent.com/FlowMeadow01/ChannelFlow-Jellyfin/master/manifest.json`

Then Catalog → ChannelFlow-Jellyfin → Install, and restart Jellyfin.

## Configure

Dashboard → Plugins → ChannelFlow-Jellyfin:

- ChannelFlow Server URL (example `http://ChannelFlow-Server:8097`)
- API key (same as `CHANNELFLOW_API_KEY` on the server)
- **Test connection** — checks that Jellyfin can reach ChannelFlow Server with that URL and key
- Auto-register Live TV — adds the ChannelFlow M3U tuner and XMLTV guide
- **Send libraries** — publishes Jellyfin TV/movie/music libraries to ChannelFlow Server
- Write blackframe chapters onto Jellyfin items

Scheduled tasks:

- **ChannelFlow Catalog Sync** — metadata, paths, and chapters for the libraries selected on ChannelFlow Server
- **ChannelFlow Commercial Blackframe Detection**

Manage channels in the ChannelFlow Server Web UI, not this page.
