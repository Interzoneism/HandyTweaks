using System;
using System.Collections.Generic;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using HandyTweaks.Internal;

namespace HandyTweaks.Features
{
    /// <summary>
    /// Internal helper: temporarily widens the player's pickup reach after specific triggers
    /// (called from FastPickupPlus). Uses vanilla collector; no aging here; no config/commands.
    /// </summary>
    public class PickupRangeBoost : ModSystem
    {
        private static ICoreServerAPI Sapi;

        private class BoostState
        {
            public float Radius;
            public long ExpireMs;
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

        /// <summary>Called by FastPickupPlus. Player-side radius & duration are passed in.</summary>
        public static void Activate(IPlayer player, float radiusBlocks, int durationMs)
        {
            if (Sapi == null) return;
            var sp = player as IServerPlayer;
            if (sp == null) return;

            float r = GameMath.Clamp(radiusBlocks, 0.5f, 40f);
            long now = Sapi.World.ElapsedMilliseconds;

            Boosts[sp.PlayerUID] = new BoostState
            {
                Radius = r,
                ExpireMs = now + Math.Max(200, durationMs)
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

                if (now > bs.ExpireMs)
                {
                    Boosts.Remove(uid);
                    continue;
                }

                var sp = Sapi.World.PlayerByUid(uid) as IServerPlayer;
                if (sp?.Entity == null) continue;

                // Respect "sneak to pick up" servers
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

                    // No force-aging here — too-fresh items will be allowed by FPP path anyway.
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
