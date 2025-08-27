using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.API.Client;
using Vintagestory.API.Server;

namespace HandyTweaks.Features.Discard
{
    /// <summary>
    /// Fristående ModSystem för Discard. Har guard-flaggor så att dubbelstart inte gör skada.
    /// </summary>
    public class DiscardModSystem : ModSystem
    {
        private Harmony harmony;
        private DiscardClient client;
        public static DiscardServer DiscardSrv; // använd av patch

        // Guards för att undvika dubbelinit om flera ModSystem försöker
        private static bool patched;
        private static bool serverStarted;
        private static bool clientStarted;

        public override void Start(ICoreAPI api)
        {
            if (patched) return;
            patched = true;

            harmony = new Harmony("handytweaks.discard");
            harmony.PatchAll(typeof(DiscardModSystem).Assembly);
        }

        public override void StartServerSide(ICoreServerAPI sapi)
        {
            if (serverStarted) return;
            serverStarted = true;

            // tagSeconds: 0 = blockera endast medan mode är ON
            DiscardSrv = new DiscardServer(sapi, tagSeconds: 0);
            DiscardSrv.Start();
        }

        public override void StartClientSide(ICoreClientAPI capi)
        {
            if (clientStarted) return;
            clientStarted = true;

            client = new DiscardClient(capi, startEnabled: false);
            client.Start();
        }

        public override void Dispose()
        {
            // Låt Harmony vara kvar; om spelet stänger ned gör det inget.
            base.Dispose();
        }
    }
}
