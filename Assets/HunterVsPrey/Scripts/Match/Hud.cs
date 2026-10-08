using UnityEngine;

namespace HunterVsPrey
{
    /// <summary>
    /// Prototype HUD (IMGUI): role menu, timer, buckets, abilities, objective markers, feed, results.
    /// Laid out on a virtual 1920×1080 (16:9) canvas and scaled to the real screen, so it looks the same at any resolution.
    /// </summary>
    public class Hud : MonoBehaviour
    {
        const float RefW = 1920f, RefH = 1080f;

        MatchManager match;
        GUIStyle big, mid, small, center;
        Texture2D white, dot;
        /// <summary>Virtual canvas size (H is 1080 on a 16:9 screen; W grows on wider screens).</summary>
        float W, H, scale = 1f;

        void Awake() => match = GetComponent<MatchManager>();

        void Styles()
        {
            GUI.skin.button.fontSize = 17;
            if (big != null) return;
            white = Texture2D.whiteTexture;
            big = new GUIStyle(GUI.skin.label) { fontSize = 40, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter };
            mid = new GUIStyle(GUI.skin.label) { fontSize = 21, fontStyle = FontStyle.Bold };
            small = new GUIStyle(GUI.skin.label) { fontSize = 16 };
            center = new GUIStyle(small) { alignment = TextAnchor.MiddleCenter };
            dot = new Texture2D(32, 32) { filterMode = FilterMode.Bilinear };
            for (int y = 0; y < 32; y++)
                for (int x = 0; x < 32; x++)
                {
                    float d = Vector2.Distance(new Vector2(x + 0.5f, y + 0.5f), new Vector2(16, 16));
                    dot.SetPixel(x, y, new Color(1, 1, 1, Mathf.Clamp01(15.5f - d)));
                }
            dot.Apply();
        }

        void OnGUI()
        {
            scale = Mathf.Min(Screen.width / RefW, Screen.height / RefH);
            W = Screen.width / scale;
            H = Screen.height / scale;
            GUI.matrix = Matrix4x4.Scale(new Vector3(scale, scale, 1f));
            Styles();
            if (match.State == MatchManager.Phase.Menu) { Menu(); return; }

            Timer();
            BucketsPanel();
            Feed();
            var local = match.LocalActor;
            var cam = match.Cam;
            bool spectating = !local || cam.CurrentMode != PlayerCamera.Mode.Local;

            if (local && !spectating) HurtFlash(local);
            if (local && local.IsActive && !spectating)
            {
                Crosshair();
                HitMarker(local);
                TeammateMarkers(local);
                if (local is HunterActor h) HunterPanel(h);
                else if (local is PreyActor p) PreyPanel(p);
            }
            if (local is HunterActor dead && !dead.IsActive && !spectating)
                Box(new Rect(W / 2 - 200, H / 2 - 30, 400, 60), $"Respawning in {dead.RespawnLeft:0.0}s", big);
            if (spectating) SpectatorBar();
            if (showBrains) Brains();
            if (match.HumanRecorder && SlSettings.Recording) RecordingPanel();

            if (match.State == MatchManager.Phase.Over) Results();
            else if (match.CanQuickRestart) OutPanel();
        }

        /// <summary>You're out (or watching AI vs AI): restart right away instead of waiting for the match to end.</summary>
        void OutPanel()
        {
            float w = 520, x = W / 2 - w / 2, y = 92;
            string why = !match.LocalActor ? "AI vs AI"
                : match.LocalActor is PreyActor { Escaped: true } ? "You escaped! Waiting for the other Prey to get out"
                : "You're out — no need to wait for the match to end";
            Box(new Rect(x, y, w, 30), why, small);
            y += 34;
            float bw = (w - 8) / 3f;
            if (GUI.Button(new Rect(x, y, bw, 30), "[R] Restart now")) match.Restart(true);
            if (GUI.Button(new Rect(x + bw + 4, y, bw, 30), "[M] Main menu")) match.Restart(false);
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            GUI.Label(new Rect(x + 2 * (bw + 4), y, bw, 30), $"[T] Speed {match.SpeedUp:0}× (debug)", center);
#endif
        }

        /// <summary>Teammates are always visible to each other (Prey see Prey, Hunters see Hunters).</summary>
        void TeammateMarkers(Actor local)
        {
            foreach (var a in match.AllActors)
                if (a != local && a.IsActive && a.Team == local.Team)
                    Marker(a.Chest, a is PreyActor pa ? Palette.Of(pa.Color) : new Color(0.75f, 0.55f, 1f), a.DisplayName, MarkerKind.Person);
        }

        // ---------------- AI debug (B) ----------------

        bool showBrains;

        void Update()
        {
            if (UnityEngine.InputSystem.Keyboard.current?.bKey.wasPressedThisFrame == true) showBrains = !showBrains;
        }

        /// <summary>Behaviour-tree branch + fuzzy values floating over every bot.</summary>
        void Brains()
        {
            var cam = Camera.main;
            foreach (var a in match.AllActors)
            {
                if (!a.IsActive || a.Input is not BotBase bot) continue;
                Vector3 sp = cam.WorldToScreenPoint(a.transform.position + Vector3.up * 2.3f);
                if (sp.z < 0f || sp.z > 60f) continue;
                sp.x /= scale; sp.y /= scale; // pixels → virtual canvas
                Box(new Rect(sp.x - 210, H - sp.y - 40, 420, 40), $"{a.DisplayName}: {bot.BrainText}", small);
            }
        }

        // ---------------- menu ----------------

        string savedSummary;

        void Menu()
        {
            float w = 420, x = W / 2 - w / 2, y = H / 2 - 290;
            GUI.color = new Color(0, 0, 0, 0.75f);
            GUI.DrawTexture(new Rect(0, 0, W, H), white);
            GUI.color = Color.white;
            GUI.Label(new Rect(x, y, w, 50), "HUNTER VS PREY", big);
            GUI.Label(new Rect(x, y + 50, w, 24), "2 Hunters vs 4 Prey · choose your role", center);
            y += 90;
            if (GUI.Button(new Rect(x, y, w, 40), "Play as Hunter")) match.BeginMatch(Team.Hunter);
            y += 50;
            for (int i = 0; i < 4; i++)
            {
                var c = (PreyColor)i;
                GUI.backgroundColor = Palette.Of(c);
                if (GUI.Button(new Rect(x + i * (w / 4), y, w / 4 - 6, 40), $"{c} Prey")) match.BeginMatch(Team.Prey, c);
            }
            GUI.backgroundColor = Color.white;
            y += 50;
            if (GUI.Button(new Rect(x, y, w, 40), "Spectate AI vs AI")) match.BeginMatch(null);
            y += 50;
            SlSettings.HunterBrain = BrainToggle(new Rect(x, y, w, 26), "Hunter AI", SlSettings.HunterBrain, HunterBot.SlAvailable);
            y += 30;
            SlSettings.PreyBrain = BrainToggle(new Rect(x, y, w, 26), "Prey AI", SlSettings.PreyBrain, PreyBot.SlAvailable);
            y += 32;
            GUI.backgroundColor = SlSettings.Recording ? new Color(1f, 0.45f, 0.45f) : Color.white;
            if (GUI.Button(new Rect(x, y, w, 30), SlSettings.Recording
                    ? "● Recording my gameplay for SL: ON (click to turn off)"
                    : "Record my gameplay for SL: OFF (click to turn on)"))
                SlSettings.Recording = !SlSettings.Recording;
            GUI.backgroundColor = Color.white;
            y += 32;
            savedSummary ??= SlRecorder.Summary(); // read once per menu (files are only written while playing)
            GUI.Label(new Rect(x, y, w - 96, 22), savedSummary, small);
            if (GUI.Button(new Rect(x + w - 92, y, 92, 22), "Open folder"))
            {
                System.IO.Directory.CreateDirectory(SlRecorder.DataFolder);
                Application.OpenURL("file:///" + SlRecorder.DataFolder.Replace('\\', '/'));
            }
            y += 36;
            GUI.Label(new Rect(x, y, w, 120),
                "WASD move · Shift run · Space jump · Ctrl/C slide\n" +
                "Hunter: LMB tag · Q X-Ray (shared, cooldown 20s × 4 / Prey alive) · painted: 2s stun + 1.5s slow\n" +
                "Prey: LMB paintball (20s reload) · RMB show a teammate their ball (10s, 20s cooldown)\n" +
                "Out: R restart · M menu · T fast-forward (debug) · Spectator: V view, LMB/RMB switch\n" +
                "Esc frees the mouse · K instant death · P save stuck spot · B AI thoughts (debug)", center);
        }

        /// <summary>"Hunter AI: [BT + Fuzzy] [SL]" — SL is greyed out until a trained model is in Resources/SL.</summary>
        BrainKind BrainToggle(Rect r, string label, BrainKind value, bool slReady)
        {
            GUI.Label(new Rect(r.x, r.y + 3, 90, r.height), label, small);
            float bw = (r.width - 95) / 2f;
            GUI.backgroundColor = value == BrainKind.BehaviourTree ? new Color(0.4f, 0.9f, 0.5f) : Color.white;
            if (GUI.Button(new Rect(r.x + 95, r.y, bw - 4, r.height), "BT + Fuzzy")) value = BrainKind.BehaviourTree;
            GUI.enabled = slReady;
            GUI.backgroundColor = value == BrainKind.SL ? new Color(0.4f, 0.9f, 0.5f) : Color.white;
            if (GUI.Button(new Rect(r.x + 95 + bw, r.y, bw - 4, r.height), slReady ? "SL (trained)" : "SL (not trained yet)")) value = BrainKind.SL;
            GUI.enabled = true;
            GUI.backgroundColor = Color.white;
            return slReady ? value : BrainKind.BehaviourTree;
        }

        /// <summary>Small REC badge: what the recorder thinks you're doing right now (the label being saved).</summary>
        void RecordingPanel()
        {
            var rec = match.HumanRecorder;
            string doing = rec is PreyBot p ? p.HumanLabel : rec is HunterBot h ? h.HumanLabel : "-";
            Box(new Rect(W - 432, H - 40, 420, 28),
                $"● REC  ·  you: {doing}  ·  {SlRecorder.SamplesThisMatch} rows saved this match ({SlRecorder.SamplesThisMatch / 600f:0.0} min)",
                small, new Color(0.7f, 0.15f, 0.15f));
        }

        // ---------------- always-on ----------------

        void Timer()
        {
            float t = Mathf.Max(0, match.TimeLeft);
            Box(new Rect(W / 2 - 70, 10, 140, 44), $"{(int)t / 60}:{(int)t % 60:00}", big);
            if (match.GateOpen)
            {
                var g = match.Gate.Camp;
                string s = g.Progress > 0.05f ? $"GATE OPEN · camp {g.Progress * 100:0}%" : "GATE OPEN";
                Box(new Rect(W / 2 - 110, 58, 220, 26), s, center, Palette.GateGold * 0.6f);
            }
        }

        void BucketsPanel()
        {
            float x = 12, y = 12;
            foreach (var b in match.Buckets.Values)
            {
                string who = b.Dropped ? "DROPPED" : b.Holder.Escaped ? $"{b.Holder.DisplayName} (out)" : b.Holder.DisplayName;
                Rect r = new(x, y, 210, 26);
                GUI.color = new Color(0, 0, 0, 0.55f);
                GUI.DrawTexture(r, white);
                GUI.color = Palette.Of(b.Color);
                GUI.DrawTexture(new Rect(x, y, 210f * b.Count / Bucket.Capacity, 26), white);
                GUI.color = Color.white;
                GUI.Label(new Rect(x + 8, y + 3, 200, 22), $"{b.Color}  {b.Count}/{Bucket.Capacity}  · {who}", small);
                y += 30;
            }
            y += 4;
            foreach (var h in match.Hunters)
            {
                string s = h.IsActive ? $"{h.DisplayName}  HP {h.Hp}/{h.MaxHp}{(h.Stunned ? "  STUNNED" : h.Slowed ? "  SLOWED" : "")}" : $"{h.DisplayName}  DOWN {h.RespawnLeft:0}s";
                Box(new Rect(x, y, 210, 24), s, small, Palette.Hunter * 0.7f);
                y += 28;
            }
            // Prey status: who's still in, caught, or already out through the Gate
            y += 4;
            foreach (var p in match.Prey)
            {
                string s = p.Escaped ? "ESCAPED" : p.Dead ? "CAUGHT" : $"HP {p.Hp}/{p.MaxHp}";
                Color bg = p.Escaped ? Palette.GateGold * 0.7f : p.Dead ? new Color(0.2f, 0.2f, 0.2f) : Palette.Of(p.Color) * 0.6f;
                Box(new Rect(x, y, 210, 24), $"{p.DisplayName}  {s}", small, bg);
                y += 28;
            }
        }

        void Feed()
        {
            float y = 12;
            foreach (var (text, _) in match.Feed)
            {
                Box(new Rect(W - 432, y, 420, 24), text, small);
                y += 27;
            }
        }

        // ---------------- local player ----------------

        void Crosshair()
        {
            float cx = W / 2f, cy = H / 2f;
            GUI.DrawTexture(new Rect(cx - 9, cy - 1, 18, 2), white);
            GUI.DrawTexture(new Rect(cx - 1, cy - 9, 2, 18), white);
        }

        /// <summary>Red screen flash that fades over 0.4 s after you get tagged / painted.</summary>
        void HurtFlash(Actor a)
        {
            float t = Time.time - a.LastHurtTime;
            if (t > 0.4f) return;
            GUI.color = new Color(0.9f, 0.1f, 0.1f, 0.35f * (1f - t / 0.4f));
            GUI.DrawTexture(new Rect(0, 0, W, H), white);
            GUI.color = Color.white;
        }

        /// <summary>X around the crosshair for 0.3 s after you land a tag / paintball.</summary>
        void HitMarker(Actor a)
        {
            float t = Time.time - a.LastLandedHitTime;
            if (t > 0.3f) return;
            float cx = W / 2f, cy = H / 2f;
            GUI.color = new Color(1f, 0.95f, 0.4f, 1f - t / 0.3f);
            var m = GUI.matrix;
            foreach (float ang in new[] { 45f, 135f, 225f, 315f })
            {
                GUIUtility.RotateAroundPivot(ang, new Vector2(cx, cy) * scale); // pivot is in screen pixels
                GUI.DrawTexture(new Rect(cx + 8, cy - 1.5f, 12, 3), white);
                GUI.matrix = m;
            }
            GUI.color = Color.white;
        }

        void HunterPanel(HunterActor h)
        {
            float y = H - 120;
            Box(new Rect(12, y, 260, 30), $"HP  {h.Hp}/{h.MaxHp}{(h.Stunned ? "   STUNNED" : h.Slowed ? "   SLOWED" : "")}", mid);
            Ability(12, y + 36, "LMB Tag", h.TagCd, HunterActor.TagCooldown);
            string xray = h.XrayActive ? "X-RAY ON"
                : match.TeamXrayLeft > 0f ? $"{match.XrayUser.DisplayName} X-RAY"
                : h.XrayCd > 0f && match.XrayUser ? $"Q X-Ray (used: {match.XrayUser.DisplayName.Replace("Hunter ", "H")})"
                : "Q Team X-Ray";
            Ability(100, y + 36, xray, h.XrayCd, match.TeamXrayCdMax, 172);

            if (h.XrayActive)
                foreach (var p in match.Prey)
                    if (p.IsActive) Marker(p.Chest, Palette.Of(p.Color), p.DisplayName, MarkerKind.Person);
        }

        void PreyPanel(PreyActor p)
        {
            float y = H - 120;
            Box(new Rect(12, y, 260, 30), $"HP  {p.Hp}/{p.MaxHp}{(p.Motor.Boosted ? "   SPEED BOOST" : "")}", mid);
            Ability(12, y + 36, "LMB Paintball", p.ReloadLeft, PreyActor.ReloadTime, 176);
            Ability(194, y + 36, "RMB Show ball", p.MarkCd, PreyActor.MarkCooldown, 150);

            // RMB prompt when a ball is in the crosshair and the ability is ready
            var aimed = p.MarkCd <= 0f ? p.BallInSights() : null;
            if (aimed) Box(new Rect(W / 2 - 140, H / 2 + 24, 280, 26), $"RMB: show {aimed.Color} their ball ({PreyActor.MarkDuration:0}s)", small);

            // a Hunter's X-Ray is on: every Prey is visible to them right now → move!
            if (match.TeamXrayLeft > 0f)
                Box(new Rect(W / 2 - 230, 150, 460, 36), $"X-RAY! The Hunters can see you  ({match.TeamXrayLeft:0.0}s)", mid, new Color(0.75f, 0.1f, 0.1f));

            // Shared Prey knowledge: balls a teammate is showing right now (10 s), dropped buckets.
            // A shown ball's indicator is visible through walls, but only when you look its way (no edge arrow),
            // so you have to turn and find it. Everything else (other balls, the Gate) has to be found by exploring.
            foreach (var kv in match.Balls)
            {
                var ball = kv.Value;
                if (!ball || !ball.Marked) continue;
                Vector3 vp = Camera.main.WorldToViewportPoint(ball.transform.position);
                if (vp.z <= 0f || vp.x < 0f || vp.x > 1f || vp.y < 0f || vp.y > 1f) continue; // not in view
                bool mine = p.Carried.Exists(b => b.Color == kv.Key);
                Marker(ball.transform.position, Palette.Of(kv.Key), mine ? "MY BALL" : $"{kv.Key}'s ball", MarkerKind.Ball);
            }
            foreach (var b in match.Buckets.Values)
                if (b.Dropped) Marker(b.transform.position + Vector3.up * 0.5f, Palette.Of(b.Color), $"{b.Color}'s bucket", MarkerKind.Bucket);
        }

        void Ability(float x, float y, string label, float cd, float max, float w = 84)
        {
            Rect r = new(x, y, w, 40);
            GUI.color = new Color(0, 0, 0, 0.6f);
            GUI.DrawTexture(r, white);
            GUI.color = cd > 0 ? new Color(0.4f, 0.4f, 0.4f) : new Color(0.3f, 0.75f, 0.4f);
            GUI.DrawTexture(new Rect(x, y + 36, w * (1 - cd / max), 4), white);
            GUI.color = Color.white;
            GUI.Label(r, cd > 0 ? $"{label}\n{cd:0.0}s" : $"{label}\nREADY", center);
        }

        /// <summary>Person = diamond (a player), Ball = round dot, Bucket = hollow square — so a ball never looks like a teammate.</summary>
        enum MarkerKind { Person, Ball, Bucket }

        void Marker(Vector3 world, Color c, string label, MarkerKind kind)
        {
            var cam = Camera.main;
            Vector3 sp = cam.WorldToScreenPoint(world);
            sp.x /= scale; sp.y /= scale; // pixels → virtual canvas
            if (sp.z < 0) { sp.x = W - sp.x; sp.y = 0; }
            float x = Mathf.Clamp(sp.x, 30, W - 30);
            float y = Mathf.Clamp(H - sp.y, 30, H - 44); // room for the label below
            GUI.color = c;
            switch (kind)
            {
                case MarkerKind.Person:
                    var m = GUI.matrix;
                    GUIUtility.RotateAroundPivot(45f, new Vector2(x, y) * scale); // pivot is in screen pixels
                    GUI.DrawTexture(new Rect(x - 8, y - 8, 16, 16), white);
                    GUI.matrix = m;
                    break;
                case MarkerKind.Ball:
                    GUI.DrawTexture(new Rect(x - 8, y - 8, 16, 16), dot);
                    break;
                default:
                    GUI.DrawTexture(new Rect(x - 9, y - 9, 18, 3), white);
                    GUI.DrawTexture(new Rect(x - 9, y + 6, 18, 3), white);
                    GUI.DrawTexture(new Rect(x - 9, y - 9, 3, 18), white);
                    GUI.DrawTexture(new Rect(x + 6, y - 9, 3, 18), white);
                    break;
            }
            GUI.color = Color.white;
            GUI.Label(new Rect(Mathf.Clamp(x - 80, 4, W - 164), y + 10, 160, 22), $"{label} {Vector3.Distance(cam.transform.position, world):0}m", center);
        }

        void SpectatorBar()
        {
            var cam = match.Cam;
            string who = cam.CurrentMode == PlayerCamera.Mode.Ghost || !cam.Target ? "Free-roam ghost" : $"Spectating {cam.Target.DisplayName}";
            string mode = cam.CurrentMode switch { PlayerCamera.Mode.FirstPerson => "First person", PlayerCamera.Mode.ThirdPerson => "Third person", _ => "Ghost" };
            Box(new Rect(W / 2 - 380, H - 54, 760, 40),
                $"{who}  ·  [V] {mode}  ·  LMB/RMB switch player", mid);
            if (cam.Target && cam.CurrentMode != PlayerCamera.Mode.Ghost && cam.Target.Input is BotBase bot)
                Box(new Rect(W / 2 - 330, H - 112, 660, 52), $"AI  {bot.BrainText}", small);
            if (cam.Target is HunterActor h && h.XrayActive && cam.CurrentMode != PlayerCamera.Mode.Ghost)
                foreach (var p in match.Prey)
                    if (p.IsActive) Marker(p.Chest, Palette.Of(p.Color), p.DisplayName, MarkerKind.Person);
        }

        void Results()
        {
            GUI.color = new Color(0, 0, 0, 0.7f);
            GUI.DrawTexture(new Rect(0, H / 2 - 80, W, 160), white);
            GUI.color = match.Winner == Team.Prey ? Palette.GateGold : new Color(0.75f, 0.55f, 1f);
            GUI.Label(new Rect(0, H / 2 - 60, W, 50), match.Winner == Team.Prey ? "PREY WIN" : "HUNTERS WIN", big);
            GUI.color = Color.white;
            GUI.Label(new Rect(0, H / 2, W, 30), match.ResultText, center);
            GUI.Label(new Rect(0, H / 2 + 30, W, 30), "R play again (same role)  ·  M main menu", center);
            if (GUI.Button(new Rect(W / 2 - 154, H / 2 + 90, 150, 30), "Play again")) match.Restart(true);
            if (GUI.Button(new Rect(W / 2 + 4, H / 2 + 90, 150, 30), "Main menu")) match.Restart(false);
        }

        void Box(Rect r, string text, GUIStyle style, Color? bg = null)
        {
            GUI.color = bg ?? new Color(0, 0, 0, 0.55f);
            if (bg.HasValue) GUI.color = new Color(GUI.color.r, GUI.color.g, GUI.color.b, 0.8f);
            GUI.DrawTexture(r, white);
            GUI.color = Color.white;
            var s = new GUIStyle(style) { alignment = TextAnchor.MiddleCenter };
            GUI.Label(r, text, s);
        }
    }
}
