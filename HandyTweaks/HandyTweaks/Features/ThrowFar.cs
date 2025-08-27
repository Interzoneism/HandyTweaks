using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

namespace HandyTweaks.Features
{
    /// <summary>
    /// ThrowFar: Multiplies the initial velocity when dropping items from the inventory ground slot.
    /// Config: HtConfig.ThrowFar.ThrowVelocityMultiplier
    /// </summary>
    public class ThrowFar : ModSystem
    {
        Harmony harmony;
        static float VelocityMul;

        public override void Start(ICoreAPI api)
        {
            HandyTweaks.HtShared.EnsureLoaded(api);
            var cfg = HandyTweaks.HtShared.Config.ThrowFar;
            if (!cfg.Enabled) return;

            VelocityMul = cfg.ThrowVelocityMultiplier;
            harmony = new Harmony("handytweaks.throwfar");

            var groundType = AccessTools.TypeByName("Vintagestory.Common.InventoryPlayerGround");
            var mOnMod = AccessTools.Method(groundType, "OnItemSlotModified", new[] { typeof(ItemSlot) });
            var pre = new HarmonyMethod(typeof(ThrowFar), nameof(Ground_OnItemSlotModified_Prefix));
            harmony.Patch(mOnMod, prefix: pre);
        }

        public override void Dispose() => harmony?.UnpatchAll("handytweaks.throwfar");

        static bool Ground_OnItemSlotModified_Prefix(object __instance, ItemSlot slot)
        {
            try
            {
                if (slot?.Itemstack == null) return false;

                var api = (ICoreAPI)AccessTools.Field(__instance.GetType().BaseType, "Api").GetValue(__instance);
                var playerUid = (string)AccessTools.Field(__instance.GetType().BaseType, "playerUID").GetValue(__instance);

                var player = api.World.PlayerByUid(playerUid);
                var entityplayer = player?.Entity;
                if (entityplayer == null) return false;

                var spawnpos = entityplayer.SidedPos.XYZ.Add(
                    0.0,
                    entityplayer.CollisionBox.Y1 + entityplayer.CollisionBox.Y2 * 0.75f,
                    0.0
                );

                Vec3d velocity =
                    (entityplayer.SidedPos.AheadCopy(1.0).XYZ.Add(entityplayer.LocalEyePos) - spawnpos) * 0.1
                    + entityplayer.SidedPos.Motion * 1.5;

                velocity.Mul(VelocityMul);

                var stack = slot.Itemstack;
                slot.Itemstack = null;

                while (stack.StackSize > 0)
                {
                    var velo = velocity.Clone()
                        .Add((float)(api.World.Rand.NextDouble() - 0.5) / 60f,
                             (float)(api.World.Rand.NextDouble() - 0.5) / 60f,
                             (float)(api.World.Rand.NextDouble() - 0.5) / 60f);

                    var dropStack = stack.Clone();
                    dropStack.StackSize = System.Math.Min(4, stack.StackSize);
                    stack.StackSize -= dropStack.StackSize;

                    api.World.SpawnItemEntity(dropStack, spawnpos, velo);
                }
                return false;
            }
            catch { return true; }
        }
    }
}
