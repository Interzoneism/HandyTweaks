using System;
using System.Collections.Generic;
using System.Reflection;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Server;

namespace HandyTweaks.Internal
{

    public static class HtPickupCore
    {
        private static Type TCollectBehavior;
        private static MethodInfo MiOnFoundCollectible;       
        private static MethodInfo MiEntityGetBehaviorGeneric; 
        private static MethodInfo MiGetCollectorBehavior;     


        private static bool Resolved;

        public static event global::System.Func<IServerPlayer, EntityItem, bool> GlobalPickupGate;

        private const int DefaultProcessTtlMs = 1500;
        private static readonly Dictionary<long, long> processedUntilMs = new Dictionary<long, long>();

        public static void ResolveMembers()
        {
            if (Resolved) return;

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

        private static object GetCollectBehavior(Entity e)
        {
            if (e == null || MiGetCollectorBehavior == null) return null;
            try { return MiGetCollectorBehavior.Invoke(e, null); }
            catch { return null; }
        }

        public static bool TryCollectViaBehavior(IServerPlayer sp, EntityItem ei)
        {
            if (sp?.Entity == null || ei == null || !ei.Alive) return false;

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

            var beh = GetCollectBehavior(sp.Entity);
            if (beh == null || MiOnFoundCollectible == null) return false;

            int before = ei.Itemstack?.StackSize ?? 0;
            try
            {
                MiOnFoundCollectible.Invoke(beh, new object[] { ei });
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

        public static void Clear()
        {
            try { processedUntilMs.Clear(); } catch { }
        }
    }
}
