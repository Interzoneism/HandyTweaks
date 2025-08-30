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
    ///  - Global pickup gate hook so Discard Mode and similar rules can block collection consistently.
    /// </summary>
    public static class HtPickupCore
    {
        // -------- Vanilla collector reflection --------
        private static Type TCollectBehavior;
        private static MethodInfo MiOnFoundCollectible;       // void OnFoundCollectible(Entity found)
        private static MethodInfo MiEntityGetBehaviorGeneric; // Entity.GetBehavior<T>()
        private static MethodInfo MiGetCollectorBehavior;     // Entity.GetBehavior<EntityBehaviorCollectEntities>()

        // Resolved once
        private static bool Resolved;

        // -------- Global pickup gate (e.g., Discard Mode) --------
        /// <summary>
        /// Return true to ALLOW pickup, false to BLOCK pickup. Called before we call vanilla collector.
        /// </summary>
        public static event global::System.Func<IServerPlayer, EntityItem, bool> GlobalPickupGate;

        // -------- De-duplication across features --------
        private const int DefaultProcessTtlMs = 1500;
        private static readonly Dictionary<long, long> processedUntilMs = new Dictionary<long, long>();

        /// <summary>
        /// Best-effort resolution of vanilla's collector behavior and the "OnFoundCollectible" entry point.
        /// Safe to call repeatedly; only resolves once.
        /// </summary>
        public static void ResolveMembers()
        {
            if (Resolved) return;

            // 1) Find the collector behavior type
            TCollectBehavior = Type.GetType("Vintagestory.GameContent.EntityBehaviorCollectEntities, Vintagestory");
            if (TCollectBehavior == null)
            {
                try
                {
                    foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                    {
                        Type[] types;
                        try { types = asm.GetTypes(); } catch { continue; }
                        foreach (var t in types)
                        {
                            if (t.IsAbstract || t.IsInterface) continue;
                            if (t.Name.IndexOf("Collect", StringComparison.OrdinalIgnoreCase) < 0) continue;
                            var bt = t.BaseType;
                            while (bt != null)
                            {
                                if (bt.FullName == "Vintagestory.API.Common.Entities.EntityBehavior")
                                {
                                    TCollectBehavior = t;
                                    break;
                                }
                                bt = bt.BaseType;
                            }
                            if (TCollectBehavior != null) break;
                        }
                        if (TCollectBehavior != null) break;
                    }
                }
                catch { /* best-effort */ }
            }

            // 2) Find OnFoundCollectible(Entity) on the behavior
            if (TCollectBehavior != null)
            {
                try
                {
                    MiOnFoundCollectible = TCollectBehavior.GetMethod(
                        "OnFoundCollectible",
                        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                        binder: null,
                        types: new[] { typeof(Entity) },
                        modifiers: null
                    );
                }
                catch { /* ignore */ }
            }

            // 3) Find Entity.GetBehavior<T>() and construct the generic for our collector type
            try
            {
                foreach (var mi in typeof(Entity).GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                {
                    if (!mi.IsGenericMethodDefinition) continue;
                    if (!string.Equals(mi.Name, "GetBehavior", StringComparison.Ordinal)) continue;
                    var pars = mi.GetParameters();
                    if (pars != null && pars.Length == 0)
                    {
                        MiEntityGetBehaviorGeneric = mi;
                        break;
                    }
                }
                if (MiEntityGetBehaviorGeneric != null && TCollectBehavior != null)
                {
                    MiGetCollectorBehavior = MiEntityGetBehaviorGeneric.MakeGenericMethod(TCollectBehavior);
                }
            }
            catch { /* ignore */ }

            Resolved = true;
        }

        /// <summary>Get the vanilla collector behavior from an entity (server-side player entity).</summary>
        private static object GetCollectBehavior(Entity e)
        {
            if (e == null || MiGetCollectorBehavior == null) return null;
            try { return MiGetCollectorBehavior.Invoke(e, null); }
            catch { return null; }
        }

        /// <summary>
        /// Ask the vanilla collector to collect this EntityItem for this player,
        /// but first run our global pickup gate and consult vanilla's CanCollect logic.
        /// Returns true if the item was (partially) collected.
        /// </summary>
        public static bool TryCollectViaBehavior(IServerPlayer sp, EntityItem ei)
        {
            if (sp?.Entity == null || ei == null || !ei.Alive) return false;

            // 0) External/global gate: Discard Mode etc.
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

            // 1) Ask vanilla's CanCollect on the item (Harmony in HtDiscardMode can block here)
            try
            {
                if (!ei.CanCollect(sp.Entity)) return false;
            }
            catch
            {
                // If the signature changes and throws, fail OPEN; GlobalPickupGate still protects us.
            }

            // 2) Invoke the vanilla "found collectible" hook to actually perform pickup
            var beh = GetCollectBehavior(sp.Entity);
            if (beh == null || MiOnFoundCollectible == null) return false;

            int before = ei.Itemstack?.StackSize ?? 0;
            try
            {
                MiOnFoundCollectible.Invoke(beh, new object[] { ei });
            }
            catch { /* ignore */ }

            // Success if entity died (consumed) or stack size decreased
            if (!ei.Alive) return true;
            int after = ei.Itemstack?.StackSize ?? 0;
            return after < before;
        }

        // ------------- De-duplication helpers -------------

        /// <summary>Mark an entity as processed until now + ttlMs (default 1.5 s).</summary>
        public static void MarkProcessed(long entityId, long nowMs, int ttlMs = DefaultProcessTtlMs)
        {
            try { processedUntilMs[entityId] = nowMs + Math.Max(100, ttlMs); } catch { }
        }

        /// <summary>True if the entity has been processed very recently (to avoid double work).</summary>
        public static bool WasJustProcessed(long entityId, long nowMs)
        {
            try
            {
                if (!processedUntilMs.TryGetValue(entityId, out long until)) return false;
                return until > nowMs;
            }
            catch { return false; }
        }

        /// <summary>Clean out expired dedupe entries. Call every few ticks.</summary>
        public static void Cull(long nowMs, int maxToScan = 64)
        {
            try
            {
                if (processedUntilMs.Count == 0) return;
                int scanned = 0;
                var toRemove = new List<long>();
                foreach (var kv in processedUntilMs)
                {
                    if (scanned++ >= maxToScan) break;
                    if (kv.Value <= nowMs) toRemove.Add(kv.Key);
                }
                for (int i = 0; i < toRemove.Count; i++) processedUntilMs.Remove(toRemove[i]);
            }
            catch { }
        }

        /// <summary>Clear all dedupe entries (optional; TTL-based culling makes this unnecessary).</summary>
        public static void Clear()
        {
            try { processedUntilMs.Clear(); } catch { }
        }
    }
}
