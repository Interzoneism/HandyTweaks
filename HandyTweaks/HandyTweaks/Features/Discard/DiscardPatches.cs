using HarmonyLib;
using System.Collections.Generic;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Server;

namespace HandyTweaks.Features.Discard
{
    [HarmonyPatch(typeof(EntityItem), nameof(EntityItem.CanCollect))]
    public static class Patch_EntityItem_CanCollect
    {
        // throttle: at most one debug line per entity every 250 ms
        private static readonly Dictionary<long, long> lastLogByEntity = new();
        private const int LogEveryMs = 250;

        private static bool ShouldLog(ICoreServerAPI sapi, long entId)
        {
            long now = sapi.World.ElapsedMilliseconds;
            if (!lastLogByEntity.TryGetValue(entId, out long last) || now - last >= LogEveryMs)
            {
                lastLogByEntity[entId] = now;
                return true;
            }
            return false;
        }

        public static bool Prefix(EntityItem __instance, Entity byEntity, ref bool __result)
        {
            // Server-only
            var sapi = byEntity?.Api as ICoreServerAPI;
            if (sapi == null) return true;

            // Only players
            if (byEntity is not EntityPlayer ep) return true;
            var sp = ep.Player as IServerPlayer;
            var collectorUid = sp?.PlayerUID;
            if (collectorUid == null) return true;

            // Live server instance
            var srv = HandyTweaksModSystem.DiscardSrv;

            // Spawn tag / timer branch
            var wat = __instance.WatchedAttributes;
            long until = wat?.GetLong("ht_discard_until") ?? 0;
            long now = sapi.World.ElapsedMilliseconds;
            bool stillTimed = (until != 0 && now < until);

            // Try native link first
            string dropperUid = __instance.ByPlayerUid;
            // after string dropperUid = __instance.ByPlayerUid;
            if (string.IsNullOrEmpty(dropperUid))
            {
                // read our tag if the engine UID wasn’t set yet when the item was tagged
                dropperUid = __instance.WatchedAttributes?.GetString("ht_discard_uid", null);
            }

            bool isOwnDrop = (dropperUid == collectorUid);
            bool modeOn = srv?.IsModeOn(collectorUid) == true;

            // Veto rule: block if (mode ON and it’s your own drop) OR (still within timed block)
            if ((modeOn && isOwnDrop) || stillTimed)
            {
                __result = false;
                // Debug (throttled, log only)
                if (srv?.Debug == true && ShouldLog(sapi, __instance.EntityId))
                {
                    sapi.Logger.Notification(
                        $"[DiscardDBG] VETO CanCollect ent={__instance.EntityId} coll={collectorUid} dropper={dropperUid ?? "<null>"} own={isOwnDrop} modeOn={modeOn} timed={stillTimed}");
                }
                return false;
            }

            // Debug only on interesting cases, throttled, log only (no chat)
            if (srv?.Debug == true && ShouldLog(sapi, __instance.EntityId))
            {
                bool interesting = modeOn || stillTimed || !string.IsNullOrEmpty(dropperUid);
                if (interesting)
                {
                    sapi.Logger.Notification(
                        $"[DiscardDBG] CanCollect pass ent={__instance.EntityId} coll={collectorUid} dropper={dropperUid ?? "<null>"} own={isOwnDrop} modeOn={modeOn} timed={stillTimed}");
                }
            }

            return true;
        }
    }
}
