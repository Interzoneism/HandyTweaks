using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Config;
using Vintagestory.API.Server;

namespace HandyTweaks.Features.Discard
{
    /// <summary>
    /// Veto innan pickup: EntityItem.CanCollect(byEntity)
    /// Blockerar enbart dropparen, och bara för taggade entities.
    /// </summary>
    [HarmonyPatch(typeof(EntityItem), nameof(EntityItem.CanCollect))]
    public static class Patch_EntityItem_CanCollect
    {
        public static bool Prefix(EntityItem __instance, Entity byEntity, ref bool __result)
        {
            // Only players matter
            if (byEntity is not EntityPlayer ep) return true;

            string dropperUid = __instance.ByPlayerUid;
            string collectorUid = ep.Player?.PlayerUID;

            // Only block the original dropper
            if (string.IsNullOrEmpty(dropperUid) || collectorUid != dropperUid) return true;

            var wat = __instance.WatchedAttributes;
            if (wat == null || !wat.GetBool("ht_discard_marked")) return true;

            // Server decides
            var sapi = byEntity.Api as ICoreServerAPI;
            if (sapi == null) return true;

            // >>> FIX: read whichever server actually started
            var srv = global::HandyTweaksModSystem.DiscardSrv ?? DiscardModSystem.DiscardSrv;

            bool modeOn = srv?.IsModeOn(dropperUid) ?? false;

            long until = wat.GetLong("ht_discard_until");
            long now = sapi.World.ElapsedMilliseconds;
            bool stillTimed = (until != 0 && now < until);

            // Optional debug to log + the collector's chat
            if (srv?.Debug == true)
            {
                sapi.Logger.Notification($"[DiscardDBG] CanCollect? coll={collectorUid} dropper={dropperUid} marked=1 modeOn={modeOn} timed={stillTimed} now={now} until={until}");
                if (ep.Player is IServerPlayer sp)
                {
                    sp.SendMessage(GlobalConstants.GeneralChatGroup,
                        $"[DiscardDBG] modeOn={modeOn} timed={stillTimed}", EnumChatType.Notification);
                }
            }

            if (modeOn || stillTimed)
            {
                __result = false;   // veto pickup
                return false;       // skip vanilla CanCollect
            }

            return true;
        }
    }

}
