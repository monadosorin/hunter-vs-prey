using UnityEngine;

namespace HunterVsPrey
{
    /// <summary>
    /// Prey AI = Behaviour Tree (what to do, in priority order) + Fuzzy Logic (how much it wants to).
    ///
    ///   Prey (selector)
    ///   ├─ Escape     Gate known, buckets full, fuzzy Commit ≥ Danger     → Run to the Gate
    ///   ├─ Fight      Hunter visible, loaded, fuzzy ShootDesire ≥ 0.60    → Aim (with lead) and shoot
    ///   ├─ Break Camp a Hunter is camping a dropped bucket, loaded,
    ///   │             fuzzy Courage ≥ 0.50                                 → Sneak into paintball range of the camper
    ///   ├─ Evade      fuzzy Danger ≥ 0.40 and Danger > Commit             → Flee to cover, glancing back, peek
    ///   ├─ Collect    a ball for my bucket is known                        → Get it
    ///   ├─ Take Over  my buckets are full, a dropped bucket is known       → Pick it up
    ///   └─ Explore    search unseen areas, away from known Hunters
    ///
    /// Fair: no GPS. Balls, buckets, the Gate and Hunters are known only by sight (110° cone, line of sight,
    /// 200–300 ms reaction), by teammate callouts (~1 s delay, like voice chat) or by feeling a tag.
    /// </summary>
    public class PreyBot : BotBase
    {
        /// <summary>Prey call out balls of any colour they spot ("blue ball in the market!").</summary>
        public static bool ShareBallSightings = true;

        /// <summary>What a Prey assumes about an unseen chaser's speed (everyone knows Hunters run ~5 m/s).</summary>
        const float HunterRunSpeed = 5f;

        // ======================= Fuzzy systems =======================

        /// <summary>How dangerous is this Hunter? (distance, how fresh the info is, my HP, is it coming at me)</summary>
        public static readonly FuzzySystem DangerFis = new("Danger", 4);
        /// <summary>Is the objective worth the risk? (path distance to it, current danger, time left)</summary>
        public static readonly FuzzySystem CommitFis = new("Commit", 3);
        /// <summary>Is this a good moment for my one paintball? (distance, Hunter approaching?, danger)</summary>
        public static readonly FuzzySystem ShootFis = new("Shoot desire", 3);
        /// <summary>
        /// Dare to go fight a Hunter camping a dropped bucket? (distance to the camper, my HP, Prey alive)
        /// A hit stuns the camper 5 s per living Hunter and reloads in 20 × alive/4 s, so the fewer Prey are left,
        /// the more it pays off (and the more it's needed).
        /// </summary>
        public static readonly FuzzySystem CourageFis = new("Courage", 3);

        static PreyBot()
        {
            var dist = DangerFis.Input("HunterDistance", 0f, 40f)
                .Term("Close", Mf.Left(4f, 8f)).Term("Medium", Mf.Tri(5f, 11f, 18f)).Term("Far", Mf.Right(13f, 24f));
            var age = DangerFis.Input("InfoAge", 0f, 8f)
                .Term("Now", Mf.Left(0.3f, 1f)).Term("Recent", Mf.Tri(0.5f, 2f, 4f)).Term("Old", Mf.Right(3f, 6f));
            var hp = DangerFis.Input("MyHp", 1f, 2f)
                .Term("Low", Mf.Left(1f, 2f)).Term("Full", Mf.Right(1f, 2f));
            var coming = DangerFis.Input("ClosingSpeed", -5f, 5f)
                .Term("Leaving", Mf.Left(-1.5f, 0f)).Term("Approaching", Mf.Right(0.5f, 2.5f));
            DangerFis.Output01("Danger");
            DangerFis
                .Rule(coming["Approaching"] & !dist["Far"] & !age["Old"], "High") // it's running at me
                .Rule(coming["Leaving"] & !dist["Close"], "Low")
                .Rule(dist["Close"] & age["Now"], "High")
                .Rule(dist["Close"] & age["Recent"], "High")
                .Rule(dist["Close"] & age["Old"], "Medium")
                .Rule(dist["Medium"] & age["Now"] & hp["Full"], "Medium")
                .Rule(dist["Medium"] & age["Now"] & hp["Low"], "High") // one more tag and I'm out
                .Rule(dist["Medium"] & age["Recent"], "Medium")
                .Rule(dist["Far"] & !age["Old"], "Low")
                .Rule(age["Old"] & !dist["Close"], "Low");

            var objDist = CommitFis.Input("ObjectiveDistance", 0f, 60f)
                .Term("Near", Mf.Left(5f, 10f)).Term("Mid", Mf.Tri(6f, 16f, 28f)).Term("Far", Mf.Right(20f, 35f));
            var danger = CommitFis.Input("Danger", 0f, 1f)
                .Term("Low", Mf.Left(0.2f, 0.45f)).Term("Medium", Mf.Tri(0.3f, 0.5f, 0.7f)).Term("High", Mf.Right(0.55f, 0.8f));
            var time = CommitFis.Input("TimeLeft", 0f, 300f)
                .Term("Little", Mf.Left(45f, 90f)).Term("Some", Mf.Tri(60f, 120f, 200f)).Term("Plenty", Mf.Right(150f, 220f));
            CommitFis.Output01("Commit");
            CommitFis
                .Rule(danger["Low"], "High")
                .Rule(objDist["Near"] & danger["Medium"], "High")
                .Rule(objDist["Near"] & danger["High"], "Medium")
                .Rule(objDist["Mid"] & danger["Medium"], "Medium")
                .Rule(objDist["Mid"] & danger["High"], "Low")
                .Rule(objDist["Far"] & !danger["Low"], "Low")
                .Rule(time["Little"] & objDist["Near"], "High") // clock's running out: take risks
                .Rule(time["Little"] & objDist["Mid"], "Medium")
                .Rule(time["Plenty"] & danger["High"], "Low");

            var range = ShootFis.Input("HunterDistance", 0f, 30f)
                .Term("PointBlank", Mf.Left(3f, 6f)).Term("Good", Mf.Tri(3f, 9f, 16f)).Term("Long", Mf.Right(12f, 20f));
            var closing = ShootFis.Input("ClosingSpeed", -5f, 5f)
                .Term("Leaving", Mf.Left(-2f, 0f)).Term("Steady", Mf.Tri(-1.5f, 0f, 1.5f)).Term("Approaching", Mf.Right(0f, 2.5f));
            var threat = ShootFis.Input("Danger", 0f, 1f)
                .Term("Low", Mf.Left(0.2f, 0.45f)).Term("Medium", Mf.Tri(0.3f, 0.5f, 0.7f)).Term("High", Mf.Right(0.55f, 0.8f));
            ShootFis.Output01("Shoot");
            ShootFis
                .Rule(range["PointBlank"], "High")
                .Rule(range["Good"] & closing["Approaching"], "High")
                .Rule(range["Good"] & closing["Steady"], "Medium")
                .Rule(range["Good"] & closing["Leaving"], "Low")
                .Rule(range["Long"], "Low")
                .Rule(threat["High"] & range["Good"], "High")
                .Rule(threat["Low"] & !range["PointBlank"], "Low"); // not a threat: save the ball

            var campDist = CourageFis.Input("CamperDistance", 0f, 60f)
                .Term("Near", Mf.Left(10f, 20f)).Term("Mid", Mf.Tri(12f, 25f, 40f)).Term("Far", Mf.Right(30f, 45f));
            var myHp = CourageFis.Input("MyHp", 1f, 2f)
                .Term("Low", Mf.Left(1f, 2f)).Term("Full", Mf.Right(1f, 2f));
            var alive = CourageFis.Input("PreyAlive", 1f, 4f)
                .Term("Few", Mf.Left(1.5f, 3f)).Term("Many", Mf.Right(2f, 3.5f));
            CourageFis.Output01("Courage");
            CourageFis
                .Rule(myHp["Full"] & !campDist["Far"], "High")
                .Rule(myHp["Full"] & campDist["Far"], "Medium")
                .Rule(myHp["Low"] & campDist["Near"] & alive["Few"], "High") // last ones standing: no choice
                .Rule(myHp["Low"] & campDist["Near"] & alive["Many"], "Medium")
                .Rule(myHp["Low"] & !campDist["Near"], "Low")
                .Rule(alive["Few"] & !campDist["Far"], "High"); // fast reload, and the bucket is needed
        }

        // ======================= Blackboard =======================

        enum Goal { None, Gate, Ball, Bucket }

        PreyActor prey;
        ActorInput output;
        float dt;

        HunterActor threat;
        Vector3 threatPos;
        float threatDist = float.PositiveInfinity, closingSpeed;
        bool threatSeen;
        float danger, commit, shootDesire;

        Goal goal;
        Object goalKey;
        Vector3 goalPos;
        float goalDist = 60f, goalTimer;
        Vector3 goalDirection; // first leg of the A* path to the goal

        readonly System.Collections.Generic.Dictionary<HunterActor, float> stunnedUntil = new();
        Vector3 fleePoint, aimError;
        float fleeTimer, glanceBackTimer;

        // Staying calm (no back-and-forth): keep an escape spot for a while, commit to fleeing for a moment once
        // started, rest a moment before starting again, and peek back from cover before deciding what next.
        const float FleeHold = 2.5f, MinEvade = 2f, EvadeRest = 1.5f, PeekTime = 1f, PeekMinDist = 14f;
        float evadeSince = -99f, evadeEnded = -99f, peekLeft;
        bool wasEvading, peeked;

        // A Hunter's X-Ray is on (every Prey sees the warning on their HUD): they know where I am → keep moving.
        float exposedUntil = -99f;
        bool xrayWasOn;
        bool Exposed => Time.time < exposedUntil;

        // a Hunter believed to be camping a dropped bucket (from where I last saw / heard it)
        HunterActor camper;
        Vector3 camperPos, shotSpot;
        float camperDist, courage, shotTimer;
        bool camperSeen;

        // ======================= Supervised learning =======================

        /// <summary>The behaviours (top-level BT branches) = the SL classes; for a human player they are inferred from what they do.</summary>
        public enum Act { Escape, Fight, Evade, Collect, TakeOver, Explore }
        static readonly string[] BranchNames = { "Escape", "Fight", "Evade", "Collect", "Take Over", "Explore" };

        /// <summary>
        /// What the network sees: only fair, perceived information (never true positions of unseen things)
        /// and no fuzzy outputs — it has to learn the danger/commit/shoot judgement itself.
        /// </summary>
        public static readonly string[] FeatureNames =
        {
            "h1_known", "h1_dist", "h1_age", "h1_closing", "h1_seen", "h1_stunned",
            "h2_known", "h2_dist", "h2_age", "h2_closing", "h2_seen", "h2_stunned",
            "wounded", "has_ammo", "reload", "boosted", "invulnerable",
            "goal_none", "goal_gate", "goal_ball", "goal_bucket", "goal_dist",
            "time_left", "all_full", "gate_open", "gate_known", "bucket_fill", "teammates_alive",
            "prev_escape", "prev_fight", "prev_evade", "prev_collect", "prev_takeover", "prev_explore",
        };
        public const string PolicyModel = "prey_policy", Dataset = "prey";

        struct HunterInfo { public HunterActor h; public Vector3 pos; public float dist, age, closing; public bool seen, stunned; }
        readonly System.Collections.Generic.List<HunterInfo> known = new();
        readonly float[] features = new float[FeatureNames.Length];
        readonly bool[] valid = new bool[6];
        Act previous = Act.Explore, current = Act.Explore;
        Mlp policy;
        float confidence;

        public static bool SlAvailable => Mlp.Load(PolicyModel)?.Matches(FeatureNames) == true;

        public PreyBot Init(MatchManager m, PreyActor p)
        {
            InitBase(m, p);
            prey = p;
            if (SlSettings.PreyBrain == BrainKind.SL && SlAvailable)
            {
                policy = Mlp.Load(PolicyModel);
                UsesSL = true;
            }
            tree = new BehaviourTree(new Selector("Prey",
                new Sequence("Escape",
                    new Condition("gate known, worth the risk", () => goal == Goal.Gate && (commit >= danger || goalDist < 10f)),
                    new BtAction("Run to Gate", GoToGoal)),
                new Sequence("Fight",
                    new Condition("good shot", () => shootDesire >= 0.6f),
                    new BtAction("Aim & shoot", Shoot)),
                new Sequence("Break Camp",
                    new Condition("camper known, brave enough", () => camper && prey.HasAmmo && courage >= (tree.WasActive("Break Camp") ? 0.4f : 0.5f)),
                    new BtAction("Get in range of the camper", BreakCamp)),
                new Sequence("Evade",
                    new Condition("in danger", InDanger),
                    new BtAction("Flee to cover", Flee)),
                new Sequence("Collect",
                    new Condition("know a ball", () => goal == Goal.Ball),
                    new BtAction("Get ball", GoToGoal)),
                new Sequence("Take Over",
                    new Condition("know a dropped bucket", () => goal == Goal.Bucket),
                    new BtAction("Get bucket", GoToGoal)),
                new BtAction("Explore", Explore)));
            return this;
        }

        public override ActorInput Read(float dt)
        {
            this.dt = dt;
            TickExploration(dt);
            Listen();
            SenseHunters();
            SenseObjects();
            ChooseGoal();
            PlanMark();
            SenseXray();
            output = new ActorInput();
            previous = current;
            if (UsesSL) { DecideSL(); TrackEvade(current == Act.Evade); }
            else
            {
                commit = CommitFis.Evaluate(goal == Goal.None ? 60f : goalDist, danger, match.TimeLeft);
                // a Hunter camping a dropped bucket: aim at the camper (long stun + fast reload make it always worth it)
                if (camper && camperSeen && prey.HasAmmo && camperDist < 22f)
                {
                    threat = camper; threatPos = camperPos; threatDist = camperDist; threatSeen = true;
                    shootDesire = 0.8f;
                }
                else shootDesire = threatSeen && prey.HasAmmo ? ShootFis.Evaluate(threatDist, closingSpeed, danger) : 0f;
                courage = camper ? CourageFis.Evaluate(camperDist, prey.Hp, PreyAlive()) : 0f;
                tree.Tick();
                TrackEvade(tree.IsActive("Evade"));
                current = Label();
                TraceDecision();
            }
            UseAbilities();
            return output;
        }

        // ======================= Team abilities (same for BT and SL brains: not a learned decision) =======================

        Ball markTarget;

        /// <summary>Spotted a teammate's ball, the ability is ready and no Hunter is on me → look at it so it can be shown.</summary>
        void PlanMark()
        {
            markTarget = null;
            lookOverride = null;
            bool safe = threat == null || threatDist > 15f || perception.Age(threat) > 3f;
            if (!safe || prey.MarkCd > 0f) return;
            foreach (var ball in match.Balls.Values)
            {
                if (!ball || ball.Marked || !perception.Noticed(ball)) continue;
                if (prey.Carried.Exists(b => b.Color == ball.Color)) continue; // my own: just go get it
                var bucket = match.Buckets[ball.Color];
                if (bucket.Dropped || !bucket.Holder.IsActive) continue; // nobody to show it to
                if (Vector3.Distance(ball.transform.position, prey.EyePos) > PreyActor.MarkRange) continue;
                markTarget = ball;
                lookOverride = ball.transform.position;
                return;
            }
        }

        void UseAbilities()
        {
            if (markTarget && prey.BallInSights() == markTarget) output.mark = true;
        }

        // ======================= Recording a human (shadow mode) =======================

        /// <summary>Attach to a human Prey: perceive like a bot, label what the human does, write training rows.</summary>
        public PreyBot InitShadow(MatchManager m, PreyActor p)
        {
            Init(m, p);
            Shadow = true;
            UsesSL = false; // it records the human; it never decides anything
            return this;
        }

        /// <summary>What the human is doing right now, as one of the behaviour classes (shown on the HUD while recording).</summary>
        public string HumanLabel => BranchNames[(int)current];

        protected override void ShadowTick(float dt)
        {
            this.dt = dt;
            Listen();
            SenseHunters();
            SenseObjects();
            ChooseGoal();
            previous = current;
            var label = InferHumanLabel();
            if (ShouldSample(dt)) SlRecorder.Write(Dataset, FeatureNames, Features(), (int)label);
            current = label;
        }

        /// <summary>
        /// Turns the human's actions into a behaviour label:
        /// shooting or lining up a shot → Fight · heading for the Gate / a ball / a dropped bucket → Escape / Collect / Take Over ·
        /// running away from a Hunter seen in the last 3 s within 15 m → Evade · anything else → Explore.
        /// "Heading for" uses the A* path's first leg, so following a corridor around a wall still counts.
        /// </summary>
        Act InferHumanLabel()
        {
            if (prey.LastFireTime > lastSampleAt - 0.2f) return Act.Fight;
            if (threatSeen && prey.HasAmmo && Vector3.Angle(prey.Eye.forward, threat.Chest - prey.EyePos) < 6f) return Act.Fight; // aiming

            if (goal != Goal.None && (goalDist < 3f || Heading(goalDirection, 0.6f)))
                return goal switch { Goal.Gate => Act.Escape, Goal.Ball => Act.Collect, _ => Act.TakeOver };
            if (threat && threatDist < 15f && perception.Age(threat) < 3f && Heading(prey.transform.position - threatPos, 0.3f)) return Act.Evade;
            return Act.Explore;
        }

        /// <summary>Which top-level branch the BT ran this frame (the training label).</summary>
        Act Label()
        {
            if (tree.IsActive("Break Camp")) return Act.Fight; // going to fight the camper counts as fighting
            for (int k = 0; k < BranchNames.Length; k++) if (tree.IsActive(BranchNames[k])) return (Act)k;
            return Act.Explore;
        }

        int PreyAlive()
        {
            int n = 0;
            foreach (var p in match.Prey) if (p.IsActive) n++;
            return n;
        }

        float[] Features()
        {
            var f = features;
            int i = 0;
            for (int k = 0; k < 2; k++)
            {
                bool has = k < known.Count;
                var info = has ? known[k] : default;
                f[i++] = Feat.B(has);
                f[i++] = has ? Feat.N(info.dist, 40f) : 1f;
                f[i++] = has ? Feat.N(info.age, 8f) : 1f;
                f[i++] = has ? Feat.N(info.closing, 5f) : 0f;
                f[i++] = Feat.B(has && info.seen);
                f[i++] = Feat.B(has && info.stunned);
            }
            f[i++] = Feat.B(prey.Hp <= 1);
            f[i++] = Feat.B(prey.HasAmmo);
            f[i++] = Feat.N(prey.ReloadLeft, PreyActor.ReloadTime);
            f[i++] = Feat.B(prey.Motor.Boosted);
            f[i++] = Feat.B(prey.InvulnLeft > 0f);
            f[i++] = Feat.B(goal == Goal.None);
            f[i++] = Feat.B(goal == Goal.Gate);
            f[i++] = Feat.B(goal == Goal.Ball);
            f[i++] = Feat.B(goal == Goal.Bucket);
            f[i++] = goal == Goal.None ? 1f : Feat.N(goalDist, 60f);
            f[i++] = Feat.N(match.TimeLeft, match.matchDuration);
            f[i++] = Feat.B(prey.AllCarriedFull);
            f[i++] = Feat.B(match.GateOpen);
            f[i++] = Feat.B(match.GateOpen && perception.Recall(match.Gate, 300f, out _));
            int balls = 0;
            foreach (var b in prey.Carried) balls += b.Count;
            f[i++] = prey.Carried.Count > 0 ? balls / (float)(Bucket.Capacity * prey.Carried.Count) : 1f;
            int alive = 0;
            foreach (var p in match.Prey) if (p != prey && p.IsActive) alive++;
            f[i++] = alive / 3f;
            for (int k = 0; k < 6; k++) f[i++] = Feat.B((int)previous == k);
            return f;
        }

        /// <summary>
        /// SL brain: the network picks the branch; the same action leaves as the BT then carry it out.
        /// Actions that are impossible right now are masked (e.g. can't Collect without knowing a ball).
        /// </summary>
        void DecideSL()
        {
            valid[(int)Act.Escape] = goal == Goal.Gate;
            valid[(int)Act.Fight] = threatSeen && prey.HasAmmo;
            valid[(int)Act.Evade] = threat != null;
            valid[(int)Act.Collect] = goal == Goal.Ball;
            valid[(int)Act.TakeOver] = goal == Goal.Bucket;
            valid[(int)Act.Explore] = true;

            var probs = policy.Forward(Features());
            int choice = Mlp.ArgMax(probs, valid);
            if (Run((Act)choice) == BtStatus.Failure) // e.g. walked to a ball that's gone
            {
                choice = Mlp.ArgMax(probs, valid, choice);
                if (choice < 0 || Run((Act)choice) == BtStatus.Failure) { choice = (int)Act.Explore; Run(Act.Explore); }
            }
            current = (Act)choice;
            confidence = probs[choice];
        }

        BtStatus Run(Act a) => a switch
        {
            Act.Fight => Shoot(),
            Act.Evade => Flee(),
            Act.Explore => Explore(),
            _ => GoToGoal(), // Escape / Collect / Take Over all walk to the current goal
        };

        protected override string DecisionText => UsesSL ? $"SL > {BranchNames[(int)current]} ({confidence * 100f:0}%)" : base.DecisionText;

        // ======================= Sensing + fuzzy evaluation =======================

        /// <summary>
        /// Builds the list of known Hunters (estimated position, distance, info age...).
        /// BT brain: the threat is the most dangerous one (fuzzy Danger per Hunter).
        /// SL brain: no fuzzy at all — the threat is simply the nearest one.
        /// </summary>
        void SenseHunters()
        {
            threat = null;
            danger = 0f;
            threatSeen = false;
            threatDist = float.PositiveInfinity;
            closingSpeed = 0f;
            camper = null;
            camperSeen = false;
            camperDist = float.PositiveInfinity;
            exploreAvoid.Clear();
            known.Clear();
            foreach (var h in match.Hunters)
            {
                if (!h.IsActive) { perception.Forget(h); continue; }
                bool seen = perception.Observe(h, Vision.CanSee(prey, h), h.transform.position);
                if (seen) Callout(h, h.transform.position, 1.5f);
                if (!perception.Recall(h, 20f, out var pos)) continue;
                exploreAvoid.Add(pos);
                float age = perception.Age(h);
                if (age > 6f) continue; // too old to act on: stop worrying and get back to work (still avoided when exploring)
                Vector3 toMe = prey.transform.position - pos; toMe.y = 0;
                float closing = Vector3.Dot(perception.LastVelocity(h), toMe.normalized); // + = coming at me
                // Out of sight: remember where it was last and work out where it probably is now.
                //  · it was chasing me → assume it kept coming for a moment (at most 1.5 s worth: past that it's a
                //    guess, and a guess that follows me around kept the bot running from nothing)
                //  · it was moving elsewhere → it kept going that way for a bit (snapped to where it can walk)
                //  · it was standing still (camping, guarding) → it's still there
                if (!seen && closing > 0.5f && age < 4f)
                    pos += toMe.normalized * Mathf.Min(HunterRunSpeed * Mathf.Min(age, 1.5f), Mathf.Max(0f, toMe.magnitude - 3f));
                else if (!seen && age > 0.3f)
                {
                    Vector3 drift = perception.LastVelocity(h) * Mathf.Min(age, 1.5f);
                    if (drift.sqrMagnitude > 0.25f) pos = Arena.Snap(pos + drift, 3f) ?? pos;
                }
                float d = Vector3.Distance(pos, prey.transform.position);
                if (seen && h.Stunned) stunnedUntil[h] = Time.time + h.StunLeft; // it's flashing
                bool stunned = stunnedUntil.TryGetValue(h, out var until) && Time.time < until;
                known.Add(new HunterInfo { h = h, pos = pos, dist = d, age = age, closing = closing, seen = seen, stunned = stunned });

                // camping a dropped bucket (as far as I know) and not already stunned → a target worth going for
                if (!stunned && age < 6f && d < camperDist && match.DroppedBucketNear(pos))
                {
                    camper = h; camperPos = pos; camperDist = d; camperSeen = seen;
                }
            }
            known.Sort((a, b) => a.dist.CompareTo(b.dist));

            HunterInfo? pick = null;
            if (UsesSL || Shadow) { if (known.Count > 0) pick = known[0]; }
            else
                foreach (var info in known)
                {
                    float dg = DangerFis.Evaluate(info.dist, info.age, prey.Hp, info.closing);
                    if (info.stunned) dg *= 0.3f; // free to move while it recovers
                    if (dg <= danger) continue;
                    danger = dg;
                    pick = info;
                }
            if (pick is not { } t) return;
            threat = t.h;
            threatPos = t.pos;
            threatDist = t.dist;
            threatSeen = t.seen;
            closingSpeed = t.closing;
        }

        /// <summary>
        /// Balls and the Gate: by sight, then called out to teammates. What every Prey (human or bot) also gets on
        /// their HUD is known directly: balls a teammate is showing, and dropped buckets.
        /// </summary>
        void SenseObjects()
        {
            foreach (var kv in match.Balls)
            {
                var ball = kv.Value;
                if (!ball) continue;
                Vector3 p = ball.transform.position;
                // Seen directly, or a teammate is showing it and its indicator is in my view (the indicator shows
                // through walls, but only once I look its way — same 110° cone and reaction time as seeing it).
                bool indicatorInView = ball.Marked && Vector3.Angle(prey.Eye.forward, p - prey.EyePos) < Vision.Fov * 0.5f;
                if (perception.Observe(ball, Vision.CanSeePoint(prey, p) || indicatorInView, p) && ShareBallSightings) Callout(ball, p, 5f);
            }
            foreach (var b in match.Buckets.Values)
            {
                if (!b.Dropped) continue;
                Vector3 p = b.transform.position;
                perception.Observe(b, Vision.CanSeePoint(prey, p + Vector3.up * 0.5f), p);
                perception.Inform(b, p); // every Prey knows where a bucket fell
            }
            if (match.GateOpen)
            {
                Vector3 g = match.Gate.transform.position;
                if (perception.Observe(match.Gate, Vision.CanSeePoint(prey, g + Vector3.up * 1.6f), g)) Callout(match.Gate, g, 5f);
            }
        }

        /// <summary>Gate (if my buckets are done) → a known ball for a bucket I carry → a known dropped bucket.</summary>
        void ChooseGoal()
        {
            Goal g = Goal.None;
            Object key = null;
            Vector3 pos = default;
            if (match.GateOpen && prey.AllCarriedFull && perception.Recall(match.Gate, 300f, out pos)) { g = Goal.Gate; key = match.Gate; }
            if (g == Goal.None)
                foreach (var b in prey.Carried)
                    if (!b.Full && match.Balls.TryGetValue(b.Color, out var ball) && ball && perception.Recall(ball, 120f, out pos)) { g = Goal.Ball; key = ball; break; }
            if (g == Goal.None && prey.AllCarriedFull)
                foreach (var b in match.Buckets.Values)
                    if (b.Dropped && perception.Recall(b, 120f, out pos)) { g = Goal.Bucket; key = b; break; }

            goalTimer -= dt;
            if (key != goalKey || goalTimer <= 0f)
            {
                goalTimer = 0.3f;
                goalDist = g == Goal.None ? 60f : Arena.PathLength(prey.transform.position, pos, scratch); // A*
                goalDirection = g != Goal.None && !float.IsInfinity(goalDist) && scratch.corners.Length > 1
                    ? scratch.corners[1] - prey.transform.position : pos - prey.transform.position;
                if (float.IsInfinity(goalDist)) goalDist = 60f;
            }
            goal = g; goalKey = key; goalPos = pos;
        }

        // ======================= Actions =======================

        /// <summary>
        /// Flee when danger is high enough and outweighs the objective. Hysteresis (easier to keep fleeing
        /// than to start) stops the bot flickering between Evade and Explore.
        /// </summary>
        bool InDanger()
        {
            bool fleeing = tree.WasActive("Evade");
            if (fleeing && Time.time - evadeSince < MinEvade && danger >= 0.15f) return true; // started running: commit to it
            if (!fleeing && Time.time - evadeEnded < EvadeRest && danger < 0.6f) return false; // just calmed down: don't bounce back
            if (danger < (fleeing ? 0.3f : 0.4f)) return false;
            if (goal == Goal.None) return true; // nothing worth risking it for
            return danger > commit - (fleeing ? 0.1f : 0f);
        }

        /// <summary>When fleeing started / stopped (for the commit + rest times); a fresh flee starts with a fresh escape spot.</summary>
        void TrackEvade(bool evading)
        {
            if (evading && !wasEvading) { evadeSince = Time.time; fleeTimer = 0f; peekLeft = 0f; peeked = false; }
            if (!evading && wasEvading)
            {
                evadeEnded = Time.time;
                peekLeft = 0f;
                NewExploreTarget(); // the old one may lead straight back to the Hunter
            }
            wasEvading = evading;
        }

        /// <summary>
        /// The X-Ray warning every Prey gets on their HUD: the Hunters can see me for a few seconds, so hiding
        /// in place is pointless → no peeking, and if I was just exploring, go somewhere else (away from known Hunters).
        /// </summary>
        void SenseXray()
        {
            bool on = match.TeamXrayLeft > 0f;
            if (on && !xrayWasOn)
            {
                exposedUntil = Time.time + HunterActor.XrayDuration + 2f;
                NewExploreTarget();
            }
            xrayWasOn = on;
        }

        /// <summary>Walk to the goal's last known spot; forget it if we get there and it's gone.</summary>
        BtStatus GoToGoal()
        {
            GoTo(goalPos);
            if (Arrived(1.5f) && !perception.Noticed(goalKey))
            {
                perception.Forget(goalKey);
                return BtStatus.Failure;
            }
            output = Glance(Drive(Steer(dt), null, true, dt), dt);
            return BtStatus.Running;
        }

        /// <summary>Lead the moving Hunter and compensate for paintball drop, with a small human aim error.</summary>
        BtStatus Shoot()
        {
            if (previous != Act.Fight) aimError = Random.insideUnitSphere * 0.3f;
            float t = threatDist / Paintball.Speed;
            Vector3 aim = threat.Chest + perception.Velocity(threat) * t + Vector3.up * (0.5f * Paintball.Drop * t * t) + aimError;
            output = Drive(Vector3.zero, aim, false, dt);
            if (Vector3.Angle(prey.Eye.forward, aim - prey.EyePos) < 2.5f
                && !Physics.Linecast(prey.EyePos, aim, Layers.EnvMask, QueryTriggerInteraction.Ignore))
                output.primary = true;
            return BtStatus.Running;
        }

        BtStatus Flee()
        {
            Vector3 lookBack = threatPos + Vector3.up * 1.2f;
            // reached cover: stop and look back for a moment before deciding what next (like a human peeking)
            if (peekLeft > 0f)
            {
                peekLeft -= dt;
                if (!threatSeen && !Exposed && threatDist > PeekMinDist)
                {
                    output = Drive(Vector3.zero, lookBack, false, dt);
                    return BtStatus.Running;
                }
                peekLeft = 0f; // it's still there (or they can see me): keep running
            }
            fleeTimer -= dt;
            bool arrived = Arrived(1.5f);
            // only when it's (believed) far: stopping with a Hunter just around the corner gets you tagged
            if (arrived && !peeked && !threatSeen && !Exposed && threatDist > PeekMinDist)
            {
                peeked = true;
                peekLeft = PeekTime;
                output = Drive(Vector3.zero, lookBack, false, dt);
                return BtStatus.Running;
            }
            // Keep the same escape spot for a while (re-picking it every moment made the bot zig-zag).
            // Change it early only when it's reached or the Hunter is now closer to it than I am.
            Vector3 me = prey.transform.position;
            bool cutOff = Vector3.Distance(fleePoint, threatPos) < Vector3.Distance(fleePoint, me);
            if (fleeTimer <= 0f || arrived || cutOff)
            {
                fleeTimer = FleeHold;
                fleePoint = PickFleePoint(threatPos);
                peeked = false;
            }
            // glance back now and then to keep track of the chaser
            // (long enough to turn ~150° and re-notice it: 0.5 s turning + 0.2–0.3 s reaction)
            glanceBackTimer -= dt;
            if (glanceBackTimer < -0.9f) glanceBackTimer = 1.4f;
            Vector3? look = glanceBackTimer < 0f ? threatPos + Vector3.up * 1.2f : null;
            output = Travel(fleePoint, dt, look);
            return BtStatus.Running;
        }

        BtStatus Explore()
        {
            output = Glance(Travel(ExploreTarget(dt), dt), dt);
            return BtStatus.Running;
        }

        /// <summary>
        /// Sneak to a spot 10–14 m from where the camper should be, with a clear line of fire, keeping my eyes on it.
        /// Once it's in sight the Fight branch (higher priority) takes the shot.
        /// </summary>
        BtStatus BreakCamp()
        {
            shotTimer -= dt;
            if (shotTimer <= 0f || Arrived(1.5f))
            {
                shotTimer = 1f;
                shotSpot = PickShotSpot(camperPos);
            }
            output = Travel(shotSpot, dt, camperPos + Vector3.up * 1.2f);
            return BtStatus.Running;
        }

        Vector3 PickShotSpot(Vector3 target)
        {
            Vector3 me = prey.transform.position;
            Vector3 best = Arena.Snap(target, 3f) ?? target;
            float bestLen = float.PositiveInfinity;
            for (int k = 0; k < 12; k++)
            {
                var s = Arena.Snap(target + Quaternion.Euler(0, k * 30f, 0) * Vector3.forward * Random.Range(10f, 14f), 2f);
                if (s == null) continue;
                if (Physics.Linecast(s.Value + Vector3.up * 1.6f, target + Vector3.up * 1.2f, Layers.EnvMask, QueryTriggerInteraction.Ignore)) continue;
                float len = Arena.PathLength(me, s.Value, scratch);
                if (len < bestLen) { bestLen = len; best = s.Value; }
            }
            return best;
        }

        /// <summary>A reachable spot ~9 m away, away from the Hunter, preferably out of its line of sight.</summary>
        Vector3 PickFleePoint(Vector3 hunterPos) => PickFleePoint(hunterPos, true) ?? PickFleePoint(hunterPos, false) ?? prey.transform.position;

        Vector3? PickFleePoint(Vector3 hunterPos, bool checkRoute)
        {
            Vector3 me = prey.transform.position;
            Vector3? best = null;
            float bestScore = float.MinValue;
            for (int k = 0; k < 10; k++)
            {
                float ang = k * 36f + Random.Range(-10f, 10f);
                var s = Arena.Snap(me + Quaternion.Euler(0, ang, 0) * Vector3.forward * 9f, 2.5f);
                if (s == null) continue;
                // A* route check: no long detours (dead ends, crawl spaces) and no running past the Hunter
                if (checkRoute)
                {
                    float len = Arena.PathLength(me, s.Value, scratch);
                    if (float.IsInfinity(len) || len > Vector3.Distance(me, s.Value) * 1.6f + 2f) continue;
                    if (scratch.corners.Length > 1 &&
                        Vector3.Dot((scratch.corners[1] - me).normalized, (hunterPos - me).normalized) > 0.5f) continue;
                }
                float score = Vector3.Distance(s.Value, hunterPos)
                              - Vector3.Dot((s.Value - me).normalized, (hunterPos - me).normalized) * 6f;
                foreach (var a in exploreAvoid) if (Vector3.Distance(s.Value, a) < 6f) score -= 6f; // other known Hunters
                if (Physics.Linecast(hunterPos + Vector3.up * 1.6f, s.Value + Vector3.up, Layers.EnvMask)) score += 5f; // cover
                if (score > bestScore) { bestScore = score; best = s.Value; }
            }
            return best;
        }

        protected override string FuzzySummary() =>
            (UsesSL ? "" : $"danger {danger:0.00}  ·  commit {commit:0.00}  ·  shoot {shootDesire:0.00}  ·  " +
                           (camper ? $"courage {courage:0.00} vs camper {camperDist:0}m  ·  " : "")) +
            $"{(threat ? $"{threat.DisplayName} {threatDist:0}m" : "no threat")}  ·  goal {goal}{(goal != Goal.None ? $" {goalDist:0}m" : "")}";
    }
}
