// File: Features/HtDiscardMode.cs
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
using Vintagestory.API.Common.CommandAbbr;
using HandyTweaks.Internal;     // HtPickupCore

namespace HandyTweaks.Features
{
    public class HtDiscardMode : ModSystem
    {
        // ----- Attributes on EntityItem -----
        private const string DropperAttr = "handytweaks:dropperUid";
        private const string DropperEpochAttr = "handytweaks:dropperEpoch";

        // ----- Commands -----
        private const string CmdRoot = "htdiscard";

        // ----- Server fields -----
        private ICoreServerAPI sapi;
        private Harmony harmony;

        // Players with mode enabled
        private static readonly HashSet<string> enabled = new(StringComparer.Ordinal);

        // Per-player blacklist of item codes
        private static readonly Dictionary<string, HashSet<string>> blockedByPlayerUid =
            new(StringComparer.OrdinalIgnoreCase);

        // Per-player session/epoch (increments on OFF)
        private static readonly Dictionary<string, int> epochByPlayerUid =
            new(StringComparer.Ordinal);

        // Marking window (thread-local)
        [ThreadStatic] private static int tsMarkSpawnDepth;
        [ThreadStatic] private static string tsDropperUid;
        [ThreadStatic] private static int tsDropperEpoch;

        // ----- Client -----
        private ICoreClientAPI capi;

        public override void StartServerSide(ICoreServerAPI api)
        {
            sapi = api;
            harmony = new Harmony("com.martin.handytweaks.discardmode");

            // Patches
            Patch_GroundMoveWindow(); // open/close mark window + add type to blacklist
            Patch_PickupGate();       // block pickup in vanilla path

            RegisterServerCommands();

            // Hook global pickup gate
            try
            {
                HtPickupCore.ResolveMembers();
                HtPickupCore.GlobalPickupGate += GlobalGate;
            }
            catch (Exception e)
            {
                sapi?.Logger.Warning("[HandyTweaks:DiscardMode] Failed to hook GlobalPickupGate: {0}", e);
            }

            // Instead of patching SpawnItemEntity (abstract in interface), use server spawn event
            sapi.Event.OnEntitySpawn += OnEntitySpawn;

            sapi.Event.PlayerLeave += OnPlayerLeave;
            sapi.Logger.Event("[HandyTweaks:DiscardMode] Server initialized");
        }

        public override void StartClientSide(ICoreClientAPI api)
        {
            capi = api;

            capi.Input.RegisterHotKey(
                "httoggle-discard",
                "HandyTweaks: Toggle Discard Mode",
                GlKeys.B,
                HotkeyType.GUIOrOtherControls
            );
            capi.Input.SetHotKeyHandler("httoggle-discard", _ =>
            {
                capi.SendChatMessage("/htdiscard toggle");
                capi.ShowChatMessage("[HandyTweaks] Toggling Discard Mode...");
                return true;
            });

            capi.Input.RegisterHotKey(
                "httoggle-discard-world",
                "HandyTweaks: Toggle Discard Mode (World)",
                GlKeys.B,
                HotkeyType.CharacterControls
            );
            capi.Input.SetHotKeyHandler("httoggle-discard-world", _ =>
            {
                capi.SendChatMessage("/htdiscard toggle");
                return true;
            });

            capi.Logger.Event("[HandyTweaks:DiscardMode] Client hotkeys registered (B)");
        }

        public override void Dispose()
        {
            try { harmony?.UnpatchAll("com.martin.handytweaks.discardmode"); } catch { }
            try { HtPickupCore.GlobalPickupGate -= GlobalGate; } catch { }
            try { if (sapi != null) sapi.Event.OnEntitySpawn -= OnEntitySpawn; } catch { }
        }

        // ===== Commands =====
        private void RegisterServerCommands()
        {
            var parsers = sapi.ChatCommands.Parsers;

            var root = sapi.ChatCommands
                .Create(CmdRoot)
                .WithDescription("HandyTweaks: Toggle/inspect Discard Mode")
                .RequiresPlayer()
                .RequiresPrivilege(Privilege.chat);

            root.BeginSubCommand("on")
                .WithDescription("Enable Discard Mode")
                .RequiresPlayer().RequiresPrivilege(Privilege.chat)
                .HandleWith(ctx =>
                {
                    var uid = ctx.Caller.Player.PlayerUID;
                    enabled.Add(uid);
                    if (!epochByPlayerUid.ContainsKey(uid)) epochByPlayerUid[uid] = 1;
                    return TextCommandResult.Success("[HandyTweaks] Discard Mode: ON");
                })
                .EndSubCommand();

            root.BeginSubCommand("off")
                .WithDescription("Disable Discard Mode and clear the blocklist")
                .RequiresPlayer().RequiresPrivilege(Privilege.chat)
                .HandleWith(ctx =>
                {
                    var uid = ctx.Caller.Player.PlayerUID;
                    int cleared = blockedByPlayerUid.TryGetValue(uid, out var set) ? set.Count : 0;
                    enabled.Remove(uid);
                    blockedByPlayerUid.Remove(uid);
                    NextEpoch(uid);
                    return TextCommandResult.Success($"[HandyTweaks] Discard Mode: OFF (cleared {cleared} types; session reset)");
                })
                .EndSubCommand();

            root.BeginSubCommand("toggle")
                .WithDescription("Toggle Discard Mode")
                .RequiresPlayer().RequiresPrivilege(Privilege.chat)
                .HandleWith(ctx =>
                {
                    var uid = ctx.Caller.Player.PlayerUID;
                    if (enabled.Contains(uid))
                    {
                        int cleared = blockedByPlayerUid.TryGetValue(uid, out var set) ? set.Count : 0;
                        enabled.Remove(uid);
                        blockedByPlayerUid.Remove(uid);
                        NextEpoch(uid);
                        return TextCommandResult.Success($"[HandyTweaks] Discard Mode: OFF (cleared {cleared} types; session reset)");
                    }
                    else
                    {
                        enabled.Add(uid);
                        if (!epochByPlayerUid.ContainsKey(uid)) epochByPlayerUid[uid] = 1;
                        return TextCommandResult.Success("[HandyTweaks] Discard Mode: ON");
                    }
                })
                .EndSubCommand();

            root.BeginSubCommand("status")
                .WithDescription("Show current state and how many blocked types you have")
                .RequiresPlayer().RequiresPrivilege(Privilege.chat)
                .HandleWith(ctx =>
                {
                    var uid = ctx.Caller.Player.PlayerUID;
                    bool on = enabled.Contains(uid);
                    int n = blockedByPlayerUid.TryGetValue(uid, out var set) ? set.Count : 0;
                    int ep = CurrentEpoch(uid);
                    return TextCommandResult.Success($"[HandyTweaks] Discard Mode: {(on ? "ON" : "OFF")} (blocked types: {n}, epoch: {ep})");
                })
                .EndSubCommand();

            root.BeginSubCommand("list")
                .WithDescription("List all blocked item codes")
                .RequiresPlayer().RequiresPrivilege(Privilege.chat)
                .HandleWith(ctx =>
                {
                    var uid = ctx.Caller.Player.PlayerUID;
                    if (!blockedByPlayerUid.TryGetValue(uid, out var set) || set.Count == 0)
                        return TextCommandResult.Success("[HandyTweaks] No blocked types.");
                    var items = string.Join("\n - ", set.OrderBy(s => s));
                    return TextCommandResult.Success("[HandyTweaks] Blocked types:\n - " + items);
                })
                .EndSubCommand();

            root.BeginSubCommand("remove")
                .WithDescription("Remove one blocked item code (domain:path)")
                .RequiresPlayer().RequiresPrivilege(Privilege.chat)
                .WithArgs(parsers.Word("code"))
                .HandleWith(ctx =>
                {
                    var uid = ctx.Caller.Player.PlayerUID;
                    var code = ctx.Parsers[0]?.ToString();
                    if (string.IsNullOrWhiteSpace(code)) return TextCommandResult.Error("Usage: /htdiscard remove <domain:path>");
                    if (!blockedByPlayerUid.TryGetValue(uid, out var set) || !set.Remove(code))
                        return TextCommandResult.Success("[HandyTweaks] Nothing removed.");
                    return TextCommandResult.Success($"[HandyTweaks] Removed '{code}' from blocked types.");
                })
                .EndSubCommand();

            // IMPORTANT: No trailing root.EndSubCommand() here (fixes “Not inside a subcommand”)
        }

        private void OnPlayerLeave(IServerPlayer player)
        {
            var uid = player?.PlayerUID;
            if (string.IsNullOrEmpty(uid)) return;
            enabled.Remove(uid);
            blockedByPlayerUid.Remove(uid);
            epochByPlayerUid.Remove(uid);
        }

        // ===== Patches =====

        // (1) Open/close a “mark window” around moves into ground-* and add the item type to blocked set
        private void Patch_GroundMoveWindow()
        {
            var flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            var itemSlotType = typeof(ItemSlot);
            var mi = itemSlotType.GetMethod("TryPutInto", flags, null,
                new Type[] { typeof(ItemSlot), typeof(ItemStackMoveOperation).MakeByRefType() }, null);

            if (mi == null)
            {
                sapi.Logger.Error("[HandyTweaks:DiscardMode] Could not find ItemSlot.TryPutInto(ItemSlot, ref ItemStackMoveOperation).");
                return;
            }

            harmony.Patch(mi,
                prefix: new HarmonyMethod(typeof(HtDiscardMode), nameof(TryPutInto_Prefix)),
                postfix: new HarmonyMethod(typeof(HtDiscardMode), nameof(TryPutInto_Postfix)));

            sapi.Logger.Event("[HandyTweaks:DiscardMode] Patched ItemSlot.TryPutInto");
        }

        public static void TryPutInto_Prefix(ItemSlot __instance, ItemSlot sinkSlot, ref ItemStackMoveOperation op, ref string __state)
        {
            __state = null;
            try
            {
                var inv = sinkSlot?.Inventory;
                var invName = inv?.ClassName ?? inv?.InventoryID ?? "";
                bool looksGround = (!string.IsNullOrEmpty(invName) && invName.IndexOf("ground", StringComparison.OrdinalIgnoreCase) >= 0);
                if (!looksGround) return;

                var sp = op?.ActingPlayer as IServerPlayer;
                var uid = sp?.PlayerUID;
                if (string.IsNullOrEmpty(uid) || !enabled.Contains(uid)) return;

                tsMarkSpawnDepth++;
                tsDropperUid = uid;
                tsDropperEpoch = CurrentEpoch(uid);
                __state = uid;

                var code = CodeOf(__instance?.Itemstack);
                if (code != null)
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

        public static void TryPutInto_Postfix(string __state)
        {
            if (__state == null) return;
            if (tsMarkSpawnDepth > 0) tsMarkSpawnDepth--;
            if (tsMarkSpawnDepth == 0) { tsDropperUid = null; tsDropperEpoch = 0; }
        }

        // (2) Vanilla pickup gate
        private void Patch_PickupGate()
        {
            var canCollect = typeof(EntityItem).GetMethod("CanCollect",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                null, new Type[] { typeof(Entity) }, null);

            if (canCollect == null)
            {
                sapi.Logger.Warning("[HandyTweaks:DiscardMode] Could not find EntityItem.CanCollect(Entity).");
                return;
            }

            harmony.Patch(canCollect,
                prefix: new HarmonyMethod(typeof(HtDiscardMode), nameof(CanCollect_Prefix)));

            sapi.Logger.Event("[HandyTweaks:DiscardMode] Patched EntityItem.CanCollect");
        }

        public static bool CanCollect_Prefix(EntityItem __instance, Entity byEntity, ref bool __result)
        {
            try
            {
                if (!(byEntity is EntityPlayer ep)) return true;

                var uid = ep.Player?.PlayerUID;
                if (string.IsNullOrEmpty(uid) || !enabled.Contains(uid)) return true;

                var dropper = __instance.WatchedAttributes?.GetString(DropperAttr);
                int entEpoch = __instance.WatchedAttributes?.GetInt(DropperEpochAttr) ?? -1;

                if (!string.IsNullOrEmpty(dropper) && dropper == uid && entEpoch == CurrentEpoch(uid))
                {
                    __result = false;
                    return false;
                }

                var code = CodeOf(__instance.Itemstack);
                if (code != null && blockedByPlayerUid.TryGetValue(uid, out var set) && set.Contains(code))
                {
                    __result = false;
                    return false;
                }
            }
            catch { }

            return true;
        }

        // ===== Server entity spawn stamping (replaces SpawnItemEntity patch) =====
        private void OnEntitySpawn(Entity entity)
        {
            try
            {
                if (tsMarkSpawnDepth <= 0 || tsDropperUid == null) return;
                if (entity is not EntityItem item) return;

                if (item.WatchedAttributes == null)
                    item.WatchedAttributes = new SyncedTreeAttribute();

                item.WatchedAttributes.SetString(DropperAttr, tsDropperUid);
                item.WatchedAttributes.SetInt(DropperEpochAttr, tsDropperEpoch);
            }
            catch { }
        }

        // ===== Global gate (used by FastPickupPlus / PRB paths) =====
        private static bool GlobalGate(IServerPlayer sp, EntityItem ei)
        {
            try
            {
                var uid = sp?.PlayerUID;
                if (string.IsNullOrEmpty(uid) || !enabled.Contains(uid)) return true;

                var dropper = ei?.WatchedAttributes?.GetString(DropperAttr);
                int entEpoch = ei?.WatchedAttributes?.GetInt(DropperEpochAttr) ?? -1;
                if (!string.IsNullOrEmpty(dropper) && dropper == uid && entEpoch == CurrentEpoch(uid))
                    return false;

                var code = CodeOf(ei?.Itemstack);
                if (code != null && blockedByPlayerUid.TryGetValue(uid, out var set) && set.Contains(code))
                    return false;

                return true;
            }
            catch { return true; }
        }

        // ===== Helpers =====
        private static string CodeOf(ItemStack stack) => stack?.Collectible?.Code?.ToString();
        private static int CurrentEpoch(string uid) => epochByPlayerUid.TryGetValue(uid, out var e) ? e : 0;
        private static int NextEpoch(string uid) => epochByPlayerUid[uid] = CurrentEpoch(uid) + 1;
    }
}
