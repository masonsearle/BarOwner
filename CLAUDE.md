# BarOwner: working notes for Claude

First-person, physics-based co-op bar management game. Design: `docs/GDD.md` (read it before
adding systems; update its roadmap when a milestone lands).

## Project facts
- Unity 6000.3.x, URP (Universal 3D template), Input System with **project-wide actions**
  (`Assets/InputSystem_Actions.inputactions`, read via `InputSystem.actions`).
- Netcode for GameObjects 2.x + Unity Transport. Co-op is client-hosted (host + up to 3).
- The developer works on Windows in the Unity Editor; Claude usually can't run the Editor, so
  code must be written carefully against the real APIs, and every change ends with clear
  "what to click in Unity" steps.
- Git with LFS (see `.gitattributes`). Never commit `Library/`, `Temp/`, `Logs/`, `UserSettings/`.
  Every new asset/script/folder is committed together with its `.meta`.

## Layout
```
Assets/_Project/
  Scripts/<Domain>/   Player, Interaction, Networking, UI, (later) Bar, Customers, Economy, Staff
  Editor/             editor-only tools (scene builders, validators)
  Prefabs/ Scenes/ Materials/ Art/ Audio/
```
Namespace = `BarOwner.<Domain>`. No assembly definitions yet; add them when compile times hurt.

## Netcode rules (important)
- **Server authority** for simulation: customers, money, stock, time of day, spawning/despawning.
  Clients ask with `[Rpc(SendTo.Server)]`; the server validates and changes `NetworkVariable`s.
- **Owner authority** for feel: each player's own movement, and the object a player is holding
  (`NetworkTransform.AuthorityMode = Owner`; grabbing transfers ownership via the server).
- Never trust a client for money or inventory. Never use `FindObjectOfType` per frame.
- Anything that must exist for all players is a `NetworkObject`, spawned by the server or placed in scene.
- Test every feature in co-op (Host + a second client) before calling it done.

## Code style
- Small single-purpose components; `[SerializeField] private` fields with sane defaults.
- Interactions go through `IInteractable` (look-at prompt + use); held physics objects use `Grabbable`.
  Prompts starting with `~` are info-only. Hold-to-use things implement `IHoldInteractable`.
- Things players carry that the server creates/consumes (glasses, kegs) are runtime-spawned network
  prefabs: "plugging in" or "serving" despawns them and moves their data into the receiver, rather than
  teleporting owner-authoritative objects.
- No `OnGUI` for player-facing UI; use uGUI (Canvas) for now.
- Physics: Unity 6 names (`linearVelocity`, `linearDamping`, `angularDamping`).
