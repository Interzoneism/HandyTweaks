using System;
using System.Collections.Generic;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Server;
using Vintagestory.GameContent;

namespace HandyTweaks.Internal
{

    public static class HtPickupCore
    {
        public static event global::System.Func<IServerPlayer, EntityItem, bool> GlobalPickupGate;

        private const int DefaultProcessTtlMs = 1500;
        private static readonly Dictionary<long, long> processedUntilMs = new Dictionary<long, long>();
        private static readonly Dictionary<long, long> thrownUntilMs = new Dictionary<long, long>();

        public static bool TryCollectViaBehavior(IServerPlayer sp, EntityItem ei)
        {
            if (sp?.Entity == null || ei == null || !ei.Alive) return false;
            if (IsRecentlyThrown(ei.EntityId, sp.Entity.World.ElapsedMilliseconds)) return false;

            var del = GlobalPickupGate;
            if (del != null)
            {
                try
                {
                    foreach (var d in del.GetInvocationList())
                    {
                        var fn = (global::System.Func<IServerPlayer, EntityItem, bool>)d;
                        bool allow = true;
                        try { allow = fn(sp, ei); } catch { /* ignore and allow */ }
                        if (!allow) return false;
                    }
                }
                catch { /* ignore */ }
            }

            try
            {
                if (!ei.CanCollect(sp.Entity)) return false;
            }
            catch
            {
            }

            var behavior = sp.Entity.GetBehavior<EntityBehaviorCollectEntities>();
            if (behavior == null) return false;

            int before = ei.Itemstack?.StackSize ?? 0;
            try
            {
                behavior.OnFoundCollectible(ei);
            }
            catch { /* ignore */ }

            if (!ei.Alive) return true;
            int after = ei.Itemstack?.StackSize ?? 0;
            return after < before;
        }


        public static void MarkProcessed(long entityId, long nowMs, int ttlMs = DefaultProcessTtlMs)
        {
            try { processedUntilMs[entityId] = nowMs + Math.Max(100, ttlMs); } catch { }
        }

        public static void MarkThrown(long entityId, long nowMs, int graceMs = 2000)
        {
            try { thrownUntilMs[entityId] = nowMs + Math.Max(100, graceMs); } catch { }
        }

        private static bool IsRecentlyThrown(long entityId, long nowMs)
        {
            try
            {
                return thrownUntilMs.TryGetValue(entityId, out long until) && until > nowMs;
            }
            catch { return false; }
        }

        public static bool WasJustProcessed(long entityId, long nowMs)
        {
            try
            {
                if (!processedUntilMs.TryGetValue(entityId, out long until)) return false;
                return until > nowMs;
            }
            catch { return false; }
        }

        public static void Cull(long nowMs, int maxToScan = 64)
        {
            try
            {
                if (processedUntilMs.Count == 0 && thrownUntilMs.Count == 0) return;
                int scanned = 0;
                var toRemove = new List<long>();
                foreach (var kv in processedUntilMs)
                {
                    if (scanned++ >= maxToScan) break;
                    if (kv.Value <= nowMs) toRemove.Add(kv.Key);
                }
                for (int i = 0; i < toRemove.Count; i++) processedUntilMs.Remove(toRemove[i]);

                scanned = 0;
                toRemove.Clear();
                foreach (var kv in thrownUntilMs)
                {
                    if (scanned++ >= maxToScan) break;
                    if (kv.Value <= nowMs) toRemove.Add(kv.Key);
                }
                for (int i = 0; i < toRemove.Count; i++) thrownUntilMs.Remove(toRemove[i]);
            }
            catch { }
        }

        public static void Clear()
        {
            try { processedUntilMs.Clear(); } catch { }
            try { thrownUntilMs.Clear(); } catch { }
        }
    }
}
