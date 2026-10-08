using UnityEngine;

namespace HunterVsPrey
{
    public class HunterActor : Actor
    {
        public const int StartHp = 5;
        public const float TagRange = 2.4f, TagAngle = 40f, TagCooldown = 1.2f;
        public const float XrayDuration = 3f, XrayCooldown = 20f; // shared by both Hunters; cooldown = 20 s × 4 / Prey alive (see MatchManager)
        public const float RespawnTime = 30f;
        public const float SpeedEdge = 1.01f; // 1% faster than Prey
        public const float StunTime = 2f;
        public const float SlowFactor = 0.5f, SlowTime = 1.5f; // after any paintball stun: half speed for 1.5 s
        public const float CampStunPerHunter = 5f; // painted while camping a dropped bucket → 5 s × Hunters alive

        public float TagCd { get; private set; }
        /// <summary>Team X-Ray cooldown: one charge shared by both Hunters.</summary>
        public float XrayCd => match.TeamXrayCd;
        /// <summary>True only for the Hunter who triggered the current X-Ray.</summary>
        public bool XrayActive => match.XrayUser == this && match.TeamXrayLeft > 0f;
        public float RespawnLeft { get; private set; }
        /// <summary>When this Hunter last swung a tag (SL labels a human's swing as "Attack").</summary>
        public float LastTagTime { get; private set; } = -99f;

        bool slowAfterStun;
        /// <summary>Half speed for a moment right after a paintball stun.</summary>
        public bool Slowed => !Stunned && Motor.Boosted;

        Vector3 spawnPos;
        float spawnYaw;

        public void Init(MatchManager m, string name, Transform eye, Vector3 spawn, float yaw)
        {
            Setup(m, Team.Hunter, name, StartHp, eye, yaw);
            Motor.speedMultiplier = SpeedEdge;
            spawnPos = spawn;
            spawnYaw = yaw;
        }

        protected override void TickTimers(float dt)
        {
            base.TickTimers(dt);
            TagCd = Mathf.Max(0f, TagCd - dt);
            // a paintball stun wears off into a short slow (still recovering)
            if (slowAfterStun && !Stunned) { slowAfterStun = false; Motor.Boost(SlowFactor, SlowTime); }
            if (!IsActive && RespawnLeft > 0f)
            {
                RespawnLeft -= dt;
                if (RespawnLeft <= 0f) Respawn();
            }
        }

        protected override void UseAbilities(in ActorInput i)
        {
            if (i.primary && TagCd <= 0f) TryTag();
            if (i.xray) match.TryUseXray(this);
        }

        /// <summary>Best prey in the tag cone right now (null if none). Bots use this to decide when to press tag.</summary>
        public PreyActor PreyInTagReach()
        {
            PreyActor best = null;
            float bestD = float.MaxValue;
            // The swing reaches down too: a Prey crouched under a wagon / beam can be tagged from beside it
            // even though the line from the eyes is blocked by the wagon body.
            Vector3 low = transform.position + Vector3.up * 0.6f;
            foreach (var p in match.Prey)
            {
                if (!p.IsActive) continue;
                Vector3 to = p.Chest - EyePos;
                float d = to.magnitude;
                if (d > TagRange + 0.4f || d >= bestD) continue;
                Vector3 flat = new Vector3(to.x, 0, to.z);
                if (Vector3.Angle(transform.forward, flat) > TagAngle) continue;
                if (Physics.Linecast(EyePos, p.Chest, Layers.EnvMask, QueryTriggerInteraction.Ignore) &&
                    Physics.Linecast(low, p.Chest, Layers.EnvMask, QueryTriggerInteraction.Ignore)) continue;
                best = p; bestD = d;
            }
            return best;
        }

        void TryTag()
        {
            TagCd = TagCooldown; // the swing costs the cooldown even on a miss
            LastTagTime = Time.time;
            var p = PreyInTagReach();
            if (p) p.TakeTag(this);
        }

        public void TakePaintball(PreyActor shooter)
        {
            if (!IsActive) return;
            Hp--;
            // Camping a dropped bucket is punished: a long stun (5 s per living Hunter) and the shooter reloads faster
            var camped = match.DroppedBucketNear(transform.position);
            int huntersAlive = 0, preyAlive = 0;
            foreach (var h in match.Hunters) if (h.IsActive) huntersAlive++;
            foreach (var p in match.Prey) if (p.IsActive) preyAlive++;
            float stun = camped ? CampStunPerHunter * huntersAlive : StunTime;
            if (camped) shooter.OnHitCamper(preyAlive);
            StunLeft = stun;
            slowAfterStun = true;
            shooter.MarkLandedHit();
            MarkHurt(shooter);
            HitBurst.Spawn(Chest, Palette.Of(shooter.Color), Hp > 0 ? 18 : 30, Hp > 0 ? 5f : 7f);
            Flash(stun);
            match.Log(camped && Hp > 0
                ? $"{shooter.DisplayName} caught {DisplayName} camping {camped.Color}'s bucket! Stunned {stun:0}s ({Hp}/{MaxHp})"
                : $"{shooter.DisplayName} painted {DisplayName} ({Hp}/{MaxHp}){(Hp > 0 ? $", stunned {stun:0}s + slowed {SlowTime:0.#}s" : "")}");
            if (Hp <= 0) GoDown();
        }

        public override void DebugKill()
        {
            if (!IsActive) return;
            Hp = 0;
            MarkHurt();
            HitBurst.Spawn(Chest, Palette.Hunter, 30, 7f);
            match.Log($"[debug] {DisplayName} killed");
            GoDown();
        }

        void GoDown()
        {
            SetPresent(false);
            RespawnLeft = RespawnTime;
            match.Log($"{DisplayName} is down for {RespawnTime:0}s");
        }

        void Respawn()
        {
            Hp = MaxHp;
            StunLeft = 0f;
            slowAfterStun = false;
            Motor.Boost(1f, 0f); // no leftover slow
            Motor.Teleport(spawnPos, spawnYaw);
            SetPresent(true);
            match.Log($"{DisplayName} respawned");
        }
    }
}
