using UnityEngine;

namespace HunterVsPrey
{
    /// <summary>Prey projectile. Swept sphere each frame so it can't tunnel; hits on a Hunter stun + 1 damage.</summary>
    public class Paintball : MonoBehaviour
    {
        public const float Speed = 45f, Drop = 4f;
        const float Radius = 0.12f, Life = 3f;

        PreyActor owner;
        Vector3 velocity;
        float age;

        public static void Spawn(PreyActor owner, Vector3 pos, Vector3 dir)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            DestroyImmediate(go.GetComponent<Collider>());
            go.name = "Paintball";
            go.transform.position = pos;
            go.transform.localScale = Vector3.one * Radius * 2f;
            go.GetComponent<Renderer>().sharedMaterial = Palette.Get(Palette.Of(owner.Color), true);
            var pb = go.AddComponent<Paintball>();
            pb.owner = owner;
            pb.velocity = dir.normalized * Speed;
        }

        void Update()
        {
            float dt = Time.deltaTime;
            age += dt;
            if (age > Life) { Destroy(gameObject); return; }

            velocity += Vector3.down * Drop * dt;
            Vector3 step = velocity * dt;
            var hits = Physics.SphereCastAll(transform.position, Radius, step.normalized, step.magnitude,
                Layers.EnvAndActorsMask, QueryTriggerInteraction.Ignore);
            RaycastHit? first = null;
            foreach (var h in hits)
            {
                if (owner && h.collider.transform.IsChildOf(owner.transform)) continue;
                if (first == null || h.distance < first.Value.distance) first = h;
            }

            if (first is RaycastHit hit)
            {
                var hunter = hit.collider.GetComponentInParent<HunterActor>();
                if (hunter) hunter.TakePaintball(owner);
                else if (hit.collider.GetComponentInParent<Actor>() == null) Splat(hit);
                Destroy(gameObject);
                return;
            }
            transform.position += step;
        }

        void Splat(RaycastHit hit)
        {
            var s = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            DestroyImmediate(s.GetComponent<Collider>());
            s.name = "Splat";
            s.transform.position = hit.point + hit.normal * 0.01f;
            s.transform.up = hit.normal;
            s.transform.localScale = new Vector3(0.7f, 0.005f, 0.7f);
            s.GetComponent<Renderer>().sharedMaterial = Palette.Get(Palette.Of(owner.Color));
            Destroy(s, 20f);
        }
    }
}
