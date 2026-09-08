# LWF FPS Boost

Threaded Spine animation and mesh generation for **Lazy Witch's Factory** (tested on ver 0.27.0).

> Experimental build. Attach the two log files below when you report a problem.

- Frame time drops by about half in factories with many familiars
- The gain scales with logical CPU cores; a single core gains nothing
- Panel text follows the game language

![Title screen](https://raw.githubusercontent.com/KiyonakaNata/lwf-fps-boost/main/img/en/title-panel.png)

## Install

- Install with a mod manager, or put `LwfFpsBoost.dll` into `BepInEx/plugins/`
- Start the game
- The title screen shows the panel above

## Check it works

- Press **F9** on the title screen and wait for the verdict
- `PASS` → done
- `INCONCLUSIVE` → press F9 again
- `FAIL` → report it with `BepInEx/LwfFpsBoost-incidents.log`

| Key (title screen) | Action |
|---|---|
| F9 | load test, ~53 s |
| F10 | speed test, ~66 s |

- The PC is under heavy load during a test
- Leaving the title screen during a test stops the test and turns threading off; restart the game
- After a test that produced an error, the game shows its error screen once on the next start; close it and restart

![Load test running](https://raw.githubusercontent.com/KiyonakaNata/lwf-fps-boost/main/img/en/loadtest-running.png)

![PASS](https://raw.githubusercontent.com/KiyonakaNata/lwf-fps-boost/main/img/en/loadtest-pass.png)

## Measured on the author's PC

Ryzen 7 5700X (8C/16T), 1500 familiars.

| Threading | Frame time | fps |
|---|---|---|
| on | 21.9 ms | 45.6 |
| off | 51.1 ms | 19.6 |

## Report

- Post in the Mod channel of the [official Discord](https://discord.com/invite/pZjA34FCWQ)
- Attach `BepInEx/LwfFpsBoost-games.log`, written once per factory run
- Attach `BepInEx/LwfFpsBoost-incidents.log` when the file exists

## Remove

- Delete `BepInEx/plugins/LwfFpsBoost.dll`
- When the game ships threaded Spine updates, this mod applies nothing
- Delete it when the title screen reads `The game runs Spine threaded`
- Delete it before reporting a game bug to the developer

## Settings

`BepInEx/config/kiyonakanata.lwffpsboost.cfg`

| Setting | Default | Values |
|---|---|---|
| `1. General/Enabled` | `true` | `false` = off |

The `9. Developer` section is for development.

## Links

- [GitHub](https://github.com/KiyonakaNata/lwf-fps-boost)
- [DEVELOPER.md](https://github.com/KiyonakaNata/lwf-fps-boost/blob/main/DEVELOPER.md) — cause, reproduction, and a fix for the game
