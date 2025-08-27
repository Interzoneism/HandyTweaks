using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.API.Client;
using Vintagestory.API.Server;

namespace HandyTweaks.Features.Discard
{
    /// <summary>
    /// Standalone ModSystem just for the Discard feature.
    /// Safe to add alongside your existing HandyTweaks ModSystem.
    /// </summary>
    public class DiscardModSystem : ModSystem
    {
        private Harmony harmony;
        private DiscardClient client;
        public static DiscardServer DiscardSrv; // referenced by Harmony patch

        public override void Start(ICoreAPI api)
        {
            // Patch only the types in this feature's assembly that have Harmony attributes
            harmony = new Harmony("handytweaks.discard");
            harmony.PatchAll(typeof(DiscardModSystem).Assembly);
        }

        public override void StartServerSide(ICoreServerAPI sapi)
        {
            // tagSeconds: 0 = block only while mode is ON.
            // e.g. set to 300 to also block for 5 minutes even after toggling OFF.
            DiscardSrv = new DiscardServer(sapi, tagSeconds: 0);
            DiscardSrv.Start();
        }

        public override void StartClientSide(ICoreClientAPI capi)
        {
            client = new DiscardClient(capi, startEnabled: false);
            client.Start();
        }

        public override void Dispose()
        {
            harmony?.UnpatchAll("handytweaks.discard");
            base.Dispose();
        }
    }
}
