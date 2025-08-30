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
using HandyTweaks.Internal;     // HtPickupCore (global pickup gate + helpers)

namespace HandyTweaks.Features
{
    /// <summary>
    /// Discard Mode:
    ///  - When ON, items you drop are tagged and blocked from re-pickup.
    ///  - The item type you drop is added to a per-player "blocked types" set.
    ///  - When OFF, the blocked-type set is cleared AND a per-player "epoch" increments,
    ///    so any old ground entities from the previous session are *instantly* ignored.
    /// Integrations:
    ///  - Harmony patch of EntityItem.CanCollect for vanilla pickup path.
    ///  - Global pickup gate via HtPickupCore.GlobalPickupGate for feature paths (FPP/PRB).
    /// </summary>
    public class HtDiscardMode : ModSystem
    {
        // ------------- Attributes stored on EntityItem.WatchedAttributes -------------
        private const string DropperAttr = "handytweaks:dropperUid";
        private const string DropperEpochAttr = "handytweaks:dropperEpoch";

        // ------------- Commands -------------
        private const string CmdRoot = "htdiscard";

        // ------------- Server fields -------------
        private ICoreServerAPI sapi;
        private Harmony harmony;

        // Players with mode enabled (by PlayerUID)
        private static readonly HashSet<string> enabled = new HashSet<string>(StringComparer.Ordinal);

        // Per-player blacklist of item codes (domain:path) they should never auto-pick while mode is ON
        private static readonly Dictionary<string, HashSet<string>> blockedByPlayerUid =
            new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

        // Per-player session/epoch (increments when turning OFF; stamps are compared against current epoch)
        private static readonly Dictionary<string, int> epochByPlayerUid =
            new Dictionary<string, int>(StringComparer.Ordinal);

        // Marking window for “this thread is currently spawning a ground drop for UID X”
        [ThreadStatic] private static int tsMarkSpawnDepth;
        [ThreadStatic] private static string tsDropperUid;
        [ThreadStatic] private static int tsDropperEpoch;

        // ------------- Client fields -------------
        private ICoreClientAPI capi;

        // =========================================================
        // Startup
        // =========================================================
        public override void StartServerSide(ICoreServerAPI api)
        {
            sapi = api;
            harmony = new Harmony("com.martin.handytweaks.discardmode");

            // Patches
            Patch_GroundMoveWindow();      // detect moves into ground-*, open mark window if mode ON; also add type to blacklist
            Patch_WorldSpawnStamp();       // stamp spawned EntityItem with dropper UID+epoch during window
            Patch_PickupGate();            // block pickup when mode ON (own drops [with epoch] or blocked type)

            RegisterServerCommands();

            // Hook global pickup gate so FastPickupPlus / other callers go through the same rules
            try
            {
                HtPickupCore.ResolveMembers(); // safe to call repeatedly
                HtPickupCore.GlobalPickupGate += GlobalGate;
            }
            catch (Exception e)
            {
                sapi?.Logger.Warning("[HandyTweaks:DiscardMode] Failed to hook GlobalPickupGate: {0}", e);
            }

            sapi.Event.PlayerLeave += OnPlayerLeave; // tidy up on disconnect
            sapi.Logger.Event("[HandyTweaks:DiscardMode] Server initialized");
        }

        public override void StartClientSide(ICoreClientAPI api)
        {
            capi = api;

            // Works in menus and in general
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

            // Also bind for in-world character controls
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

            capi.Logger.Event("[HandyTweaks:DiscardMode] Client hotkeys registered (B) in GUIOrOtherControls + CharacterControls");
        }

        public override void Dispose()
        {
            try { harmony?.UnpatchAll("com.martin.handytweaks.discardmode"); } catch { }
            try { HtPickupCore.GlobalPickupGate -= GlobalGate; } catch { }
        }

        // =========================================================
        // Server: Chat commands
        // =========================================================
        private void RegisterServerCommands()
        {
            var parsers = sapi.ChatCommands.Parsers;

            // Root: /htdiscard
            var root = sapi.ChatCommands
                .Create(CmdRoot)
                .WithDescription("HandyTweaks: Toggle/inspect Discard Mode")
                .RequiresPlayer()
                .RequiresPrivilege(Privilege.chat);

            // /htdiscard on
            root.BeginSubCommand("on")
                .WithDescription("Enable Discard Mode")
                .RequiresPlayer().RequiresPrivilege(Privilege.chat)
                .HandleWith(ctx =>
                {
                    var uid = ctx.Caller.Player.PlayerUID;
                    enabled.Add(uid);
                    if (!epochByPlayerUid.ContainsKey(uid)) epochByPlayerUid[uid] = 1; // start epoch
                    return TextCommandResult.Success("[HandyTweaks] Discard Mode: ON");
                })
                .EndSubCommand();

            // /htdiscard off
            root.BeginSubCommand("off")
                .WithDescription("Disable Discard Mode and clear the blocklist")
                .RequiresPlayer().RequiresPrivilege(Privilege.chat)
                .HandleWith(ctx =>
                {
                    var uid = ctx.Caller.Player.PlayerUID;
                    int cleared = blockedByPlayerUid.TryGetValue(uid, out var set) ? set.Count : 0;
                    enabled.Remove(uid);
                    blockedByPlayerUid.Remove(uid);
                    NextEpoch(uid); // invalidate all existing world entities stamped in previous session
                    return TextCommandResult.Success($"[HandyTweaks] Discard Mode: OFF (cleared {cleared} types; session reset)");
                })
                .EndSubCommand();

            // /htdiscard toggle
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

            // /htdiscard status
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

            // /htdiscard list
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

            // /htdiscard remove <domain:path>
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

            // make sure the root is registered
            root.EndSubCommand();
        }

        private void OnPlayerLeave(IServerPlayer player)
        {
            var uid = player?.PlayerUID;
            if (string.IsNullOrEmpty(uid)) return;
            enabled.Remove(uid);
            blockedByPlayerUid.Remove(uid);
            epochByPlayerUid.Remove(uid);
        }

        // =========================================================
        // Server: Patches
        // =========================================================

        // 1) Open/close a “mark window” around moves into ground-* for
        //    a player with mode ON, and add the source item code to that player's blocked set.
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
                // Only care about moves into ground inventories
                var inv = sinkSlot?.Inventory;
                var invName = inv?.ClassName ?? inv?.InventoryID ?? "";
                bool looksGround =
                    (!string.IsNullOrEmpty(invName) && invName.IndexOf("ground", StringComparison.OrdinalIgnoreCase) >= 0);

                if (!looksGround) return;

                // Only while moving by a player who has Discard Mode ON
                var sp = op?.ActingPlayer as IServerPlayer;
                var uid = sp?.PlayerUID;
                if (string.IsNullOrEmpty(uid) || !enabled.Contains(uid)) return;

                // Open spawn window and capture session epoch
                tsMarkSpawnDepth++;
                tsDropperUid = uid;
                tsDropperEpoch = CurrentEpoch(uid);
                __state = uid;

                // Add the source item code to player's blocked set
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
            catch { /* keep safe */ }
        }

        public static void TryPutInto_Postfix(string __state)
        {
            if (__state == null) return;
            if (tsMarkSpawnDepth > 0) tsMarkSpawnDepth--;
            if (tsMarkSpawnDepth == 0) { tsDropperUid = null; tsDropperEpoch = 0; }
        }

        // 2) Stamp spawned EntityItem with the dropper UID + EPOCH during the mark window
        private void Patch_WorldSpawnStamp()
        {
            // Patch all World.SpawnItemEntity overloads we can find (server-side)
            var worldType = typeof(ICoreServerAPI).Assembly
                .GetTypes()
                .FirstOrDefault(t => t.FullName == "Vintagestory.Server.CoreServerAPI")?
                .GetField("world", BindingFlags.Instance | BindingFlags.NonPublic)?.FieldType
                ?? typeof(Entity).Assembly.GetTypes().FirstOrDefault(t => t.FullName == "Vintagestory.Server.ServerMain");

            // Fallback: scan all for methods named SpawnItemEntity
            int patched = 0;
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type[] types;
                try { types = asm.GetTypes(); } catch { continue; }
                foreach (var t in types)
                {
                    MethodInfo[] mis;
                    try { mis = t.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic); }
                    catch { continue; }

                    foreach (var mi in mis)
                    {
                        if (mi.Name != "SpawnItemEntity") continue;

                        var prms = mi.GetParameters();
                        if (prms.Length >= 2 &&
                            prms[0].ParameterType == typeof(ItemStack) &&
                            prms[1].ParameterType == typeof(Vec3d))
                        {
                            try
                            {
                                harmony.Patch(mi,
                                    postfix: new HarmonyMethod(typeof(HtDiscardMode), nameof(WorldSpawn_Postfix)));
                                patched++;
                            }
                            catch (Exception e)
                            {
                                sapi?.Logger.Warning("[HandyTweaks:DiscardMode] Failed patch {0}.{1}: {2}", t.FullName, mi.Name, e);
                            }
                        }
                    }
                }
            }

            sapi.Logger.Event("[HandyTweaks:DiscardMode] Patched {0} SpawnItemEntity overload(s)", patched);
        }

        public static void WorldSpawn_Postfix(ref Entity __result)
        {
            try
            {
                if (tsMarkSpawnDepth > 0 && tsDropperUid != null && __result is EntityItem item)
                {
                    if (item.WatchedAttributes == null) item.WatchedAttributes = new SyncedTreeAttribute();
                    item.WatchedAttributes.SetString(DropperAttr, tsDropperUid);
                    item.WatchedAttributes.SetInt(DropperEpochAttr, tsDropperEpoch);
                }
            }
            catch { /* be defensive */ }
        }

        // 3) Patch EntityItem.CanCollect so vanilla pickup respects Discard Mode
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
                if (string.IsNullOrEmpty(uid) || !enabled.Contains(uid)) return true; // mode OFF → vanilla

                // Block own drops tagged during *current epoch* of Discard Mode
                var dropper = __instance.WatchedAttributes?.GetString(DropperAttr);
                int entEpoch = __instance.WatchedAttributes?.GetInt(DropperEpochAttr) ?? -1;

                if (!string.IsNullOrEmpty(dropper) && dropper == uid && entEpoch == CurrentEpoch(uid))
                {
                    __result = false;
                    return false;
                }

                // Block all stacks of a type you've dropped during this mode
                var code = CodeOf(__instance.Itemstack);
                if (code != null && blockedByPlayerUid.TryGetValue(uid, out var set) && set.Contains(code))
                {
                    __result = false;
                    return false;
                }
            }
            catch
            {
                // fail-open on exceptions
            }

            return true;
        }

        // =========================================================
        // Global gate hook (FastPickupPlus / other feature paths)
        // =========================================================
        private static bool GlobalGate(IServerPlayer sp, EntityItem ei)
        {
            try
            {
                var uid = sp?.PlayerUID;
                if (string.IsNullOrEmpty(uid) || !enabled.Contains(uid)) return true;

                // Own-drop in current epoch?
                var dropper = ei?.WatchedAttributes?.GetString(DropperAttr);
                int entEpoch = ei?.WatchedAttributes?.GetInt(DropperEpochAttr) ?? -1;
                if (!string.IsNullOrEmpty(dropper) && dropper == uid && entEpoch == CurrentEpoch(uid))
                    return false;

                // Blocked type?
                var code = CodeOf(ei?.Itemstack);
                if (code != null && blockedByPlayerUid.TryGetValue(uid, out var set) && set.Contains(code))
                    return false;

                return true;
            }
            catch { return true; } // fail-open
        }

        // =========================================================
        // Helpers
        // =========================================================
        private static string CodeOf(ItemStack stack)
        {
            // domain:path (e.g., "game:drygrass")
            return stack?.Collectible?.Code?.ToString();
        }

        private static int CurrentEpoch(string uid) =>
            epochByPlayerUid.TryGetValue(uid, out var e) ? e : 0;

        private static int NextEpoch(string uid) =>
            epochByPlayerUid[uid] = CurrentEpoch(uid) + 1;
    }
}
