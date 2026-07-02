# Jellyfin Plugin — TeleScreenshot

Adds a **"Take Screenshot"** button to the Jellyfin web video player. On click the current
frame is captured and sent to a **Telegram bot** via `sendPhoto`, with the item title and a
timecode as the caption.

Built for **Jellyfin 10.11** (.NET 9).

> ⚠️ This plugin depends on the
> [File Transformation](https://github.com/IAmParadox27/jellyfin-plugin-file-transformation)
> plugin to inject the button into the web UI. Install **File Transformation first**, then
> this plugin, then restart Jellyfin.

---

## How it works

```
web player
  │  ① File Transformation intercepts /web/index.html and calls our IndexTransformer
  │     via reflection → injects <script src="configurationpage?name=telescreenshot.js">
  │
  │  ② telescreenshot.js observes the DOM, adds the camera button to the OSD
  │
  ▼  on click
canvas.drawImage(video) → PNG → base64
  │
  ▼
POST /TeleScreenshot/Send { itemId, positionTicks, imageBase64 }
  │  ③ backend resolves the item title (ILibraryManager) + formats the timecode
  │
  ▼
TelegramService.SendPhotoAsync → POST api.telegram.org/bot<TOKEN>/sendPhoto (multipart)
```

The bot token never leaves the server: the browser posts the base64 image to the plugin's
backend, and the backend signs the Telegram request.

---

## Setup

### 1. Create a Telegram bot

1. Open [@BotFather](https://t.me/BotFather) in Telegram and send `/newbot`.
2. Pick a name and username; you'll receive a token like `123456789:ABC-DEF...`.
3. **Start a conversation with your new bot** (send `/start` to it) from the account that
   should receive screenshots. Bots can only message users who have initiated the chat.

### 2. Find your chat id

The recipient must be a numeric chat id or `@channelusername`. To find your personal chat id:

1. Message your bot (any text).
2. Open `https://api.telegram.org/bot<TOKEN>/getUpdates` in a browser.
3. Find `"chat":{"id": 123456789, ...}` in the JSON — that number is your chat id.

For a channel, create / open the channel, add the bot as administrator, and use
`@YourChannelUsername` as the chat id.

### 3. Install the plugins

In **Dashboard → Plugins → Repositories**, add **both** plugin repositories:

- **File Transformation** — see its README for the manifest URL:
  `https://www.iamparadox.dev/jellyfin/plugins/manifest.json`
- **TeleScreenshot** — this plugin's manifest:
  `https://vladimir214sd.github.io/jellyfin-plugin-telescreenshot/manifest.json`

Then install (in order, restarting Jellyfin after the first one):

1. **File Transformation** (GUID `5e87cc92-571a-4d8d-8d98-d2d4147f9f90`)
2. **TeleScreenshot** (this plugin)

Restart Jellyfin.

### 4. Configure

Go to **Dashboard → Plugins → TeleScreenshot**:

| Field | Description |
| --- | --- |
| Enable screenshot button | Master switch. |
| Bot token | The token from BotFather. |
| Chat id | Numeric id or `@channelusername`. |
| Caption format | Template using `{title}` and `{timecode}`. Example: `{title} — {timecode}`. |
| Show "captured" toast | Extra toast right after frame capture. |

Click **Test connection** — it calls Telegram's `getMe` and reports whether the bot token is
valid.

---

## Build from source

Requirements: .NET 9 SDK.

```bash
dotnet build -c Release
```

The output DLL is at
`Jellyfin.Plugin.TeleScreenshot/bin/Release/net9.0/Jellyfin.Plugin.TeleScreenshot.dll`.

For fast local iteration, build with the `JellyfinPluginDir` property pointing at your
Jellyfin plugins folder; the post-build target copies the DLL, PDB and logo into the right
guid-named subfolder:

```bash
dotnet build -c Release -p:JellyfinPluginDir=/var/lib/jellyfin/plugins
# → /var/lib/jellyfin/plugins/13ec7a52-d9a7-46c3-be6c-c58149187a6c/
```

Then restart Jellyfin.

---

## API reference

The plugin exposes three endpoints under `/TeleScreenshot`:

| Method | Path | Auth | Body | Purpose |
| --- | --- | --- | --- | --- |
| `GET` | `/Config` | any user | — | Secret-free config (`enabled`, `showCaptureToast`) read by the frontend. |
| `POST` | `/Send` | any user | `{ itemId?, positionTicks?, imageBase64 }` | Capture → forward to Telegram. Returns `{ ok, message }`. |
| `POST` | `/Test` | admin | — | Calls Telegram `getMe`. Returns `{ ok, message, username? }`. |

---

## Troubleshooting

**The button doesn't appear in the player.**
- Confirm **File Transformation** is installed and enabled *before* this plugin, and that you
  restarted Jellyfin afterward. Check the server log for the line
  `Registered index.html transformation with the File Transformation plugin`.
- If you instead see `File Transformation plugin assembly '...' not found`, the dependency is
  missing or named differently in your build.
- Hard-refresh the browser (Ctrl/Cmd+Shift+R) — `index.html` is cached.

**"Frame capture blocked by the browser (tainted canvas / CORS)".**
- The video element's stream is cross-origin and the canvas is tainted. This typically happens
  with direct-play URLs behind a reverse proxy on a different host. There is no server-side
  fix; the browser enforces this. Same-origin/MSE playback (the common case) is unaffected.

**"Bot token or chat id is not configured."** — fill them in on the settings page and Save.

**Telegram returns `400: Bad Request: chat not found`.** — the chat id is wrong, or the
recipient never started a conversation with the bot. Message the bot `/start` first.

**Telegram returns `401: Unauthorized`.** — the bot token is invalid. Use **Test connection**.

---

## Limitations

- Jellyfin 10.11 / .NET 9 only (10.10 multi-targeting is not in this version).
- No server-side screenshot history; screenshots are forwarded fire-and-forget.
- The injected script relies on DOM selectors and jellyfin-web globals that may shift between
  builds; the code degrades gracefully (fallback toast, floating button) but may need
  adjustment on non-standard clients.
