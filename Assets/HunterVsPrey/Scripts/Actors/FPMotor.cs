using UnityEngine;

namespace HunterVsPrey
{
    /// <summary>
    /// First-person movement shared by every character: walk, run, jump, slide (and crouch under low ceilings).
    /// The transform pivot is at the feet.
    /// </summary>
    [RequireComponent(typeof(CharacterController))]
    public class FPMotor : MonoBehaviour
    {
        public float walkSpeed = 3.0f;
        public float runSpeed = 5.0f;
        public float crouchSpeed = 1.9f;
        public float jumpVelocity = 6.3f;
        public float gravity = -20f;
        public float airControl = 8f;

        public float standHeight = 1.8f;
        public float slideHeight = 0.85f;
        public float slideBoost = 1.7f;
        public float slideDuration = 0.75f;
        public float slideCooldown = 0.6f;
        public float impulseDecay = 47f;
        /// <summary>Hunters run 1% faster than Prey.</summary>
        public float speedMultiplier = 1f;
        public bool Boosted => boostLeft > 0f;
        float boostMult = 1f, boostLeft;

        /// <summary>Temporary speed boost (Prey escaping after a tag).</summary>
        public void Boost(float mult, float duration) { boostMult = mult; boostLeft = duration; }
        float SpeedScale => speedMultiplier * (boostLeft > 0f ? boostMult : 1f);

        public float Yaw { get; private set; }
        public float Pitch { get; private set; }
        public bool IsSliding { get; private set; }
        public bool IsCrouched { get; private set; }
        public bool IsGrounded => cc.isGrounded;
        public Vector3 Velocity => horizontal + impulse + Vector3.up * vertical;

        CharacterController cc;
        Transform eye;
        Vector3 horizontal, impulse, slideDir;
        float vertical, slideT, slideCd;

        public void Init(Transform eyeTransform, float yaw)
        {
            cc = GetComponent<CharacterController>();
            eye = eyeTransform;
            Yaw = yaw;
            Pitch = 0f;
            lastSafePos = transform.position;
            SetCrouch(false);
            ApplyRotation();
        }

        public void Tick(in ActorInput i, float dt)
        {
            Yaw = Mathf.Repeat(Yaw + i.yawDelta, 360f);
            Pitch = Mathf.Clamp(Pitch + i.pitchDelta, -85f, 85f);
            ApplyRotation();

            bool grounded = cc.isGrounded;
            slideCd -= dt;
            boostLeft -= dt;

            Vector3 wish = transform.right * i.move.x + transform.forward * i.move.y;
            wish = Vector3.ClampMagnitude(wish, 1f);
            float hSpeed = new Vector3(horizontal.x, 0, horizontal.z).magnitude;

            // Start a slide: needs ground, some speed and the cooldown to be over.
            if (i.slide && !IsSliding && grounded && slideCd <= 0f && hSpeed > 2f)
            {
                IsSliding = true;
                slideT = 0f;
                slideDir = wish.sqrMagnitude > 0.01f ? wish.normalized : transform.forward;
                SetCrouch(true);
            }

            Vector3 desired;
            if (IsSliding)
            {
                slideT += dt;
                float s = Mathf.Lerp(runSpeed + slideBoost, crouchSpeed, slideT / slideDuration) * SpeedScale;
                desired = slideDir * s;
                if (slideT >= slideDuration) { IsSliding = false; slideCd = slideCooldown; }
            }
            else
            {
                // Holding slide while slow = crouch. Can't stand up under something low.
                bool wantCrouch = i.slide || !CanStand();
                if (wantCrouch != IsCrouched && (wantCrouch || CanStand())) SetCrouch(wantCrouch);
                float speed = (IsCrouched ? crouchSpeed : (i.run ? runSpeed : walkSpeed)) * SpeedScale;
                desired = wish * speed;
            }

            horizontal = grounded ? desired : Vector3.MoveTowards(horizontal, desired, airControl * dt);

            // Jump buffering + coyote time: a press is remembered briefly, and you can still jump a moment
            // after leaving the ground. Stops presses being eaten when "grounded" flickers against a wall.
            jumpBuffer = i.jump ? JumpBufferTime : jumpBuffer - dt;
            coyote = grounded ? CoyoteTime : coyote - dt;
            if (grounded && vertical < 0f) vertical = -2f;
            if (jumpBuffer > 0f && coyote > 0f && vertical <= 0f && (!IsCrouched || CanStand()))
            {
                if (IsSliding) { IsSliding = false; slideCd = slideCooldown; } // slide-jump keeps momentum
                SetCrouch(false);
                vertical = jumpVelocity;
                jumpBuffer = coyote = 0f;
            }
            vertical += gravity * dt;
            impulse = Vector3.MoveTowards(impulse, Vector3.zero, impulseDecay * dt);

            var flags = SafeMove((horizontal + impulse + Vector3.up * vertical) * dt);
            if ((flags & CollisionFlags.Above) != 0 && vertical > 0f) vertical = 0f;
        }

        Vector3 lastSafePos;
        const float JumpBufferTime = 0.15f, CoyoteTime = 0.1f;
        float jumpBuffer, coyote;

        /// <summary>
        /// Moves in ≤ 0.25 m steps so a long frame (editor hitch, first frame) can't push the
        /// controller through the floor; anyone who still ends up below the world is put back.
        /// </summary>
        CollisionFlags SafeMove(Vector3 motion)
        {
            int steps = Mathf.Clamp(Mathf.CeilToInt(motion.magnitude / 0.25f), 1, 8);
            CollisionFlags flags = CollisionFlags.None;
            for (int k = 0; k < steps; k++) flags |= cc.Move(motion / steps);
            if (cc.isGrounded) lastSafePos = transform.position;
            else if (transform.position.y < -3f)
            {
                Teleport(lastSafePos + Vector3.up * 0.1f, Yaw);
                return CollisionFlags.None;
            }
            return flags;
        }

        /// <summary>Gravity only (stunned / frozen characters still fall).</summary>
        public void TickPassive(float dt)
        {
            horizontal = Vector3.MoveTowards(horizontal, Vector3.zero, 30f * dt);
            impulse = Vector3.zero;
            if (cc.isGrounded && vertical < 0f) vertical = -2f;
            vertical += gravity * dt;
            SafeMove((horizontal + Vector3.up * vertical) * dt);
        }

        public void AddImpulse(Vector3 v) => impulse += v;

        public void Teleport(Vector3 pos, float yaw)
        {
            cc.enabled = false;
            transform.position = pos;
            lastSafePos = pos;
            cc.enabled = true;
            horizontal = impulse = Vector3.zero;
            vertical = 0f;
            Yaw = yaw;
            Pitch = 0f;
            IsSliding = false;
            SetCrouch(false);
            ApplyRotation();
        }

        void ApplyRotation()
        {
            transform.rotation = Quaternion.Euler(0f, Yaw, 0f);
            if (eye) eye.localRotation = Quaternion.Euler(Pitch, 0f, 0f);
        }

        void SetCrouch(bool crouch)
        {
            IsCrouched = crouch;
            float h = crouch ? slideHeight : standHeight;
            cc.height = h;
            cc.center = Vector3.up * (h * 0.5f);
            if (eye) eye.localPosition = Vector3.up * (h - 0.15f);
        }

        bool CanStand()
        {
            float r = cc.radius * 0.95f;
            Vector3 p = transform.position;
            return !Physics.CheckCapsule(p + Vector3.up * (r + 0.05f), p + Vector3.up * (standHeight - r),
                r, Layers.EnvMask, QueryTriggerInteraction.Ignore);
        }
    }
}
