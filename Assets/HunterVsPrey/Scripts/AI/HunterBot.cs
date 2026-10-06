using UnityEngine;

namespace HunterVsPrey
{
    /// <summary>
    /// Hunter AI = Behaviour Tree (what to do, in priority order) + Fuzzy Logic (how much it wants to).
    ///
    ///   Hunter (selector)
    ///   ├─ Attack      prey in tag reach, not flashing, tag ready          → Tag
    ///   ├─ Hunt        fuzzy TargetPriority ≥ 0.30                          → Pursue (fresh info) / Investigate (last known spot)
    ///   ├─ X-Ray       team charge ready and fuzzy XrayUrgency ≥ 0.60       → Use X-Ray
    ///   ├─ Guard Gate  Gate seen and fuzzy GuardDesire ≥ 0.55               → Watch the Gate from outside the camp radius
    ///   └─ Patrol      search the least-recently-seen areas, split from the teammate
    ///
    /// Fair: it only knows Prey it has seen (110° cone, line of sight, 200–300 ms reaction), heard from its
    /// teammate (callouts with ~1 s delay), felt (where a paintball came from) or revealed with X-Ray.
    /// It never knows where balls are.
    /// </summary>
    public class HunterBot : BotBase
    {
        // ======================= Fuzzy systems =======================

        /// <summary>How worth chasing is this Prey? (A* path distance, how fresh the info is, its HP)</summary>
        public static readonly FuzzySystem TargetFis = new("Target priority", 3);
        /// <summary>Should the team spend the shared X-Ray now? (time since anyone saw a Prey, how close Prey are to opening the Gate)</summary>
        public static readonly FuzzySystem XrayFis = new("X-Ray urgency", 2);
        /// <summary>Should I go watch the Gate? (distance to it, time since contact)</summary>
        public static readonly FuzzySystem GuardFis = new("Guard desire", 2);

        static HunterBot()
        {
            var dist = TargetFis.Input("PathDistance", 0f, 60f)
                .Term("Near", Mf.Left(6f, 12f)).Term("Mid", Mf.Tri(8f, 18f, 30f)).Term("Far", Mf.Right(22f, 40f));
            var age = TargetFis.Input("InfoAge", 0f, 15f)
                .Term("Fresh", Mf.Left(0.5f, 2f)).Term("Recent", Mf.Tri(1f, 4f, 8f)).Term("Stale", Mf.Right(6f, 12f));
            var hp = TargetFis.Input("PreyHp", 1f, 2f)
                .Term("Wounded", Mf.Left(1f, 2f)).Term("Healthy", Mf.Right(1f, 2f));
            TargetFis.Output01("Priority");
            TargetFis
                .Rule(dist["Near"] & age["Fresh"], "High")
                .Rule(dist["Mid"] & age["Fresh"], "High")
                .Rule(dist["Far"] & age["Fresh"], "Medium")
                .Rule(dist["Near"] & age["Recent"], "High")
                .Rule(dist["Mid"] & age["Recent"], "Medium")
                .Rule(dist["Far"] & age["Recent"], "Low")
                .Rule(age["Stale"], "Low")
                .Rule(hp["Wounded"] & !dist["Far"] & !age["Stale"], "High") // one more tag finishes them
                .Rule(hp["Healthy"] & dist["Far"], "Low");

            var contact = XrayFis.Input("TimeSinceContact", 0f, 40f)
                .Term("Short", Mf.Left(2f, 6f)).Term("Medium", Mf.Tri(4f, 10f, 18f)).Term("Long", Mf.Right(12f, 22f));
            var progress = XrayFis.Input("PreyProgress", 0f, 1f)
                .Term("Low", Mf.Left(0.2f, 0.45f)).Term("Mid", Mf.Tri(0.3f, 0.55f, 0.8f)).Term("High", Mf.Right(0.65f, 0.9f));
            XrayFis.Output01("Urgency");
            XrayFis
                .Rule(contact["Short"], "Low")
                .Rule(contact["Medium"] & progress["Low"], "Low")
                .Rule(contact["Medium"] & progress["Mid"], "Medium")
                .Rule(contact["Medium"] & progress["High"], "High")
                .Rule(contact["Long"], "High");

            var gateDist = GuardFis.Input("GateDistance", 0f, 80f)
                .Term("Near", Mf.Left(10f, 18f)).Term("Mid", Mf.Tri(12f, 25f, 40f)).Term("Far", Mf.Right(30f, 50f));
            var since = GuardFis.Input("TimeSinceContact", 0f, 40f)
                .Term("Short", Mf.Left(2f, 6f)).Term("Medium", Mf.Tri(4f, 10f, 18f)).Term("Long", Mf.Right(12f, 22f));
            GuardFis.Output01("Desire");
            GuardFis
                .Rule(since["Short"], "Low")
                .Rule(since["Medium"] & gateDist["Near"], "High")
                .Rule(since["Medium"] & !gateDist["Near"], "Medium")
                .Rule(since["Long"] & !gateDist["Far"], "High")
                .Rule(since["Long"] & gateDist["Far"], "Medium");
        }

        // ======================= Blackboard =======================

        HunterActor hunter;
        ActorInput output;
        float dt;

        PreyActor target;
        float targetPriority, targetAge, selectTimer;
        Vector3 lostVelocity;
        float searchSpin;

        float lastContact;          // last time any Prey position was < 2 s old (seen, called out or X-Rayed)
        float xrayUrgency, guardDesire;
        bool gateKnown;
        Vector3 gatePos, guardPoint;
        float guardTimer;

        // ======================= Supervised learning =======================

        /// <summary>The behaviours = the SL classes; for a human player they are inferred from what they do.</summary>
        public enum Act { Attack, Pursue, Investigate, Xray, Guard, Patrol }
        static readonly string[] ActNames = { "Attack", "Pursue", "Investigate", "X-Ray", "Guard Gate", "Patrol" };

        /// <summary>Policy input: perceived info only (no fuzzy outputs, no true positions of unseen Prey).</summary>
        public static readonly string[] FeatureNames =
        {
            "has_target", "target_dist", "target_age", "target_wounded", "target_visible", "target_fresh",
            "can_tag", "tag_cd", "prey_in_reach",
            "xray_ready", "xray_cd", "xray_mine", "since_contact",
            "gate_open", "gate_known", "gate_dist", "mate_guarding",
            "prey_progress", "time_left", "my_hp", "prey_alive", "mate_alive", "mate_same_target",
            "prev_attack", "prev_pursue", "prev_investigate", "prev_xray", "prev_guard", "prev_patrol",
        };
        /// <summary>Second network (regression): "would a human go after this Prey?" (1 = the one they chased, 0 = the others).</summary>
        public static readonly string[] TargetFeatureNames = { "path_dist", "info_age", "wounded", "mate_on_it", "is_current" };
        public const string PolicyModel = "hunter_policy", TargetModel = "hunter_target", Dataset = "hunter", TargetDataset = "hunter_target";

        readonly float[] features = new float[FeatureNames.Length];
        readonly float[] targetFeatures = new float[TargetFeatureNames.Length];
        readonly bool[] valid = new bool[6];
        Act previous = Act.Patrol, current = Act.Patrol;
        Mlp policy, targetNet;
        float confidence, targetDist = float.PositiveInfinity, sinceContact;

        public static bool SlAvailable =>
            Mlp.Load(PolicyModel)?.Matches(FeatureNames) == true && Mlp.Load(TargetModel)?.Matches(TargetFeatureNames) == true;

        public PreyActor Target => target;
        public bool IsGuarding => UsesSL || Shadow ? current == Act.Guard : tree != null && tree.WasActive("Guard Gate");
        /// <summary>The other Hunter's brain (a bot, or the shadow recording a human teammate).</summary>
        HunterBot Mate
        {
            get
            {
                foreach (var h in match.Hunters) if (h != hunter && h.IsActive && h.GetComponent<HunterBot>() is { } b) return b;
                return null;
            }
        }

        public HunterBot Init(MatchManager m, HunterActor h)
        {
            InitBase(m, h);
            hunter = h;
            lastContact = Time.time;
            if (SlSettings.HunterBrain == BrainKind.SL && SlAvailable)
            {
                policy = Mlp.Load(PolicyModel);
                targetNet = Mlp.Load(TargetModel);
                UsesSL = true;
            }
            tree = new BehaviourTree(new Selector("Hunter",
                new Sequence("Attack",
                    new Condition("prey in reach", CanTagNow),
                    new BtAction("Tag", Tag)),
                new Sequence("Hunt",
                    new Condition("worth chasing", () => target && targetPriority >= (tree.WasActive("Hunt") ? 0.22f : 0.30f)),
                    new Selector("",
                        new Sequence("Pursue",
                            new Condition("info fresh", () => targetAge < 0.6f),
                            new BtAction("Run down", Pursue)),
                        new BtAction("Investigate", Investigate))),
                new Sequence("X-Ray",
                    new Condition("urgent", () => match.TeamXrayCd <= 0f && xrayUrgency >= 0.6f),
                    new BtAction("Use X-Ray", UseXray)),
                new Sequence("Guard Gate",
                    new Condition("worth guarding", () => gateKnown && guardDesire >= (tree.WasActive("Guard Gate") ? 0.45f : 0.55f)),
                    new BtAction("Watch", Guard)),
                new BtAction("Patrol", Patrol)));
            return this;
        }

        public override ActorInput Read(float dt)
        {
            this.dt = dt;
            TickExploration(dt);
            Listen();
            Sense();
            output = new ActorInput();
            previous = current;
            if (UsesSL)
            {
                DecideSL();
                return output;
            }
            tree.Tick();
            current = Label();
            TraceDecision();
            return output;
        }

        Act Label()
        {
            for (int k = 0; k < ActNames.Length; k++) if (tree.IsActive(ActNames[k])) return (Act)k;
            return Act.Patrol;
        }

        float[] Features()
        {
            var f = features;
            int i = 0;
            bool has = target;
            f[i++] = Feat.B(has);
            f[i++] = has ? Feat.N(targetDist, 60f) : 1f;
            f[i++] = has ? Feat.N(targetAge, 15f) : 1f;
            f[i++] = Feat.B(has && target.Hp <= 1);
            f[i++] = Feat.B(has && perception.Noticed(target));
            f[i++] = Feat.B(has && targetAge < 0.6f);
            f[i++] = Feat.B(CanTagNow());
            f[i++] = Feat.N(hunter.TagCd, HunterActor.TagCooldown);
            f[i++] = Feat.B(hunter.PreyInTagReach());
            f[i++] = Feat.B(match.TeamXrayCd <= 0f);
            f[i++] = Feat.N(match.TeamXrayCd, HunterActor.XrayCooldown);
            f[i++] = Feat.B(hunter.XrayActive);
            f[i++] = Feat.N(sinceContact, 40f);
            f[i++] = Feat.B(match.GateOpen);
            f[i++] = Feat.B(gateKnown);
            f[i++] = gateKnown ? Feat.N(Vector3.Distance(gatePos, hunter.transform.position), 80f) : 1f;
            var mate = Mate;
            f[i++] = Feat.B(mate && mate.IsGuarding);
            f[i++] = PreyProgress();
            f[i++] = Feat.N(match.TimeLeft, match.matchDuration);
            f[i++] = Feat.N(hunter.Hp, hunter.MaxHp);
            int alive = 0;
            foreach (var p in match.Prey) if (p.IsActive) alive++;
            f[i++] = alive / 4f;
            f[i++] = Feat.B(mate);
            f[i++] = Feat.B(mate && has && mate.Target == target);
            for (int k = 0; k < 6; k++) f[i++] = Feat.B((int)previous == k);
            return f;
        }

        /// <summary>SL brain: the policy network picks the behaviour, impossible ones masked; BT action leaves execute it.</summary>
        void DecideSL()
        {
            valid[(int)Act.Attack] = CanTagNow();
            valid[(int)Act.Pursue] = target && targetAge < 1f;
            valid[(int)Act.Investigate] = target;
            valid[(int)Act.Xray] = match.TeamXrayCd <= 0f;
            valid[(int)Act.Guard] = gateKnown;
            valid[(int)Act.Patrol] = true;

            var probs = policy.Forward(Features());
            int choice = Mlp.ArgMax(probs, valid);
            Run((Act)choice);
            current = (Act)choice;
            confidence = probs[choice];
        }

        BtStatus Run(Act a) => a switch
        {
            Act.Attack => Tag(),
            Act.Pursue => Pursue(),
            Act.Investigate => Investigate(),
            Act.Xray => UseXray(),
            Act.Guard => Guard(),
            _ => Patrol(),
        };

        protected override string DecisionText => UsesSL ? $"SL > {ActNames[(int)current]} ({confidence * 100f:0}%)" : base.DecisionText;

        // ======================= Sensing + fuzzy evaluation =======================

        void Sense()
        {
            float freshest = float.PositiveInfinity;
            foreach (var p in match.Prey)
            {
                if (!p.IsActive) { perception.Forget(p); if (target == p) target = null; continue; }
                if (perception.Observe(p, Vision.CanSee(hunter, p), p.transform.position))
                    Callout(p, p.transform.position, 1f);
                if (hunter.XrayActive) perception.Inform(p, p.transform.position); // the one "wallhack"
                freshest = Mathf.Min(freshest, perception.Age(p));
            }
            if (freshest < 2f) lastContact = Time.time;
            sinceContact = Time.time - lastContact;

            if (match.GateOpen)
            {
                Vector3 g = match.Gate.transform.position;
                if (perception.Observe(match.Gate, Vision.CanSeePoint(hunter, g + Vector3.up * 1.6f), g)) Callout(match.Gate, g, 5f);
            }
            gateKnown = match.GateOpen && perception.Recall(match.Gate, 300f, out gatePos);

            selectTimer -= dt;
            if (selectTimer <= 0f) { selectTimer = 0.2f; SelectTarget(); }
            targetAge = target ? perception.Age(target) : float.PositiveInfinity;
            if (target && targetAge < 0.3f) lostVelocity = perception.Velocity(target);

            xrayUrgency = guardDesire = 0f;
            if (UsesSL || Shadow) return; // SL brains and human recordings use no fuzzy logic
            xrayUrgency = XrayFis.Evaluate(sinceContact, PreyProgress());
            if (gateKnown)
            {
                var mate = Mate;
                guardDesire = GuardFis.Evaluate(Vector3.Distance(gatePos, hunter.transform.position), sinceContact)
                              * (mate && mate.IsGuarding ? 0.6f : 1f); // one guard is enough
            }
        }

        /// <summary>
        /// Score every remembered Prey and keep the best.
        /// BT brain: fuzzy TargetPriority (+ teamwork tweaks).
        /// SL brain: the target network = "how likely is a human to go after this one?".
        /// Shadow (recording a human): the target is whichever known Prey the human is looking at / running toward,
        /// and every candidate is written as a training row (1 = the one they chose, 0 = the others).
        /// </summary>
        void SelectTarget()
        {
            var mate = Mate;
            PreyActor best = null;
            float bestScore = UsesSL ? float.NegativeInfinity : 0f, bestLen = float.PositiveInfinity; // SL: always the likeliest; the policy decides whether to chase
            candidates.Clear();
            foreach (var p in match.Prey)
            {
                if (!p.IsActive || !perception.Recall(p, 15f, out var pos)) continue;
                float len = Arena.PathLength(hunter.transform.position, pos, scratch); // A*
                if (float.IsInfinity(len)) len = Vector3.Distance(hunter.transform.position, pos) * 1.5f;
                float age = perception.Age(p);
                bool mateOnIt = mate && mate.Target == p && Vector3.Distance(mate.transform.position, pos) < len;

                var tf = targetFeatures;
                tf[0] = Feat.N(len, 60f);
                tf[1] = Feat.N(age, 15f);
                tf[2] = Feat.B(p.Hp <= 1); // HP is public: the feed announces tags
                tf[3] = Feat.B(mateOnIt);
                tf[4] = Feat.B(p == target);

                float score;
                if (Shadow)
                {
                    candidates.Add((p, (float[])tf.Clone()));
                    score = HumanFocus(p, pos);
                }
                else if (UsesSL) score = targetNet.Forward(tf)[0];
                else
                {
                    score = TargetFis.Evaluate(len, age, p.Hp);
                    if (mateOnIt) score *= 0.8f;       // spread out
                    if (p == target) score += 0.05f;   // a little commitment, no flip-flopping
                }
                if (score > bestScore) { bestScore = score; best = p; bestLen = len; }
            }
            if (Shadow && best && SlSettings.Recording)
                foreach (var (p, row) in candidates) SlRecorder.Write(TargetDataset, TargetFeatureNames, row, p == best ? 1f : 0f);
            if (best != target) { lostVelocity = Vector3.zero; searchSpin = 0f; }
            target = best;
            targetPriority = bestScore;
            targetDist = bestLen;
        }

        readonly System.Collections.Generic.List<(PreyActor p, float[] row)> candidates = new();

        /// <summary>
        /// How clearly the human is going after this Prey: looking at it or running toward it.
        /// 0 unless it's well inside their view (≈ 30°) or path direction; sticky for the current target.
        /// </summary>
        float HumanFocus(PreyActor p, Vector3 lastKnown)
        {
            Vector3 to = lastKnown - hunter.transform.position; to.y = 0;
            Vector3 look = hunter.Eye.forward; look.y = 0;
            float d = Mathf.Max(Vector3.Dot(look.normalized, to.normalized),
                                humanVelocity.magnitude > 1.5f ? Vector3.Dot(humanVelocity.normalized, PathDirection(lastKnown).normalized) : 0f);
            float need = p == target ? 0.75f : 0.87f;
            return d > need ? d : 0f;
        }

        // ======================= Recording a human (shadow mode) =======================

        /// <summary>Attach to a human Hunter: perceive like a bot, label what the human does, write training rows.</summary>
        public HunterBot InitShadow(MatchManager m, HunterActor h)
        {
            Init(m, h);
            Shadow = true;
            UsesSL = false; // it records the human; it never decides anything
            return this;
        }

        public string HumanLabel => ActNames[(int)current];
        bool wasXrayActive;
        float xrayStartedAt = -99f;

        protected override void ShadowTick(float dt)
        {
            this.dt = dt;
            Listen();
            Sense();
            previous = current;
            var label = InferHumanLabel();
            if (ShouldSample(dt) && SlSettings.Recording) SlRecorder.Write(Dataset, FeatureNames, Features(), (int)label);
            current = label;
        }

        /// <summary>
        /// Turns the human's actions into a behaviour label:
        /// swung a tag → Attack · just used X-Ray → X-Ray · moving toward a Prey seen right now → Pursue ·
        /// toward where a Prey was last known → Investigate · hanging around near the Gate → Guard · otherwise → Patrol.
        /// </summary>
        Act InferHumanLabel()
        {
            if (hunter.XrayActive && !wasXrayActive) xrayStartedAt = Time.time;
            wasXrayActive = hunter.XrayActive;
            // quick events: anything since the last saved row still gets labelled
            if (xrayStartedAt > lastSampleAt - 0.2f) return Act.Xray; // rarest, so it goes first
            if (hunter.LastTagTime > lastSampleAt - 0.2f) return Act.Attack;
            if (target && perception.Recall(target, 15f, out var pos))
            {
                bool closeIn = Vector3.Distance(pos, hunter.transform.position) < 4f;
                if (targetAge < 0.6f && (closeIn || Heading(PathDirection(pos), 0.5f))) return Act.Pursue;
                if (Heading(PathDirection(pos), 0.5f)) return Act.Investigate;
            }
            if (gateKnown && Vector3.Distance(gatePos, hunter.transform.position) < 18f && humanVelocity.magnitude < 2.5f) return Act.Guard;
            return Act.Patrol;
        }

        /// <summary>Public info (bucket bar on everyone's HUD): how close the Prey are to opening the Gate.</summary>
        float PreyProgress()
        {
            if (match.GateOpen) return 1f;
            int balls = 0;
            foreach (var b in match.Buckets.Values) balls += b.Count;
            return balls / (float)(Bucket.Capacity * match.Buckets.Count);
        }

        // ======================= Actions =======================

        bool CanTagNow()
        {
            var reach = hunter.PreyInTagReach();
            // don't waste the swing on a Prey that's still flashing (i-frames are visible)
            return reach && perception.Noticed(reach) && hunter.TagCd <= 0f && reach.InvulnLeft <= 0f;
        }

        BtStatus Tag()
        {
            var reach = hunter.PreyInTagReach();
            output = Travel(reach.transform.position, dt, reach.Chest);
            output.primary = true;
            return BtStatus.Success;
        }

        BtStatus Pursue()
        {
            perception.Recall(target, 1f, out var pos);
            Vector3 vel = perception.Velocity(target);
            float d = Vector3.Distance(pos, hunter.transform.position);
            Vector3 lead = pos + vel * Mathf.Clamp(d / 6f, 0f, 0.8f); // cut them off a little
            Vector3 dest = Arena.Snap(lead, 3f) ?? pos;
            output = Travel(dest, dt, perception.Noticed(target) ? target.Chest : (Vector3?)null);
            return BtStatus.Running;
        }

        /// <summary>Go where they were heading when we lost them, look around, give up if they're not there.</summary>
        BtStatus Investigate()
        {
            perception.Recall(target, 15f, out var last);
            Vector3 guess = Arena.Snap(last + lostVelocity * 1.5f, 3f) ?? last;
            GoTo(guess);
            if (!Arrived(2f))
            {
                output = Glance(Drive(Steer(dt), null, true, dt), dt);
                return BtStatus.Running;
            }
            searchSpin += dt;
            output = Drive(Vector3.zero, null, false, dt);
            output.yawDelta = TurnRate * 0.7f * dt; // look around
            if (searchSpin > 1.5f) { perception.Forget(target); target = null; searchSpin = 0f; }
            return BtStatus.Running;
        }

        BtStatus UseXray()
        {
            Patrol();
            output.xray = true;
            return BtStatus.Success;
        }

        /// <summary>Watch the Gate from 11–15 m: outside the 8 m anti-camp radius, with line of sight to it.</summary>
        BtStatus Guard()
        {
            guardTimer -= dt;
            if (guardTimer <= 0f || Vector3.Distance(guardPoint, gatePos) > 20f)
            {
                guardTimer = 6f;
                guardPoint = gatePos;
                for (int k = 0; k < 16; k++)
                {
                    Vector3 dir = Quaternion.Euler(0, Random.Range(0f, 360f), 0) * Vector3.forward;
                    var s = Arena.Snap(gatePos + dir * Random.Range(11f, 15f), 3f);
                    if (s == null) continue;
                    if (Physics.Linecast(s.Value + Vector3.up * 1.6f, gatePos + Vector3.up * 1.6f, Layers.EnvMask, QueryTriggerInteraction.Ignore)) continue;
                    guardPoint = s.Value;
                    break;
                }
            }
            GoTo(guardPoint);
            if (!Arrived(1.5f))
            {
                output = Drive(Steer(dt), null, true, dt);
                return BtStatus.Running;
            }
            // at the post: watch the Gate, sweep side to side
            Vector3 side = Vector3.Cross(Vector3.up, gatePos - hunter.transform.position).normalized;
            output = Drive(Vector3.zero, gatePos + Vector3.up * 1.6f + side * Mathf.Sin(Time.time * 0.8f) * 8f, false, dt);
            // it teleported (anti-camp) and we can see the empty spot → forget it
            if (Vision.CanSeePoint(hunter, gatePos + Vector3.up * 1.6f) && (match.Gate.transform.position - gatePos).sqrMagnitude > 1f)
                perception.Forget(match.Gate);
            return BtStatus.Running;
        }

        BtStatus Patrol()
        {
            exploreAvoid.Clear();
            var mate = Mate;
            if (mate) { exploreAvoid.Add(mate.CurrentGoal); exploreAvoid.Add(mate.transform.position); }
            output = Glance(Travel(ExploreTarget(dt), dt), dt);
            return BtStatus.Running;
        }

        protected override string FuzzySummary() =>
            $"target {(target ? target.DisplayName : "-")}  prio {targetPriority:0.00}" +
            (UsesSL ? "" : $"  ·  x-ray {xrayUrgency:0.00}  ·  guard {guardDesire:0.00}");
    }
}
