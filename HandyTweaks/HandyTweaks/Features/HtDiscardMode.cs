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

namespace HandyTweaks.Features
{
    public class HtDiscardMode : ModSystem
    {
        private const string DropperAttr = "handytweaks:dropperUid";
        private const string DropperEpochAttr = "handytweaks:dropperEpoch";
        private const string CmdRoot = "htdiscard";

        private ICoreServerAPI sapi;
        private ICoreClientAPI capi;
        private Harmony harmony;

        private static readonly HashSet<string> enabled = new(StringComparer.Ordinal);
        private static readonly Dictionary<string, HashSet<string>> blockedByPlayerUid = new(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, int> epochByPlayerUid = new(StringComparer.Ordinal);

        // Thread-local link between a ground-drop operation and the subsequent entity spawn event
        [ThreadStatic] private static int tsMarkSpawnDepth;
        [ThreadStatic] private static string tsDropperUid;
        [ThreadStatic] private static int tsDropperEpoch;

        // Cached via reflection to avoid hard ref (works across minor API moves)
        private static Type TInventoryPlayerGround;

        public override void StartServerSide(ICoreServerAPI api)
        {
            sapi = api;

            // Config gate (default to enabled if config missing)
            try
            {
                HandyTweaks.HtShared.EnsureLoaded(api);
                var cfg = HandyTweaks.HtShared.Config?.DiscardMode;
                if (cfg != null && !cfg.Enabled)
                {
                    sapi.Logger.Event("[HandyTweaks:DiscardMode] Disabled via config.");
                    return;
                }
            }
            catch { /* ignore if config types not present */ }

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

            // Config gate (default to enabled if config missing)
            try
            {
                HandyTweaks.HtShared.EnsureLoaded(api);
                var cfg = HandyTweaks.HtShared.Config?.DiscardMode;
                if (cfg != null && !cfg.Enabled) return;
            }
            catch { /* ignore */ }

            // Single hotkey only (no duplicate “World” binding)
            capi.Input.RegisterHotKey(
                "httoggle-discard",
                "HandyTweaks: Toggle Discard Mode",
                GlKeys.B,
                HotkeyType.GUIOrOtherControls
            );
            capi.Input.SetHotKeyHandler("httoggle-discard", _ =>
            {
                // Only the command reply will be shown in chat
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

        // -----------------------------
        // Server Commands
        // -----------------------------
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

            // Optional: status peek
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

        // Clear per-player state on leave to prevent leaks
        private void OnPlayerLeave(IServerPlayer player)
        {
            var uid = player?.PlayerUID;
            if (string.IsNullOrEmpty(uid)) return;
            enabled.Remove(uid);
            blockedByPlayerUid.Remove(uid);
            epochByPlayerUid.Remove(uid);

            // Reset thread-local only if this player was the active dropper (best-effort)
            if (tsDropperUid == uid)
            {
                tsMarkSpawnDepth = 0;
                tsDropperUid = null;
                tsDropperEpoch = 0;
            }
        }

        // -----------------------------
        // Harmony: Gate ground-drop → mark next spawn
        // -----------------------------
        private void Patch_GroundMoveWindow()
        {
            // ItemSlot.TryPutInto(ItemSlot sinkSlot, ref ItemStackMoveOperation op)
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

            harmony.Patch(method,
                prefix: new HarmonyMethod(typeof(HtDiscardMode), nameof(TryPutInto_Prefix)),
                finalizer: new HarmonyMethod(typeof(HtDiscardMode), nameof(TryPutInto_Finalizer)));

            sapi.Logger.Event("[HandyTweaks:DiscardMode] Patched ItemSlot.TryPutInto (prefix+finalizer)");
        }

        // Hardened Prefix: only triggers for the actual Player Ground inventory
        public static void TryPutInto_Prefix(ItemSlot __instance, ItemSlot sinkSlot, ref ItemStackMoveOperation op, ref string __state)
        {
            __state = null;
            try
            {
                var inv = sinkSlot?.Inventory;
                if (inv == null) return;

                // Cache InventoryPlayerGround type reflectively (no hard ref)
                if (TInventoryPlayerGround == null)
                    TInventoryPlayerGround = HarmonyLib.AccessTools.TypeByName("Vintagestory.Common.InventoryPlayerGround");

                bool isGround = TInventoryPlayerGround != null && TInventoryPlayerGround.IsInstanceOfType(inv);

                // Strict fallback if that type name isn't available in this build
                if (!isGround)
                {
                    var cname = inv.ClassName ?? string.Empty;
                    var iid = inv.InventoryID ?? string.Empty;
                    if (!string.Equals(cname, "playerground", StringComparison.OrdinalIgnoreCase) &&
                        !string.Equals(iid, "playerground", StringComparison.OrdinalIgnoreCase))
                    {
                        return; // Not the ground inventory
                    }
                }

                var sp = op?.ActingPlayer as IServerPlayer;
                var uid = sp?.PlayerUID;
                if (string.IsNullOrEmpty(uid) || !enabled.Contains(uid)) return;

                // --- Start of a new (outermost) drop action? Bump epoch once. ---
                if (tsMarkSpawnDepth == 0)
                {
                    tsDropperEpoch = NextEpoch(uid);
                }

                tsMarkSpawnDepth++;
                tsDropperUid = uid;
                __state = uid; // gate cleanup

                // Remember the code being dropped so discard mode can block pickup of identical items
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
            catch
            {
                // Never break vanilla move path
            }
        }

        // Finalizer: guaranteed cleanup even if other patches or vanilla throw
        public static void TryPutInto_Finalizer(string __state, Exception __exception)
        {
            try
            {
                if (__state != null)
                {
                    // If something blew up anywhere in this (possibly nested) call chain,
                    // or we're at the outermost frame, force a full reset.
                    if (__exception != null || tsMarkSpawnDepth <= 1)
                    {
                        tsMarkSpawnDepth = 0;
                        tsDropperUid = null;
                        tsDropperEpoch = 0;
                    }
                    else
                    {
                        // Normal unwind of a nested frame
                        tsMarkSpawnDepth--;
                    }
                }

                // Belt & suspenders: clamp depth
                if (tsMarkSpawnDepth < 0) tsMarkSpawnDepth = 0;
            }
            catch
            {
                // Cleanup must never throw
                tsMarkSpawnDepth = 0;
                tsDropperUid = null;
                tsDropperEpoch = 0;
            }
        }

        // -----------------------------
        // Harmony: Pickup gate — block immediate re-pickup and blocked codes
        // -----------------------------
        private void Patch_PickupGate()
        {
            // EntityItem.CanCollect(Entity byEntity)
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

            harmony.Patch(canCollect,
                prefix: new HarmonyMethod(typeof(HtDiscardMode), nameof(CanCollect_Prefix)));

            sapi.Logger.Event("[HandyTweaks:DiscardMode] Patched EntityItem.CanCollect");
        }

        public static bool CanCollect_Prefix(EntityItem __instance, Entity byEntity, ref bool __result)
        {
            try
            {
                if (byEntity is not EntityPlayer ep) return true;

                var uid = ep.Player?.PlayerUID;
                if (string.IsNullOrEmpty(uid) || !enabled.Contains(uid)) return true;

                var dropper = __instance.WatchedAttributes?.GetString(DropperAttr);
                int entEpoch = __instance.WatchedAttributes?.GetInt(DropperEpochAttr) ?? -1;

                // 1) Never pick up your just-discarded item during the active epoch window
                if (!string.IsNullOrEmpty(dropper) && dropper == uid && entEpoch == CurrentEpoch(uid))
                {
                    __result = false;
                    return false;
                }

                // 2) Block codes you just discarded while mode is ON
                var code = CodeOf(__instance.Itemstack);
                if (!string.IsNullOrEmpty(code)
                    && blockedByPlayerUid.TryGetValue(uid, out var set)
                    && set.Contains(code))
                {
                    __result = false;
                    return false;
                }
            }
            catch
            {
                // If anything goes wrong, fall back to vanilla
            }

            return true;
        }

        // -----------------------------
        // Spawn hook — tag the spawned entity with dropper+epoch during mark window
        // -----------------------------
        private void OnEntitySpawn(Entity entity)
        {
            try
            {
                if (tsMarkSpawnDepth <= 0 || string.IsNullOrEmpty(tsDropperUid)) return;
                if (entity is not EntityItem ei) return;

                // Make sure we use a SyncedTreeAttribute (so the tag syncs to clients)
                var wattr = ei.WatchedAttributes;
                if (wattr == null)
                {
                    wattr = new SyncedTreeAttribute();
                    ei.WatchedAttributes = wattr;
                }

                wattr.SetString(DropperAttr, tsDropperUid);
                wattr.SetInt(DropperEpochAttr, tsDropperEpoch);
            }
            catch
            {
                // Non-fatal
            }
        }

        // -----------------------------
        // Helpers
        // -----------------------------
        private static string CodeOf(ItemStack stack) => stack?.Collectible?.Code?.ToString();

        private static int CurrentEpoch(string uid) =>
            epochByPlayerUid.TryGetValue(uid, out var e) ? e : 0;

        private static int NextEpoch(string uid) =>
            epochByPlayerUid[uid] = CurrentEpoch(uid) + 1;

        // Add inside class HtDiscardMode (namespace HandyTweaks.Features)
        public static bool IsBlockedFor(IServerPlayer sp, EntityItem ei)
        {
            try
            {
                if (sp == null || ei == null) return false;

                var uid = sp.PlayerUID;
                if (string.IsNullOrEmpty(uid) || !enabled.Contains(uid)) return false;

                // 1) Block your own fresh discard during the current epoch
                var wattr = ei.WatchedAttributes;
                var dropper = wattr?.GetString(DropperAttr);
                int entEpoch = wattr?.GetInt(DropperEpochAttr) ?? -1;

                if (!string.IsNullOrEmpty(dropper) && dropper == uid && entEpoch == CurrentEpoch(uid))
                    return true;

                // 2) Block exact item codes the player just discarded (while mode is ON)
                var code = CodeOf(ei.Itemstack);
                return !string.IsNullOrEmpty(code)
                    && blockedByPlayerUid.TryGetValue(uid, out var set)
                    && set.Contains(code);
            }
            catch { return false; }
        }

    }
}
