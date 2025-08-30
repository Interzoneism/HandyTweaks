// HandyTweaks/HandyTweaksModSystem.cs
using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Server;

public class HandyTweaksModSystem : ModSystem
{
    private Harmony harmony;

    public override void Start(ICoreAPI api)
    {
        harmony = new Harmony("ht.discard");
        harmony.PatchAll(typeof(HandyTweaksModSystem).Assembly);

#if DEBUG
try
{
    var mi = AccessTools.Method(typeof(EntityItem), "CanCollect", new[] { typeof(Entity) });
    var info = Harmony.GetPatchInfo(mi);
    api.Logger.Notification($"[DiscardDBG] CanCollect prefixes={info?.Prefixes?.Count ?? 0}");
}
catch (System.Exception e)
{
    api.Logger.Warning("[DiscardDBG] Could not inspect CanCollect: " + e.Message);
}
#endif

    }
    public override void Dispose()
    {
        harmony?.UnpatchAll("ht.discard");
        base.Dispose();
    }
}
