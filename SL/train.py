"""
Hunter vs Prey - Supervised Learning (behavioural cloning of HUMAN players).

Reads the CSVs the game writes to SL/data while you play with "Record my gameplay for SL" on,
trains three small MLPs and exports them as JSON into Assets/HunterVsPrey/Resources/SL, where the
game's C# Mlp class loads them (the "SL" buttons in the menu light up once they exist).

  prey_policy    classifier   perceived situation -> what the human Prey did (Escape/Fight/Evade/Collect/TakeOver/Explore)
  hunter_policy  classifier   perceived situation -> what the human Hunter did (Attack/Pursue/Investigate/Xray/Guard/Patrol)
  hunter_target  regressor    one known Prey's features -> 1 if the human chased it, 0 if not ("who would a human go after?")

Usage (from the SL folder):
  uv run train.py                 # train all three
  uv run train.py prey_policy     # train one
  uv run train.py --epochs 80 --hidden 128
"""
import argparse
import json
import random
import re
from pathlib import Path

import numpy as np
import torch
import torch.nn as nn

ROOT = Path(__file__).resolve().parent
DATA = ROOT / "data"
OUT = ROOT.parent / "Assets" / "HunterVsPrey" / "Resources" / "SL"
REPORTS = ROOT / "reports"

# Class order MUST match the C# enums (PreyBot.Act / HunterBot.Act).
MODELS = {
    "prey_policy": dict(prefix="prey", kind="classifier",
                        classes=["Escape", "Fight", "Evade", "Collect", "TakeOver", "Explore"]),
    "hunter_policy": dict(prefix="hunter", kind="classifier",
                          classes=["Attack", "Pursue", "Investigate", "Xray", "Guard", "Patrol"]),
    "hunter_target": dict(prefix="hunter_target", kind="regressor", classes=["priority"]),
}


def load_matches(prefix):
    """One array per recorded match file: (features, target), plus the feature names from the header."""
    pattern = re.compile(rf"{re.escape(prefix)}_(\d{{8}}_\d{{6}}|manual)\.csv")
    files = sorted(f for f in DATA.glob("*.csv") if pattern.fullmatch(f.name))
    matches, names = [], None
    for f in files:
        with open(f, encoding="utf-8-sig") as fh:
            header = fh.readline().strip().split(",")
        rows = np.loadtxt(f, delimiter=",", skiprows=1, ndmin=2, dtype=np.float32)
        if rows.size == 0:
            continue
        if names is None:
            names = header[:-1]
        elif header[:-1] != names:
            print(f"  ! skipping {f.name}: different feature layout (recorded with an older version?)")
            continue
        matches.append((rows[:, :-1], rows[:, -1]))
    return matches, names


def split_by_match(matches, val_fraction, seed):
    """Validation = whole held-out matches, so we test on situations the model never saw."""
    random.Random(seed).shuffle(matches)
    if len(matches) >= 2:
        n_val = max(1, int(round(len(matches) * val_fraction)))
        train, val = matches[n_val:], matches[:n_val]
    else:  # only one match recorded: fall back to splitting rows
        x, y = matches[0]
        cut = int(len(x) * (1 - val_fraction))
        train, val = [(x[:cut], y[:cut])], [(x[cut:], y[cut:])]
    cat = lambda ms: (np.concatenate([m[0] for m in ms]), np.concatenate([m[1] for m in ms]))
    return cat(train), cat(val)


def make_mlp(n_in, hidden, n_out, layers):
    mods, prev = [], n_in
    for _ in range(layers):
        mods += [nn.Linear(prev, hidden), nn.ReLU()]
        prev = hidden
    mods.append(nn.Linear(prev, n_out))
    return nn.Sequential(*mods)


def train_model(name, spec, args):
    print(f"\n=== {name} ===")
    matches, names = load_matches(spec["prefix"])
    if not matches:
        print(f"  no data: expected {spec['prefix']}_<date>_<time>.csv files in {DATA}")
        return
    (xtr, ytr), (xva, yva) = split_by_match(matches, args.val, args.seed)
    print(f"  {len(matches)} matches · {len(xtr)} train / {len(xva)} validation samples · {xtr.shape[1]} features")

    mean = xtr.mean(0)
    std = xtr.std(0)
    std[std < 1e-6] = 1.0
    norm = lambda x: torch.tensor((x - mean) / std, dtype=torch.float32)
    Xtr, Xva = norm(xtr), norm(xva)

    classifier = spec["kind"] == "classifier"
    k = len(spec["classes"]) if classifier else 1
    if classifier:
        Ytr, Yva = torch.tensor(ytr, dtype=torch.long), torch.tensor(yva, dtype=torch.long)
        counts = np.bincount(ytr.astype(int), minlength=k).astype(np.float32)
        print("  label counts:", {c: int(n) for c, n in zip(spec["classes"], counts)})
        # rare behaviours (Fight, Escape, X-Ray...) matter as much as common ones (Explore, Patrol)
        weights = counts.sum() / np.maximum(counts, 1.0) / k
        loss_fn = nn.CrossEntropyLoss(weight=torch.tensor(np.clip(weights, 0.1, 20.0)))
    else:
        Ytr, Yva = torch.tensor(ytr).unsqueeze(1), torch.tensor(yva).unsqueeze(1)
        loss_fn = nn.MSELoss()

    torch.manual_seed(args.seed)
    model = make_mlp(Xtr.shape[1], args.hidden, k, args.layers)
    opt = torch.optim.Adam(model.parameters(), lr=args.lr, weight_decay=1e-5)

    def evaluate():
        model.eval()
        with torch.no_grad():
            out = model(Xva)
            loss = loss_fn(out, Yva).item()
            score = (out.argmax(1) == Yva).float().mean().item() if classifier \
                else 1 - ((out - Yva) ** 2).sum().item() / max(((Yva - Yva.mean()) ** 2).sum().item(), 1e-8)
        model.train()
        return loss, score, out

    history, best = [], (float("-inf"), None)
    for epoch in range(1, args.epochs + 1):
        perm = torch.randperm(len(Xtr))
        total = 0.0
        for i in range(0, len(Xtr), args.batch):
            idx = perm[i:i + args.batch]
            opt.zero_grad()
            loss = loss_fn(model(Xtr[idx]), Ytr[idx])
            loss.backward()
            opt.step()
            total += loss.item() * len(idx)
        val_loss, score, _ = evaluate()
        history.append((total / len(Xtr), val_loss, score))
        if score > best[0]:
            best = (score, {k_: v.clone() for k_, v in model.state_dict().items()})
        if epoch == 1 or epoch % 5 == 0 or epoch == args.epochs:
            metric = "accuracy" if classifier else "R²"
            print(f"  epoch {epoch:3d}  train loss {history[-1][0]:.4f}  val loss {val_loss:.4f}  val {metric} {score:.3f}")

    model.load_state_dict(best[1])
    _, score, out = evaluate()
    print(f"  best validation {'accuracy' if classifier else 'R²'}: {score:.3f}")
    if classifier:
        report_confusion(name, spec["classes"], Yva.numpy(), out.argmax(1).numpy())
    else:
        print(f"  mean absolute error: {(out - Yva).abs().mean().item():.4f}")
    plot_history(name, history, classifier)
    export(name, spec, model, mean, std, names, score)


def report_confusion(name, classes, truth, pred):
    k = len(classes)
    cm = np.zeros((k, k), dtype=int)
    for t, p in zip(truth, pred):
        cm[t, p] += 1
    print("  confusion matrix (rows = what the human did, columns = SL model):")
    print("  " + " " * 12 + "".join(f"{c[:8]:>9}" for c in classes))
    for i, c in enumerate(classes):
        recall = cm[i, i] / cm[i].sum() if cm[i].sum() else float("nan")
        print(f"  {c[:12]:>12}" + "".join(f"{v:9d}" for v in cm[i]) + f"   recall {recall:.2f}")
    try:
        import matplotlib.pyplot as plt
        REPORTS.mkdir(parents=True, exist_ok=True)
        norm = cm / np.maximum(cm.sum(1, keepdims=True), 1)
        fig, ax = plt.subplots(figsize=(6, 5))
        ax.imshow(norm, cmap="Blues", vmin=0, vmax=1)
        ax.set_xticks(range(k), classes, rotation=45, ha="right")
        ax.set_yticks(range(k), classes)
        ax.set_xlabel("SL model")
        ax.set_ylabel("human (teacher)")
        for i in range(k):
            for j in range(k):
                ax.text(j, i, f"{norm[i, j]:.2f}", ha="center", va="center", fontsize=8)
        ax.set_title(f"{name}: confusion (row-normalised)")
        fig.tight_layout()
        fig.savefig(REPORTS / f"{name}_confusion.png", dpi=130)
        plt.close(fig)
    except ImportError:
        pass


def plot_history(name, history, classifier):
    try:
        import matplotlib.pyplot as plt
    except ImportError:
        return
    REPORTS.mkdir(parents=True, exist_ok=True)
    h = np.array(history)
    fig, (a, b) = plt.subplots(1, 2, figsize=(10, 4))
    a.plot(h[:, 0], label="train")
    a.plot(h[:, 1], label="validation")
    a.set_title("loss")
    a.set_xlabel("epoch")
    a.legend()
    b.plot(h[:, 2])
    b.set_title("validation accuracy" if classifier else "validation R²")
    b.set_xlabel("epoch")
    fig.suptitle(name)
    fig.tight_layout()
    fig.savefig(REPORTS / f"{name}_training.png", dpi=130)
    plt.close(fig)


def export(name, spec, model, mean, std, names, score):
    """Same layout the C# Mlp reads: weights row-major [out][in] per layer, all layers concatenated."""
    linears = [m for m in model if isinstance(m, nn.Linear)]
    data = {
        "kind": spec["kind"],
        "sizes": [linears[0].in_features] + [l.out_features for l in linears],
        "weights": [float(v) for l in linears for v in l.weight.detach().flatten()],
        "biases": [float(v) for l in linears for v in l.bias.detach()],
        "mean": [float(v) for v in mean],
        "std": [float(v) for v in std],
        "classes": spec["classes"],
        "featureNames": names,
        "valAccuracy": float(score),
    }
    OUT.mkdir(parents=True, exist_ok=True)
    path = OUT / f"{name}.json"
    path.write_text(json.dumps(data), encoding="utf-8")
    print(f"  exported -> {path}")


def main():
    p = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    p.add_argument("models", nargs="*", help=f"any of: {', '.join(MODELS)} (default: all)")
    p.add_argument("--epochs", type=int, default=40)
    p.add_argument("--hidden", type=int, default=64)
    p.add_argument("--layers", type=int, default=2)
    p.add_argument("--batch", type=int, default=256)
    p.add_argument("--lr", type=float, default=1e-3)
    p.add_argument("--val", type=float, default=0.2, help="fraction of matches held out for validation")
    p.add_argument("--seed", type=int, default=7)
    args = p.parse_args()
    unknown = [m for m in args.models if m not in MODELS]
    if unknown:
        p.error(f"unknown model(s) {unknown}; choose from {list(MODELS)}")
    for name in args.models or MODELS:
        train_model(name, MODELS[name], args)


if __name__ == "__main__":
    main()
