using Vintagestory.API.Common;

public class HandyTweaksModSystem : ModSystem
{
    public override void Start(ICoreAPI api)
    {
        HandyTweaks.HtShared.EnsureLoaded(api);
    }
}
