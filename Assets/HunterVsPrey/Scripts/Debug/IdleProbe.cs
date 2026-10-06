using System.Collections.Generic;
using UnityEngine;

namespace HunterVsPrey
{
    /// <summary>Test helper: logs any bot that barely moved over the last 4 s (stuck / standing around), with its BT branch.</summary>
    public class IdleProbe : MonoBehaviour
    {
        MatchManager match;
        float timer;
        readonly Dictionary<Actor, Vector3> last = new();
        public static readonly List<string> Reports = new();

        void Start() => match = FindFirstObjectByType<MatchManager>();

        void Update()
        {
            if (match == null || match.State != MatchManager.Phase.Playing) return;
            timer -= Time.deltaTime;
            if (timer > 0f) return;
            timer = 4f;
            foreach (var a in match.AllActors)
            {
                if (!a.IsActive || a.Stunned || a.Input is not BotBase bot) { last.Remove(a); continue; }
                Vector3 p = a.transform.position;
                if (last.TryGetValue(a, out var prev) && Vector3.Distance(prev, p) < 1.0f)
                {
                    string s = $"[IDLE {match.matchDuration - match.TimeLeft:0}s] {a.DisplayName} at ({p.x:0.0},{p.y:0.0},{p.z:0.0}) crouch {a.Motor.IsCrouched} | {bot.BrainText.Replace("\n", " | ")}";
                    Reports.Add(s);
                    Debug.Log(s);
                }
                last[a] = p;
            }
        }
    }
}
