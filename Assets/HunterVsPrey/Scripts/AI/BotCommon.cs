using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;

namespace HunterVsPrey
{
    /// <summary>
    /// Fair first-person vision for bots: a 110° view cone, limited range, and walls block line of sight.
    /// Bots never read an enemy's position unless this says they can see it (Hunter X-Ray is the one exception).
    /// </summary>
    public static class Vision
    {
        public const float Fov = 110f, Range = 40f;

        public static bool CanSee(Actor viewer, Actor target)
        {
            if (!target.IsActive) return false;
            Vector3 eye = viewer.EyePos;
            Vector3 to = target.Chest - eye;
            if (to.magnitude > Range) return false;
            if (Vector3.Angle(viewer.Eye.forward, to) > Fov * 0.5f) return false;
            Vector3 head = target.transform.position + Vector3.up * (target.Motor.IsCrouched ? 0.7f : 1.6f);
            if (Clear(eye, target.Chest) || Clear(eye, head)) return true;
            // Up close a player would duck to look under a wagon / beam: peek from knee height.
            // Without this a Prey crouched under a wagon is invisible to a bot standing right next to it.
            if (to.magnitude > PeekRange) return false;
            Vector3 low = viewer.transform.position + Vector3.up * 0.6f;
            return Clear(low, target.Chest) || Clear(low, head);
        }

        public const float PeekRange = 4f;
        static bool Clear(Vector3 a, Vector3 b) => !Physics.Linecast(a, b, Layers.EnvMask, QueryTriggerInteraction.Ignore);

        /// <summary>Same rules for objects (balls, buckets, the Gate).</summary>
        public static bool CanSeePoint(Actor viewer, Vector3 point, float range = Range)
        {
            Vector3 eye = viewer.EyePos;
            Vector3 to = point - eye;
            if (to.magnitude > range) return false;
            if (Vector3.Angle(viewer.Eye.forward, to) > Fov * 0.5f) return false;
            return !Physics.Linecast(eye, point, Layers.EnvMask, QueryTriggerInteraction.Ignore);
        }
    }

    /// <summary>
    /// What a bot knows. Anything newly in view is only *noticed* after a human-like reaction delay
    /// (200–300 ms of continuous sight). Once noticed it's tracked while visible and remembered
    /// (last known position + estimated velocity) after it disappears.
    /// </summary>
    public class Perception
    {
        public const float ReactionMin = 0.2f, ReactionMax = 0.3f;

        class Entry
        {
            public float visibleSince = -1f, delay, lastSeen = -999f;
            public bool noticed;
            public Vector3 lastPos, vel;
        }

        readonly Dictionary<Object, Entry> entries = new();

        /// <summary>Feed one observation. Returns true if the bot currently sees AND has noticed it.</summary>
        public bool Observe(Object key, bool visibleNow, Vector3 pos)
        {
            if (!entries.TryGetValue(key, out var e)) entries[key] = e = new Entry();
            if (!visibleNow)
            {
                e.visibleSince = -1f;
                if (Time.time - e.lastSeen > 1f) e.noticed = false; // out of sight a while → must re-notice
                return false;
            }
            if (e.visibleSince < 0f)
            {
                e.visibleSince = Time.time;
                e.delay = Random.Range(ReactionMin, ReactionMax);
            }
            if (!e.noticed && Time.time - e.visibleSince >= e.delay) e.noticed = true;
            if (!e.noticed) return false;
            Track(e, pos, Time.time);
            return true;
        }

        static void Track(Entry e, Vector3 pos, float time)
        {
            float gap = time - e.lastSeen;
            if (gap > 0.01f && gap < 0.5f) e.vel = Vector3.Lerp(e.vel, (pos - e.lastPos) / gap, 0.3f);
            else if (gap >= 0.5f) e.vel = Vector3.zero;
            e.lastPos = pos;
            e.lastSeen = time;
        }

        /// <summary>Last known position if it was known within maxAge seconds.</summary>
        public bool Recall(Object key, float maxAge, out Vector3 pos)
        {
            pos = default;
            if (key == null || !entries.TryGetValue(key, out var e) || e.lastSeen < -900f || Time.time - e.lastSeen > maxAge) return false;
            pos = e.lastPos;
            return true;
        }

        /// <summary>
        /// Told about it rather than seeing it: Hunter X-Ray (age 0, no reaction delay — it's a deliberate ability),
        /// a teammate's callout, or feeling where a hit came from. <paramref name="age"/> = how old the info already is.
        /// </summary>
        public void Inform(Object key, Vector3 pos, float age = 0f)
        {
            if (!entries.TryGetValue(key, out var e)) entries[key] = e = new Entry();
            float t = Time.time - age;
            if (t <= e.lastSeen) return; // we already know something newer
            if (age > 0f) { e.lastPos = pos; e.lastSeen = t; e.vel = Vector3.zero; }
            else Track(e, pos, t);
        }

        /// <summary>Seconds since we last knew where it was (infinity if never).</summary>
        public float Age(Object key) =>
            key != null && entries.TryGetValue(key, out var e) && e.lastSeen > -900f ? Time.time - e.lastSeen : float.PositiveInfinity;

        /// <summary>Estimated velocity from the last few sightings (zero if the info is old).</summary>
        public Vector3 Velocity(Object key) =>
            key != null && entries.TryGetValue(key, out var e) && Time.time - e.lastSeen < 0.5f ? e.vel : Vector3.zero;

        /// <summary>Velocity at the last sighting, however old (for guessing where it went).</summary>
        public Vector3 LastVelocity(Object key) =>
            key != null && entries.TryGetValue(key, out var e) ? e.vel : Vector3.zero;

        public bool Noticed(Object key) => key != null && entries.TryGetValue(key, out var e) && e.noticed && e.visibleSince >= 0f;

        public void Forget(Object key) { if (key != null) entries.Remove(key); }

        /// <summary>Drop entries for destroyed objects (collected balls).</summary>
        public void Prune()
        {
            var dead = new List<Object>();
            foreach (var k in entries.Keys) if (k == null) dead.Add(k);
            foreach (var k in dead) entries.Remove(k);
        }
    }

    /// <summary>
    /// Coarse exploration memory (8 m cells). Bots that don't know where something is go search the
    /// part of the map they've looked at least recently — the honest alternative to a GPS.
    /// </summary>
    public class Explorer
    {
        const float Cell = 8f;
        readonly int n;
        readonly float[] lastSeen;

        public Explorer()
        {
            n = Mathf.CeilToInt(Arena.Half * 2 / Cell);
            lastSeen = new float[n * n];
            for (int k = 0; k < lastSeen.Length; k++) lastSeen[k] = Random.Range(-30f, 0f); // spread first picks
        }

        Vector3 Centre(int cx, int cz) => new(-Arena.Half + (cx + 0.5f) * Cell, 0.5f, -Arena.Half + (cz + 0.5f) * Cell);

        /// <summary>Marks cells the viewer can currently see (near + in view) as explored.</summary>
        public void Look(Actor viewer)
        {
            for (int cx = 0; cx < n; cx++)
                for (int cz = 0; cz < n; cz++)
                {
                    Vector3 c = Centre(cx, cz);
                    float d = Vector3.Distance(new Vector3(c.x, 0, c.z), new Vector3(viewer.transform.position.x, 0, viewer.transform.position.z));
                    if (d < Cell * 0.75f || (d < 22f && Vision.CanSeePoint(viewer, c + Vector3.up)))
                        lastSeen[cx * n + cz] = Time.time;
                }
        }

        /// <summary>Least-recently-seen reachable cell, nearer is better, cells near <paramref name="avoid"/> are penalised.</summary>
        public Vector3 NextTarget(Vector3 from, List<Vector3> avoid = null, float avoidRadius = 20f)
        {
            float best = float.MinValue;
            Vector3 target = from;
            for (int cx = 0; cx < n; cx++)
                for (int cz = 0; cz < n; cz++)
                {
                    Vector3 c = Centre(cx, cz);
                    float score = (Time.time - lastSeen[cx * n + cz]) - Vector3.Distance(c, from) * 0.6f + Random.Range(0f, 4f);
                    if (avoid != null)
                        foreach (var a in avoid)
                        {
                            float d = Vector2.Distance(new Vector2(c.x, c.z), new Vector2(a.x, a.z));
                            if (d < avoidRadius) score -= (avoidRadius - d) * 2f; // Prey keep away from known Hunters, Hunters split up
                        }
                    if (score <= best) continue;
                    var s = Arena.Snap(c, 4f);
                    if (s == null) continue;
                    best = score;
                    target = s.Value;
                }
            return target;
        }
    }

    /// <summary>
    /// Base for bots: follows A* paths over the navmesh (Unity's NavMesh.CalculatePath is A* on navmesh polygons),
    /// auto-jumps low walls, slides under low beams, and turns at a human-like rate.
    /// Also handles team callouts (like voice chat) and knowing who just hit you.
    /// </summary>
    public abstract class BotBase : InputProvider
    {
        protected MatchManager match;
        protected Actor self;
        protected readonly Perception perception = new();
        protected Explorer explorer; // created in InitBase: Random can't run in a MonoBehaviour field initializer
        protected BehaviourTree tree;
        protected const float TurnRate = 320f; // deg/s, no instant 180° snaps

        /// <summary>Points the explorer should keep away from (filled by the subclass).</summary>
        protected readonly List<Vector3> exploreAvoid = new();
        Vector3 exploreTarget;
        float exploreTimer, lookTimer, pruneTimer;

        /// <summary>The goal this bot is walking to (teammates use it to spread out).</summary>
        public Vector3 CurrentGoal => destination;

        // ---------------- debug ----------------

        /// <summary>Active behaviour-tree branch + fuzzy values, shown on the spectator HUD.</summary>
        public string BrainText => $"{DecisionText}\n{FuzzySummary()}";
        protected virtual string DecisionText => tree?.ActivePath;
        protected abstract string FuzzySummary();

        // ---------------- supervised learning ----------------

        /// <summary>True when this bot's decisions come from the trained network instead of the BT + fuzzy logic.</summary>
        public bool UsesSL { get; protected set; }

        /// <summary>
        /// Shadow = attached to a HUMAN player to record SL training data. It perceives the match exactly like a bot
        /// would (vision cone, reaction delay, memory), works out what the human is doing (the label), and never steers.
        /// </summary>
        public bool Shadow { get; protected set; }
        float sampleTimer;
        /// <summary>When the last training row was written (quick events since then still get labelled).</summary>
        protected float lastSampleAt = -99f;
        Vector3 lastPos;
        /// <summary>The human's smoothed horizontal velocity (shadow mode).</summary>
        protected Vector3 humanVelocity;

        /// <summary>Time to record another (features, label) sample? (10 Hz.)</summary>
        protected bool ShouldSample(float dt)
        {
            sampleTimer -= dt;
            if (sampleTimer > 0f) return false;
            sampleTimer = SlSettings.SampleInterval;
            lastSampleAt = Time.time;
            return true;
        }

        /// <summary>Runs after the human's own movement this frame.</summary>
        void LateUpdate()
        {
            if (!Shadow || match == null || match.State != MatchManager.Phase.Playing || !self.IsActive || self.Frozen) return;
            float dt = Time.deltaTime;
            if (dt <= 0f) return;
            Vector3 v = (self.transform.position - lastPos) / dt; v.y = 0;
            lastPos = self.transform.position;
            humanVelocity = v.magnitude > 15f ? Vector3.zero : Vector3.Lerp(humanVelocity, v, 0.3f); // ignore teleports
            ShadowTick(dt);
        }

        protected virtual void ShadowTick(float dt) { }

        /// <summary>Is the human heading that way? (flat velocity vs. a direction)</summary>
        protected bool Heading(Vector3 dir, float minDot)
        {
            dir.y = 0;
            return humanVelocity.magnitude > 1.5f && dir.sqrMagnitude > 0.01f && Vector3.Dot(humanVelocity.normalized, dir.normalized) > minDot;
        }

        /// <summary>Direction of the first leg of the A* path from me to a point (follows corridors, not walls).</summary>
        protected Vector3 PathDirection(Vector3 to)
        {
            Vector3 me = self.transform.position;
            float len = Arena.PathLength(me, to, scratch);
            if (!float.IsInfinity(len) && scratch.corners.Length > 1) return scratch.corners[1] - me;
            return to - me;
        }

        /// <summary>Log every branch change to the console (for tuning).</summary>
        public static bool TraceDecisions;
        string lastTrace;

        protected void TraceDecision()
        {
            if (!TraceDecisions) return;
            string p = tree.ActivePath;
            if (p == lastTrace) return;
            lastTrace = p;
            Debug.Log($"[AI {Time.time:0.0}] {self.DisplayName}: {p}  |  {FuzzySummary()}");
        }

        // ---------------- team callouts ----------------

        // A callout takes a moment to say and hear, so it arrives with a delay and as slightly old info.
        const float CalloutDelayMin = 0.8f, CalloutDelayMax = 1.4f;
        readonly List<(Object key, Vector3 pos, float arrive, float age)> inbox = new();
        readonly Dictionary<Object, float> lastCallout = new();

        /// <summary>Tell bot teammates where something is ("Hunter at the market!"), at most once per <paramref name="every"/> s.</summary>
        protected void Callout(Object key, Vector3 pos, float every = 1.5f)
        {
            if (Shadow) return; // the real human does their own talking
            if (lastCallout.TryGetValue(key, out var t) && Time.time - t < every) return;
            lastCallout[key] = Time.time;
            TeamCallout(match, self.Team, key, pos, self);
        }

        public static void TeamCallout(MatchManager m, Team team, Object key, Vector3 pos, Actor from)
        {
            foreach (var a in m.AllActors)
                if (a != from && a.Team == team && a.IsActive && a.Input is BotBase bot) // humans (and their shadows) don't hear bot callouts
                {
                    float delay = Random.Range(CalloutDelayMin, CalloutDelayMax);
                    bot.inbox.Add((key, pos, Time.time + delay, delay));
                }
        }

        /// <summary>Deliver callouts whose delay has passed; also learn where a hit came from.</summary>
        protected void Listen()
        {
            for (int k = inbox.Count - 1; k >= 0; k--)
            {
                var (key, pos, arrive, age) = inbox[k];
                if (Time.time < arrive) continue;
                if (key != null) perception.Inform(key, pos, age);
                inbox.RemoveAt(k);
            }
            // you feel a tag / paintball and know roughly where it came from
            if (self.LastHurtTime > lastHurtHandled)
            {
                lastHurtHandled = self.LastHurtTime;
                if (self.LastAttacker) perception.Inform(self.LastAttacker, self.LastAttackerPos, Time.time - self.LastHurtTime + 0.01f);
            }
            pruneTimer -= Time.deltaTime;
            if (pruneTimer <= 0f) { pruneTimer = 5f; perception.Prune(); }
        }
        float lastHurtHandled = -99f;

        // ---------------- exploration ----------------

        /// <summary>Update the exploration map a few times a second (cheap enough for 6 bots).</summary>
        protected void TickExploration(float dt)
        {
            lookTimer -= dt;
            if (lookTimer > 0f) return;
            lookTimer = 0.3f;
            explorer.Look(self);
        }

        /// <summary>Search the least-recently-seen part of the map.</summary>
        protected Vector3 ExploreTarget(float dt)
        {
            exploreTimer -= dt;
            if (exploreTimer <= 0f || Arrived(2f))
            {
                exploreTimer = 10f;
                exploreTarget = explorer.NextTarget(self.transform.position, exploreAvoid);
            }
            return exploreTarget;
        }

        // ---------------- movement ----------------

        NavMeshPath path;
        protected NavMeshPath scratch; // for path-length (A*) queries
        Vector3[] corners = new Vector3[0];
        int corner;
        Vector3 destination;
        bool hasDest;
        float repathTimer;
        Vector3 stuckCheckPos;
        float stuckTimer, unstickTimer;

        protected void InitBase(MatchManager m, Actor a)
        {
            match = m;
            self = a;
            path = new NavMeshPath();
            scratch = new NavMeshPath();
            explorer = new Explorer();
            stuckCheckPos = lastPos = a.transform.position;
            destination = a.transform.position;
        }

        protected void GoTo(Vector3 dest)
        {
            if (hasDest && (dest - destination).sqrMagnitude < 0.5f) return;
            destination = dest;
            hasDest = true;
            repathTimer = 0f;
        }

        protected void Stop() { hasDest = false; corners = new Vector3[0]; }

        protected bool Arrived(float within = 1.2f)
        {
            if (!hasDest) return true;
            Vector3 d = destination - self.transform.position; d.y = 0;
            return d.magnitude < within;
        }

        /// <summary>Direction to walk this frame (world, flat), or zero.</summary>
        protected Vector3 Steer(float dt)
        {
            if (!hasDest) return Vector3.zero;
            repathTimer -= dt;
            Vector3 pos = self.transform.position;
            if (repathTimer <= 0f)
            {
                repathTimer = 0.4f;
                var a = Arena.Snap(pos, 3f);
                var b = Arena.Snap(destination, 3f);
                if (a != null && b != null && NavMesh.CalculatePath(a.Value, b.Value, NavMesh.AllAreas, path))
                {
                    corners = path.corners;
                    corner = corners.Length > 1 ? 1 : 0;
                }
                else corners = new Vector3[0];
            }
            while (corner < corners.Length)
            {
                Vector3 d = corners[corner] - pos; d.y = 0;
                if (d.magnitude > 0.6f) return d.normalized;
                corner++;
            }
            Vector3 last = destination - pos; last.y = 0;
            return last.magnitude > 0.5f ? last.normalized : Vector3.zero;
        }

        /// <summary>Somewhere to look while walking when the action itself doesn't say (e.g. a ball the Prey wants to mark).</summary>
        protected Vector3? lookOverride;

        /// <summary>Turns the desired world move + look direction into an ActorInput (same controls a human has).</summary>
        protected ActorInput Drive(Vector3 moveDir, Vector3? lookAt, bool run, float dt)
        {
            var i = new ActorInput { run = run };
            Vector3 pos = self.transform.position;
            lookAt ??= lookOverride;

            // stuck → jump and sidestep briefly (alternating sides; crouch too if it keeps happening)
            stuckTimer += dt;
            if (stuckTimer > 1.2f)
            {
                if (moveDir != Vector3.zero && (pos - stuckCheckPos).magnitude < 0.4f)
                {
                    stuckCount++;
                    unstickTimer = 0.6f;
                    i.jump = true;
                    repathTimer = 0f;
                }
                else stuckCount = 0;
                stuckCheckPos = pos;
                stuckTimer = 0f;
            }
            bool unsticking = unstickTimer > 0f;
            if (unsticking) { unstickTimer -= dt; moveDir = Quaternion.Euler(0, stuckCount % 2 == 0 ? -70 : 70, 0) * moveDir; }

            // face where we look (enemy) or where we go
            Vector3 face = lookAt.HasValue ? lookAt.Value - self.EyePos : moveDir;
            if (face.sqrMagnitude > 0.001f)
            {
                float targetYaw = Mathf.Atan2(face.x, face.z) * Mathf.Rad2Deg;
                i.yawDelta = Mathf.Clamp(Mathf.DeltaAngle(self.Motor.Yaw, targetYaw), -TurnRate * dt, TurnRate * dt);
                float flat = new Vector2(face.x, face.z).magnitude;
                float targetPitch = lookAt.HasValue ? -Mathf.Atan2(face.y, flat) * Mathf.Rad2Deg : 0f;
                i.pitchDelta = Mathf.Clamp(Mathf.DeltaAngle(self.Motor.Pitch, targetPitch), -TurnRate * dt, TurnRate * dt);
            }

            if (moveDir != Vector3.zero)
            {
                // the motor turns first, then moves: express the move in the post-turn frame
                Vector3 local = Quaternion.Inverse(Quaternion.Euler(0, self.Motor.Yaw + i.yawDelta, 0)) * moveDir;
                i.move = new Vector2(local.x, local.z);

                // parkour: jump low walls, slide under low beams
                Vector3 flatDir = new Vector3(moveDir.x, 0, moveDir.z).normalized;
                bool kneeBlocked = Physics.Raycast(pos + Vector3.up * 0.4f, flatDir, 1.0f, Layers.EnvMask, QueryTriggerInteraction.Ignore);
                bool waistClear = !Physics.Raycast(pos + Vector3.up * 1.0f, flatDir, 1.2f, Layers.EnvMask, QueryTriggerInteraction.Ignore);
                if (kneeBlocked && waistClear && self.Motor.IsGrounded) i.jump = true;
                bool headBlocked = Physics.Raycast(pos + Vector3.up * 1.5f, flatDir, 1.8f, Layers.EnvMask, QueryTriggerInteraction.Ignore);
                bool shinClear = !Physics.Raycast(pos + Vector3.up * 0.5f, flatDir, 1.8f, Layers.EnvMask, QueryTriggerInteraction.Ignore);
                if ((headBlocked && shinClear) || self.Motor.IsCrouched && Physics.Raycast(pos + Vector3.up * 0.5f, Vector3.up, 1.4f, Layers.EnvMask))
                    i.slide = true;
                if (unsticking && stuckCount >= 2) i.slide = true; // wedged under something: get low
            }
            return i;
        }
        int stuckCount;

        /// <summary>Walk the current path, facing where we go.</summary>
        protected ActorInput Travel(Vector3 dest, float dt, Vector3? lookAt = null)
        {
            GoTo(dest);
            return Drive(Steer(dt), lookAt, true, dt);
        }

        float glanceTimer;

        /// <summary>Occasionally glance around (like a human checking their back) so things behind can be noticed.</summary>
        protected ActorInput Glance(ActorInput i, float dt)
        {
            glanceTimer -= dt;
            if (glanceTimer < 0f) glanceTimer = Random.Range(3f, 6f);
            if (glanceTimer < 0.6f) i.yawDelta = TurnRate * dt;
            return i;
        }

        protected Vector3 RandomNavPoint()
        {
            for (int k = 0; k < 20; k++)
            {
                var p = Arena.Snap(new Vector3(Random.Range(-Arena.Half + 2f, Arena.Half - 2f), 1f, Random.Range(-Arena.Half + 2f, Arena.Half - 2f)), 2f);
                if (p != null) return p.Value;
            }
            return Vector3.zero;
        }
    }
}
