using UnityEngine;

namespace HunterVsPrey
{
    /// <summary>Small burst of cubes flying out from a hit (tag or paintball).</summary>
    public class HitBurst : MonoBehaviour
    {
        const float Life = 0.55f;
        Vector3[] velocities;
        Transform[] bits;
        float age;

        public static void Spawn(Vector3 pos, Color color, int count = 14, float speed = 5f)
        {
            var root = new GameObject("HitBurst").AddComponent<HitBurst>();
            root.transform.position = pos;
            root.bits = new Transform[count];
            root.velocities = new Vector3[count];
            for (int k = 0; k < count; k++)
            {
                var bit = Palette.Shape(PrimitiveType.Cube, root.transform, Vector3.zero, Vector3.one * 0.14f, color, true).transform;
                bit.localRotation = Random.rotation;
                root.bits[k] = bit;
                root.velocities[k] = (Random.onUnitSphere + Vector3.up * 0.6f).normalized * speed * Random.Range(0.6f, 1.2f);
            }
        }

        void Update()
        {
            float dt = Time.deltaTime;
            age += dt;
            if (age > Life) { Destroy(gameObject); return; }
            float scale = 0.14f * (1f - age / Life);
            for (int k = 0; k < bits.Length; k++)
            {
                velocities[k] += Vector3.down * 12f * dt;
                bits[k].localPosition += velocities[k] * dt;
                bits[k].localScale = Vector3.one * scale;
            }
        }
    }
}
