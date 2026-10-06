using System.Collections.Generic;
using UnityEngine;

namespace HunterVsPrey
{
    public enum Team { Hunter, Prey }

    public enum PreyColor { Red, Blue, Green, Yellow }

    /// <summary>
    /// Everything a character can "press" in one frame. Humans and bots both produce this,
    /// which keeps them on identical rules and doubles as the action format for SL recordings.
    /// </summary>
    public struct ActorInput
    {
        public Vector2 move;      // x = strafe, y = forward, in [-1, 1]
        public float yawDelta;    // degrees this frame, positive = turn right
        public float pitchDelta;  // degrees this frame, positive = look down
        public bool run;
        public bool jump;         // pressed this frame
        public bool slide;        // held (also crouches when not moving fast)
        public bool primary;      // pressed this frame: Hunter tag / Prey paintball
        public bool xray;         // Hunter only
        public bool mark;         // Prey only: mark the ball in your crosshair for the whole team
        public bool locate;       // Prey only: reveal where your own ball is (cooldown)
    }

    public static class Layers
    {
        // Characters live on the built-in "Ignore Raycast" layer so vision and
        // placement checks only ever hit level geometry on Default.
        public const int Actor = 2;
        public const int EnvMask = 1 << 0;
        public const int EnvAndActorsMask = (1 << 0) | (1 << Actor);
    }

    public static class Palette
    {
        static readonly Dictionary<(Color, bool), Material> cache = new();
        static Shader lit;

        public static readonly Color Hunter = new(0.42f, 0.24f, 0.62f);
        public static readonly Color GateGold = new(1f, 0.78f, 0.2f);

        public static Color Of(PreyColor c) => c switch
        {
            PreyColor.Red => new Color(0.86f, 0.22f, 0.2f),
            PreyColor.Blue => new Color(0.2f, 0.45f, 0.92f),
            PreyColor.Green => new Color(0.15f, 0.68f, 0.38f),
            _ => new Color(0.98f, 0.82f, 0.15f),
        };

        public static Material Get(Color c, bool emissive = false)
        {
            if (cache.TryGetValue((c, emissive), out var m) && m) return m;
            if (!lit) lit = Shader.Find("Universal Render Pipeline/Lit");
            m = new Material(lit);
            m.SetColor("_BaseColor", c);
            if (emissive)
            {
                m.EnableKeyword("_EMISSION");
                m.SetColor("_EmissionColor", c * 0.9f);
            }
            cache[(c, emissive)] = m;
            return m;
        }

        /// <summary>Primitive without collider, coloured, parented.</summary>
        public static GameObject Shape(PrimitiveType type, Transform parent, Vector3 localPos, Vector3 scale, Color c, bool emissive = false)
        {
            var go = GameObject.CreatePrimitive(type);
            Object.DestroyImmediate(go.GetComponent<Collider>());
            go.transform.SetParent(parent, false);
            go.transform.localPosition = localPos;
            go.transform.localScale = scale;
            go.GetComponent<Renderer>().sharedMaterial = Get(c, emissive);
            return go;
        }
    }
}
