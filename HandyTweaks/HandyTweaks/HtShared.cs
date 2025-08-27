using Vintagestory.API.Common;

namespace HandyTweaks
{
    public static class HtShared
    {
        public static HtConfig Config { get; private set; }

        public static void EnsureLoaded(ICoreAPI api)
        {
            if (Config != null) return;

            var loaded = api.LoadModConfig<HtConfig>("HandyTweaks_config.json");
            if (loaded == null)
            {
                Config = HtConfig.CreateDefault();
                api.StoreModConfig(Config, "HandyTweaks_config.json");
            }
            else
            {
                loaded.MergeDefaults();
                Config = loaded;
                api.StoreModConfig(Config, "HandyTweaks_config.json");
            }
        }
    }
}
