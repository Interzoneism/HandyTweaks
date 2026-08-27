using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;
using Vintagestory.GameContent;

namespace HandyTweaks.Features
{

    public class RightClickPickupInventoryFriendly : ModSystem
    {
        Harmony harmony;

        static bool PickupWithoutMatchingStack;
        static bool AllowWhenActiveSlotEmpty;
        static bool IncludeHandbookAlternates;

        public override void Start(ICoreAPI api)
        {
            HandyTweaks.HtShared.EnsureLoaded(api);
            var cfg = HandyTweaks.HtShared.Config.RightClickPickup;
            if (!cfg.Enabled) return;

            PickupWithoutMatchingStack = cfg.PickupWithoutMatchingStack;
            AllowWhenActiveSlotEmpty = cfg.AllowWhenActiveSlotEmpty;
            IncludeHandbookAlternates = cfg.IncludeHandbookAlternates;

            harmony = new Harmony("handytweaks.rcpick.inventoryfriendly");

            var t = typeof(BlockBehaviorRightClickPickup);
            var m = AccessTools.Method(t, "OnBlockInteractStart",
                new[] { typeof(IWorldAccessor), typeof(IPlayer), typeof(BlockSelection), typeof(EnumHandling).MakeByRefType() });

            var pre = new HarmonyMethod(typeof(RightClickPickupInventoryFriendly), nameof(OnBlockInteractStart_Prefix));
            harmony.Patch(m, prefix: pre);
        }

        public override void Dispose() => harmony?.UnpatchAll("handytweaks.rcpick.inventoryfriendly");

        static bool OnBlockInteractStart_Prefix(
            BlockBehaviorRightClickPickup __instance,
            IWorldAccessor world,
            IPlayer byPlayer,
            BlockSelection blockSel,
            ref EnumHandling handling,
            ref bool __result)
        {
            if (world == null || byPlayer == null || blockSel == null) return true;

            try
            {
                var fiDropsPickupMode = AccessTools.Field(typeof(BlockBehaviorRightClickPickup), "dropsPickupMode");
                var fiPickupSound = AccessTools.Field(typeof(BlockBehaviorRightClickPickup), "pickupSound");
                var fiBlock = AccessTools.Field(typeof(BlockBehavior), "block");

                bool dropsPickupMode = fiDropsPickupMode != null && (bool)fiDropsPickupMode.GetValue(__instance);
                SoundAttributes? pickupSound = fiPickupSound?.GetValue(__instance) is SoundAttributes sound
                    ? sound
                    : null;
                var block = (Block)fiBlock.GetValue(__instance);

                ItemStack[] dropStacks = new ItemStack[] { block.OnPickBlock(world, blockSel.Position) };
                var activeSlot = byPlayer.InventoryManager?.ActiveHotbarSlot;

                var candidates = new List<ItemStack>();
                if (dropStacks != null) candidates.AddRange(dropStacks.Where(s => s != null));

                if (dropsPickupMode)
                {
                    float dropMul = 1f;
                    if (block.Attributes != null && block.Attributes.IsTrue("forageStatAffected"))
                    {
                        dropMul *= byPlayer.Entity.Stats.GetBlended("forageDropRate");
                    }
                    dropStacks = block.GetDrops(world, blockSel.Position, byPlayer, dropMul);
                    if (dropStacks != null) candidates.AddRange(dropStacks.Where(s => s != null));

                    if (IncludeHandbookAlternates)
                    {
                        var allDropsForHB = block.GetDropsForHandbook(new ItemStack(block, 1), byPlayer);
                        if (allDropsForHB != null)
                        {
                            foreach (var dd in allDropsForHB)
                                if (dd?.ResolvedItemstack != null) candidates.Add(dd.ResolvedItemstack);
                        }
                    }
                }

                bool allowPickup;
                if (PickupWithoutMatchingStack)
                {
                    allowPickup = true;
                }
                else
                {
                    bool inventoryHasMatch = InventoryContainsAny(byPlayer, world, candidates);
                    allowPickup = inventoryHasMatch || (AllowWhenActiveSlotEmpty && activeSlot != null && activeSlot.Empty);
                }

                if (!allowPickup || !world.Claims.TryAccess(byPlayer, blockSel.Position, EnumBlockAccessFlags.BuildOrBreak))
                {
                    return true; // vanilla path
                }

                if (!byPlayer.Entity.Controls.ShiftKey)
                {
                    if (world.Side == EnumAppSide.Server && BlockBehaviorReinforcable.AllowRightClickPickup(world, blockSel.Position, byPlayer))
                    {
                        bool blockToBreak = true;

                        if (dropStacks != null)
                        {
                            foreach (ItemStack stack in dropStacks)
                            {
                                if (stack == null) continue;

                                ItemStack origStack = stack.Clone();

                                if (!byPlayer.InventoryManager.TryGiveItemstack(stack, true))
                                {
                                    world.SpawnItemEntity(stack, blockSel.Position.ToVec3d().AddCopy(0.5, 0.1, 0.5), null);
                                }

                                world.Logger.Audit("{0} Took {1}x{2} from Ground at {3}.",
                                    byPlayer.PlayerName, origStack.StackSize, origStack.Collectible.Code, blockSel.Position);

                                var tree = new TreeAttribute();
                                tree["itemstack"] = new ItemstackAttribute(origStack.Clone());
                                tree["byentityid"] = new LongAttribute(byPlayer.Entity.EntityId);
                                world.Api.Event.PushEvent("onitemcollected", tree);

                                if (blockToBreak)
                                {
                                    blockToBreak = false;
                                    world.BlockAccessor.SetBlock(0, blockSel.Position);
                                    world.BlockAccessor.TriggerNeighbourBlockUpdate(blockSel.Position);
                                }

                                var placeSound = block.GetSounds(world.BlockAccessor, blockSel, null).Place;
                                var soundToPlay = pickupSound.HasValue && pickupSound.Value.Location != null
                                    ? pickupSound.Value
                                    : placeSound;
                                world.PlaySoundAt(soundToPlay, byPlayer, null, 1f);
                            }
                        }
                    }

                    handling = EnumHandling.PreventDefault;
                    __result = true;
                    return false;
                }

                return true; 
            }
            catch { return true; }
        }

        static bool InventoryContainsAny(IPlayer player, IWorldAccessor world, IEnumerable<ItemStack> candidates)
        {
            if (player?.InventoryManager == null || candidates == null) return false;
            var cand = candidates.Where(s => s != null).ToList();
            if (cand.Count == 0) return false;

            foreach (var inv in EnumeratePlayerInventories(player))
            {
                if (inv == null) continue;
                for (int i = 0; i < inv.Count; i++)
                {
                    var slot = inv[i];
                    if (slot == null || slot.Empty) continue;

                    foreach (var c in cand)
                        if (slot.Itemstack.Equals(world, c, GlobalConstants.IgnoredStackAttributes))
                            return true;
                }
            }
            return false;
        }

        static IEnumerable<IInventory> EnumeratePlayerInventories(IPlayer player)
        {
            var mgr = player?.InventoryManager;
            if (mgr == null) yield break;

            foreach (var inv in mgr.InventoriesOrdered)
            {
                if (inv is InventoryBasePlayer) yield return inv;
            }
        }
    }
}
