using HarmonyLib;
using HandyTweaks.Internal;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

namespace HandyTweaks.Features
{

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

                var player = (__instance as InventoryBasePlayer)?.Player;
                var entityplayer = player?.Entity;
                if (entityplayer == null) return false;
                var world = entityplayer.World;

                var spawnpos = entityplayer.Pos.XYZ.Add(
                    0.0,
                    entityplayer.CollisionBox.Y1 + entityplayer.CollisionBox.Y2 * 0.75f,
                    0.0
                );

                Vec3d velocity =
                    (entityplayer.Pos.AheadCopy(1.0).XYZ.Add(entityplayer.LocalEyePos) - spawnpos) * 0.1
                    + entityplayer.Pos.Motion * 1.5;

                velocity.Mul(VelocityMul);

                var stack = slot.Itemstack;
                slot.Itemstack = null;

                while (stack.StackSize > 0)
                {
                    var velo = velocity.Clone()
                        .Add((float)(world.Rand.NextDouble() - 0.5) / 60f,
                             (float)(world.Rand.NextDouble() - 0.5) / 60f,
                             (float)(world.Rand.NextDouble() - 0.5) / 60f);

                    var dropStack = stack.Clone();
                    dropStack.StackSize = System.Math.Min(4, stack.StackSize);
                    stack.StackSize -= dropStack.StackSize;

                    var thrownEntity = world.SpawnItemEntity(dropStack, spawnpos, velo) as EntityItem;
                    if (thrownEntity != null)
                    {
                        thrownEntity.ByPlayerUid = player.PlayerUID;
                        HtPickupCore.MarkThrown(thrownEntity.EntityId, world.ElapsedMilliseconds);
                    }
                }
                return false;
            }
            catch { return true; }
        }
    }
}
