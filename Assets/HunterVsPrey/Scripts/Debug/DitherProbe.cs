using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace HunterVsPrey
{
    /// <summary>
    /// Test helper: measures how "jittery" the Prey bots are — branch switches, sharp turn-backs while not fighting,
    /// time spent fleeing while no Hunter is actually within 20 m (running from a memory) — and which branch they
    /// were in when tagged. <see cref="RunBatch"/> plays several all-AI matches in a row (fast-forwarded) and
    /// adds up the numbers over all of them.
    /// </summary>
    public class DitherProbe : MonoBehaviour
    {
        static readonly FieldInfo CurrentField = typeof(PreyBot).GetField("current", BindingFlags.NonPublic | BindingFlags.Instance);
        static readonly FieldInfo PeekField = typeof(PreyBot).GetField("peekLeft", BindingFlags.NonPublic | BindingFlags.Instance);
        static readonly PropertyInfo SpeedProp = typeof(MatchManager).GetProperty("SpeedUp");

        class Stat { public int hp = -1; public string last; public Vector3 lastDir; public float dirTimer; }
        readonly Dictionary<PreyActor, Stat> stats = new();
        readonly Dictionary<string, int> pairs = new();
        int switches, reversals;
        float evade, phantom, alive;
        public readonly List<string> Results = new();

        MatchManager match;
        int runsLeft;
        float speed = 4f;
        bool counted;

        /// <summary>Play <paramref name="runs"/> all-AI matches back to back at <paramref name="timeScale"/>×.</summary>
        public static DitherProbe RunBatch(int runs, float timeScale = 4f)
        {
            var go = new GameObject("DitherProbe");
            DontDestroyOnLoad(go);
            var p = go.AddComponent<DitherProbe>();
            p.runsLeft = runs;
            p.speed = timeScale;
            return p;
        }

        public bool Done => runsLeft <= 0;

        void Update()
        {
            if (match == null)
            {
                match = FindFirstObjectByType<MatchManager>();
                if (match == null || runsLeft <= 0) return;
                stats.Clear();
                counted = false;
                match.BeginMatch(null);
                SpeedProp.SetValue(match, speed);
            }
            if (match.State == MatchManager.Phase.Over)
            {
                if (counted) return;
                counted = true;
                Results.Add($"match {Results.Count + 1}: {match.matchDuration - match.TimeLeft:0}s · {(match.Feed.Count > 0 ? match.Feed[match.Feed.Count - 1].text : "")}");
                if (--runsLeft > 0) { match.Restart(false); match = null; }
                return;
            }
            if (match.State != MatchManager.Phase.Playing) return;
            float dt = Time.deltaTime;
            foreach (var p in match.Prey)
            {
                if (p.Input is not PreyBot bot || p.Escaped) continue;
                if (!stats.TryGetValue(p, out var s)) stats[p] = s = new Stat();
                if (s.hp >= 0 && p.Hp < s.hp) Count("HIT in " + s.last + ((float)PeekField.GetValue(bot) > 0f ? " (peeking)" : ""));
                s.hp = p.Hp;
                if (!p.IsActive) continue;
                string b = CurrentField.GetValue(bot).ToString();
                alive += dt;
                if (s.last != null && b != s.last) { switches++; Count(s.last + ">" + b); }
                s.last = b;
                if (b == "Evade")
                {
                    evade += dt;
                    float near = float.PositiveInfinity;
                    foreach (var h in match.Hunters) if (h.IsActive) near = Mathf.Min(near, Vector3.Distance(h.transform.position, p.transform.position));
                    if (near > 20f) phantom += dt;
                }
                // turn-backs: compare the walking direction every 0.4 s
                s.dirTimer -= dt;
                if (s.dirTimer <= 0f)
                {
                    s.dirTimer = 0.4f;
                    Vector3 v = p.Motor.Velocity; v.y = 0;
                    if (v.magnitude > 1.5f)
                    {
                        Vector3 d = v.normalized;
                        if (s.lastDir != Vector3.zero && b != "Fight" && Vector3.Dot(d, s.lastDir) < -0.5f) { reversals++; Count("rev:" + b); }
                        s.lastDir = d;
                    }
                }
            }
        }

        void Count(string k) => pairs[k] = pairs.TryGetValue(k, out var n) ? n + 1 : 1;

        public string Report()
        {
            var sb = new System.Text.StringBuilder();
            foreach (var r in Results) sb.AppendLine(r);
            float m = Mathf.Max(alive, 1f) / 60f;
            sb.AppendLine($"Prey-minutes alive {m:0.0} · switches/min {switches / m:0.0} · turn-backs/min {reversals / m:0.0} · evade {evade:0}s, phantom {phantom:0}s ({(evade > 0 ? phantom / evade * 100f : 0):0}%)");
            var top = new List<KeyValuePair<string, int>>(pairs);
            top.Sort((a, c) => c.Value.CompareTo(a.Value));
            foreach (var kv in top) sb.Append(kv.Key).Append(' ').Append(kv.Value).Append(" · ");
            return sb.ToString();
        }
    }
}
