using UnityEngine;

namespace HunterVsPrey
{
    /// <summary>Prototype HUD (IMGUI): role menu, timer, buckets, abilities, objective markers, feed, results.</summary>
    public class Hud : MonoBehaviour
    {
        MatchManager match;
        GUIStyle big, mid, small, center;
        Texture2D white;

        void Awake() => match = GetComponent<MatchManager>();

        void Styles()
        {
            if (big != null) return;
            white = Texture2D.whiteTexture;
            big = new GUIStyle(GUI.skin.label) { fontSize = 34, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter };
            mid = new GUIStyle(GUI.skin.label) { fontSize = 18, fontStyle = FontStyle.Bold };
            small = new GUIStyle(GUI.skin.label) { fontSize = 14 };
            center = new GUIStyle(small) { alignment = TextAnchor.MiddleCenter };
        }

        void OnGUI()
        {
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
                Box(new Rect(Screen.width / 2 - 200, Screen.height / 2 - 30, 400, 60), $"Respawning in {dead.RespawnLeft:0.0}s", big);
            if (spectating) SpectatorBar();
            if (showBrains) Brains();
            if (match.HumanRecorder && SlSettings.Recording) RecordingPanel();

            if (match.State == MatchManager.Phase.Over) Results();
            else if (match.CanQuickRestart) OutPanel();
        }

        /// <summary>You're out (or watching AI vs AI): restart right away instead of waiting for the match to end.</summary>
        void OutPanel()
        {
            float w = 520, x = Screen.width / 2 - w / 2, y = 92;
            Box(new Rect(x, y, w, 30), match.LocalActor ? "You're out — no need to wait for the match to end" : "AI vs AI", small);
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
                    Marker(a.Chest, a is PreyActor pa ? Palette.Of(pa.Color) : new Color(0.75f, 0.55f, 1f), a.DisplayName);
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
                Box(new Rect(sp.x - 210, Screen.height - sp.y - 40, 420, 40), $"{a.DisplayName}: {bot.BrainText}", small);
            }
        }

        // ---------------- menu ----------------

        string savedSummary;

        void Menu()
        {
            float w = 420, x = Screen.width / 2 - w / 2, y = Screen.height / 2 - 290;
            GUI.color = new Color(0, 0, 0, 0.75f);
            GUI.DrawTexture(new Rect(0, 0, Screen.width, Screen.height), white);
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
                "Hunter: LMB tag · Q X-Ray (one shared by both Hunters)\n" +
                "Prey: LMB paintball (20s reload) · RMB mark a ball for the team · F find my ball (20s)\n" +
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
            Box(new Rect(Screen.width - 432, Screen.height - 40, 420, 28),
                $"● REC  ·  you: {doing}  ·  {SlRecorder.SamplesThisMatch} rows saved this match ({SlRecorder.SamplesThisMatch / 600f:0.0} min)",
                small, new Color(0.7f, 0.15f, 0.15f));
        }

        // ---------------- always-on ----------------

        void Timer()
        {
            float t = Mathf.Max(0, match.TimeLeft);
            Box(new Rect(Screen.width / 2 - 70, 10, 140, 44), $"{(int)t / 60}:{(int)t % 60:00}", big);
            if (match.GateOpen)
            {
                var g = match.Gate.Camp;
                string s = g.Progress > 0.05f ? $"GATE OPEN · camp {g.Progress * 100:0}%" : "GATE OPEN";
                Box(new Rect(Screen.width / 2 - 110, 58, 220, 26), s, center, Palette.GateGold * 0.6f);
            }
        }

        void BucketsPanel()
        {
            float x = 12, y = 12;
            foreach (var b in match.Buckets.Values)
            {
                string who = b.Dropped ? "DROPPED" : b.Holder.DisplayName;
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
                string s = h.IsActive ? $"{h.DisplayName}  HP {h.Hp}/{h.MaxHp}{(h.Stunned ? "  STUNNED" : "")}" : $"{h.DisplayName}  DOWN {h.RespawnLeft:0}s";
                Box(new Rect(x, y, 210, 24), s, small, Palette.Hunter * 0.7f);
                y += 28;
            }
        }

        void Feed()
        {
            float y = 12;
            foreach (var (text, _) in match.Feed)
            {
                Box(new Rect(Screen.width - 432, y, 420, 24), text, small);
                y += 27;
            }
        }

        // ---------------- local player ----------------

        void Crosshair()
        {
            float cx = Screen.width / 2f, cy = Screen.height / 2f;
            GUI.DrawTexture(new Rect(cx - 9, cy - 1, 18, 2), white);
            GUI.DrawTexture(new Rect(cx - 1, cy - 9, 2, 18), white);
        }

        /// <summary>Red screen flash that fades over 0.4 s after you get tagged / painted.</summary>
        void HurtFlash(Actor a)
        {
            float t = Time.time - a.LastHurtTime;
            if (t > 0.4f) return;
            GUI.color = new Color(0.9f, 0.1f, 0.1f, 0.35f * (1f - t / 0.4f));
            GUI.DrawTexture(new Rect(0, 0, Screen.width, Screen.height), white);
            GUI.color = Color.white;
        }

        /// <summary>X around the crosshair for 0.3 s after you land a tag / paintball.</summary>
        void HitMarker(Actor a)
        {
            float t = Time.time - a.LastLandedHitTime;
            if (t > 0.3f) return;
            float cx = Screen.width / 2f, cy = Screen.height / 2f;
            GUI.color = new Color(1f, 0.95f, 0.4f, 1f - t / 0.3f);
            var m = GUI.matrix;
            foreach (float ang in new[] { 45f, 135f, 225f, 315f })
            {
                GUIUtility.RotateAroundPivot(ang, new Vector2(cx, cy));
                GUI.DrawTexture(new Rect(cx + 8, cy - 1.5f, 12, 3), white);
                GUI.matrix = m;
            }
            GUI.color = Color.white;
        }

        void HunterPanel(HunterActor h)
        {
            float y = Screen.height - 120;
            Box(new Rect(12, y, 260, 30), $"HP  {h.Hp}/{h.MaxHp}{(h.Stunned ? "   STUNNED" : "")}", mid);
            Ability(12, y + 36, "LMB Tag", h.TagCd, HunterActor.TagCooldown);
            string xray = h.XrayActive ? "X-RAY ON"
                : match.TeamXrayLeft > 0f ? $"{match.XrayUser.DisplayName} X-RAY"
                : h.XrayCd > 0f && match.XrayUser ? $"Q X-Ray (used: {match.XrayUser.DisplayName.Replace("Hunter ", "H")})"
                : "Q Team X-Ray";
            Ability(100, y + 36, xray, h.XrayCd, HunterActor.XrayCooldown, 172);

            if (h.XrayActive)
                foreach (var p in match.Prey)
                    if (p.IsActive) Marker(p.Chest, Palette.Of(p.Color), p.DisplayName);
        }

        void PreyPanel(PreyActor p)
        {
            float y = Screen.height - 120;
            Box(new Rect(12, y, 260, 30), $"HP  {p.Hp}/{p.MaxHp}{(p.Motor.Boosted ? "   SPEED BOOST" : "")}", mid);
            Ability(12, y + 36, "LMB Paintball", p.ReloadLeft, PreyActor.ReloadTime, 176);
            Ability(194, y + 36, p.Locating ? "F Find my ball: ON" : "F Find my ball", p.LocateCd, PreyActor.LocateCooldown, 150);

            // RMB prompt when a ball is in the crosshair
            var aimed = p.BallInSights();
            if (aimed) Box(new Rect(Screen.width / 2 - 120, Screen.height / 2 + 24, 240, 26), $"RMB: mark {aimed.Color}'s ball for the team", small);

            // Shared Prey knowledge: marked balls, my own ball while "find my ball" is on, dropped buckets.
            // Everything else (unmarked balls, the Gate) still has to be found by looking around.
            foreach (var kv in match.Balls)
            {
                var ball = kv.Value;
                if (!ball) continue;
                bool mine = p.Carried.Exists(b => b.Color == kv.Key);
                if (ball.Marked || mine && p.Locating)
                    Marker(ball.transform.position, Palette.Of(kv.Key), mine ? "MY BALL" : $"{kv.Key} ball");
            }
            foreach (var b in match.Buckets.Values)
                if (b.Dropped) Marker(b.transform.position + Vector3.up * 0.5f, Palette.Of(b.Color), $"{b.Color}'s bucket");
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

        void Marker(Vector3 world, Color c, string label)
        {
            var cam = Camera.main;
            Vector3 sp = cam.WorldToScreenPoint(world);
            if (sp.z < 0) { sp.x = Screen.width - sp.x; sp.y = 0; }
            float x = Mathf.Clamp(sp.x, 30, Screen.width - 30);
            float y = Mathf.Clamp(Screen.height - sp.y, 30, Screen.height - 30);
            GUI.color = c;
            GUI.DrawTexture(new Rect(x - 7, y - 7, 14, 14), white);
            GUI.color = Color.white;
            GUI.Label(new Rect(x - 60, y + 8, 120, 20), $"{label} {Vector3.Distance(cam.transform.position, world):0}m", center);
        }

        void SpectatorBar()
        {
            var cam = match.Cam;
            string who = cam.CurrentMode == PlayerCamera.Mode.Ghost || !cam.Target ? "Free-roam ghost" : $"Spectating {cam.Target.DisplayName}";
            string mode = cam.CurrentMode switch { PlayerCamera.Mode.FirstPerson => "First person", PlayerCamera.Mode.ThirdPerson => "Third person", _ => "Ghost" };
            Box(new Rect(Screen.width / 2 - 260, Screen.height - 54, 520, 40),
                $"{who}  ·  [V] {mode}  ·  LMB/RMB switch player", mid);
            if (cam.Target && cam.CurrentMode != PlayerCamera.Mode.Ghost && cam.Target.Input is BotBase bot)
                Box(new Rect(Screen.width / 2 - 330, Screen.height - 112, 660, 52), $"AI  {bot.BrainText}", small);
            if (cam.Target is HunterActor h && h.XrayActive && cam.CurrentMode != PlayerCamera.Mode.Ghost)
                foreach (var p in match.Prey)
                    if (p.IsActive) Marker(p.Chest, Palette.Of(p.Color), p.DisplayName);
        }

        void Results()
        {
            GUI.color = new Color(0, 0, 0, 0.7f);
            GUI.DrawTexture(new Rect(0, Screen.height / 2 - 80, Screen.width, 160), white);
            GUI.color = match.Winner == Team.Prey ? Palette.GateGold : new Color(0.75f, 0.55f, 1f);
            GUI.Label(new Rect(0, Screen.height / 2 - 60, Screen.width, 50), match.Winner == Team.Prey ? "PREY WIN" : "HUNTERS WIN", big);
            GUI.color = Color.white;
            GUI.Label(new Rect(0, Screen.height / 2, Screen.width, 30), match.ResultText, center);
            GUI.Label(new Rect(0, Screen.height / 2 + 30, Screen.width, 30), "R play again (same role)  ·  M main menu", center);
            if (GUI.Button(new Rect(Screen.width / 2 - 154, Screen.height / 2 + 90, 150, 30), "Play again")) match.Restart(true);
            if (GUI.Button(new Rect(Screen.width / 2 + 4, Screen.height / 2 + 90, 150, 30), "Main menu")) match.Restart(false);
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
