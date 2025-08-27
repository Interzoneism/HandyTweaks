// HandyTweaks/HandyTweaksModSystem.cs
using HarmonyLib;
using HandyTweaks.Features.Discard;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Server;

public class HandyTweaksModSystem : ModSystem
{
    private Harmony harmony;
    private DiscardClient discardClient;
    public static DiscardServer DiscardSrv; // accessed by patch

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

    public override void StartServerSide(ICoreServerAPI sapi)
    {
        DiscardSrv = new DiscardServer(sapi,
            tagSeconds: 0   // 0 = ignore while mode is ON, allow immediately when mode is OFF
                            // e.g. 300 = also ignore for 5 minutes even after turning mode OFF
        );
        DiscardSrv.Start();
    }

    public override void StartClientSide(ICoreClientAPI capi)
    {
        discardClient = new DiscardClient(capi, startEnabled: false);
        discardClient.Start();
    }

    public override void Dispose()
    {
        harmony?.UnpatchAll("ht.discard");
        base.Dispose();
    }
}
