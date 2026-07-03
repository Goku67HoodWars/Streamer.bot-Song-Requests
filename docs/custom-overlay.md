# 🎨 Make your own “Now Playing” overlay

The built-in overlay (`nowplaying.html`) is just an HTML file that reads the current song from the app and draws it on stream. You can replace it with **your own design** — any look you want — and OBS will use it automatically.

You don't need to know how to code. You can have an AI (ChatGPT, Claude, etc.) build it for you — just paste it the prompt near the bottom of this page.

---

## 1. The data the app gives you

While the queue engine is running, it serves the current song as JSON at:

```
http://127.0.0.1:8090/nowplaying
```

Fetch that URL from your overlay (it allows cross-origin reads, so a local file works in an OBS Browser source). It returns:

| Field        | Type    | Meaning                                                            |
|--------------|---------|--------------------------------------------------------------------|
| `playing`    | boolean | `true` while a song is playing; `false` when nothing is on         |
| `source`     | string  | `"spotify"` or `"youtube"`                                         |
| `title`      | string  | Song title                                                         |
| `artist`     | string  | Artist (or the YouTube channel)                                    |
| `art`        | string  | Album-art / thumbnail image URL                                    |
| `progress`   | number  | How far into the song, in **milliseconds**                         |
| `duration`   | number  | Total length of the song, in **milliseconds**                      |
| `next`       | boolean | `true` if something is queued next                                 |
| `nextTitle`  | string  | The next song's title                                              |
| `nextArtist` | string  | The next song's artist                                             |
| `nextArt`    | string  | The next song's art URL                                            |

When nothing is playing you'll just get `{"playing": false}` — hide your overlay in that case.

**Example while a song plays:**

```json
{
  "playing": true,
  "source": "spotify",
  "title": "Never Gonna Give You Up",
  "artist": "Rick Astley",
  "art": "https://i.scdn.co/image/ab67616d0000b273...",
  "progress": 41200,
  "duration": 213000,
  "next": true,
  "nextTitle": "One More Time",
  "nextArtist": "Daft Punk",
  "nextArt": "https://i.scdn.co/image/..."
}
```

---

## 2. Have an AI build it for you

Open ChatGPT / Claude / whatever you like and paste this, then tweak the **style** line to describe the look you want:

> Build me a single self-contained HTML file for an **OBS Browser source** (transparent background, about **640×150 px**). Every 500 ms it should `fetch('http://127.0.0.1:8090/nowplaying')`, which returns JSON with these fields: `playing` (bool), `source` (`"spotify"`/`"youtube"`), `title`, `artist`, `art` (image URL), `progress` and `duration` (both in **milliseconds**), `next` (bool), `nextTitle`, `nextArtist`, `nextArt`.
>
> When `playing` is `true`, show the **album art**, the **title**, the **artist**, and a **progress bar** (progress ÷ duration). Smoothly **fade the whole thing out** when `playing` is `false`. Handle fetch errors quietly (just keep the last state or stay hidden).
>
> **Style:** *(describe it — e.g. “rounded dark glassy card, art on the left, white title, grey artist, a thin green progress bar, subtle drop shadow”).*
>
> Put **all** CSS and JavaScript **inline** in the one file — no external libraries, fonts, or images. Output only the complete HTML file.

Tips:
- Ask it to also show **“up next”** using `nextTitle` / `nextArt`.
- Ask for a specific **font** or **accent color** to match your stream.
- If something looks off, paste the file back and describe the fix.

---

## 3. Save it so OBS uses it

Save your finished file as **`nowplaying.html`** in your install folder:

```
C:\Users\<your-name>\AppData\Local\SongRequests\nowplaying.html
```

> The app copies that exact path to your clipboard when you click **“Make your own — custom overlay guide.”** Just paste it into your editor's Save dialog.

That's the same file the built-in **“Now Playing”** Browser source already points at, so **OBS picks up your version automatically** — no need to re-add anything.

- **Updates will not overwrite it.** The app keeps the factory design in a separate file (`nowplaying-default.html`), so your custom `nowplaying.html` is safe.
- **Want to start over?** Delete your `nowplaying.html` and reopen the app — it recreates the default.
- **Prefer a separate source instead of replacing the built-in one?** Save your file anywhere (Desktop is fine) and in OBS add **Sources → + → Browser → Local file** pointing at it.

---

## 4. Test it

Open OBS with the setup done and play a song request. Your overlay should show the song and move the progress bar. To preview outside OBS, just double-click your `nowplaying.html` while the queue engine is running — most browsers will render it (some block the local fetch, but OBS won't).

Happy theming. 🎵
