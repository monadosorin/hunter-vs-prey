using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;

namespace HunterVsPrey
{
    /// <summary>
    /// Map queries: runtime navmesh bake (used by bots for A* pathfinding), random spawn spots for balls,
    /// and the Gate placement validation.
    /// </summary>
    public static class Arena
    {
        public const float Half = 48f;     // inner play area is [-48, 48] on x and z
        const float Margin = 1.5f;
        static Vector3 reachRef;           // a point every legal spot must be reachable from

        public static void BakeNavMesh(Transform arenaRoot, Vector3 reachableReference)
        {
            var sources = new List<NavMeshBuildSource>();
            NavMeshBuilder.CollectSources(arenaRoot, Layers.EnvMask, NavMeshCollectGeometry.PhysicsColliders, 0,
                new List<NavMeshBuildMarkup>(), sources);
            // Boxes are rasterised as surfaces, so anything taller than the agent gets a hollow "floor"
            // inside it. Mark every solid box's interior (not its top) as unwalkable — raised ones too
            // (wagon bodies, slide beams): bots were picking spots inside a wagon and standing still under it.
            foreach (var box in arenaRoot.GetComponentsInChildren<BoxCollider>())
            {
                var t = box.transform;
                Vector3 size = Vector3.Scale(box.size, t.lossyScale);
                if (size.y < 0.9f || t.name == "Floor") continue;
                sources.Add(new NavMeshBuildSource
                {
                    shape = NavMeshBuildSourceShape.ModifierBox,
                    transform = Matrix4x4.TRS(t.TransformPoint(box.center) + Vector3.down * 0.15f, t.rotation, Vector3.one),
                    size = new Vector3(Mathf.Max(0.1f, size.x - 0.1f), size.y - 0.2f, Mathf.Max(0.1f, size.z - 0.1f)),
                    area = 1, // built-in "Not Walkable"
                });
            }
            var s = NavMesh.GetSettingsByID(0);
            s.agentRadius = 0.4f;
            s.agentHeight = 1.0f;   // low enough to path through slide gaps (bots crouch/slide there)
            s.agentClimb = 0.75f;   // low walls and crates count as connected (bots jump them)
            s.agentSlope = 45f;
            var bounds = new Bounds(Vector3.up * 3f, new Vector3(Half * 2 + 4, 10f, Half * 2 + 4));
            var data = NavMeshBuilder.BuildNavMeshData(s, sources, bounds, Vector3.zero, Quaternion.identity);
            NavMesh.RemoveAllNavMeshData();
            NavMesh.AddNavMeshData(data);
            reachRef = Snap(reachableReference, 3f) ?? reachableReference;
        }

        public static Vector3? Snap(Vector3 p, float maxDist = 2f)
        {
            return NavMesh.SamplePosition(p, out var hit, maxDist, NavMesh.AllAreas) ? hit.position : null;
        }

        public static bool Reachable(Vector3 from, Vector3 to)
        {
            var a = Snap(from, 3f);
            var b = Snap(to, 1.5f);
            if (a == null || b == null) return false;
            var path = new NavMeshPath();
            return NavMesh.CalculatePath(a.Value, b.Value, NavMesh.AllAreas, path) && path.status == NavMeshPathStatus.PathComplete;
        }

        /// <summary>A* path length over the navmesh (infinity if no full path).</summary>
        public static float PathLength(Vector3 from, Vector3 to, NavMeshPath reuse = null)
        {
            var a = Snap(from, 3f);
            var b = Snap(to, 3f);
            if (a == null || b == null) return float.PositiveInfinity;
            var path = reuse ?? new NavMeshPath();
            if (!NavMesh.CalculatePath(a.Value, b.Value, NavMesh.AllAreas, path) || path.status != NavMeshPathStatus.PathComplete)
                return float.PositiveInfinity;
            float len = 0f;
            for (int i = 1; i < path.corners.Length; i++) len += Vector3.Distance(path.corners[i - 1], path.corners[i]);
            return len;
        }

        static Vector3 RandomPoint() =>
            new(Random.Range(-Half + Margin, Half - Margin), 1f, Random.Range(-Half + Margin, Half - Margin));

        static bool FarFrom(Vector3 p, IEnumerable<Vector3> others, float minDist)
        {
            foreach (var o in others)
            {
                Vector3 d = o - p; d.y = 0;
                if (d.sqrMagnitude < minDist * minDist) return false;
            }
            return true;
        }

        /// <summary>Random reachable spot (floor, ramps, crates or platform tops) for a ball.</summary>
        public static Vector3 BallSpot(IEnumerable<Vector3> avoid, float minDist)
        {
            for (int attempt = 0; attempt < 60; attempt++)
            {
                var s = Snap(RandomPoint(), 2f);
                if (s == null) continue;
                float dist = attempt < 40 ? minDist : minDist * 0.5f;
                if (!FarFrom(s.Value, avoid, dist) || !Reachable(reachRef, s.Value)) continue;
                return s.Value + Vector3.up * 0.6f;
            }
            return reachRef + Vector3.up * 0.6f;
        }

        /// <summary>Floor-level reachable spot away from Hunters (fallback spot for the Gate).</summary>
        public static Vector3 GroundSpot(IEnumerable<Vector3> avoid, float minDist)
        {
            for (int attempt = 0; attempt < 60; attempt++)
            {
                var s = Snap(RandomPoint(), 2f);
                if (s == null || s.Value.y > 0.3f) continue;
                if (!FarFrom(s.Value, avoid, attempt < 40 ? minDist : minDist * 0.5f) || !Reachable(reachRef, s.Value)) continue;
                return s.Value;
            }
            return reachRef;
        }

        /// <summary>
        /// Gate validation. The Gate always stands against the OUTER wall (an exit out of the arena), at a random spot:
        ///  1. on the floor (navmesh, ground level) right in front of the outer wall
        ///  2. the gate frame doesn't overlap any obstacle
        ///  3. there's open clearance in front of it (not blocked by cover)
        ///  4. every living Prey can actually path to it
        ///  5. (fairness) it's not right next to a Hunter or where the gate just was
        /// Otherwise reroll. After many failures step 5 is relaxed so it always resolves.
        /// </summary>
        public static bool TryGateSpot(List<Vector3> preyPositions, List<Vector3> hunterPositions, Vector3? previous,
            out Vector3 pos, out float yaw)
        {
            for (int attempt = 0; attempt < 160; attempt++)
            {
                bool strict = attempt < 110;
                // pick one of the 4 outer walls and a point along it; the gate faces into the arena
                int side = Random.Range(0, 4);
                float along = Random.Range(-Half + 3f, Half - 3f);
                const float FromWall = 0.45f;
                Vector3 inward = side switch { 0 => Vector3.back, 1 => Vector3.forward, 2 => Vector3.left, _ => Vector3.right };
                Vector3 wish = side switch
                {
                    0 => new Vector3(along, 0, Half - FromWall),   // north wall
                    1 => new Vector3(along, 0, -Half + FromWall),  // south wall
                    2 => new Vector3(Half - FromWall, 0, along),   // east wall
                    _ => new Vector3(-Half + FromWall, 0, along),  // west wall
                };
                yaw = side < 2 ? 0f : 90f;
                pos = wish;
                var s = Snap(wish + inward * 0.8f, 1.5f);
                if (s == null || s.Value.y > 0.3f) continue;                                     // 1. floor next to the wall

                var rot = Quaternion.Euler(0, yaw, 0);
                Vector3 frame = new(Gate.Width / 2 + 0.4f, Gate.Height / 2, 0.2f);
                if (Physics.CheckBox(pos + Vector3.up * (Gate.Height / 2 + 0.1f), frame, rot,
                        Layers.EnvMask, QueryTriggerInteraction.Ignore)) continue;                 // 2. no overlap

                Vector3 clear = new(Gate.Width / 2, 1.1f, 1.0f);
                if (Physics.CheckBox(pos + inward * 1.6f + Vector3.up * 1.2f, clear, rot, Layers.EnvMask, QueryTriggerInteraction.Ignore))
                    continue;                                                                       // 3. clearance in front

                bool reachable = true;
                foreach (var p in preyPositions) if (!Reachable(p, pos)) { reachable = false; break; }
                if (!reachable) continue;                                                           // 4. accessible

                if (strict)
                {
                    if (!FarFrom(pos, hunterPositions, 10f)) continue;                              // 5. fairness
                    if (previous.HasValue && !FarFrom(pos, new[] { previous.Value }, 12f)) continue;
                }
                return true;
            }
            pos = Vector3.zero;
            yaw = 0f;
            return false;
        }
    }
}
