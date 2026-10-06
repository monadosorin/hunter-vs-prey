using UnityEngine;

namespace HunterVsPrey
{
    /// <summary>Test helper: logs close encounters (Hunter within 8 m of a Prey) 4× per second.</summary>
    public class AiProbe : MonoBehaviour
    {
        MatchManager match;
        float timer;
        readonly System.Collections.Generic.Dictionary<Actor, Vector3> last = new();

        void Start() => match = FindFirstObjectByType<MatchManager>();

        void Update()
        {
            timer -= Time.deltaTime;
            if (timer > 0f || match == null || match.State != MatchManager.Phase.Playing) return;
            float step = 0.25f - timer;
            timer = 0.25f;
            foreach (var p in match.Prey)
            {
                if (!p.IsActive) continue;
                foreach (var h in match.Hunters)
                {
                    if (!h.IsActive) continue;
                    float d = Vector3.Distance(p.transform.position, h.transform.position);
                    if (d > 8f) continue;
                    Debug.Log($"[PROBE {match.matchDuration - match.TimeLeft:0.00}] {p.DisplayName} hp{p.Hp} v{Speed(p, step):0.0} ammo{(p.HasAmmo ? 1 : 0)} <- {h.DisplayName} v{Speed(h, step):0.0} d{d:0.0} | " +
                              $"{(p.Input as BotBase)?.BrainText.Replace("\n", " | ")}");
                }
            }
            foreach (var a in match.AllActors) last[a] = a.transform.position;
        }

        float Speed(Actor a, float dt)
        {
            if (!last.TryGetValue(a, out var prev)) return 0f;
            Vector3 d = a.transform.position - prev; d.y = 0;
            return d.magnitude / Mathf.Max(dt, 0.01f);
        }
    }
}
