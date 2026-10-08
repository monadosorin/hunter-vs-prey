using UnityEngine;

namespace HunterVsPrey
{
    /// <summary>A collectible ball. Only one per colour exists at a time.</summary>
    public class Ball : MonoBehaviour
    {
        public PreyColor Color { get; private set; }
        /// <summary>A Prey pointed it out: every Prey sees it through walls for a few seconds.</summary>
        public bool Marked => Time.time < markedUntil;
        float markedUntil = -1f;
        Vector3 basePos;

        public void Mark(float duration) => markedUntil = Time.time + duration;

        public static Ball Spawn(PreyColor c, Vector3 pos)
        {
            var go = Palette.Shape(PrimitiveType.Sphere, null, pos, Vector3.one * 0.6f, Palette.Of(c), true);
            go.name = $"Ball_{c}";
            var b = go.AddComponent<Ball>();
            b.Color = c;
            b.basePos = pos;
            return b;
        }

        void Update()
        {
            transform.position = basePos + Vector3.up * (Mathf.Sin(Time.time * 2.5f) * 0.12f);
            transform.Rotate(0f, 90f * Time.deltaTime, 0f);
        }
    }

    /// <summary>
    /// Stores progress for one colour. Carried on a Prey's back; dropped where its carrier dies,
    /// then picked up by a surviving Prey whose own buckets are already full. It never moves once dropped:
    /// a Hunter camping it risks a long stun instead (see HunterActor.TakePaintball).
    /// </summary>
    public class Bucket : MonoBehaviour
    {
        public const int Capacity = 4;

        public PreyColor Color { get; private set; }
        public int Count { get; private set; }
        public bool Full => Count >= Capacity;
        public PreyActor Holder { get; private set; }
        public bool Dropped => Holder == null;

        GameObject ring;

        public static Bucket Create(PreyColor c)
        {
            var root = new GameObject($"Bucket_{c}");
            var col = Palette.Of(c);
            Palette.Shape(PrimitiveType.Cylinder, root.transform, new Vector3(0, 0.22f, 0), new Vector3(0.45f, 0.22f, 0.45f), col);
            Palette.Shape(PrimitiveType.Cylinder, root.transform, new Vector3(0, 0.45f, 0), new Vector3(0.5f, 0.02f, 0.5f), col * 0.6f);
            var b = root.AddComponent<Bucket>();
            b.Color = c;
            b.ring = Palette.Shape(PrimitiveType.Cylinder, root.transform, new Vector3(0, 0.02f, 0), new Vector3(1.6f, 0.01f, 1.6f), col, true);
            b.ring.SetActive(false);
            return b;
        }

        public void Add() => Count = Mathf.Min(Capacity, Count + 1);

        public void AttachTo(PreyActor p)
        {
            Holder = p;
            if (!p.Carried.Contains(this)) p.Carried.Add(this);
            transform.SetParent(p.transform, false);
            int slot = p.Carried.IndexOf(this);
            transform.localPosition = new Vector3((slot % 2 == 0 ? -0.18f : 0.18f), 0.9f + 0.45f * (slot / 2), -0.45f);
            transform.localRotation = Quaternion.identity;
            transform.localScale = Vector3.one * 0.7f;
            ring.SetActive(false);
        }

        public void DropAt(Vector3 groundPos)
        {
            if (Holder) Holder.Carried.Remove(this);
            Holder = null;
            transform.SetParent(null, true);
            transform.position = groundPos;
            transform.rotation = Quaternion.identity;
            transform.localScale = Vector3.one;
            ring.SetActive(true);
        }
    }

    /// <summary>Exit gate. No colliders: it's purely a goal volume checked by distance.</summary>
    public class Gate : MonoBehaviour
    {
        public const float Width = 3f, Height = 3.2f;
        public CampTimer Camp { get; } = new();

        public static Gate Create()
        {
            var root = new GameObject("Gate");
            var gold = Palette.GateGold;
            Palette.Shape(PrimitiveType.Cube, root.transform, new Vector3(-Width / 2, Height / 2, 0), new Vector3(0.3f, Height, 0.4f), gold, true);
            Palette.Shape(PrimitiveType.Cube, root.transform, new Vector3(Width / 2, Height / 2, 0), new Vector3(0.3f, Height, 0.4f), gold, true);
            Palette.Shape(PrimitiveType.Cube, root.transform, new Vector3(0, Height, 0), new Vector3(Width + 0.3f, 0.3f, 0.4f), gold, true);
            var portal = Palette.Shape(PrimitiveType.Cube, root.transform, new Vector3(0, Height / 2, 0), new Vector3(Width - 0.3f, Height - 0.15f, 0.05f), new Color(1f, 0.9f, 0.55f), true);
            portal.name = "Portal";
            var light = new GameObject("GateLight").AddComponent<Light>();
            light.transform.SetParent(root.transform, false);
            light.transform.localPosition = Vector3.up * 2f;
            light.color = gold;
            light.range = 12f;
            light.intensity = 3f;
            return root.AddComponent<Gate>();
        }

        public void PlaceAt(Vector3 pos, float yaw)
        {
            transform.SetPositionAndRotation(pos, Quaternion.Euler(0, yaw, 0));
            Camp.Reset();
        }

        public bool Contains(Vector3 p)
        {
            Vector3 local = transform.InverseTransformPoint(p);
            return Mathf.Abs(local.x) < Width / 2 && Mathf.Abs(local.z) < 0.9f && local.y > -0.5f && local.y < Height;
        }
    }

    /// <summary>
    /// Anti-camping: fills while any active Hunter stays within the radius, drains at half speed
    /// when they leave (so stepping out for a second doesn't reset it). Returns true when it fires.
    /// </summary>
    public class CampTimer
    {
        public float Elapsed { get; private set; }
        public float Limit { get; private set; } = 1f;
        public float Progress => Elapsed / Limit;

        public void Reset() => Elapsed = 0f;

        public bool Tick(Vector3 pos, float radius, float limit, System.Collections.Generic.IEnumerable<HunterActor> hunters, float dt)
        {
            Limit = limit;
            bool camped = false;
            foreach (var h in hunters)
            {
                if (!h.IsActive) continue;
                Vector3 d = h.transform.position - pos;
                d.y = 0;
                if (d.sqrMagnitude < radius * radius) { camped = true; break; }
            }
            Elapsed = camped ? Elapsed + dt : Mathf.Max(0f, Elapsed - dt * 0.5f);
            if (Elapsed < limit) return false;
            Elapsed = 0f;
            return true;
        }
    }
}
