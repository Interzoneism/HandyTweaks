using HandyTweaks.Internal;
using HarmonyLib;
using System;
using System.Collections.Generic;
using System.Reflection;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;

namespace HandyTweaks.Features
{
    /// <summary>
    /// FastPickupPlus: near-instant pickup of fresh block drops by the breaking player.
    /// Honors Discard Mode by going through HtPickupCore.TryCollectViaBehavior(), which
    /// consults the global gate and vanilla CanCollect (Harmony-patched on your side).
    ///
    /// Public controls come from HtConfig.FastPickup:
    ///   - FreshDropWindowMs: how long a spawn counts as “fresh” after break
    ///   - FreshDropRadiusBlocks: scan radius around the break center
    ///   - ForceAgeMs: age override so CanCollect passes immediately (if permitted)
    ///
    /// Implementation notes:
    ///   - We patch Block.OnBlockBroken to open a short-lived window per break
    ///   - A lightweight server tick scans for EntityItem within the radius
    ///   - We set the spawn age, then call HtPickupCore.TryCollectViaBehavior(sp, e)
    ///   - We only de-dupe on success, so PRB can still try if FPP was blocked
    /// </summary>
    public class FastPickupPlus : ModSystem
    {
        private Harmony harmony;
        private static ICoreServerAPI Sapi;

        // Reflection: only the spawned timestamp is needed
        private static FieldInfo FiItemSpawnedMs; // public long; name varies by VS build

        // Tunables (from config)
        private static int FreshDropWindowMs;
        private static float ScanRadiusBlocks;
        private static int ForceAgeMs;

        // Internal helper sweep duration (player-centric reach widen), not exposed in config
        private const int HiddenBoostDurationMs = 1500;

        // Require player within this distance (same as scan radius)
        private static double RequireWithinDist => ScanRadiusBlocks;

        // Break window state
        private struct BreakWindow
        {
            public Vec3d Center;
            public string OwnerUid;
            public long StartMs;
            public long ExpireMs;
        }

        private static readonly List<BreakWindow> Windows = new();
        private static long TickId;

        public override void Start(ICoreAPI api)
        {
            base.Start(api);

            // Load config and bail if disabled
            HandyTweaks.HtShared.EnsureLoaded(api);
            var cfg = HandyTweaks.HtShared.Config.FastPickup;
            if (cfg == null || !cfg.Enabled) return;

            // Clamp user-facing knobs to safe ranges
            FreshDropWindowMs = Math.Max(200, Math.Min(2000, cfg.FreshDropWindowMs));
            ScanRadiusBlocks = Math.Max(0.9f, Math.Min(40.0f, cfg.FreshDropRadiusBlocks));
            ForceAgeMs = Math.Max(1100, cfg.ForceAgeMs); // must exceed vanilla "too fresh" threshold

            harmony = new Harmony("handytweaks.fastpickup.behaviorpath.positional");

            // Minimal reflection targets
            ResolveSpawnedMsField();

            // Make sure core helpers are ready (global gate, de-dupe, behavior path)
            HtPickupCore.ResolveMembers();

            // Patch base Block.OnBlockBroken (virtual)
            PatchOnBlockBroken(harmony);
        }

        public override void StartServerSide(ICoreServerAPI sapi) => Sapi = sapi;

        public override void Dispose()
        {
            try { harmony?.UnpatchAll("handytweaks.fastpickup.behaviorpath.positional"); } catch { }
            if (Sapi != null && TickId != 0)
            {
                Sapi.World.UnregisterGameTickListener(TickId);
                TickId = 0;
            }
            Windows.Clear();
            HtPickupCore.Clear();
        }

        private static void PatchOnBlockBroken(Harmony h)
        {
            try
            {
                var flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
                var mi = typeof(Block).GetMethod("OnBlockBroken", flags);
                if (mi != null) h.Patch(mi, postfix: new HarmonyMethod(typeof(FastPickupPlus), nameof(AfterOnBlockBroken_Postfix)));
            }
            catch { /* best effort */ }
        }

        /// <summary>
        /// Harmony postfix positional args: __0 world, __1 pos, __2 byPlayer, __3 dropQuantityMultiplier (ignored)
        /// </summary>
        public static void AfterOnBlockBroken_Postfix(object __instance, IWorldAccessor __0, BlockPos __1, IPlayer __2, float __3)
        {
            var world = __0;
            var pos = __1;
            var byPlayer = __2;

            if (world == null || byPlayer == null || world.Side != EnumAppSide.Server) return;
            if (Sapi == null) return;
            if (FiItemSpawnedMs == null) return;

            long now = world.ElapsedMilliseconds;
            Vec3d center = pos.ToVec3d().Add(0.5, 0.5, 0.5);

            Windows.Add(new BreakWindow
            {
                Center = center,
                OwnerUid = byPlayer.PlayerUID,
                StartMs = now,
                ExpireMs = now + FreshDropWindowMs
            });

            // Hidden helper: brief player-centric sweep matching the same radius
            PickupRangeBoost.Activate(byPlayer, ScanRadiusBlocks, HiddenBoostDurationMs, now, now + FreshDropWindowMs);

            if (TickId == 0)
            {
                TickId = Sapi.World.RegisterGameTickListener(ServerTick, 50); // 20 Hz
            }
        }

        private static void ServerTick(float dt)
        {
            if (Sapi == null) { StopTick(); return; }

            // Cull stale de-dupe entries
            HtPickupCore.Cull(Sapi.World.ElapsedMilliseconds);

            if (FiItemSpawnedMs == null)
            {
                StopTick(); return;
            }

            long now = Sapi.World.ElapsedMilliseconds;

            // Drop expired windows
            for (int i = Windows.Count - 1; i >= 0; i--)
            {
                if (now > Windows[i].ExpireMs) Windows.RemoveAt(i);
            }

            if (Windows.Count == 0)
            {
                HtPickupCore.Clear();
                StopTick();
                return;
            }

            // Process each window
            for (int widx = 0; widx < Windows.Count; widx++)
            {
                var w = Windows[widx];
                var sp = Sapi.World.PlayerByUid(w.OwnerUid) as IServerPlayer;
                if (sp?.Entity == null) continue;

                // Respect "sneak to pick up" servers
                if (sp.ItemCollectMode == 1)
                {
                    var agent = sp.Entity as EntityAgent;
                    bool sneaking = agent != null && agent.Controls != null && agent.Controls.Sneak;
                    if (!sneaking) continue;
                }

                Entity[] entities;
                try { entities = Sapi.World.GetEntitiesAround(w.Center, ScanRadiusBlocks, ScanRadiusBlocks); }
                catch { continue; }
                if (entities == null || entities.Length == 0) continue;

                for (int i = 0; i < entities.Length; i++)
                {
                    var e = entities[i] as EntityItem;
                    if (e == null || !e.Alive) continue;
                    if (e.Itemstack == null || e.Itemstack.StackSize <= 0) continue;

                    long spawned = GetSpawnedMs(e);
                    if (spawned < 0) continue;
                    if (spawned < w.StartMs) continue; // only brand-new drops
                    if (HtPickupCore.WasJustProcessed(e.EntityId, now)) continue;

                    double dist2 = Dist2(sp.Entity.ServerPos, e.ServerPos);
                    if (dist2 > RequireWithinDist * RequireWithinDist) continue;

                    // Age so vanilla CanCollect() passes immediately (if permitted)
                    SetSpawnedMs(e, now - ForceAgeMs);

                    // Respect Discard Mode / global gate by going through the core
                    if (HtPickupCore.TryCollectViaBehavior(sp, e))
                    {
                        HtPickupCore.MarkProcessed(e.EntityId, now); // de-dupe only on success
                    }
                }
            }
        }

        private static void StopTick()
        {
            if (Sapi != null && TickId != 0)
            {
                Sapi.World.UnregisterGameTickListener(TickId);
                TickId = 0;
            }
        }

        // ---------------- reflection helpers: spawned timestamp only ----------------

        private static void ResolveSpawnedMsField()
        {
            try
            {
                // VS builds expose a public long; name can vary
                FiItemSpawnedMs =
                    typeof(EntityItem).GetField("itemSpawnedMilliseconds", BindingFlags.Instance | BindingFlags.Public) ??
                    typeof(EntityItem).GetField("spawnedMs", BindingFlags.Instance | BindingFlags.Public) ??
                    typeof(EntityItem).GetField("spawnMs", BindingFlags.Instance | BindingFlags.Public);

                if (FiItemSpawnedMs == null)
                {
                    // Heuristic fallback
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
            if (FiItemSpawnedMs == null) return -1;
            try
            {
                var v = FiItemSpawnedMs.GetValue(ei);
                if (v is long l) return l;
                if (v is int i) return i;
            }
            catch { }
            return -1;
        }

        private static void SetSpawnedMs(EntityItem ei, long value)
        {
            if (FiItemSpawnedMs == null) return;
            try { FiItemSpawnedMs.SetValue(ei, value); } catch { }
        }

        private static double Dist2(EntityPos a, EntityPos b)
        {
            double dx = a.X - b.X, dy = a.Y - b.Y, dz = a.Z - b.Z;
            return dx * dx + dy * dy + dz * dz;
        }
    }
}
