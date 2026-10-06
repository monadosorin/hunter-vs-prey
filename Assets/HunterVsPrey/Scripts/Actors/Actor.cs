using UnityEngine;
using UnityEngine.Rendering;

namespace HunterVsPrey
{
    /// <summary>Anything that turns game state into an ActorInput each frame (human, BT bot, SL model...).</summary>
    public abstract class InputProvider : MonoBehaviour
    {
        public abstract ActorInput Read(float dt);
    }

    public abstract class Actor : MonoBehaviour
    {
        public Team Team { get; protected set; }
        public string DisplayName { get; protected set; }
        public int MaxHp { get; protected set; }
        public int Hp { get; protected set; }
        public bool IsHuman { get; set; }
        public bool Frozen { get; set; }

        /// <summary>In the arena and able to act / be seen.</summary>
        public bool IsActive { get; protected set; } = true;
        public float StunLeft { get; protected set; }
        public bool Stunned => StunLeft > 0f;

        public FPMotor Motor { get; private set; }
        public Transform Eye { get; private set; }
        public InputProvider Input { get; set; }
        public Vector3 EyePos => Eye.position;
        /// <summary>Centre of the body (lower when crouched / sliding, e.g. hiding under a wagon).</summary>
        public Vector3 Chest => transform.position + Vector3.up * (Motor && Motor.IsCrouched ? 0.5f : 1.1f);

        protected MatchManager match;
        protected CharacterController cc;
        Renderer[] renderers;

        protected void Setup(MatchManager m, Team team, string name, int hp, Transform eye, float yaw)
        {
            match = m;
            Team = team;
            DisplayName = name;
            MaxHp = Hp = hp;
            Eye = eye;
            cc = GetComponent<CharacterController>();
            Motor = GetComponent<FPMotor>();
            Motor.Init(eye, yaw);
            renderers = GetComponentsInChildren<Renderer>();
        }

        /// <summary>Time this actor was last hurt (screen flash for a human player).</summary>
        public float LastHurtTime { get; private set; } = -99f;
        /// <summary>Time this actor last landed a tag / paintball (hit-marker for a human player).</summary>
        public float LastLandedHitTime { get; private set; } = -99f;
        public float FlashLeft { get; private set; }

        /// <summary>Blink the body for a while (Prey i-frames after a tag, Hunter stun after a paintball).</summary>
        protected void Flash(float duration) => FlashLeft = Mathf.Max(FlashLeft, duration);
        /// <summary>Who last hurt this actor and where they stood (bots "feel" where a hit came from).</summary>
        public Actor LastAttacker { get; private set; }
        public Vector3 LastAttackerPos { get; private set; }

        protected void MarkHurt(Actor by = null)
        {
            LastHurtTime = Time.time;
            LastAttacker = by;
            if (by) LastAttackerPos = by.transform.position;
        }
        public void MarkLandedHit() => LastLandedHitTime = Time.time;

        protected virtual void Update()
        {
            float dt = Time.deltaTime;
            TickTimers(dt);
            TickFlash(dt);
            if (!IsActive) return;
            if (Frozen || Stunned || Input == null) { Motor.TickPassive(dt); return; }
            var i = Input.Read(dt);
            Motor.Tick(i, dt);
            UseAbilities(i);
        }

        protected virtual void TickTimers(float dt) => StunLeft = Mathf.Max(0f, StunLeft - dt);

        /// <summary>Debug: die instantly (K key in the editor).</summary>
        public abstract void DebugKill();
        protected abstract void UseAbilities(in ActorInput i);

        void TickFlash(float dt)
        {
            if (FlashLeft <= 0f) return;
            FlashLeft -= dt;
            bool on = FlashLeft <= 0f || Mathf.FloorToInt(FlashLeft * 12f) % 2 == 0; // ~6 blinks per second
            if (!IsActive) on = false;
            foreach (var r in renderers) if (r) r.enabled = on;
        }

        protected void SetPresent(bool present)
        {
            IsActive = present;
            cc.enabled = present;
            if (!present) FlashLeft = 0f;
            foreach (var r in renderers) if (r) r.enabled = present;
        }

        /// <summary>First-person camera owner: hide own body but keep its shadow.</summary>
        public void SetFirstPersonHidden(bool hidden)
        {
            foreach (var r in renderers)
                if (r) r.shadowCastingMode = hidden ? ShadowCastingMode.ShadowsOnly : ShadowCastingMode.On;
        }
    }
}
