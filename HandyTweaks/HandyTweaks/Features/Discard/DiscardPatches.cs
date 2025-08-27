using HarmonyLib;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Common;
using Vintagestory.API.Server;

namespace HandyTweaks.Features.Discard
{
    /// <summary>
    /// Intercept BEFORE pickup using EntityItem.CanCollect(byEntity).
    /// If the collector is the original dropper AND the item was tagged, veto pickup.
    /// </summary>
    [HarmonyPatch(typeof(EntityItem), nameof(EntityItem.CanCollect))]
    public static class Patch_EntityItem_CanCollect
    {
        public static bool Prefix(EntityItem __instance, Entity byEntity, ref bool __result)
        {
            // Only care about players
            if (byEntity is not EntityPlayer ep) return true;

            string dropperUid = __instance.ByPlayerUid;
            string collectorUid = ep.Player?.PlayerUID;

            // Only block the original dropper
            if (string.IsNullOrEmpty(dropperUid) || collectorUid != dropperUid) return true;

            var wat = __instance.WatchedAttributes;
            if (wat == null || !wat.GetBool("ht_discard_marked")) return true;

            // Only enforce on the server (authoritative). Client can pass through.
            var sapi = byEntity.Api as ICoreServerAPI;
            if (sapi == null) return true;

            // Check server-side mode and optional per-entity timer
            bool modeOn = DiscardModSystem.DiscardSrv?.IsModeOn(dropperUid) ?? false;

            long until = wat.GetLong("ht_discard_until");
            long now = sapi.World.ElapsedMilliseconds;
            bool stillTimed = (until != 0 && now < until);

            if (modeOn || stillTimed)
            {
                __result = false; // veto: cannot collect
                return false;     // skip vanilla CanCollect
            }

            return true;
        }
    }
}
