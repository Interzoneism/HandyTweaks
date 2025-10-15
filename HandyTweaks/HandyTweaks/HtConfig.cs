namespace HandyTweaks
{
    public class HtConfig
    {
        public string _ModName { get; set; } = "Handy Tweaks — Settings";

        public FastPickupCfg FastPickup { get; set; } = new();
        public RightClickPickupCfg RightClickPickup { get; set; } = new();
        public ThrowCfg ThrowFar { get; set; } = new();
        public OffhandCfg OffhandConditionalHunger { get; set; } = new();
        public DiscardCfg DiscardMode { get; set; } = new();

        public static HtConfig CreateDefault() => new HtConfig();

        public void MergeDefaults()
        {
            OffhandConditionalHunger ??= new OffhandCfg();
            FastPickup ??= new FastPickupCfg();
            RightClickPickup ??= new RightClickPickupCfg();
            ThrowFar ??= new ThrowCfg();
            DiscardMode ??= new DiscardCfg();
        }
    }

    public class FastPickupCfg
    {
        public bool Enabled { get; set; } = true;

        public float FreshDropRadiusBlocks { get; set; } = 4.0f;

        public int PickupDelayMs { get; set; } = 150;
    }

    public class DiscardCfg
    {
        public bool Enabled { get; set; } = true;
        public bool AllowSneakBypass = true;
    }

    public class RightClickPickupCfg
    {
        public bool Enabled { get; set; } = true;
        public bool PickupWithoutMatchingStack { get; set; } = true;
        public bool AllowWhenActiveSlotEmpty { get; set; } = true;
        public bool IncludeHandbookAlternates { get; set; } = true;
    }

    public class ThrowCfg
    {
        public bool Enabled { get; set; } = true;
        public float ThrowVelocityMultiplier { get; set; } = 1.5f;
    }

    public class OffhandCfg
    {
        public bool Enabled { get; set; } = false;
        public int PenaltyPercent { get; set; } = 40;
        public float PenaltyDurationSeconds { get; set; } = 5.0f;
        public bool TriggerOnSprint { get; set; } = true;
        public bool TriggerOnLeftClick { get; set; } = true;
        public bool TriggerOnRightClick { get; set; } = true;
        public bool RequireOffhandItem { get; set; } = true;
    }
}
