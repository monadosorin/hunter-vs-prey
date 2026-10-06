using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace HunterVsPrey
{
    public class MatchManager : MonoBehaviour
    {
        public enum Phase { Menu, Playing, Over }

        [Header("Match")]
        public float matchDuration = 300f;
        public float ballRespawnDelay = 1f;
        public float ballMinDistance = 18f;

        [Header("Anti-camping")]
        public float gateCampRadius = 8f;
        public float gateCampTime = 30f;
        [Tooltip("A Hunter painted within this distance of a dropped bucket counts as camping it (5× stun).")]
        public float bucketCampRadius = 6f;

        public Phase State { get; private set; } = Phase.Menu;
        public float TimeLeft { get; private set; }
        public Team? Winner { get; private set; }
        public string ResultText { get; private set; }

        public readonly List<HunterActor> Hunters = new();
        public readonly List<PreyActor> Prey = new();
        public readonly Dictionary<PreyColor, Bucket> Buckets = new();
        public readonly Dictionary<PreyColor, Ball> Balls = new();
        public Gate Gate { get; private set; }
        public bool GateOpen => Gate != null;
        public Actor LocalActor { get; private set; }
        public PlayerCamera Cam { get; private set; }
        public IEnumerable<Actor> AllActors => Hunters.Cast<Actor>().Concat(Prey);

        public readonly List<(string text, float time)> Feed = new();

        // One X-Ray shared by the Hunter team: whoever uses it puts it on cooldown for both.
        public float TeamXrayCd { get; private set; }
        public float TeamXrayLeft { get; private set; }
        public HunterActor XrayUser { get; private set; }

        public bool TryUseXray(HunterActor h)
        {
            if (TeamXrayCd > 0f) return false;
            XrayUser = h;
            TeamXrayLeft = HunterActor.XrayDuration;
            TeamXrayCd = HunterActor.XrayCooldown;
            Log($"{h.DisplayName} used the team X-Ray");
            return true;
        }

        readonly Dictionary<PreyColor, float> ballTimers = new();
        Transform[] hunterSpawns, preySpawns;
        Vector3? lastGatePos;

        void Start()
        {
            Time.timeScale = 1f;
            var arena = GameObject.Find("Arena").transform;
            var spawns = GameObject.Find("Spawns").transform;
            hunterSpawns = Enumerable.Range(0, 2).Select(i => spawns.Find($"HunterSpawn_{i}")).ToArray();
            preySpawns = Enumerable.Range(0, 4).Select(i => spawns.Find($"PreySpawn_{i}")).ToArray();
            Arena.BakeNavMesh(arena, preySpawns[0].position);

            var camGo = Camera.main.gameObject;
            Cam = camGo.GetComponent<PlayerCamera>() ?? camGo.AddComponent<PlayerCamera>();
            Cam.Init(this);
            if (!GetComponent<Hud>()) gameObject.AddComponent<Hud>();

            // "Restart" from the previous match: skip the menu and go straight in with the same role
            if (pendingStart is { } s)
            {
                pendingStart = null;
                BeginMatch(s.role, s.color, s.slot);
            }
        }

        // ---------------- restart ----------------

        static (Team? role, PreyColor color, int slot)? pendingStart;
        (Team? role, PreyColor color, int slot) currentSetup;

        /// <summary>You're out (caught, or a Hunter waiting to respawn) or watching AI vs AI: you may restart right away.</summary>
        public bool CanQuickRestart => State == Phase.Over || State == Phase.Playing && (!LocalActor || !LocalActor.IsActive);

        /// <summary>Reload the scene; <paramref name="sameRole"/> starts the new match immediately with the same role.</summary>
        public void Restart(bool sameRole)
        {
            pendingStart = sameRole ? currentSetup : ((Team?, PreyColor, int)?)null;
            Time.timeScale = 1f;
            SceneManager.LoadScene(SceneManager.GetActiveScene().name);
        }

        /// <summary>Debug fast-forward while you're out (1× → 2× → 4× → 8×).</summary>
        public float SpeedUp { get; private set; } = 1f;

        void TickRestartAndSpeed()
        {
            var kb = UnityEngine.InputSystem.Keyboard.current;
            if (kb == null) return;
            if (CanQuickRestart)
            {
                if (kb.rKey.wasPressedThisFrame) { Restart(true); return; }
                if (kb.mKey.wasPressedThisFrame) { Restart(false); return; }
            }
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            if (State == Phase.Playing && CanQuickRestart && kb.tKey.wasPressedThisFrame) SpeedUp = SpeedUp >= 8f ? 1f : SpeedUp * 2f;
#endif
            if (State != Phase.Playing || !CanQuickRestart) SpeedUp = 1f; // back in the game (Hunter respawned) → normal speed
            Time.timeScale = SpeedUp;
        }

        /// <summary>The recorder riding along on the human player (null unless "Record my gameplay" is on).</summary>
        public BotBase HumanRecorder { get; private set; }

        void OnDestroy() => SlRecorder.CloseAll();

        /// <summary>role == null → spectate an all-AI match.</summary>
        public void BeginMatch(Team? role, PreyColor preyColor = PreyColor.Red, int hunterSlot = 0)
        {
            currentSetup = (role, preyColor, hunterSlot);
            for (int i = 0; i < 2; i++)
            {
                bool human = role == Team.Hunter && i == hunterSlot;
                var t = hunterSpawns[i];
                var h = CreateActor<HunterActor>($"Hunter {i + 1}", t.position, t.eulerAngles.y, Palette.Hunter, new Color(0.95f, 0.25f, 0.25f));
                h.Init(this, $"Hunter {i + 1}", h.transform.Find("Eye"), t.position, t.eulerAngles.y);
                AttachInput(h, human);
                Hunters.Add(h);
            }
            for (int i = 0; i < 4; i++)
            {
                var c = (PreyColor)i;
                bool human = role == Team.Prey && c == preyColor;
                var t = preySpawns[i];
                var p = CreateActor<PreyActor>(c.ToString(), t.position, t.eulerAngles.y, Palette.Of(c), Color.white);
                p.Init(this, c, p.transform.Find("Eye"), t.eulerAngles.y);
                AttachInput(p, human);
                Prey.Add(p);

                var b = Bucket.Create(c);
                b.AttachTo(p);
                Buckets[c] = b;
                ballTimers[c] = 0.5f;
            }

            if (LocalActor) Cam.FollowLocal(LocalActor);
            else Cam.StartSpectating();

            TimeLeft = matchDuration;
            State = Phase.Playing;
            SlRecorder.BeginMatch();
            Log($"Fill every bucket with {Bucket.Capacity} balls to open the Gate!");
        }

        T CreateActor<T>(string name, Vector3 pos, float yaw, Color body, Color visor) where T : Actor
        {
            var go = new GameObject(name) { layer = Layers.Actor };
            go.transform.SetPositionAndRotation(pos + Vector3.up * 0.1f, Quaternion.Euler(0, yaw, 0)); // start just above the floor
            var cc = go.AddComponent<CharacterController>();
            cc.radius = 0.4f;
            cc.height = 1.8f;
            cc.center = Vector3.up * 0.9f;
            cc.stepOffset = 0.3f;
            cc.skinWidth = 0.05f;
            cc.minMoveDistance = 0f;
            go.AddComponent<FPMotor>();

            Palette.Shape(PrimitiveType.Capsule, go.transform, Vector3.up * 0.9f, new Vector3(0.8f, 0.9f, 0.8f), body);
            var eye = new GameObject("Eye").transform;
            eye.SetParent(go.transform, false);
            eye.localPosition = Vector3.up * 1.65f;
            Palette.Shape(PrimitiveType.Cube, eye, new Vector3(0, 0, 0.36f), new Vector3(0.55f, 0.16f, 0.12f), visor, true);
            return go.AddComponent<T>();
        }

        void AttachInput(Actor a, bool human)
        {
            a.IsHuman = human;
            if (human)
            {
                a.Input = a.gameObject.AddComponent<HumanInput>();
                LocalActor = a;
                // SL: watch the human with a bot's eyes and write (situation → what they did) rows
                if (SlSettings.Recording)
                    HumanRecorder = a is HunterActor h
                        ? a.gameObject.AddComponent<HunterBot>().InitShadow(this, h)
                        : a.gameObject.AddComponent<PreyBot>().InitShadow(this, (PreyActor)a);
            }
            else if (a is HunterActor h) a.Input = a.gameObject.AddComponent<HunterBot>().Init(this, h);
            else a.Input = a.gameObject.AddComponent<PreyBot>().Init(this, (PreyActor)a);
        }

        void Update()
        {
            TickRestartAndSpeed();
            if (State != Phase.Playing) return;

            float dt = Time.deltaTime;
            TimeLeft -= dt;
            TeamXrayCd = Mathf.Max(0f, TeamXrayCd - dt);
            TeamXrayLeft = Mathf.Max(0f, TeamXrayLeft - dt);
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            // Debug: K = instantly kill whoever you're playing
            if (LocalActor && LocalActor.IsActive && UnityEngine.InputSystem.Keyboard.current?.kKey.wasPressedThisFrame == true)
                LocalActor.DebugKill();
            // Debug: P = "I'm stuck here" → appends my position to RL/stuck_spots.txt so the map can be fixed
            if (LocalActor && LocalActor.IsActive && UnityEngine.InputSystem.Keyboard.current?.pKey.wasPressedThisFrame == true)
            {
                Vector3 p = LocalActor.transform.position;
                string file = System.IO.Path.GetFullPath(System.IO.Path.Combine(Application.dataPath, "..", "stuck_spots.txt"));
                System.IO.File.AppendAllText(file, string.Format(System.Globalization.CultureInfo.InvariantCulture,
                    "{0:0.00}, {1:0.00}, {2:0.00}  yaw {3:0}  crouched {4}\n", p.x, p.y, p.z, LocalActor.Motor.Yaw, LocalActor.Motor.IsCrouched));
                Log($"Stuck spot saved ({p.x:0.0}, {p.z:0.0})");
            }
#endif
            UpdateBalls(dt);
            UpdateBuckets(dt);
            UpdateGate(dt);
            CheckWin();
            Feed.RemoveAll(f => Time.time - f.time > 6f);
        }

        // ---------------- balls ----------------

        void UpdateBalls(float dt)
        {
            foreach (var c in Buckets.Keys.ToList())
            {
                var bucket = Buckets[c];
                Balls.TryGetValue(c, out var ball);
                if (bucket.Full)
                {
                    if (ball) Destroy(ball.gameObject);
                    Balls[c] = null;
                    continue;
                }
                if (ball == null)
                {
                    ballTimers[c] -= dt;
                    if (ballTimers[c] <= 0f) Balls[c] = Ball.Spawn(c, Arena.BallSpot(BallAvoidList(c), ballMinDistance));
                    continue;
                }
                // collect: only whoever carries this colour's bucket
                var holder = bucket.Holder;
                if (holder && holder.IsActive && Vector3.Distance(holder.transform.position + Vector3.up * 0.9f, ball.transform.position) < 1.4f)
                {
                    bucket.Add();
                    Destroy(ball.gameObject);
                    Balls[c] = null;
                    ballTimers[c] = ballRespawnDelay;
                    Log(holder.Color == c
                        ? $"{holder.DisplayName} collected a ball ({bucket.Count}/{Bucket.Capacity})"
                        : $"{holder.DisplayName} collected {c}'s ball ({bucket.Count}/{Bucket.Capacity})");
                }
            }
        }

        List<Vector3> BallAvoidList(PreyColor c)
        {
            var list = new List<Vector3> { Buckets[c].transform.position };
            foreach (var b in Balls.Values) if (b) list.Add(b.transform.position);
            return list;
        }

        // ---------------- buckets ----------------

        void UpdateBuckets(float dt)
        {
            foreach (var b in Buckets.Values)
            {
                if (!b.Dropped) continue;
                // pick up: only a living Prey whose own bucket(s) are already full
                foreach (var p in Prey)
                {
                    if (!p.IsActive || !p.AllCarriedFull) continue;
                    Vector3 d = p.transform.position - b.transform.position;
                    if (new Vector2(d.x, d.z).magnitude < 1.5f && Mathf.Abs(d.y) < 2f)
                    {
                        b.AttachTo(p);
                        Log($"{p.DisplayName} picked up {b.Color}'s bucket ({b.Count}/{Bucket.Capacity})");
                        break;
                    }
                }
            }
        }

        /// <summary>A dropped bucket within camping distance of this spot (null if none). Prey punish campers with a 5× stun.</summary>
        public Bucket DroppedBucketNear(Vector3 pos)
        {
            foreach (var b in Buckets.Values)
            {
                if (!b.Dropped) continue;
                Vector3 d = b.transform.position - pos; d.y = 0;
                if (d.sqrMagnitude < bucketCampRadius * bucketCampRadius) return b;
            }
            return null;
        }

        public void OnPreyDied(PreyActor p)
        {
            var ground = Arena.Snap(p.transform.position, 3f) ?? p.transform.position;
            foreach (var b in p.Carried.ToList())
            {
                b.DropAt(ground); // every Prey knows where a dropped bucket is (HUD marker / bots are told)
                ground += new Vector3(0.8f, 0, 0.4f);
            }
            if (p.IsHuman) Cam.StartSpectating();
        }

        // ---------------- gate ----------------

        public bool GateConditionMet => Buckets.Values.All(b => b.Full && !b.Dropped);

        void UpdateGate(float dt)
        {
            if (Gate == null)
            {
                if (GateConditionMet)
                {
                    Gate = Gate.Create();
                    PlaceGate();
                    Log("THE GATE HAS APPEARED!");
                }
                return;
            }
            if (Gate.Camp.Tick(Gate.transform.position, gateCampRadius, gateCampTime, Hunters, dt))
            {
                PlaceGate();
                Log("The Gate was camped and teleported!");
            }
            foreach (var p in Prey)
            {
                if (p.IsActive && Gate.Contains(p.transform.position + Vector3.up))
                {
                    p.Escape();
                    EndMatch(Team.Prey, $"{p.DisplayName} escaped through the Gate!");
                    return;
                }
            }
        }

        void PlaceGate()
        {
            var prey = ActivePositions(Prey);
            var hunters = ActivePositions(Hunters);
            if (!Arena.TryGateSpot(prey, hunters, lastGatePos, out var pos, out var yaw))
                pos = Arena.GroundSpot(hunters, 10f);
            Gate.PlaceAt(pos, yaw);
            lastGatePos = pos;
        }

        static List<Vector3> ActivePositions<T>(IEnumerable<T> actors) where T : Actor =>
            actors.Where(a => a.IsActive).Select(a => a.transform.position).ToList();

        // ---------------- end ----------------

        void CheckWin()
        {
            if (Prey.All(p => p.Dead)) EndMatch(Team.Hunter, "All Prey were caught!");
            else if (TimeLeft <= 0f) { TimeLeft = 0f; EndMatch(Team.Hunter, "Time's up!"); }
        }

        void EndMatch(Team winner, string why)
        {
            State = Phase.Over;
            Winner = winner;
            ResultText = why;
            foreach (var a in AllActors) a.Frozen = true;
            Log(why);
            SlRecorder.CloseAll();
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
        }

        /// <summary>Mirror the kill feed to the console with timestamps (for testing AI matches).</summary>
        public static bool FeedToConsole;

        public void Log(string text)
        {
            if (FeedToConsole) Debug.Log($"[FEED {matchDuration - TimeLeft:0}s] {text}");
            Feed.Add((text, Time.time));
            if (Feed.Count > 6) Feed.RemoveAt(0);
        }
    }
}
