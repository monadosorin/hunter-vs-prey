# Hunter vs Prey: Supervised Learning (learns from us)

Behavioural cloning of **human players**. We play, the game records the situation each moment and what we
did, and the network learns to act like us. The BT + Fuzzy bots are just our opponents and teammates while
we record.

## 1. Record (play the game)

1. Press Play.
2. In the menu, turn on **Record my gameplay for SL**. The button goes red.
3. Pick a role (Hunter or one of the Prey colours) and play normally.
   - A **● REC** badge in the bottom-right shows what the recorder thinks you're doing right now, e.g.
     `you: Evade`. That's the label being saved.
   - Recording stops when you die or the match ends. Press R to play again; recording stays on.
4. Play many matches as **both** Hunter and Prey. Everyone in the group can record: just copy all the CSVs
   into the same `SL/data` folder. More data is better; aim for 30+ matches per role.

### What gets saved (every 0.1 s, to `SL/data/`)

A recorder rides along on your character and sees the match **the way a bot would**:
- 110° view cone, line of sight, 200–300 ms reaction time;
- memory of where things were last seen.

So the input is fair. It's never "where the enemy really is", only what you could have known.

| File | Columns |
|---|---|
| `prey_<date>_<time>.csv` | 34 situation features + what you did (0–5) |
| `hunter_<date>_<time>.csv` | 29 situation features + what you did (0–5) |
| `hunter_target_<date>_<time>.csv` | 5 features per known Prey + 1 if that's the one you went after, else 0 |

**How "what you did" is worked out from your actions:**

| Prey | |
|---|---|
| Fight | you shot, or you're aiming at a Hunter you can see |
| Escape / Collect / Take Over | you're heading for the Gate / your ball / a dropped bucket (along the walkable path) |
| Evade | you're running away from a Hunter seen in the last 3 s within 15 m |
| Explore | anything else |

| Hunter | |
|---|---|
| X-Ray | you used the X-Ray |
| Attack | you swung a tag |
| Pursue | you're heading for a Prey you can see right now |
| Investigate | you're heading for where a Prey was last seen |
| Guard | you're hanging around near the Gate |
| Patrol | anything else |

The Prey you're "going after" is the known Prey you're looking at or running toward.

## 2. Train

Needs [uv](https://docs.astral.sh/uv/). It installs Python, PyTorch (CPU), numpy and matplotlib on first run.

```
cd SL
uv run train.py                       # trains prey_policy, hunter_policy, hunter_target
uv run train.py prey_policy --epochs 80 --hidden 128
```

The script:
- holds out 20% of the **matches** for validation;
- uses class weights, so rare actions (Fight, X-Ray, Escape) still count;
- prints accuracy and a confusion matrix (what you did vs what the model predicts);
- saves plots in `SL/reports/`;
- exports the models to `Assets/HunterVsPrey/Resources/SL/*.json`.

## 3. Play against it

Go back to Unity. The menu's **SL** buttons switch from "not trained yet" to "SL (trained)". Pick **SL** for
Hunters and/or Prey, then play or spectate. Press **B** to see each bot's choice, e.g. `SL > Evade (91%)`.

At runtime an SL bot:
- uses the same fair perception as everything else;
- has the network decide **what to do**. Impossible options are masked: it can't Collect without knowing
  where a ball is, and it can't X-Ray while the team X-Ray is on cooldown;
- carries the decision out with the shared movement and aiming code (A* pathing, jumping, sliding, aiming).

An SL Hunter picks its target with the `hunter_target` network ("which Prey would a human chase?").
There are no BT conditions and no fuzzy logic in an SL bot.

If someone changes `PreyBot.FeatureNames` / `HunterBot.FeatureNames`, old models stop loading
(the names are checked) and old CSVs are skipped. Record again after such a change.
