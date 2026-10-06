using UnityEngine;

namespace HunterVsPrey
{
    /// <summary>Test helper: drives a character with fixed inputs (move/run/slide held, jump at a set time).</summary>
    public class ScriptedInput : InputProvider
    {
        public Vector2 move = Vector2.up;
        public bool run = true, slide;
        public float jumpAt = -1f, jumpAt2 = -1f;
        public float MaxY { get; private set; } = float.MinValue;
        public bool Jumped { get; private set; }
        float t;

        public override ActorInput Read(float dt)
        {
            float prev = t;
            t += dt;
            MaxY = Mathf.Max(MaxY, transform.position.y);
            if (jumpAt >= 0f && prev < jumpAt && t >= jumpAt) Jumped = true;
            return new ActorInput
            {
                move = move,
                run = run,
                slide = slide,
                jump = (jumpAt >= 0f && prev < jumpAt && t >= jumpAt) || (jumpAt2 >= 0f && prev < jumpAt2 && t >= jumpAt2),
            };
        }
    }
}
