# Hunter vs Prey

College project for *AI for Game Development*: a first-person 2 Hunters vs 4 Prey game in Unity 6 (URP).
Bots use a **Behaviour Tree + Fuzzy Logic** brain, or a **Supervised Learning** brain trained on our own gameplay.

## Open the project

1. Install **Unity 6000.3.10f1** (Unity Hub → Installs).
2. Clone this repo, then Unity Hub → **Add project from disk** → pick the cloned folder.
3. The first open takes a few minutes (Unity rebuilds its `Library/` cache).
4. Open `Assets/HunterVsPrey/Scenes/Arena.unity` and press Play.

If the map ever looks wrong, rebuild it: menu **Hunter vs Prey → Build Arena Scene**.

## Controls

WASD move · Shift run · Space jump · Ctrl/C slide (hold to crouch) · Esc frees the mouse

- **Hunter:** LMB tag · Q team X-Ray
- **Prey:** LMB paintball · RMB mark a ball for the team · F find my ball
- **When you're out:** R restart · M main menu · T fast-forward (editor only)
- **Debug (editor):** K instant death · P save a stuck spot · B show AI thoughts

## Supervised learning

Turn on **Record my gameplay** in the menu and play: recordings go to `SL/data`.
Training: see [`SL/README.md`](SL/README.md) (`cd SL`, then `uv run train.py`).
Commit your new `SL/data/*.csv` files so everyone trains on the combined data.

## Code

- `Assets/HunterVsPrey/Scripts/AI`: behaviour tree, fuzzy logic, Hunter / Prey bots, SL (`AI/SL`)
- `Assets/HunterVsPrey/Scripts/Match`: match rules, HUD, camera
- `Assets/HunterVsPrey/Editor/ArenaBuilder.cs`: generates the map
- `SL/train.py`: trains the SL models
