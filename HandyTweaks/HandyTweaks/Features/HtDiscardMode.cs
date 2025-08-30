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

namespace HandyTweaks.Features
{
    public class HtDiscardMode : ModSystem
    {
        // ------------- Shared keys / ids -------------
        private const string DropperAttr = "handytweaks:dropperUid";
        private const string CmdRoot = "htdiscard";

        // ------------- Server fields -------------
        private ICoreServerAPI sapi;
        private Harmony harmony;

        // Players with mode enabled (by PlayerUID)
        private static readonly HashSet<string> enabled = new HashSet<string>(StringComparer.Ordinal);

        // Per-player blacklist of item codes (domain:path) they should never auto-pick while mode is ON
        private static readonly Dictionary<string, HashSet<string>> blockedByPlayerUid = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);

        // Marking window for “this thread is currently spawning a ground drop for UID X”
        [ThreadStatic] private static int tsMarkSpawnDepth;
        [ThreadStatic] private static string tsDropperUid;

        // ------------- Client fields -------------
        private ICoreClientAPI capi;

        // =========================================================
        // Startup
        // =========================================================
        public override void StartServerSide(ICoreServerAPI api)
        {
            sapi = api;
            harmony = new Harmony("com.martin.handytweaks.discardmode");

            Patch_GroundMoveWindow();      // detect moves into ground-* and open mark window if mode ON for that player; also add type to blacklist
            Patch_WorldSpawnStamp();       // stamp spawned EntityItem with dropper UID during window
            Patch_PickupGate();            // block pickup when mode ON (own drops or blocked type)

            RegisterServerCommands();

            sapi.Event.PlayerLeave += OnPlayerLeave; // tidy up on disconnect
            sapi.Logger.Event("[HandyTweaks:DiscardMode] Server initialized");
        }

        public override void StartClientSide(ICoreClientAPI api)
        {
            capi = api;

            // Works in menus and in general (your existing one)
            capi.Input.RegisterHotKey(
                "httoggle-discard",
                "HandyTweaks: Toggle Discard Mode",
                GlKeys.B,
                HotkeyType.GUIOrOtherControls
            );
            capi.Input.SetHotKeyHandler("httoggle-discard", _ =>
            {
                capi.SendChatMessage("/htdiscard toggle");
                capi.ShowChatMessage("[HandyTweaks] Toggling Discard Mode..."); // immediate local feedback
                return true;
            });

            // Also bind for in-world character controls (in case another GUI group eats the key)
            capi.Input.RegisterHotKey(
                "httoggle-discard-world",
                "HandyTweaks: Toggle Discard Mode (World)",
                GlKeys.B,
                HotkeyType.CharacterControls
            );
            capi.Input.SetHotKeyHandler("httoggle-discard-world", _ =>
            {
                capi.SendChatMessage("/htdiscard toggle");
                capi.ShowChatMessage("[HandyTweaks] Toggling Discard Mode...");
                return true;
            });

            capi.Logger.Event("[HandyTweaks:DiscardMode] Client hotkeys registered (B) in GUIOrOtherControls + CharacterControls");
        }





        public override void Dispose()
        {
            try { harmony?.UnpatchAll("com.martin.handytweaks.discardmode"); } catch { }
        }

        // =========================================================
        // Server: Chat commands
        // =========================================================
        private void RegisterServerCommands()
        {
            var parsers = sapi.ChatCommands.Parsers;

            // Root: /htdiscard
            var root = sapi.ChatCommands
                .Create(CmdRoot) // sets the name
                .WithDescription("HandyTweaks: Toggle/inspect Discard Mode")
                .RequiresPlayer()                          // caller must be a player
                .RequiresPrivilege(Privilege.chat);        // and must have 'chat' privilege (satisfies the 'privilege required' rule)

            // /htdiscard on
            root.BeginSubCommand("on")
                .WithDescription("Enable Discard Mode")
                .RequiresPlayer().RequiresPrivilege(Privilege.chat)
                .HandleWith(ctx =>
                {
                    var uid = ctx.Caller.Player.PlayerUID;
                    enabled.Add(uid);
                    return TextCommandResult.Success("[HandyTweaks] Discard Mode: ON");
                })
                .EndSubCommand();

            // /htdiscard off
            root.BeginSubCommand("off")
                .WithDescription("Disable Discard Mode and clear cache")
                .RequiresPlayer().RequiresPrivilege(Privilege.chat)
                .HandleWith(ctx =>
                {
                    var uid = ctx.Caller.Player.PlayerUID;
                    enabled.Remove(uid);
                    blockedByPlayerUid.Remove(uid);
                    return TextCommandResult.Success("[HandyTweaks] Discard Mode: OFF (cache cleared)");
                })
                .EndSubCommand();

            // /htdiscard toggle
            root.BeginSubCommand("toggle")
                .WithDescription("Toggle Discard Mode (also bound to B)")
                .RequiresPlayer().RequiresPrivilege(Privilege.chat)
                .HandleWith(ctx =>
                {
                    var uid = ctx.Caller.Player.PlayerUID;
                    if (enabled.Contains(uid))
                    {
                        int cleared = blockedByPlayerUid.TryGetValue(uid, out var set) ? set.Count : 0;
                        enabled.Remove(uid);
                        blockedByPlayerUid.Remove(uid);
                        return TextCommandResult.Success($"[HandyTweaks] Discard Mode: OFF (cleared {cleared} types)");
                    }
                    enabled.Add(uid);
                    return TextCommandResult.Success("[HandyTweaks] Discard Mode: ON");
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
                    return TextCommandResult.Success($"[HandyTweaks] Discard Mode: {(on ? "ON" : "OFF")} (blocked types: {n})");
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
                    var code = (string)ctx.Parsers[0].GetValue();
                    if (string.IsNullOrWhiteSpace(code))
                        return TextCommandResult.Error("Usage: /htdiscard remove <domain:path>");

                    if (blockedByPlayerUid.TryGetValue(uid, out var set) && set.Remove(code))
                        return TextCommandResult.Success($"[HandyTweaks] Unblocked '{code}'.");
                    return TextCommandResult.Error($"[HandyTweaks] '{code}' not in your blocked set.");
                })
                .EndSubCommand();

            // Optional: ensure the whole tree is complete (name, handler, privilege, description set)
            root.Validate();  // throws during startup if something’s missing (useful while iterating) :contentReference[oaicite:1]{index=1}
        }





        private void OnPlayerLeave(IServerPlayer player)
        {
            var uid = player?.PlayerUID;
            if (string.IsNullOrEmpty(uid)) return;
            enabled.Remove(uid);
            blockedByPlayerUid.Remove(uid);
        }

        // =========================================================
        // Server: Patches
        // =========================================================

        // 1) Open/close a “mark window” around moves into ground-* for a player with mode ON,
        //    and add the source item code to that player's blocked set.
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

        // __state carries the UID we opened the window for (or null)
        public static void TryPutInto_Prefix(ItemSlot __instance, ItemSlot sinkSlot, ref ItemStackMoveOperation op, ref string __state)
        {
            __state = null;
            try
            {
                var invId = sinkSlot?.Inventory?.InventoryID;     // "ground-<uid>"
                if (string.IsNullOrEmpty(invId) || !invId.StartsWith("ground-", StringComparison.Ordinal)) return;

                var uid = invId.Substring("ground-".Length);
                if (string.IsNullOrEmpty(uid) || !enabled.Contains(uid)) return;

                // Open spawn window
                tsMarkSpawnDepth++;
                tsDropperUid = uid;
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
            if (tsMarkSpawnDepth == 0) tsDropperUid = null;
        }

        // 2) Stamp spawned EntityItem with the dropper UID during the mark window
        private void Patch_WorldSpawnStamp()
        {
            var flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            int patched = 0;

            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type[] types;
                try { types = asm.GetTypes(); } catch { continue; }

                foreach (var t in types)
                {
                    if (t.IsAbstract || t.IsInterface) continue;
                    if (!typeof(IWorldAccessor).IsAssignableFrom(t)) continue;

                    var methods = t.GetMethods(flags).Where(m =>
                        m.Name == "SpawnItemEntity" &&
                        !m.IsAbstract &&
                        typeof(Entity).IsAssignableFrom(m.ReturnType));

                    foreach (var mi in methods)
                    {
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
                    // (optional breadcrumb)
                    item.World?.Api?.Logger?.Debug("[HT:Discard] Tag drop ent={0} uid={1}", item.EntityId, tsDropperUid);
                }
            }
            catch { }
        }

        // 3) Deny pickup when mode is ON for the collector:
        //    - if the entity's dropperUid == collectorUid (own drop)
        //    - OR the item code is in the collector's blocked set
        private void Patch_PickupGate()
        {
            var flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            var eiType = typeof(EntityItem);
            var canCollect = eiType.GetMethod("CanCollect", flags, null, new Type[] { typeof(Entity) }, null);

            if (canCollect == null)
            {
                sapi.Logger.Error("[HandyTweaks:DiscardMode] Could not find EntityItem.CanCollect(Entity).");
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

                // Block own drops tagged during Discard Mode
                var dropper = __instance.WatchedAttributes?.GetString(DropperAttr);
                if (!string.IsNullOrEmpty(dropper) && dropper == uid)
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
            catch { /* fall through to vanilla */ }

            return true;
        }

        // =========================================================
        // Helpers
        // =========================================================
        private static string CodeOf(ItemStack stack)
        {
            // domain:path (e.g., "game:drygrass")
            return stack?.Collectible?.Code?.ToString();
        }
    }
}
