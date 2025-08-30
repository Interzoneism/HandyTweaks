using System;
using System.Collections.Generic;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using HandyTweaks.Internal;
using System.Reflection;

namespace HandyTweaks.Features
{

    public class PickupRangeBoost : ModSystem
    {
        private static ICoreServerAPI Sapi;

        private class BoostState
        {
            public float Radius;
            public long ExpireMs;
            public long FreshSinceMs;   // NEW
            public long FreshUntilMs;   // NEW
        }

        private static FieldInfo FiItemSpawnedMs;

        private static void ResolveSpawnedMsField()
        {
            try
            {
                FiItemSpawnedMs =
                    typeof(EntityItem).GetField("itemSpawnedMilliseconds", BindingFlags.Instance | BindingFlags.Public) ??
                    typeof(EntityItem).GetField("spawnedMs", BindingFlags.Instance | BindingFlags.Public) ??
                    typeof(EntityItem).GetField("spawnMs", BindingFlags.Instance | BindingFlags.Public);

                if (FiItemSpawnedMs == null)
                {
                    foreach (var fi in typeof(EntityItem).GetFields(BindingFlags.Instance | BindingFlags.Public))
                    {
                        if (fi.FieldType == typeof(long) && fi.Name.IndexOf("spawn", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            FiItemSpawnedMs = fi; break;
                        }
                    }
                }
            }
            catch { /* best effort */ }
        }

        private static long GetSpawnedMs(EntityItem ei)
        {
            if (FiItemSpawnedMs == null || ei == null) return -1;
            try
            {
                var v = FiItemSpawnedMs.GetValue(ei);
                if (v is long l) return l;
                if (v is int i) return i;
            }
            catch { }
            return -1;
        }

        private static readonly Dictionary<string, BoostState> Boosts = new();
        private static long TickId;

        // Internal, conservative defaults
        private const int TickIntervalMs = 100;     // 10 Hz
        private const int EntitiesPerTickLimit = 64;

        public override void StartServerSide(ICoreServerAPI sapi)
        {
            Sapi = sapi;
            HtPickupCore.ResolveMembers();
            ResolveSpawnedMsField();
        }

        public override void Dispose()
        {
            base.Dispose();
            if (Sapi != null && TickId != 0)
            {
                Sapi.World.UnregisterGameTickListener(TickId);
                TickId = 0;
            }
            Boosts.Clear();
        }

        public static void Activate(IPlayer player, float radiusBlocks, int durationMs)
        {
            if (Sapi == null) return;
            long now = Sapi.World.ElapsedMilliseconds;
            Activate(player, radiusBlocks, durationMs, now, now + 1200); // default ~1.2s if not specified
        }

        public static void Activate(IPlayer player, float radiusBlocks, int durationMs, long freshSinceMs, long freshUntilMs)
        {
            if (Sapi == null) return;
            var sp = player as IServerPlayer;
            if (sp == null) return;

            float r = GameMath.Clamp(radiusBlocks, 0.5f, 40f);
            long now = Sapi.World.ElapsedMilliseconds;

            Boosts[sp.PlayerUID] = new BoostState
            {
                Radius = r,
                ExpireMs = now + Math.Max(200, durationMs),
                FreshSinceMs = freshSinceMs,
                FreshUntilMs = Math.Max(freshSinceMs, freshUntilMs)
            };

            if (TickId == 0)
            {
                TickId = Sapi.World.RegisterGameTickListener(ServerTick, TickIntervalMs);
            }
        }

        private static void ServerTick(float dt)
        {
            if (Sapi == null) { StopTick(); return; }
            long now = Sapi.World.ElapsedMilliseconds;

            HtPickupCore.Cull(now);

            int alive = 0;
            foreach (var kv in new List<KeyValuePair<string, BoostState>>(Boosts))
            {
                var uid = kv.Key;
                var bs = kv.Value;

                // End the boost if either duration elapsed OR freshness window elapsed
                if (now > bs.ExpireMs || now > bs.FreshUntilMs)
                {
                    Boosts.Remove(uid);
                    continue;
                }

                var sp = Sapi.World.PlayerByUid(uid) as IServerPlayer;
                if (sp?.Entity == null) continue;

                if (sp.ItemCollectMode == 1)
                {
                    var agent = sp.Entity as EntityAgent;
                    bool sneaking = agent != null && agent.Controls != null && agent.Controls.Sneak;
                    if (!sneaking) continue;
                }

                alive++;
                int processed = 0;

                Entity[] ents;
                try { ents = Sapi.World.GetEntitiesAround(sp.Entity.ServerPos.XYZ, bs.Radius, bs.Radius); }
                catch { continue; }
                if (ents == null || ents.Length == 0) continue;

                for (int i = 0; i < ents.Length; i++)
                {
                    if (processed >= EntitiesPerTickLimit) break;

                    var ei = ents[i] as EntityItem;
                    if (ei == null || !ei.Alive) continue;
                    if (ei.Itemstack == null || ei.Itemstack.StackSize <= 0) continue;

                    if (HtPickupCore.WasJustProcessed(ei.EntityId, now)) continue;

                    long spawned = GetSpawnedMs(ei);
                    if (spawned < 0) continue;                                     
                    if (spawned < bs.FreshSinceMs || spawned > bs.FreshUntilMs)     
                        continue;


                    if (HtPickupCore.TryCollectViaBehavior(sp, ei))
                    {
                        HtPickupCore.MarkProcessed(ei.EntityId, now);
                        processed++;
                    }
                }
            }

            if (alive == 0 && Boosts.Count == 0) StopTick();
        }

        private static void StopTick()
        {
            if (Sapi != null && TickId != 0)
            {
                Sapi.World.UnregisterGameTickListener(TickId);
            }
            TickId = 0;
        }
    }
}
