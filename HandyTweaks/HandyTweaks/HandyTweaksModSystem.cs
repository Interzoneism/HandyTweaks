using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Server;

namespace HandyTweaks
{
    /// <summary>
    /// Bootstrapper: ensures config is created/loaded; actual features live under HandyTweaks.Features.*
    /// </summary>
    public class HandyTweaksModSystem : ModSystem
    {
        public override void Start(ICoreAPI api) => HandyTweaks.HtShared.EnsureLoaded(api);
        public override void StartServerSide(ICoreServerAPI api) { }
        public override void StartClientSide(ICoreClientAPI api) { }
    }
}
