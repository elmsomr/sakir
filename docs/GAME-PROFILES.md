# Game profiles

A profile tells Şakir which process means "this game is running" and how the
game screen should look. PUBG and CS2 are built in. Adding another game is one
small JSON file, no rebuild.

## Adding a game

1. In the app, click **Game profiles folder**. It opens the `games` folder next
   to `Sakir.exe` (and creates it, with an example, if it does not exist yet).
2. Create `mygame.json` there:

```json
{
  "name": "VALORANT",
  "process": "VALORANT-Win64-Shipping",
  "steamAppId": "",
  "accent": "auto",
  "fallbackAccent": "#FF4655",
  "background": ["#0F1923", "#1F2933"],
  "motif": "scanline"
}
```

3. Save. The folder is watched, so it applies within a couple of seconds. No
   restart.

To check a file without launching a game, run `Sakir.exe --profiles` in a
terminal. It lists what loaded, which accent colour each game resolved to, and
the reason any file was rejected.

## Fields

| Field | Required | Meaning |
|---|---|---|
| `process` | **yes** | The game's process name as Task Manager shows it, without `.exe` (a trailing `.exe` is stripped). No paths. |
| `key` | no | Short id, `a-z 0-9 - _`, up to 32 characters. Defaults to the file name. Also names the image files in `backgrounds/` (`<key>.jpg`, `<key>-logo.png`). |
| `name` | no | Shown in the header when the game has no logo. Max 40 characters. |
| `steamAppId` | no | Digits only. Lets Şakir find the game's artwork in your local Steam library cache. |
| `accent` | no | `"auto"` (default) or `#RRGGBB`. |
| `fallbackAccent` | no | `#RRGGBB`, used when `accent` is `"auto"` but there is no artwork to read. |
| `background` | no | One or two `#RRGGBB` colours for the gradient shown when there is no artwork. |
| `motif` | no | `none`, `diagonal` or `scanline`. Drawn over the gradient. |

JSON comments (`// ...`) and trailing commas are allowed.

A file with the same `key` as a built-in profile **replaces it entirely**, so
include every field you still want (for example the `steamAppId`).

## How the automatic accent works

With `"accent": "auto"`, Şakir looks at the game's artwork and picks the hue
carrying the most vivid pixels, then renders it bright enough to read over the
dark overlay. Dark, desaturated art is a problem for a plain average, which comes
out muddy brown; this ignores greys and near-black instead.

If the artwork has no real colour, or there is none, the accent is
`fallbackAccent`.

The temperature warning colour (amber above 75 C, red above 85 C) is fixed and
does **not** follow the accent, so an auto-picked blue or green can never hide a
warning.

## Where artwork comes from

In order: your own `backgrounds/<key>.jpg|png` and `<key>-logo.png`, then Steam's
local `appcache\librarycache\<steamAppId>\` (`library_hero.jpg`, `logo.png`),
then the gradient. Nothing is downloaded.

## If a file is rejected

Invalid files are skipped, never half-loaded, and the reason is written to
`sakir.log` and shown by `--profiles`. Typical causes: a missing `process`, a
colour that is not `#RRGGBB`, a non-numeric `steamAppId`, or a `key` with
characters outside `a-z 0-9 - _`.

The `key` is validated strictly because it is used to build file names; a key
like `..\..\x` is refused so a profile cannot point Şakir at files outside the
`backgrounds` folder.
