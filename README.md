# BarOwner

First-person, physics-based co-op bar management game (Unity 6.3, URP, Netcode for GameObjects).
Design doc: [`docs/GDD.md`](docs/GDD.md).

## Getting started
1. Clone/pull, open the folder in Unity Hub with **Unity 6000.3.x**. Netcode for GameObjects installs automatically.
2. Menu **BarOwner → Build Greybox Bar Scene** (re-run any time to regenerate it).
3. Open `Assets/_Project/Scenes/Bar_Greybox.unity`, press **Play**, then **H** to host.

## Controls (dev)
| | |
|---|---|
| WASD / Mouse | Move / look |
| Shift / Space | Sprint / hop |
| Left mouse | Pick up / drop |
| Right mouse | Throw held item |
| Scroll | Push/pull held item |
| E | Use (e.g. light switch by the door) |
| Esc | Free the mouse (click to recapture) |
| H / J / F8 | Host / join 127.0.0.1 / disconnect |

## Testing co-op on one PC
**File → Build And Run**, press **H** in the build, then press Play in the Editor and press **J**
(or install the Multiplayer Play Mode package to run 2–4 players inside the Editor).
