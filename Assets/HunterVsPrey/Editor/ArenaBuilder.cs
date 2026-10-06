using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace HunterVsPrey.EditorTools
{
    /// <summary>
    /// Builds the Arena scene: a 96 × 96 m walled city of 3 × 3 blocks. Every block is its own
    /// hand-made district, separated by 6 m streets with cover, so sightlines stay short and
    /// X-Ray matters.
    ///
    ///   Warehouse   | Residential (Prey spawn) | Container yard
    ///   Hedge garden| Plaza                    | Market
    ///   Ruins       | Train yard (Hunter spawn)| Construction site
    ///
    /// Obstacle colour language:
    ///   slate  = tall wall / cover (blocks movement and sight)   moss = hedge (same, garden)
    ///   orange = jump over (low walls, window sills, counters)
    ///   teal   = slide under (beams, slide holes, awnings)
    ///   brown  = climb for height (crates, platforms, scaffold)
    ///   steel  = containers, wagons, cars (tall cover)
    /// </summary>
    public static class ArenaBuilder
    {
        const string Root = "Assets/HunterVsPrey";
        const string ScenePath = Root + "/Scenes/Arena.unity";
        const float Half = Arena.Half;
        const float Block = 32f; // district size; content stays within ±13 of the block centre

        // A standing character is 1.8 m tall and jumps ~1 m.
        const float WallH = 3.5f, Thick = 0.5f;
        const float SillH = 0.5f, LintelH = 3.0f;   // window: hop the sill (easy jump for players and bots), head clears the lintel
        const float SlideTop = 1.1f;                // slide hole: only fits a sliding/crouched body (0.85 m)
        const float DoorTop = 2.6f;

        static Material floorMat, lineMat, outerMat, wallMat, lowMat, slideMat, climbMat, steelMat, hedgeMat, darkMat;
        static Transform arena;
        static Vector3 origin;          // current district centre; all district coordinates are local to it
        static Material currentWall;    // tall-wall material of the current district

        enum Gap { Door, Window, Slide }
        readonly struct Op
        {
            public readonly float at, width;
            public readonly Gap kind;
            public Op(float at, float width, Gap kind) { this.at = at; this.width = width; this.kind = kind; }
        }
        static Op Door(float at, float w = 2.2f) => new(at, w, Gap.Door);
        static Op Window(float at, float w = 2.0f) => new(at, w, Gap.Window);
        static Op Slide(float at, float w = 2.2f) => new(at, w, Gap.Slide);

        [MenuItem("Hunter vs Prey/Build Arena Scene")]
        public static void Build()
        {
            Directory.CreateDirectory(Root + "/Scenes");
            Directory.CreateDirectory(Root + "/Materials");
            floorMat = Mat("Floor", new Color(0.23f, 0.25f, 0.28f));
            lineMat = Mat("FloorLine", new Color(0.31f, 0.33f, 0.37f));
            outerMat = Mat("OuterWall", new Color(0.17f, 0.19f, 0.21f));
            wallMat = Mat("TallWall", new Color(0.42f, 0.45f, 0.5f));
            lowMat = Mat("LowWall_Jump", new Color(0.93f, 0.52f, 0.2f));
            slideMat = Mat("Beam_Slide", new Color(0.15f, 0.7f, 0.76f));
            climbMat = Mat("Crate_Climb", new Color(0.55f, 0.41f, 0.27f));
            steelMat = Mat("Container", new Color(0.3f, 0.34f, 0.38f));
            hedgeMat = Mat("Hedge", new Color(0.24f, 0.31f, 0.24f));
            darkMat = Mat("Dark", new Color(0.14f, 0.15f, 0.17f));

            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            var sun = new GameObject("Sun").AddComponent<Light>();
            sun.type = LightType.Directional;
            sun.intensity = 1.2f;
            sun.shadows = LightShadows.Soft;
            sun.transform.rotation = Quaternion.Euler(52, -35, 0);
            RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Trilight;
            RenderSettings.ambientSkyColor = new Color(0.55f, 0.6f, 0.7f);
            RenderSettings.ambientEquatorColor = new Color(0.35f, 0.36f, 0.4f);
            RenderSettings.ambientGroundColor = new Color(0.2f, 0.2f, 0.22f);

            var camGo = new GameObject("Main Camera") { tag = "MainCamera" };
            var cam = camGo.AddComponent<Camera>();
            cam.farClipPlane = 400f;
            camGo.AddComponent<AudioListener>();
            camGo.transform.SetPositionAndRotation(new Vector3(0, 70, -70), Quaternion.Euler(48, 0, 0));

            arena = new GameObject("Arena").transform;
            origin = Vector3.zero;
            currentWall = wallMat;
            FloorAndOuterWalls();

            District(-1, 1, Warehouse);
            District(0, 1, Residential);
            District(1, 1, ContainerYard);
            District(-1, 0, HedgeGarden);
            District(0, 0, Plaza);
            District(1, 0, Market);
            District(-1, -1, Ruins);
            District(0, -1, TrainYard);
            District(1, -1, Construction);
            origin = Vector3.zero;
            currentWall = wallMat;
            Streets();

            var spawns = new GameObject("Spawns").transform;
            Spawn(spawns, "HunterSpawn_0", new Vector3(-4, 0, -45.5f), 0);
            Spawn(spawns, "HunterSpawn_1", new Vector3(4, 0, -45.5f), 0);
            Spawn(spawns, "PreySpawn_0", new Vector3(-12, 0, 45.5f), 180);
            Spawn(spawns, "PreySpawn_1", new Vector3(-4, 0, 45.5f), 180);
            Spawn(spawns, "PreySpawn_2", new Vector3(4, 0, 45.5f), 180);
            Spawn(spawns, "PreySpawn_3", new Vector3(12, 0, 45.5f), 180);

            new GameObject("MatchManager").AddComponent<MatchManager>();

            EditorSceneManager.SaveScene(scene, ScenePath);
            var list = new List<EditorBuildSettingsScene> { new(ScenePath, true) };
            list.AddRange(EditorBuildSettings.scenes.Where(s => s.path != ScenePath));
            EditorBuildSettings.scenes = list.ToArray();
            Debug.Log("[Hunter vs Prey] Arena scene built at " + ScenePath);
        }

        static void District(int gx, int gz, System.Action build)
        {
            origin = new Vector3(gx * Block, 0, gz * Block);
            currentWall = wallMat;
            build();
        }

        // ------------------------------------------------------------------ shell + streets

        static void FloorAndOuterWalls()
        {
            float size = Half * 2;
            Box("Floor", new Vector3(0, -0.5f, 0), new Vector3(size + 2, 1, size + 2), floorMat);
            for (float p = -Half + 4; p < Half; p += 4)
            {
                Box("Line", new Vector3(p, 0.005f, 0), new Vector3(0.06f, 0.01f, size), lineMat, collider: false);
                Box("Line", new Vector3(0, 0.005f, p), new Vector3(size, 0.01f, 0.06f), lineMat, collider: false);
            }
            Box("Outer_N", new Vector3(0, 3, Half + 0.5f), new Vector3(size + 2, 6, 1), outerMat);
            Box("Outer_S", new Vector3(0, 3, -Half - 0.5f), new Vector3(size + 2, 6, 1), outerMat);
            Box("Outer_E", new Vector3(Half + 0.5f, 3, 0), new Vector3(1, 6, size), outerMat);
            Box("Outer_W", new Vector3(-Half - 0.5f, 3, 0), new Vector3(1, 6, size), outerMat);
        }

        /// <summary>Cover in the 6 m streets between blocks so no street is one long open sightline.</summary>
        static void Streets()
        {
            // intersections: each one different
            Box("X_Kiosk", new Vector3(-16, 1.5f, 16), new Vector3(2.5f, 3, 2.5f), wallMat, 15);
            Container("X_Container", 16, 16, 45);
            Wall("X_Billboard", -18, -14, -14, -18, WallH, Slide(2.8f, 2));
            Box("X_CrateStack", new Vector3(16, 0.7f, -16), new Vector3(1.6f, 1.4f, 1.6f), climbMat, 30);
            Box("X_CrateStep", new Vector3(14.6f, 0.35f, -16.8f), new Vector3(1.4f, 0.7f, 1.4f), climbMat, 30);

            // varied street furniture along every street segment (fixed seed → same map every build)
            var rng = new System.Random(7);
            foreach (float line in new[] { -16f, 16f })
                for (int seg = -1; seg <= 1; seg++)
                    for (int k = 0; k < 2; k++)
                    {
                        float along = seg * Block + (k == 0 ? -7f : 6f) + (float)rng.NextDouble() * 3f;
                        float side = (rng.Next(2) == 0 ? -1.4f : 1.4f);
                        int type = rng.Next(5);
                        StreetProp(new Vector3(line + side, 0, along), true, type, rng);   // north-south street
                        StreetProp(new Vector3(along, 0, line - side), false, (type + 2) % 5, rng); // east-west street
                    }
            // outer ring lane (between the blocks and the outer wall)
            foreach (var p in new[] { new Vector3(-46.5f, 0, -20), new Vector3(-46.5f, 0, 22), new Vector3(46.5f, 0, -24), new Vector3(46.5f, 0, 8),
                                      new Vector3(-24, 0, -46.5f), new Vector3(26, 0, -46.5f), new Vector3(-26, 0, 46.5f), new Vector3(22, 0, 46.5f) })
                Box("Ring_Barrier", p + Vector3.up * 0.35f, new Vector3(1.5f, 0.7f, 1.5f), lowMat);
        }

        static void StreetProp(Vector3 p, bool alongZ, int type, System.Random rng)
        {
            float yaw = (alongZ ? 0 : 90) + (float)(rng.NextDouble() * 20 - 10);
            switch (type)
            {
                case 0: Box("St_Dumpster", p + Vector3.up * 0.8f, new Vector3(2.0f, 1.6f, 1.2f), steelMat, yaw + 90); break;
                case 1: Box("St_Barrier", p + Vector3.up * 0.35f, new Vector3(0.5f, 0.7f, 3f), lowMat, yaw + 90); break;
                case 2: Box("St_Pillar", p + Vector3.up * 1.75f, new Vector3(1.2f, 3.5f, 1.2f), wallMat, yaw); break;
                case 3: Box("St_Bus", p + Vector3.up * 1.3f, new Vector3(0.4f, 2.6f, 3.2f), wallMat, yaw); break;
                default: Box("St_Crate", p + Vector3.up * 0.35f, new Vector3(1.4f, 0.7f, 1.4f), climbMat, yaw); break;
            }
        }

        // ------------------------------------------------------------------ districts (local coords, ±13)

        /// <summary>NW: big roofless warehouse — doors, windows, slide hole, shelf aisles, loading dock.</summary>
        static void Warehouse()
        {
            Wall("WH_N", -11, 9, 11, 9, WallH, Window(4), Door(11), Window(18));
            Wall("WH_S", -11, -9, 11, -9, WallH, Door(3), Window(9), Slide(14), Window(19));
            Wall("WH_W", -11, -9, -11, 9, WallH, Window(5), Door(12));
            Wall("WH_E", 11, -9, 11, 9, WallH, Door(4), Window(10), Window(15));
            Box("Shelf_A", new Vector3(-6, 1.3f, -1), new Vector3(0.9f, 2.6f, 8), wallMat);
            Box("Shelf_B", new Vector3(-1, 1.3f, 2), new Vector3(0.9f, 2.6f, 8), wallMat);
            Box("Shelf_C", new Vector3(4, 1.3f, -2), new Vector3(0.9f, 2.6f, 8), wallMat);
            Box("Shelf_D", new Vector3(4.5f, 1.3f, 6.2f), new Vector3(6, 2.6f, 0.9f), wallMat);
            Box("WH_Crate1", new Vector3(-9, 0.35f, 7), new Vector3(1.4f, 0.7f, 1.4f), climbMat);
            Box("WH_Crate2", new Vector3(-9, 0.7f, 5.6f), new Vector3(1.4f, 1.4f, 1.4f), climbMat);
            Box("WH_Crate3", new Vector3(8.5f, 0.35f, -7), new Vector3(1.6f, 0.7f, 1.6f), climbMat, 12);
            Box("WH_Low", new Vector3(-3, 0.35f, -6.8f), new Vector3(3.5f, 0.7f, 0.5f), lowMat);
            Box("WH_Dock", new Vector3(-5, 0.7f, -10.5f), new Vector3(8, 1.4f, 2.5f), climbMat); // flush against the south wall
            Box("WH_DockStep", new Vector3(-9.8f, 0.35f, -10.8f), new Vector3(1.6f, 0.7f, 1.6f), climbMat);
            Box("WH_Truck", new Vector3(5, 1.4f, -12.2f), new Vector3(7, 2.8f, 2.6f), steelMat);
        }

        /// <summary>N: residential street where the Prey spawn — small houses, fences, a car, trees.</summary>
        static void Residential()
        {
            House("House_A", -8, 1, 8, 7, n: new[] { Window(2.5f) }, s: new[] { Door(4) }, e: new[] { Window(3.5f) }, w: new[] { Window(2), Door(5) });
            House("House_B", 6, 3, 7, 8, n: new[] { Door(4.5f) }, s: new[] { Window(2), Window(5) }, e: new[] { Slide(4) }, w: new[] { Window(5.5f) });
            House("House_C", -1, -8, 9, 6, n: new[] { Window(3), Door(6.5f) }, s: new[] { Window(4.5f) }, e: new[] { Door(3) }, w: new[] { Window(3) });
            Box("Fence_1", new Vector3(-8, 0.35f, 9.5f), new Vector3(8, 0.7f, 0.3f), lowMat);
            Box("Fence_2", new Vector3(7, 0.35f, 9.8f), new Vector3(6, 0.7f, 0.3f), lowMat, -6);
            Box("Fence_3", new Vector3(11.5f, 0.35f, -4), new Vector3(0.3f, 0.7f, 6), lowMat);
            Box("Car", new Vector3(10.5f, 0.75f, -10.5f), new Vector3(2, 1.5f, 4.2f), steelMat, 80);
            Box("Tree_1", new Vector3(-12, 1.75f, -6), new Vector3(1, 3.5f, 1), hedgeMat);
            Box("Tree_2", new Vector3(1.5f, 1.75f, 6.5f), new Vector3(1, 3.5f, 1), hedgeMat);
            Box("Tree_3", new Vector3(-11, 1.75f, -12), new Vector3(1.2f, 3.5f, 1.2f), hedgeMat);
            Box("Bench", new Vector3(-3.5f, 0.35f, 5.5f), new Vector3(2.4f, 0.7f, 0.7f), climbMat);
        }

        /// <summary>NE: shipping containers at irregular angles; one stack is climbable via crate stairs.</summary>
        static void ContainerYard()
        {
            Container("Ct_1", -7, 4, 90);
            Container("Ct_2", 0, 7, 90);
            Container("Ct_3", 6.5f, 1, 4);
            Container("Ct_4", -5.5f, -3.5f, -7);
            Container("Ct_5", 1.5f, -2.5f, 83);
            Container("Ct_6", 6, -8.5f, 90);
            Container("Ct_7", -1.5f, -10, 15);
            Container("Ct_8", -11, -2, 0);
            Container("Ct_9", 10, 10, 90);
            Container("Ct_10", -8, 10, 5);
            Box("Yard_Step1", new Vector3(-2.4f, 0.35f, 8.9f), new Vector3(1.4f, 0.7f, 1.4f), climbMat);
            Box("Yard_Step2", new Vector3(-1.0f, 0.7f, 8.9f), new Vector3(1.4f, 1.4f, 1.4f), climbMat);
            Box("Yard_Step3", new Vector3(0.4f, 1.05f, 8.9f), new Vector3(1.4f, 2.1f, 1.4f), climbMat);
            Box("Yard_Pallet1", new Vector3(3.5f, 0.35f, 4.5f), new Vector3(2.2f, 0.7f, 1.2f), lowMat, 20);
            Box("Yard_Pallet2", new Vector3(-9, 0.35f, -8), new Vector3(1.2f, 0.7f, 2.4f), lowMat);
            Beam("Yard_Pipe", new Vector3(-2.5f, 0, 1), 4.5f, 0);
            Box("Yard_Booth", new Vector3(11, 1.5f, -3), new Vector3(2.5f, 3, 2.5f), wallMat);
        }

        /// <summary>W: hedge garden — a loose maze of tall hedges with slide-under arches and a fountain.</summary>
        static void HedgeGarden()
        {
            currentWall = hedgeMat;
            const float h = 2.8f;
            Wall("Hedge_N", -11, 11, 11, 11, h, Door(7, 2.6f), Slide(17));
            Wall("Hedge_S", -11, -11, 11, -11, h, Door(4, 2.6f), Slide(14));
            Wall("Hedge_W", -11, -11, -11, 11, h, Door(11, 2.6f), Slide(4));
            Wall("Hedge_E", 11, -11, 11, 11, h, Door(6, 2.6f), Door(18, 2.6f));
            Wall("Hedge_A", -7, 7, -7, -2, h);
            Wall("Hedge_B", -3, 4, 6, 4, h, Slide(4.5f));
            Wall("Hedge_C", 6, 4, 6, -3, h);
            Wall("Hedge_D", -7, -5, 2, -5, h, Door(6, 2.4f));
            Wall("Hedge_E2", -2, 1, -2, -2, h);
            Box("Fountain", new Vector3(2.5f, 0.35f, 0), new Vector3(2.5f, 0.7f, 2.5f), climbMat, 45);
            Box("Garden_Statue", new Vector3(8.5f, 1.5f, 8), new Vector3(1, 3, 1), wallMat);
            Box("Garden_Bench", new Vector3(-9, 0.35f, -8.5f), new Vector3(0.7f, 0.7f, 2.4f), climbMat);
            currentWall = wallMat;
        }

        /// <summary>Centre: plaza around an off-centre stage, broken up by kiosks, a statue and cover.</summary>
        static void Plaza()
        {
            Box("Stage", new Vector3(3, 0.7f, -3), new Vector3(8, 1.4f, 5), climbMat);
            Box("Stage_StepW", new Vector3(-1.8f, 0.35f, -3.5f), new Vector3(1.6f, 0.7f, 1.6f), climbMat);
            Box("Stage_StepN", new Vector3(5.2f, 0.35f, 0.3f), new Vector3(1.6f, 0.7f, 1.6f), climbMat);
            Box("Stage_Backdrop", new Vector3(3, 2.4f, -5.7f), new Vector3(8, 2, 0.4f), wallMat);
            Box("Plaza_Pillar1", new Vector3(-6, 1.75f, 4), new Vector3(1.5f, 3.5f, 1.5f), wallMat);
            Box("Plaza_Pillar3", new Vector3(-3, 1.75f, -11), new Vector3(1.5f, 3.5f, 1.5f), wallMat);
            Box("Plaza_Low1", new Vector3(-7.5f, 0.35f, -3), new Vector3(4, 0.7f, 0.5f), lowMat, 30);
            Box("Plaza_Low2", new Vector3(11, 0.35f, -6), new Vector3(4, 0.7f, 0.5f), lowMat, -15);
            Box("Plaza_Low3", new Vector3(0.5f, 0.35f, 6.5f), new Vector3(6, 0.7f, 0.5f), lowMat);
            Box("Plaza_Low4", new Vector3(-4.5f, 0.35f, -8), new Vector3(3, 0.7f, 0.5f), lowMat, 75);
            Box("Plaza_Planter", new Vector3(-7, 0.35f, 10), new Vector3(3, 0.7f, 3), climbMat, 8);
            Beam("Plaza_Gantry", new Vector3(6.5f, 0, -11), 6, 90);
            Wall("Plaza_Screen", 9, -1, 12.5f, 3, WallH, Window(2.6f));
            Container("Plaza_Container", 0, 10.5f, 70);
            Box("Kiosk_1", new Vector3(9, 1.5f, 9), new Vector3(3, 3, 3), wallMat, 10);
            Box("Kiosk_2", new Vector3(-11.5f, 1.5f, 5), new Vector3(2.5f, 3, 2.5f), wallMat);
            Box("Statue", new Vector3(-9, 2, -9), new Vector3(2, 4, 2), wallMat, 25);
        }

        /// <summary>E: market — U-shaped stalls you can hop into over the counter, tarp awnings to slide under.</summary>
        static void Market()
        {
            Stall("Stall_1", -9, 9, 0);
            Stall("Stall_2", -3.5f, 9.5f, 0);
            Stall("Stall_3", 3, 10, 10);
            Stall("Stall_4", 9.5f, 8, -5);
            Stall("Stall_5", -10, 0.5f, 90);
            Stall("Stall_6", -3, 1.5f, 180);
            Stall("Stall_7", 4, -0.5f, 170);
            Stall("Stall_8", 10.5f, -2, 270);
            Stall("Stall_9", -8, -9, 0);
            Stall("Stall_10", 0, -9.5f, 15);
            Stall("Stall_11", 8, -10, 0);
            Beam("Tarp_1", new Vector3(0.5f, 0, 5), 5, 90);
            Beam("Tarp_2", new Vector3(-6, 0, -4.5f), 4, 90);
            Box("Mkt_Cart", new Vector3(6, 0.75f, 4.5f), new Vector3(2, 1.5f, 3), steelMat, 30);
            Box("Mkt_Crates", new Vector3(-12, 0.35f, -4.5f), new Vector3(1.4f, 0.7f, 1.4f), climbMat);
        }

        /// <summary>SW: ruins — broken walls of different heights full of windows, rubble, a fallen column.</summary>
        static void Ruins()
        {
            Wall("Ruin_1", -8, 8, 3, 8, 3f, Window(3), Slide(7.5f));
            Wall("Ruin_2", 7, 11, 7, -1, WallH, Door(3, 2), Window(8));
            Wall("Ruin_3", -6, 3, 0.5f, -3, 3f, Window(4.4f));
            Wall("Ruin_4", -4, -8, 5, -8, 2.4f, Window(2.5f), Window(6.5f));
            Wall("Ruin_5", 1, 5, 4, 5, 2.4f);
            Wall("Ruin_6", -11, -4, -11, -11, WallH, Window(3));
            Wall("Ruin_7", -11, -11, -4, -11, WallH, Door(4));
            Wall("Ruin_8", 9, -4, 12, -11, 3f, Window(3.8f));
            Wall("Ruin_Arch", -12, 10, -12, 2, 2.4f, Window(4));
            Box("Ruin_Rubble1", new Vector3(-1, 0.35f, 3.5f), new Vector3(2, 0.7f, 1.2f), lowMat, 20);
            Box("Ruin_Rubble2", new Vector3(4.5f, 0.35f, -4), new Vector3(1.5f, 0.7f, 1.5f), lowMat);
            Box("Ruin_Rubble3", new Vector3(10, 0.35f, -0.5f), new Vector3(3, 0.7f, 0.6f), lowMat, -30);
            Box("Ruin_Column", new Vector3(-4.5f, 1.25f, -1), new Vector3(1, 2.5f, 1), wallMat);
            Box("Ruin_ColumnFallen", new Vector3(-2, 0.35f, -4.8f), new Vector3(4, 0.7f, 0.7f), lowMat, 35);
            Box("Ruin_Stairs1", new Vector3(-7.8f, 0.35f, 4.5f), new Vector3(1.4f, 0.7f, 1.4f), climbMat);
            Box("Ruin_Stairs2", new Vector3(-7.8f, 0.7f, 5.9f), new Vector3(1.4f, 1.4f, 1.4f), climbMat);
        }

        /// <summary>S: train yard where the Hunters spawn — wagons you can slide underneath, a station.</summary>
        static void TrainYard()
        {
            Wagon("Wagon_1", -8.5f, 8, 9);
            Wagon("Wagon_2", 4, 8, 8);
            Wagon("Wagon_3", -3, 2, 12);
            Wagon("Wagon_4", 9.5f, 2, 6);
            Wagon("Wagon_5", -9.5f, -4, 6);
            Wagon("Wagon_6", 4.5f, -4, 10);
            Box("Platform", new Vector3(-5, 0.35f, -8.5f), new Vector3(10, 0.7f, 2.5f), climbMat);
            Wall("Station", 2, -10.5f, 9, -10.5f, 3f, Window(2), Window(5));
            Box("Signal_1", new Vector3(12, 1.75f, -8), new Vector3(1, 3.5f, 1), wallMat);
            Box("Signal_2", new Vector3(-12.5f, 1.75f, 5), new Vector3(1, 3.5f, 1), wallMat);
            Box("Yard_Shed", new Vector3(11, 1.5f, 11), new Vector3(3, 3, 3), wallMat, 8);
        }

        /// <summary>SE: construction site — stepped scaffold, catwalk to run on or slide under, half-built frame.</summary>
        static void Construction()
        {
            Box("Scaf_1", new Vector3(-7, 0.35f, 7), new Vector3(3, 0.7f, 3), climbMat);
            Box("Scaf_2", new Vector3(-4, 0.7f, 7), new Vector3(3, 1.4f, 3), climbMat);
            Box("Scaf_3", new Vector3(-1, 1.05f, 7), new Vector3(3, 2.1f, 3), climbMat);
            Box("Scaf_Catwalk", new Vector3(3, 1.6f, 7), new Vector3(5.2f, 1.0f, 1.6f), slideMat);
            Box("Scaf_4", new Vector3(6.5f, 1.05f, 7), new Vector3(3, 2.1f, 3), climbMat);
            Box("Scaf_5", new Vector3(6.5f, 0.7f, 4), new Vector3(3, 1.4f, 3), climbMat);
            Box("Scaf_6", new Vector3(6.5f, 0.35f, 1), new Vector3(3, 0.7f, 3), climbMat);
            Beam("Site_Pipe1", new Vector3(-5, 0, -3), 6, 0);
            Beam("Site_Pipe2", new Vector3(1.5f, 0, -7.5f), 5, 90);
            Box("Site_Block1", new Vector3(-8, 0.6f, 0), new Vector3(2, 1.2f, 2), wallMat, 10);
            Box("Site_Block2", new Vector3(0, 1.25f, -1.5f), new Vector3(1.5f, 2.5f, 4), wallMat);
            Box("Site_Block3", new Vector3(4.5f, 0.35f, -4.5f), new Vector3(2, 0.7f, 2), lowMat);
            Wall("Site_HalfWall", -10, -10, -2, -10, 2.8f, Window(2.5f), Window(5.8f));
            Wall("Frame_S", 4, -12, 12, -12, WallH, Window(2), Window(6));
            Wall("Frame_E", 12, -12, 12, -4, WallH, Door(2), Window(6));
            Box("Crane_Base", new Vector3(-11, 2.5f, 11), new Vector3(2, 5, 2), darkMat);
            Box("Sand_1", new Vector3(10.5f, 0.35f, 10.5f), new Vector3(2.5f, 0.7f, 2.5f), climbMat, 20);
        }

        // ------------------------------------------------------------------ builders

        /// <summary>
        /// Straight wall from a to b (local x,z) with openings placed by distance from a:
        /// Door = full gap (lintel above 2.6 m) · Window = 0.5 m sill to hop, lintel at 3 m · Slide = 1.1 m hole.
        /// </summary>
        static void Wall(string name, float ax, float az, float bx, float bz, float h, params Op[] ops)
        {
            Vector3 a = new(ax, 0, az), b = new(bx, 0, bz);
            Vector3 dir = b - a;
            float len = dir.magnitude;
            dir /= len;
            float yaw = Mathf.Atan2(dir.x, dir.z) * Mathf.Rad2Deg;

            void Seg(string part, float from, float to, float y0, float y1, Material m)
            {
                if (to - from < 0.01f || y1 - y0 < 0.01f) return;
                Vector3 c = a + dir * ((from + to) * 0.5f) + Vector3.up * ((y0 + y1) * 0.5f);
                Box($"{name}_{part}", c, new Vector3(Thick, y1 - y0, to - from), m, yaw);
            }

            float cursor = 0f;
            foreach (var op in ops.OrderBy(o => o.at))
            {
                float s = op.at - op.width / 2, e = op.at + op.width / 2;
                Seg("Solid", cursor, s, 0, h, currentWall);
                switch (op.kind)
                {
                    case Gap.Window:
                        Seg("Sill", s, e, 0, SillH, lowMat);
                        Seg("Lintel", s, e, LintelH, h, currentWall);
                        break;
                    case Gap.Slide:
                        Seg("SlideBeam", s, e, SlideTop, h, slideMat);
                        break;
                    case Gap.Door:
                        Seg("Lintel", s, e, DoorTop, h, currentWall);
                        break;
                }
                cursor = e;
            }
            Seg("Solid", cursor, len, 0, h, currentWall);
        }

        /// <summary>Roofless house: 4 walls with openings (n/s measured west→east, e/w measured south→north).</summary>
        static void House(string name, float cx, float cz, float width, float depth, Op[] n, Op[] s, Op[] e, Op[] w)
        {
            float x0 = cx - width / 2, x1 = cx + width / 2, z0 = cz - depth / 2, z1 = cz + depth / 2;
            Wall(name + "_N", x0, z1, x1, z1, WallH, n);
            Wall(name + "_S", x0, z0, x1, z0, WallH, s);
            Wall(name + "_E", x1, z0, x1, z1, WallH, e);
            Wall(name + "_W", x0, z0, x0, z1, WallH, w);
        }

        /// <summary>
        /// Market stall you can vault straight through: a counter on the front AND back (hop in one side,
        /// out the other), tall side panels that block sight, and a canopy high enough to clear a jump.
        /// </summary>
        static void Stall(string name, float x, float z, float yaw)
        {
            var rot = Quaternion.Euler(0, yaw, 0);
            Vector3 c = new(x, 0, z);
            Box(name + "_SideL", c + rot * new Vector3(-1.45f, 1.5f, 0), new Vector3(0.3f, 3.0f, 2.2f), wallMat, yaw);
            Box(name + "_SideR", c + rot * new Vector3(1.45f, 1.5f, 0), new Vector3(0.3f, 3.0f, 2.2f), wallMat, yaw);
            Box(name + "_CounterFront", c + rot * new Vector3(0, 0.35f, 0.9f), new Vector3(2.6f, 0.7f, 0.4f), lowMat, yaw);
            Box(name + "_CounterBack", c + rot * new Vector3(0, 0.35f, -0.9f), new Vector3(2.6f, 0.7f, 0.4f), lowMat, yaw);
            Box(name + "_Canopy", c + rot * new Vector3(0, 3.1f, 0), new Vector3(3.4f, 0.2f, 2.6f), darkMat, yaw);
        }

        /// <summary>Railway wagon along x: body raised on two bogies — slide under the middle.</summary>
        static void Wagon(string name, float x, float z, float len)
        {
            Box(name + "_Body", new Vector3(x, 2.25f, z), new Vector3(len, 2.3f, 2.8f), steelMat);
            Box(name + "_BogieA", new Vector3(x - len / 2 + 1.1f, 0.55f, z), new Vector3(1.6f, 1.1f, 2.4f), darkMat);
            Box(name + "_BogieB", new Vector3(x + len / 2 - 1.1f, 0.55f, z), new Vector3(1.6f, 1.1f, 2.4f), darkMat);
            Box(name + "_Rail", new Vector3(x, 0.03f, z - 0.75f), new Vector3(len + 4, 0.06f, 0.12f), darkMat, collider: false);
            Box(name + "_Rail", new Vector3(x, 0.03f, z + 0.75f), new Vector3(len + 4, 0.06f, 0.12f), darkMat, collider: false);
        }

        static void Container(string name, float x, float z, float yaw) =>
            Box(name, new Vector3(x, 1.3f, z), new Vector3(2.4f, 2.6f, 6f), steelMat, yaw);

        /// <summary>Overhead beam on two posts: slide under it (bottom at 1.1 m).</summary>
        static void Beam(string name, Vector3 centre, float length, float yaw)
        {
            var rot = Quaternion.Euler(0, yaw, 0);
            Box(name, centre + Vector3.up * 1.6f, new Vector3(0.5f, 1.0f, length), slideMat, yaw);
            Box(name + "_PostA", centre + rot * new Vector3(0, 0.55f, -length / 2 + 0.2f), new Vector3(0.3f, 1.1f, 0.3f), slideMat, yaw);
            Box(name + "_PostB", centre + rot * new Vector3(0, 0.55f, length / 2 - 0.2f), new Vector3(0.3f, 1.1f, 0.3f), slideMat, yaw);
        }

        static GameObject Box(string name, Vector3 pos, Vector3 size, Material mat, float yaw = 0f, bool collider = true)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            go.name = name;
            go.transform.SetParent(arena, false);
            go.transform.SetPositionAndRotation(origin + pos, Quaternion.Euler(0, yaw, 0));
            go.transform.localScale = size;
            go.GetComponent<Renderer>().sharedMaterial = mat;
            if (!collider) Object.DestroyImmediate(go.GetComponent<Collider>());
            go.isStatic = true;
            return go;
        }

        static void Spawn(Transform parent, string name, Vector3 pos, float yaw)
        {
            var t = new GameObject(name).transform;
            t.SetParent(parent, false);
            t.SetPositionAndRotation(pos, Quaternion.Euler(0, yaw, 0));
        }

        static Material Mat(string name, Color c)
        {
            string path = $"{Root}/Materials/{name}.mat";
            var m = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (!m)
            {
                m = new Material(Shader.Find("Universal Render Pipeline/Lit"));
                AssetDatabase.CreateAsset(m, path);
            }
            m.SetColor("_BaseColor", c);
            m.SetFloat("_Smoothness", 0.15f);
            EditorUtility.SetDirty(m);
            return m;
        }
    }
}
