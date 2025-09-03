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
    public class FastPickupPlus : ModSystem
    {
        private Harmony harmony;

        private static ICoreServerAPI Sapi;

        private static FieldInfo FiItemSpawnedMs;

        private static int FreshDropWindowMs;
        private static float ScanRadiusBlocks;
        private static int ForceAgeMs;
        private static int PickupDelayMs;

        private const int HiddenBoostDurationMs = 1500;

        private static double RequireWithinDist => ScanRadiusBlocks;

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

            HandyTweaks.HtShared.EnsureLoaded(api);
            var cfg = HandyTweaks.HtShared.Config.FastPickup;
            if (cfg == null || !cfg.Enabled) return;

            FreshDropWindowMs = 1200;
            ForceAgeMs = 1500; // must exceed vanilla "too fresh" threshold (~1s)

            ScanRadiusBlocks = Math.Max(0.9f, Math.Min(40.0f, cfg.FreshDropRadiusBlocks));
            PickupDelayMs = Math.Max(0, Math.Min(4000, cfg.PickupDelayMs));

            harmony = new Harmony("handytweaks.fastpickup.behaviorpath.positional");

            ResolveSpawnedMsField();

            HtPickupCore.ResolveMembers();

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
            var flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            var sig = new[] { typeof(IWorldAccessor), typeof(BlockPos), typeof(IPlayer), typeof(float) };
            var postfix = new HarmonyMethod(typeof(FastPickupPlus), nameof(AfterOnBlockBroken_Postfix));
            var patched = new HashSet<MethodBase>();

            try
            {
                var baseMi = AccessTools.Method(typeof(Block), "OnBlockBroken", sig);
                if (baseMi != null)
                {
                    h.Patch(baseMi, postfix: postfix);
                    patched.Add(baseMi);
                    Sapi?.World.Logger.Event("[FPP] Patched base Block.OnBlockBroken");
                }
            }
            catch { /* best effort */ }

            try
            {
                var tReeds = AccessTools.TypeByName("Vintagestory.GameContent.BlockReeds");
                if (tReeds != null)
                {
                    var miReeds = AccessTools.Method(tReeds, "OnBlockBroken", sig);
                    if (miReeds != null && !patched.Contains(miReeds))
                    {
                        h.Patch(miReeds, postfix: postfix);
                        patched.Add(miReeds);
                        Sapi?.World.Logger.Event("[FPP] Patched BlockReeds.OnBlockBroken");
                    }
                }
            }
            catch { /* best effort */ }

            try
            {
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    if (asm.IsDynamic) continue;

                    Type[] types;
                    try { types = asm.GetTypes(); }
                    catch { continue; }

                    foreach (var t in types)
                    {
                        if (t == null || t.IsAbstract) continue;
                        if (!typeof(Block).IsAssignableFrom(t)) continue;

                        MethodInfo mi;
                        try { mi = t.GetMethod("OnBlockBroken", flags, null, sig, null); }
                        catch { continue; }

                        if (mi == null) continue;

                        var baseDef = mi.GetBaseDefinition();
                        if (baseDef == null || baseDef.DeclaringType != typeof(Block)) continue;

                        if (patched.Contains(mi)) continue;

                        try
                        {
                            h.Patch(mi, postfix: postfix);
                            patched.Add(mi);
                            Sapi?.World.Logger.Event("[FPP] Patched override: " + t.FullName);
                        }
                        catch { /* best effort */ }
                    }
                }
            }
            catch { /* best effort */ }
        }

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

            int windowMs = Math.Max(FreshDropWindowMs, PickupDelayMs + 250);

            Windows.Add(new BreakWindow
            {
                Center = center,
                OwnerUid = byPlayer.PlayerUID,
                StartMs = now,
                ExpireMs = now + windowMs
            });

            PickupRangeBoost.Activate(byPlayer, ScanRadiusBlocks, HiddenBoostDurationMs, now, now + windowMs);

            if (TickId == 0)
            {
                TickId = Sapi.World.RegisterGameTickListener(ServerTick, 50); // 20 Hz
            }
        }

        private static void ServerTick(float dt)
        {
            if (Sapi == null) { StopTick(); return; }

            HtPickupCore.Cull(Sapi.World.ElapsedMilliseconds);

            if (FiItemSpawnedMs == null)
            {
                StopTick(); return;
            }

            long now = Sapi.World.ElapsedMilliseconds;

            for (int i = Windows.Count - 1; i >= 0; i--)
            {
                if (now > Windows[i].ExpireMs) Windows.RemoveAt(i);
            }
            if (Windows.Count == 0) { StopTick(); return; }

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

                    if (PickupDelayMs > 0 && now - spawned < PickupDelayMs)
                        continue;

                    double dist2 = Dist2(sp.Entity.ServerPos, e.ServerPos);
                    if (dist2 > RequireWithinDist * RequireWithinDist) continue;

                    if (HandyTweaks.Features.HtDiscardMode.IsBlockedFor(sp, e))
                        continue;

                    SetSpawnedMs(e, now - ForceAgeMs);

                    if (HtPickupCore.TryCollectViaBehavior(sp, e))
                    {
                        HtPickupCore.MarkProcessed(e.EntityId, now);
                    }
                }
            }
        }

        private static void StopTick()
        {
            if (Sapi != null && TickId != 0)
            {
                Sapi.World.UnregisterGameTickListener(TickId);
            }
            TickId = 0;
        }


        private static void ResolveSpawnedMsField()
        {
            try
            {
                foreach (var fi in typeof(EntityItem).GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                {
                    if (fi.FieldType == typeof(long))
                    {
                        var name = fi.Name.ToLowerInvariant();
                        if (name.Contains("spawn"))
                        {
                            FiItemSpawnedMs = fi;
                            return;
                        }
                        FiItemSpawnedMs ??= fi;
                    }
                }
            }
            catch { /* ignore */ }
        }

        private static long GetSpawnedMs(EntityItem ei)
        {
            if (FiItemSpawnedMs == null) return -1;
            try { return (long)FiItemSpawnedMs.GetValue(ei); } catch { return -1; }
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
