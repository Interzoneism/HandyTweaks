using System;
using System.Linq;
using System.Reflection;
using System.Collections.Generic;
using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.API.Client;
using Vintagestory.GameContent; // EntityItem

namespace HandyTweaks.Features
{
    public class HtDiscardMode : ModSystem
    {
        private const string DropperAttr = "handytweaks:dropperUid";
        private const string DropperEpochAttr = "handytweaks:dropperEpoch";
        private const string DropRunAttr = "handytweaks:dropRunId";
        private const string CmdRoot = "htdiscard";

        private ICoreServerAPI sapi;
        private ICoreClientAPI capi;
        private Harmony harmony;

        private static readonly HashSet<string> enabled = new(StringComparer.Ordinal);
        private static readonly Dictionary<string, HashSet<string>> blockedByPlayerUid = new(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, int> epochByPlayerUid = new(StringComparer.Ordinal);

        private static bool CfgAllowSneakBypass = true;

        private static int serverRunId;

        [ThreadStatic] private static int tsMarkSpawnDepth;
        [ThreadStatic] private static string tsDropperUid;
        [ThreadStatic] private static int tsDropperEpoch;

        private static Type TInventoryPlayerGround;

        public override void StartServerSide(ICoreServerAPI api)
        {
            sapi = api;

            // Load config
            try
            {
                HandyTweaks.HtShared.EnsureLoaded(api);
                var cfg = HandyTweaks.HtShared.Config?.DiscardMode;
                if (cfg != null)
                {
                    if (!cfg.Enabled)
                    {
                        sapi.Logger.Event("[HandyTweaks:DiscardMode] Disabled via config.");
                        return;
                    }
                    CfgAllowSneakBypass = cfg.AllowSneakBypass;
                }
            }
            catch { /* ignore if config types not present */ }

            serverRunId = sapi.World.Rand.Next(1, int.MaxValue);

            harmony = new Harmony("com.martin.handytweaks.discardmode");

            Patch_GroundMoveWindow();
            Patch_PickupGate();
            RegisterServerCommands();

            sapi.Event.OnEntitySpawn += OnEntitySpawn;
            sapi.Event.PlayerLeave += OnPlayerLeave;

            sapi.Logger.Event("[HandyTweaks:DiscardMode] Server initialized");
        }

        public override void StartClientSide(ICoreClientAPI api)
        {
            capi = api;

            try
            {
                HandyTweaks.HtShared.EnsureLoaded(api);
                var cfg = HandyTweaks.HtShared.Config?.DiscardMode;
                if (cfg != null)
                {
                    if (!cfg.Enabled) return;
                    CfgAllowSneakBypass = cfg.AllowSneakBypass;
                }
            }
            catch { /* ignore */ }

            capi.Input.RegisterHotKey(
                "httoggle-discard",
                "HandyTweaks: Toggle Discard Mode",
                GlKeys.B,
                HotkeyType.GUIOrOtherControls
            );
            capi.Input.SetHotKeyHandler("httoggle-discard", _ =>
            {
                capi.SendChatMessage("/htdiscard toggle");
                return true;
            });

            capi.Logger.Event("[HandyTweaks:DiscardMode] Client hotkey registered (B)");
        }

        public override void Dispose()
        {
            try { if (sapi != null) { sapi.Event.OnEntitySpawn -= OnEntitySpawn; sapi.Event.PlayerLeave -= OnPlayerLeave; } } catch { }
            try { harmony?.UnpatchAll("com.martin.handytweaks.discardmode"); } catch { }
        }

        private void RegisterServerCommands()
        {
            var root = sapi.ChatCommands.Create(CmdRoot)
                .WithDescription("Toggle Discard Mode (blocks re-pickup of what you just threw and any identical items)")
                .RequiresPlayer().RequiresPrivilege(Privilege.chat);

            root.BeginSubCommand("on")
                .WithDescription("Enable Discard Mode")
                .HandleWith(ctx =>
                {
                    var uid = ctx.Caller.Player.PlayerUID;
                    enabled.Add(uid);
                    if (!epochByPlayerUid.ContainsKey(uid)) epochByPlayerUid[uid] = 1;
                    return TextCommandResult.Success("Discard Mode: ON");
                })
                .EndSubCommand();

            root.BeginSubCommand("off")
                .WithDescription("Disable Discard Mode and clear the blocklist")
                .HandleWith(ctx =>
                {
                    var uid = ctx.Caller.Player.PlayerUID;
                    int cleared = 0;
                    if (blockedByPlayerUid.TryGetValue(uid, out var set))
                    {
                        cleared = set.Count;
                        set.Clear();
                    }
                    enabled.Remove(uid);
                    return cleared > 0
                        ? TextCommandResult.Success($"Discard Mode: OFF ({cleared} items cleared)")
                        : TextCommandResult.Success("Discard Mode: OFF");
                })
                .EndSubCommand();

            root.BeginSubCommand("toggle")
                .WithDescription("Toggle Discard Mode")
                .HandleWith(ctx =>
                {
                    var uid = ctx.Caller.Player.PlayerUID;
                    if (enabled.Contains(uid))
                    {
                        int cleared = 0;
                        if (blockedByPlayerUid.TryGetValue(uid, out var set))
                        {
                            cleared = set.Count;
                            set.Clear();
                        }
                        enabled.Remove(uid);
                        return cleared > 0
                            ? TextCommandResult.Success($"Discard Mode: OFF ({cleared} items cleared)")
                            : TextCommandResult.Success("Discard Mode: OFF");
                    }
                    else
                    {
                        enabled.Add(uid);
                        if (!epochByPlayerUid.ContainsKey(uid)) epochByPlayerUid[uid] = 1;
                        return TextCommandResult.Success("Discard Mode: ON");
                    }
                })
                .EndSubCommand();

            root.BeginSubCommand("status")
                .WithDescription("Show current Discard Mode status")
                .HandleWith(ctx =>
                {
                    var uid = ctx.Caller.Player.PlayerUID;
                    bool on = enabled.Contains(uid);
                    int blocked = blockedByPlayerUid.TryGetValue(uid, out var set) ? set.Count : 0;
                    return TextCommandResult.Success(on ? $"ON ({blocked} items blocked)" : "OFF");
                })
                .EndSubCommand();
        }

        private void OnPlayerLeave(IServerPlayer player)
        {
            var uid = player?.PlayerUID;
            if (string.IsNullOrEmpty(uid)) return;
            enabled.Remove(uid);
            blockedByPlayerUid.Remove(uid);
            epochByPlayerUid.Remove(uid);

            if (tsDropperUid == uid)
            {
                tsMarkSpawnDepth = 0;
                tsDropperUid = null;
                tsDropperEpoch = 0;
            }
        }

        private void Patch_GroundMoveWindow()
        {
            var method = typeof(ItemSlot).GetMethod(
                "TryPutInto",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                binder: null,
                types: new Type[] { typeof(ItemSlot), typeof(ItemStackMoveOperation).MakeByRefType() },
                modifiers: null
            );

            if (method == null)
            {
                sapi.Logger.Warning("[HandyTweaks:DiscardMode] Could not find ItemSlot.TryPutInto signature.");
                return;
            }

            var pre = new HarmonyMethod(typeof(HtDiscardMode), nameof(TryPutInto_Prefix)) { priority = Priority.First };
            var fin = new HarmonyMethod(typeof(HtDiscardMode), nameof(TryPutInto_Finalizer));

            harmony.Patch(method, prefix: pre, finalizer: fin);
            sapi.Logger.Event("[HandyTweaks:DiscardMode] Patched ItemSlot.TryPutInto (prefix+finalizer)");
        }

        public static void TryPutInto_Prefix(ItemSlot __instance, ItemSlot sinkSlot, ref ItemStackMoveOperation op, ref string __state)
        {
            __state = null;
            try
            {
                var inv = sinkSlot?.Inventory;
                if (inv == null) return;

                if (TInventoryPlayerGround == null)
                    TInventoryPlayerGround = AccessTools.TypeByName("Vintagestory.Common.InventoryPlayerGround");

                bool isGround = TInventoryPlayerGround != null && TInventoryPlayerGround.IsInstanceOfType(inv);
                if (!isGround)
                {
                    var cname = inv.ClassName ?? string.Empty;
                    var iid = inv.InventoryID ?? string.Empty;
                    if (!string.Equals(cname, "playerground", StringComparison.OrdinalIgnoreCase) &&
                        !string.Equals(iid, "playerground", StringComparison.OrdinalIgnoreCase))
                    {
                        return;
                    }
                }

                var sp = op?.ActingPlayer as IServerPlayer;
                var uid = sp?.PlayerUID;
                if (string.IsNullOrEmpty(uid) || !enabled.Contains(uid)) return;

                if (tsMarkSpawnDepth == 0) tsDropperEpoch = NextEpoch(uid);

                tsMarkSpawnDepth++;
                tsDropperUid = uid;
                __state = uid;

                var code = CodeOf(__instance?.Itemstack);
                if (!string.IsNullOrEmpty(code))
                {
                    if (!blockedByPlayerUid.TryGetValue(uid, out var set))
                    {
                        set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        blockedByPlayerUid[uid] = set;
                    }
                    set.Add(code);
                }
            }
            catch { }
        }

        public static void TryPutInto_Finalizer(string __state, Exception __exception)
        {
            try
            {
                if (__state != null)
                {
                    if (__exception != null || tsMarkSpawnDepth <= 1)
                    {
                        tsMarkSpawnDepth = 0;
                        tsDropperUid = null;
                        tsDropperEpoch = 0;
                    }
                    else
                    {
                        tsMarkSpawnDepth--;
                    }
                }
                if (tsMarkSpawnDepth < 0) tsMarkSpawnDepth = 0;
            }
            catch
            {
                tsMarkSpawnDepth = 0;
                tsDropperUid = null;
                tsDropperEpoch = 0;
            }
        }

        private void Patch_PickupGate()
        {
            var canCollect = typeof(EntityItem).GetMethod(
                "CanCollect",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                binder: null,
                types: new Type[] { typeof(Entity) },
                modifiers: null
            );

            if (canCollect == null)
            {
                sapi.Logger.Warning("[HandyTweaks:DiscardMode] Could not find EntityItem.CanCollect(Entity).");
                return;
            }

            var pre = new HarmonyMethod(typeof(HtDiscardMode), nameof(CanCollect_Prefix)) { priority = Priority.First };
            harmony.Patch(canCollect, prefix: pre);
            sapi.Logger.Event("[HandyTweaks:DiscardMode] Patched EntityItem.CanCollect");
        }

        public static bool CanCollect_Prefix(EntityItem __instance, Entity byEntity, ref bool __result)
        {
            try
            {
                if (byEntity is not EntityPlayer ep) return true;

                if (CfgAllowSneakBypass && ep.Controls?.Sneak == true)
                    return true;

                var uid = ep.Player?.PlayerUID;
                if (string.IsNullOrEmpty(uid) || !enabled.Contains(uid)) return true;

                var wattr = __instance.WatchedAttributes;
                var dropper = wattr?.GetString(DropperAttr);
                int entEpoch = wattr?.GetInt(DropperEpochAttr) ?? -1;
                int entRun = wattr?.GetInt(DropRunAttr) ?? 0;
                bool sameRun = entRun == serverRunId;

                if (sameRun && !string.IsNullOrEmpty(dropper) && dropper == uid && entEpoch == CurrentEpoch(uid))
                {
                    __result = false;
                    return false;
                }

                var code = CodeOf(__instance.Itemstack);
                if (!string.IsNullOrEmpty(code)
                    && blockedByPlayerUid.TryGetValue(uid, out var set)
                    && set.Contains(code))
                {
                    __result = false;
                    return false;
                }
            }
            catch { }

            return true;
        }


        private void OnEntitySpawn(Entity entity)
        {
            try
            {
                if (tsMarkSpawnDepth <= 0 || string.IsNullOrEmpty(tsDropperUid)) return;
                if (entity is not EntityItem ei) return;

                var wattr = ei.WatchedAttributes;
                if (wattr == null)
                {
                    wattr = new SyncedTreeAttribute();
                    ei.WatchedAttributes = wattr;
                }

                wattr.SetString(DropperAttr, tsDropperUid);
                wattr.SetInt(DropperEpochAttr, tsDropperEpoch);
                wattr.SetInt(DropRunAttr, serverRunId);
            }
            catch { }
        }

        private static string CodeOf(ItemStack stack) => stack?.Collectible?.Code?.ToString();

        private static int CurrentEpoch(string uid) =>
            epochByPlayerUid.TryGetValue(uid, out var e) ? e : 0;

        private static int NextEpoch(string uid) =>
            epochByPlayerUid[uid] = CurrentEpoch(uid) + 1;

        public static bool IsBlockedFor(IServerPlayer sp, EntityItem ei)
        {
            try
            {
                if (sp == null || ei == null) return false;

                var eplayer = sp.Entity as EntityPlayer;

                if (CfgAllowSneakBypass && eplayer?.Controls?.Sneak == true)
                    return false;

                var uid = sp.PlayerUID;
                if (string.IsNullOrEmpty(uid) || !enabled.Contains(uid)) return false;

                var wattr = ei.WatchedAttributes;
                var dropper = wattr?.GetString(DropperAttr);
                int entEpoch = wattr?.GetInt(DropperEpochAttr) ?? -1;
                int entRun = wattr?.GetInt(DropRunAttr) ?? 0;
                bool sameRun = entRun == serverRunId;

                if (sameRun && !string.IsNullOrEmpty(dropper) && dropper == uid && entEpoch == CurrentEpoch(uid))
                    return true;

                var code = CodeOf(ei.Itemstack);
                return !string.IsNullOrEmpty(code)
                    && blockedByPlayerUid.TryGetValue(uid, out var set)
                    && set.Contains(code);
            }
            catch { return false; }
        }
    }
}
