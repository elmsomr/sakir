# Game backgrounds

Drop your own artwork here to override what the game scene uses.

| File | Used for |
|---|---|
| `pubg.jpg` (or `.png`) | PUBG background |
| `pubg-logo.png` | PUBG logo, top left |
| `cs2.jpg` | Counter-Strike 2 background |
| `cs2-logo.png` | CS2 logo |

The file name is the profile key from `Games/GameCatalog.cs`, so adding a new
game profile there also gives you `<key>.jpg` and `<key>-logo.png` here.

If a file is missing, Şakir falls back to Steam's own local library cache
(`Steam\appcache\librarycache\<appid>\library_hero.jpg` and `logo.png`), and if
that is missing too, to a generated gradient. Nothing is ever downloaded.

Images are cropped to fill 320x240 and darkened so the readouts stay legible, so
busy artwork works better than you would expect. Anything you put here stays on
your machine - this folder is gitignored.
