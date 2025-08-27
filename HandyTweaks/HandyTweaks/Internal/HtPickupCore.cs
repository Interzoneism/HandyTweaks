using HarmonyLib;
using System;
using System.Collections.Generic;
using System.Reflection;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Server;

namespace HandyTweaks.Internal
{
    /// <summary>
    /// Shared helpers for HandyTweaks pickup features:
    ///  - Reflection bridge into the vanilla collector (EntityBehaviorCollectEntities)
    ///  - Small TTL-based de-duplication across features (FastPickupPlus + PickupRangeBoost)
    /// </summary>
    public static class HtPickupCore
    {
        // -------- Vanilla collector reflection --------
        private static Type TCollectBehavior;                 // EntityBehaviorCollectEntities
        private static MethodInfo MiOnFoundCollectible;       // void OnFoundCollectible(Entity)
        private static MethodInfo MiEntityGetBehaviorGeneric; // Entity.GetBehavior<T>()
        private static bool resolved;

        public static void ResolveMembers()
        {
            if (resolved) return;
            resolved = true;

            try
            {
                TCollectBehavior =
                    AccessTools.TypeByName("Vintagestory.API.Common.Entities.EntityBehaviorCollectEntities") ??
                    AccessTools.TypeByName("Vintagestory.GameContent.EntityBehaviorCollectEntities") ??
                    AccessTools.TypeByName("EntityBehaviorCollectEntities");

                if (TCollectBehavior != null)
                {
                    MiOnFoundCollectible = TCollectBehavior.GetMethod(
                        "OnFoundCollectible",
                        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                        null,
                        new[] { typeof(Entity) },
                        null
                    );
                }

                foreach (var mi in typeof(Entity).GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                {
                    if (mi.Name == "GetBehavior" && mi.IsGenericMethodDefinition && mi.GetParameters().Length == 0)
                    {
                        MiEntityGetBehaviorGeneric = mi;
                        break;
                    }
                }
            }
            catch { /* soft-fail; TryCollectViaBehavior will return false */ }
        }

        private static object GetCollectBehavior(Entity ent)
        {
            if (ent == null || TCollectBehavior == null || MiEntityGetBehaviorGeneric == null) return null;
            try
            {
                return MiEntityGetBehaviorGeneric.MakeGenericMethod(TCollectBehavior).Invoke(ent, null);
            }
            catch { return null; }
        }

        /// <summary>
        /// Ask vanilla collector to try picking up the entity (no magnetizing).
        /// Returns true if entity likely changed (stack shrank or entity despawned).
        /// </summary>
        public static bool TryCollectViaBehavior(IServerPlayer sp, EntityItem ei)
        {
            if (sp?.Entity == null || ei == null || !ei.Alive) return false;
            var beh = GetCollectBehavior(sp.Entity);
            if (beh == null || MiOnFoundCollectible == null) return false;

            int before = ei.Itemstack?.StackSize ?? 0;
            try { MiOnFoundCollectible.Invoke(beh, new object[] { ei }); } catch { }
            return !ei.Alive || (ei.Itemstack?.StackSize ?? 0) < before;
        }

        // -------- Global de-duplication (TTL-based) --------
        // Map: EntityId -> expireAtMs (server elapsed milliseconds)
        private static readonly Dictionary<long, long> processedUntilMs = new();

        /// <summary>Return true if this entity was touched recently (still within TTL).</summary>
        public static bool WasJustProcessed(long entityId, long nowMs)
        {
            if (entityId == 0) return false;
            if (processedUntilMs.TryGetValue(entityId, out var until)) return until > nowMs;
            return false;
        }

        /// <summary>Mark an entity as processed for a short TTL (default ~1.5s).</summary>
        public static void MarkProcessed(long entityId, long nowMs, int ttlMs = 1500)
        {
            if (entityId == 0) return;
            long until = nowMs + Math.Max(200, ttlMs);
            if (processedUntilMs.TryGetValue(entityId, out var cur))
            {
                if (until > cur) processedUntilMs[entityId] = until;
            }
            else
            {
                processedUntilMs[entityId] = until;
            }
        }

        /// <summary>Remove stale entries. Call occasionally from your tick loops.</summary>
        public static void Cull(long nowMs)
        {
            if (processedUntilMs.Count == 0) return;
            const int maxToScan = 128;  // keep it cheap
            int scanned = 0;
            var toRemove = new List<long>(16);
            foreach (var kv in processedUntilMs)
            {
                if (scanned++ >= maxToScan) break;
                if (kv.Value <= nowMs) toRemove.Add(kv.Key);
            }
            for (int i = 0; i < toRemove.Count; i++) processedUntilMs.Remove(toRemove[i]);
        }

        /// <summary>Clear all dedupe entries (optional; TTL-based culling makes this unnecessary).</summary>
        public static void Clear()
        {
            processedUntilMs.Clear();
        }
    }
}
