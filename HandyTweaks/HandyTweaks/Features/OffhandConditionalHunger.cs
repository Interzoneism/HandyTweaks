using System;
using System.Collections.Generic;
using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.API.Server;
using Vintagestory.API.Client;
using Vintagestory.API.Common.Entities;

namespace HandyTweaks.Features
{
    /// <summary>
    /// OffhandConditionalHunger: Applies a temporary extra hunger rate when sprinting or using actions,
    /// optionally only while holding something in the offhand. All values are configurable.
    /// </summary>
    public class OffhandConditionalHunger : ModSystem
    {
        Harmony harmony;
        ICoreAPI api;

        // Config‑driven
        static float PenaltyAmount;    // 0..1 (e.g. 0.40f)
        static float PenaltyDuration;  // seconds
        static bool TriggerOnSprint;
        static bool TriggerOnLeft;
        static bool TriggerOnRight;
        static bool RequireOffhandItem;

        const string StatKey = "handytweaks-offhand-conditional";

        readonly Dictionary<string, float> timerByUid = new();
        long serverTickId, clientTickId;

        public override void Start(ICoreAPI api)
        {
            this.api = api;
            HandyTweaks.HtShared.EnsureLoaded(api);
            var cfg = HandyTweaks.HtShared.Config.OffhandConditionalHunger;
            if (!cfg.Enabled) return;

            PenaltyAmount = Math.Max(0f, cfg.PenaltyPercent / 100f);
            PenaltyDuration = Math.Max(0f, cfg.PenaltyDurationSeconds);
            TriggerOnSprint = cfg.TriggerOnSprint;
            TriggerOnLeft = cfg.TriggerOnLeftClick;
            TriggerOnRight = cfg.TriggerOnRightClick;
            RequireOffhandItem = cfg.RequireOffhandItem;

            harmony = new Harmony("handytweaks.offhand.conditionalhunger");
            PatchOffhandHotbar(harmony);
        }

        public override void StartServerSide(ICoreServerAPI sapi)
        {
            if (harmony == null) return;
            serverTickId = sapi.World.RegisterGameTickListener(ServerTick, 50);
            sapi.Event.PlayerNowPlaying += OnServerJoin;
            sapi.Event.PlayerDisconnect += OnServerLeave;
        }

        public override void StartClientSide(ICoreClientAPI capi)
        {
            if (harmony == null) return;
            clientTickId = capi.World.RegisterGameTickListener(ClientTick, 50);
        }

        public override void Dispose()
        {
            harmony?.UnpatchAll("handytweaks.offhand.conditionalhunger");

            if (api is ICoreServerAPI sapi && serverTickId != 0)
            {
                sapi.World.UnregisterGameTickListener(serverTickId);
                sapi.Event.PlayerNowPlaying -= OnServerJoin;
                sapi.Event.PlayerDisconnect -= OnServerLeave;
            }
            if (api is ICoreClientAPI capi && clientTickId != 0)
            {
                capi.World.UnregisterGameTickListener(clientTickId);
            }
            timerByUid.Clear();
        }

        void OnServerJoin(IServerPlayer plr) => timerByUid[plr.PlayerUID] = 0f;
        void OnServerLeave(IServerPlayer plr) => timerByUid.Remove(plr.PlayerUID);

        void ServerTick(float dt)
        {
            var sapi = api as ICoreServerAPI; if (sapi == null) return;

            foreach (var sp in sapi.World.AllOnlinePlayers)
            {
                var e = sp.Entity; if (e == null) continue;

                var gm = sp.WorldData?.CurrentGameMode ?? EnumGameMode.Survival;
                if (gm != EnumGameMode.Survival) { RemoveIfAny(e); timerByUid[sp.PlayerUID] = 0f; continue; }

                bool offhandHasItem = OffhandHasItem(sp);
                bool triggering = IsTriggering(e) && (!RequireOffhandItem || offhandHasItem);

                if (triggering) timerByUid[sp.PlayerUID] = PenaltyDuration;

                float remaining = timerByUid.TryGetValue(sp.PlayerUID, out var t) ? t : 0f;

                if (remaining > 0f && (!RequireOffhandItem || offhandHasItem))
                {
                    e.Stats.Set("hungerrate", StatKey, PenaltyAmount, true);
                    remaining = Math.Max(0f, remaining - dt);
                    timerByUid[sp.PlayerUID] = remaining;
                }
                else
                {
                    RemoveIfAny(e);
                    timerByUid[sp.PlayerUID] = 0f;
                }
            }
        }

        void ClientTick(float dt)
        {
            var capi = api as ICoreClientAPI;
            var cp = capi?.World?.Player; var e = cp?.Entity; if (e == null) return;

            bool offhandHasItem = OffhandHasItem(cp);
            bool triggering = IsTriggering(e) && (!RequireOffhandItem || offhandHasItem);

            if (triggering) timerByUid[cp.PlayerUID] = PenaltyDuration;

            float remaining = timerByUid.TryGetValue(cp.PlayerUID, out var t) ? t : 0f;

            if (remaining > 0f && (!RequireOffhandItem || offhandHasItem))
            {
                e.Stats.Set("hungerrate", StatKey, PenaltyAmount, true);
                remaining = Math.Max(0f, remaining - dt);
                timerByUid[cp.PlayerUID] = remaining;
            }
            else
            {
                RemoveIfAny(e);
                timerByUid[cp.PlayerUID] = 0f;
            }
        }

        static bool IsTriggering(EntityPlayer e)
        {
            var c = e?.Controls; if (c == null) return false;
            return (TriggerOnSprint && c.Sprint)
                || (TriggerOnLeft && c.LeftMouseDown)
                || (TriggerOnRight && c.RightMouseDown);
        }

        static bool OffhandHasItem(IPlayer p)
        {
            var inv = p?.InventoryManager?.GetHotbarInventory();
            if (inv == null) return false;
            int offhandIndex = Math.Max(0, inv.Count - 1); // last hotbar slot is offhand
            var slot = inv[offhandIndex];
            return slot != null && !slot.Empty;
        }

        static void RemoveIfAny(EntityPlayer e) => e?.Stats?.Remove("hungerrate", StatKey);

        static void PatchOffhandHotbar(Harmony h)
        {
            var type = AccessTools.TypeByName("Vintagestory.Common.InventoryPlayerHotbar");
            if (type == null) return;

            var m = AccessTools.Method(type, "updateSlotStatMods", new[] {
                typeof(System.Collections.Generic.List<string>), typeof(ItemSlot), typeof(string)
            });
            if (m == null) return;

            var postfix = new HarmonyMethod(typeof(OffhandConditionalHunger), nameof(HotbarUpdate_Postfix));
            h.Patch(m, postfix: postfix);
        }

        public static void HotbarUpdate_Postfix(object __instance, System.Collections.Generic.List<string> list, ItemSlot slot, string handcategory)
        {
            try
            {
                if (handcategory != "offhanditem") return;
                if (slot == null || slot.Empty) { RemoveVanillaOffhandPenalty(__instance); return; }

                var attrs = slot.Itemstack?.ItemAttributes;
                bool hasCustomMods = attrs != null && attrs["statModifier"].Exists;
                if (!hasCustomMods) RemoveVanillaOffhandPenalty(__instance);
            }
            catch { }
        }

        static void RemoveVanillaOffhandPenalty(object hotbarInstance)
        {
            var apiObj = (ICoreAPI)AccessTools.Field(hotbarInstance.GetType(), "Api")?.GetValue(hotbarInstance);
            var uid = (string)AccessTools.Field(hotbarInstance.GetType(), "playerUID")?.GetValue(hotbarInstance);
            var player = apiObj?.World?.PlayerByUid(uid);
            var entity = player?.Entity;
            entity?.Stats?.Remove("hungerrate", "offhanditem");
        }
    }
}
