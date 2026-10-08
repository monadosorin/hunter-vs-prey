using System.Collections.Generic;
using UnityEngine;

namespace HunterVsPrey
{
    public class PreyActor : Actor
    {
        public const int StartHp = 2;
        public const float ReloadTime = 20f;
        public const float TagInvulnerability = 1.5f;
        public const float TagBoost = 1.6f, TagBoostTime = 3f; // +60% speed for 3 s after a tag (outlasts the i-frames)
        public const float MarkRange = 35f, MarkAngle = 8f;     // RMB: the ball must be near the crosshair and in sight
        public const float MarkCooldown = 20f, MarkDuration = 10f; // a shown ball has an indicator for every Prey for 10 s

        public PreyColor Color { get; private set; }
        public readonly List<Bucket> Carried = new();
        public float ReloadLeft { get; private set; }
        public bool HasAmmo => ReloadLeft <= 0f;
        public float InvulnLeft { get; private set; }
        /// <summary>When this Prey last fired (SL labels a human's shot as "Fight").</summary>
        public float LastFireTime { get; private set; } = -99f;
        /// <summary>Cooldown of "show a teammate their ball" (RMB).</summary>
        public float MarkCd { get; private set; }
        public bool Escaped { get; private set; }
        public bool Dead => Hp <= 0;

        /// <summary>Own bucket done (and any taken-over ones) → allowed to pick up a fallen teammate's bucket.</summary>
        public bool AllCarriedFull
        {
            get { foreach (var b in Carried) if (!b.Full) return false; return true; }
        }

        public void Init(MatchManager m, PreyColor c, Transform eye, float yaw)
        {
            Color = c;
            Setup(m, Team.Prey, c.ToString(), StartHp, eye, yaw);
        }

        protected override void TickTimers(float dt)
        {
            base.TickTimers(dt);
            ReloadLeft = Mathf.Max(0f, ReloadLeft - dt);
            InvulnLeft = Mathf.Max(0f, InvulnLeft - dt);
            MarkCd = Mathf.Max(0f, MarkCd - dt);
        }

        protected override void UseAbilities(in ActorInput i)
        {
            if (i.primary && HasAmmo) Fire();
            if (i.mark && MarkCd <= 0f) TryMarkBall();
        }

        /// <summary>The unmarked ball closest to the crosshair (within range, in line of sight), or null.</summary>
        public Ball BallInSights()
        {
            Ball best = null;
            float bestAngle = MarkAngle;
            foreach (var b in match.Balls.Values)
            {
                if (!b || b.Marked) continue;
                Vector3 to = b.transform.position - EyePos;
                if (to.magnitude > MarkRange) continue;
                float ang = Vector3.Angle(Eye.forward, to);
                if (ang >= bestAngle) continue;
                if (Physics.Linecast(EyePos, b.transform.position, Layers.EnvMask, QueryTriggerInteraction.Ignore)) continue;
                best = b; bestAngle = ang;
            }
            return best;
        }

        /// <summary>Show a ball to every Prey for a few seconds (then it's on cooldown).</summary>
        void TryMarkBall()
        {
            var b = BallInSights();
            if (!b) return;
            b.Mark(MarkDuration);
            MarkCd = MarkCooldown;
            match.Log($"{DisplayName} showed {b.Color} where their ball is");
        }

        /// <summary>
        /// Painting a Hunter who camps a dropped bucket speeds up the reload: 20 s × (Prey alive / 4),
        /// so the fewer Prey are left, the sooner they can fire again.
        /// </summary>
        public void OnHitCamper(int preyAlive)
        {
            float reload = ReloadTime * preyAlive / 4f;
            ReloadLeft = Mathf.Min(ReloadLeft, Mathf.Max(0f, reload - (Time.time - LastFireTime)));
        }

        void Fire()
        {
            ReloadLeft = ReloadTime;
            LastFireTime = Time.time;
            Paintball.Spawn(this, EyePos + Eye.forward * 0.6f, Eye.forward);
        }

        public void TakeTag(HunterActor by)
        {
            if (!IsActive || InvulnLeft > 0f) return;
            Hp--;
            InvulnLeft = TagInvulnerability;
            by.MarkLandedHit();
            MarkHurt(by);
            HitBurst.Spawn(Chest, Hp > 0 ? UnityEngine.Color.white : Palette.Of(Color), Hp > 0 ? 14 : 28, Hp > 0 ? 5f : 7f);
            Flash(TagInvulnerability);
            if (Hp > 0)
            {
                Motor.Boost(TagBoost, TagBoostTime); // adrenaline: get away before the next swing
                match.Log($"{by.DisplayName} tagged {DisplayName} (1 HP left)");
                return;
            }
            match.Log($"{by.DisplayName} caught {DisplayName}");
            Die();
        }

        public override void DebugKill()
        {
            if (!IsActive) return;
            Hp = 0;
            MarkHurt();
            HitBurst.Spawn(Chest, Palette.Of(Color), 28, 7f);
            match.Log($"[debug] {DisplayName} killed");
            Die();
        }

        void Die()
        {
            SetPresent(false);
            match.OnPreyDied(this);
        }

        public void Escape()
        {
            Escaped = true;
            SetPresent(false);
            foreach (var b in Carried) b.gameObject.SetActive(false); // the buckets leave with you (else they'd float at the Gate)
        }
    }
}
