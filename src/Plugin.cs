using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using BepInEx;
using BepInEx.Bootstrap;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using Unity.Netcode;

namespace KinkoCraft.StackableFood
{
	internal static class PluginMeta
	{
		public const string Guid = "KinkoCraft.StackableFood";
		public const string Name = "Stackable Food";
		public const string Version = "1.0.7";

		// ItemStackerFix ("ItemStack99 Safe"). When this mod is present we hand the
		// stacking cap up to 99 so the two mods agree; its own AddNewItem patch already
		// allows inventory stacks up to 99, and its shop/loot patches keep prices vanilla.
		public const string ItemStackerFixGuid = "com.travv.aleandtale.itemstack99safe";
		public const ushort ItemStackerFixCap = 99;
	}

	[BepInPlugin(PluginMeta.Guid, PluginMeta.Name, PluginMeta.Version)]
	// Soft dependency: if ItemStackerFix is installed it loads first, so it is present
	// in Chainloader.PluginInfos by the time we read it. Absent => we run standalone.
	[BepInDependency(PluginMeta.ItemStackerFixGuid, BepInDependency.DependencyFlags.SoftDependency)]
	public class StackableFoodPlugin : BaseUnityPlugin
	{
		internal static ManualLogSource Log;

		// The stack size to apply to food when ItemStackerFix is NOT installed.
		internal static ConfigEntry<int> StandaloneMaxStack;
		// Whether drinks (ItemData.Type.Drink) should be made stackable too.
		internal static ConfigEntry<bool> IncludeDrinks;

		private void Awake()
		{
			Log = Logger;

			StandaloneMaxStack = Config.Bind(
				"General",
				"StandaloneMaxStack",
				10,
				new ConfigDescription(
					"How many food items stack in one inventory slot when ItemStackerFix is NOT installed. " +
					"With ItemStackerFix installed this is ignored and the cap is 99.",
					new AcceptableValueRange<int>(2, 99)));

			IncludeDrinks = Config.Bind(
				"General",
				"IncludeDrinks",
				true,
				"Make drinks (beer, juice, etc.) stackable too. Set to false to leave drinks unstacked.");

			Harmony harmony = new Harmony(PluginMeta.Guid);
			harmony.PatchAll(typeof(FoodStackPatch));
			harmony.PatchAll(typeof(ServingStackFix));
			harmony.PatchAll(typeof(WorldSourceStackFix));

			Log.LogInfo($"{PluginMeta.Name} v{PluginMeta.Version} loaded.");
		}
	}

	/// <summary>
	/// Raises the inventory stack size of food items. The game stacks purely off
	/// ItemData.maxStack (ContainerNet.AddNewItem merges while amount &lt; maxStack),
	/// so bumping that one field is all it takes.
	/// </summary>
	public class FoodStackPatch
	{
		// Run after ItemManager.Awake has populated its item lookup. The dictionary
		// stores references to these same ItemData assets and stacking reads maxStack
		// live, so changing it here takes effect immediately and for the whole session.
		[HarmonyPatch(typeof(ItemManager), "Awake")]
		[HarmonyPostfix]
		private static void BumpFoodStacks(ItemManager __instance)
		{
			bool stackerFix = Chainloader.PluginInfos.ContainsKey(PluginMeta.ItemStackerFixGuid);
			ushort cap = stackerFix
				? PluginMeta.ItemStackerFixCap
				: (ushort)StackableFoodPlugin.StandaloneMaxStack.Value;

			bool includeDrinks = StackableFoodPlugin.IncludeDrinks.Value;

			ItemData[] all = __instance.itemDataHub?.itemData;
			if (all == null)
			{
				StackableFoodPlugin.Log.LogWarning("itemDataHub.itemData was null; no food made stackable.");
				return;
			}

			int changed = 0;
			foreach (ItemData data in all)
			{
				if (data == null || !IsFood(data, includeDrinks))
				{
					continue;
				}
				// Only raise non-stacking food. Never shrink something another mod (or the
				// base game) already made stack higher.
				if (data.maxStack < cap && data.maxStack <= 1)
				{
					data.maxStack = cap;
					changed++;
				}
			}

			StackableFoodPlugin.Log.LogInfo(
				$"Stackable Food: cap={cap} ({(stackerFix ? "ItemStackerFix detected" : "standalone")}), " +
				$"includeDrinks={includeDrinks}, updated {changed} item(s).");
		}

		private static bool IsFood(ItemData data, bool includeDrinks)
		{
			switch (data.type)
			{
				case ItemData.Type.FoodSpoon:
				case ItemData.Type.FoodFork:
					return true;
				case ItemData.Type.Drink:
					return includeDrinks;
				default:
					return false;
			}
		}
	}

	/// <summary>
	/// Keeps stacked dishes intact through every serving path. The vanilla code was
	/// written assuming food never stacks (maxStack == 1), so several spots remove the
	/// WHOLE source item when only one dish should leave the stack. Once food stacks,
	/// that destroys the rest of the stack. These patches make each path consume exactly
	/// one unit, and let "serve all" (hold) empty as much of a stack as there are free
	/// slots.
	/// </summary>
	public class ServingStackFix
	{
		// --- 1) Place one dish onto a serving slot (single press E) -----------------
		// ServingTableSlot.ServeSingleDishServerRpc removed the whole item via
		// RemoveItemById while putting only one plate down.
		[HarmonyPatch(typeof(ServingTableSlot), "ServeSingleDishServerRpc")]
		[HarmonyTranspiler]
		private static IEnumerable<CodeInstruction> Fix_ServeSingleDish(IEnumerable<CodeInstruction> instructions)
		{
			return SwapCall(instructions,
				AccessTools.Method(typeof(ContainerNet), nameof(ContainerNet.RemoveItemById)),
				AccessTools.Method(typeof(ServingStackFix), nameof(RemoveOneById)),
				"ServeSingleDishServerRpc");
		}

		// --- 2) Serve a dish to a seated customer (carry to their table) ------------
		// TableFeedPlace.ServeDish removed the whole stack via RemoveItemByDataId; a
		// 4-stack handed to a customer wiped all 4 while serving one.
		[HarmonyPatch(typeof(TableFeedPlace), "ServeDish")]
		[HarmonyTranspiler]
		private static IEnumerable<CodeInstruction> Fix_TableFeedServe(IEnumerable<CodeInstruction> instructions)
		{
			return SwapCall(instructions,
				AccessTools.Method(typeof(ContainerNet), nameof(ContainerNet.RemoveItemByDataId)),
				AccessTools.Method(typeof(ServingStackFix), nameof(RemoveOneByDataId)),
				"TableFeedPlace.ServeDish");
		}

		// --- 2b) Same fix for the waiter helper carrying a (now stackable) tray -----
		[HarmonyPatch(typeof(HelperWaiterServeDishState), "TryServeCustomerDish")]
		[HarmonyTranspiler]
		private static IEnumerable<CodeInstruction> Fix_WaiterServe(IEnumerable<CodeInstruction> instructions)
		{
			return SwapCall(instructions,
				AccessTools.Method(typeof(ContainerNet), nameof(ContainerNet.RemoveItemByDataId)),
				AccessTools.Method(typeof(ServingStackFix), nameof(RemoveOneByDataId)),
				"HelperWaiterServeDishState.TryServeCustomerDish");
		}

		private static IEnumerable<CodeInstruction> SwapCall(
			IEnumerable<CodeInstruction> instructions, MethodInfo from, MethodInfo to, string where)
		{
			int swapped = 0;
			foreach (CodeInstruction ins in instructions)
			{
				if ((ins.opcode == OpCodes.Callvirt || ins.opcode == OpCodes.Call)
					&& ins.operand as MethodInfo == from)
				{
					yield return new CodeInstruction(OpCodes.Call, to);
					swapped++;
				}
				else
				{
					yield return ins;
				}
			}
			if (swapped == 0)
			{
				StackableFoodPlugin.Log.LogWarning(
					$"ServingStackFix: '{from.Name}' call not found in {where}; stacked dishes may still be lost there.");
			}
		}

		// --- 3) Hold E "serve all": fill every free slot, not just one per stack -----
		// ServeDishesServerRpc only placed one plate per distinct food entry. After it
		// runs on the server, top up the remaining free slots from whatever food is
		// left so a stack empties into all the trays it can reach.
		//
		// The RPC body flips __rpc_exec_stage to Send at the top of its execute branch,
		// so we capture the stage in a prefix (before that happens) and read it back in
		// the postfix — running the top-up only on the real server-side execute pass.
		[ThreadStatic]
		private static bool _serveExecutePass;

		[HarmonyPatch(typeof(ServingTable), "ServeDishesServerRpc")]
		[HarmonyPrefix]
		private static void CaptureServeStage(ServingTable __instance)
		{
			_serveExecutePass = IsServerExecuteStage(__instance);
		}

		[HarmonyPatch(typeof(ServingTable), "ServeDishesServerRpc")]
		[HarmonyPostfix]
		private static void FillRemainingSlots(ServingTable __instance, ServerRpcParams serverRpcParams)
		{
			if (!_serveExecutePass)
			{
				return;
			}
			if (!ContainerManager.Instance.GetPlayerContainer(serverRpcParams.Receive.SenderClientId, out ContainerNet cont))
			{
				return;
			}
			while (__instance.HaveFreeSlot(out ServingTableSlot slot))
			{
				Item food = default(Item);
				bool found = false;
				foreach (Item it in cont.items.ToList())
				{
					if (it.amount > 0 && ItemManager.Instance.GetItemData(it.dataId, out ItemData d) && IsServable(d.type))
					{
						food = it;
						found = true;
						break;
					}
				}
				if (!found || !cont.RemoveAmount(food.dataId, 1))
				{
					break;
				}
				slot.itemDataId.Value = food.dataId;
				slot.itemRarity = food.rarity;
			}
		}

		// ---- helpers ---------------------------------------------------------------

		private static readonly FieldInfo RpcStageField =
			AccessTools.Field(typeof(NetworkBehaviour), "__rpc_exec_stage");

		// The RPC body runs once to send and once to execute on the server; only act on
		// the execute pass so we don't fill slots on the client or twice on the host.
		private static bool IsServerExecuteStage(NetworkBehaviour nb)
		{
			object stage = RpcStageField?.GetValue(nb);
			return stage != null && stage.ToString() == "Execute";
		}

		internal static bool IsServable(ItemData.Type type)
		{
			return type == ItemData.Type.FoodFork || type == ItemData.Type.FoodSpoon || type == ItemData.Type.Drink;
		}

		// --- 4) Selling a stack pays for the whole stack, not one unit --------------
		// ItemManager.GetItemSellPrice was written when food had maxStack == 1, so its
		// price is the value of ONE dish. Its stack branch (price * amount / maxStack)
		// assumes price means a full maxStack-sized stack — true for vanilla stackables,
		// false for food. After we raise maxStack, selling a food stack removes all of it
		// but pays for ~1. Recompute food/drink sell price as per-unit value * amount.
		//
		// Postfix runs last, so it also corrects ItemStackerFix's shop-sell price (it
		// patches the same method with a prefix). Harmless if food turns out unsellable.
		[HarmonyPatch(typeof(ItemManager), nameof(ItemManager.GetItemSellPrice))]
		[HarmonyPostfix]
		private static void FixFoodSellPrice(Item item, ItemData itemData, ref ushort __result)
		{
			if (itemData == null || !IsServable(itemData.type) || item.amount <= 1)
			{
				return;
			}
			// Per-unit value: base price with the same rarity scaling vanilla applies.
			// Food has no durability/charge, so those branches don't apply.
			float perUnit = itemData.price;
			if (itemData.hasRarity)
			{
				if (itemData.shopRarityPrices != null && itemData.shopRarityPrices.Count > 0)
				{
					perUnit = itemData.shopRarityPrices[item.rarity];
				}
				else if (item.rarity == ItemData.Rarity.Uncommon)
				{
					perUnit *= 1.2f;
				}
				else if (item.rarity == ItemData.Rarity.Rare)
				{
					perUnit *= 1.6f;
				}
				else if (item.rarity == ItemData.Rarity.Epic)
				{
					perUnit *= 2.2f;
				}
				else if (item.rarity == ItemData.Rarity.Legendary)
				{
					perUnit *= 3f;
				}
			}
			long total = (long)(int)perUnit * item.amount;
			__result = (ushort)(total > ushort.MaxValue ? ushort.MaxValue : total);
		}

		/// <summary>
		/// Removes one unit of the item with id <paramref name="itemId"/> — decrements a
		/// stack, or removes the item on its last unit. Matches (ContainerNet, uint)->bool.
		/// </summary>
		public static bool RemoveOneById(ContainerNet cont, uint itemId)
		{
			if (cont == null)
			{
				return false;
			}
			if (cont.GetItemById(itemId, out Item item, false) && item.amount > 1)
			{
				item.amount -= 1;
				cont.SetItem(item, item.order);
				Game.Instance.InvokeGameEvent(GameEvent.ItemRemoved, item.dataId, 1);
				return true;
			}
			return cont.RemoveItemById(itemId);
		}

		/// <summary>
		/// Removes one unit of the first stack matching <paramref name="dataId"/> and
		/// returns that single dish (amount 1, original rarity). Matches the
		/// (ContainerNet, ushort, out Item) -> bool shape of RemoveItemByDataId.
		/// </summary>
		public static bool RemoveOneByDataId(ContainerNet cont, ushort dataId, out Item removed)
		{
			removed = default(Item);
			if (cont == null)
			{
				return false;
			}
			foreach (Item it in cont.items.ToList())
			{
				if (it.dataId != dataId || it.amount <= 0)
				{
					continue;
				}
				removed = it;
				removed.amount = 1; // one dish served; reward math reads rarity, not amount
				if (it.amount > 1)
				{
					Item dec = it;
					dec.amount -= 1;
					cont.SetItem(dec, dec.order);
					Game.Instance.InvokeGameEvent(GameEvent.ItemRemoved, it.dataId, 1);
				}
				else
				{
					cont.RemoveItemById(it.id);
				}
				return true;
			}
			return false;
		}
	}

	/// <summary>
	/// Stops "infinite" world sources — barrels/kegs placed in the level as CollectibleNet
	/// objects — from handing out a whole stack of a now-stackable drink/food.
	///
	/// A pre-placed source spawns with item.dataId == 0, and CollectibleNet.OnNetworkSpawn
	/// fills its amount with itemData.maxStack. Vanilla food/drink maxStack was 1, so the
	/// barrel gave one; once we raise maxStack, the same code hands out 10/99 at a time.
	/// We clamp that initial fill back to 1 for food/drink. Dropped-item collectibles
	/// (dataId already set, carrying a real amount) hit a different branch and are untouched.
	/// </summary>
	public class WorldSourceStackFix
	{
		// __state carries "this spawn just default-initialized its item" from prefix to
		// postfix — we can only tell before OnNetworkSpawn runs, but can only clamp after.
		[HarmonyPatch(typeof(CollectibleNet), "OnNetworkSpawn")]
		[HarmonyPrefix]
		private static void DetectFreshSource(CollectibleNet __instance, out bool __state)
		{
			__state = __instance.IsServer && __instance.item != null && __instance.item.Value.dataId == 0;
		}

		[HarmonyPatch(typeof(CollectibleNet), "OnNetworkSpawn")]
		[HarmonyPostfix]
		private static void ClampFreshSource(CollectibleNet __instance, bool __state)
		{
			if (!__state || __instance.itemData == null || !ServingStackFix.IsServable(__instance.itemData.type))
			{
				return;
			}
			Item v = __instance.item.Value;
			if (v.amount > 1)
			{
				v.amount = 1;
				__instance.item.Value = v;
			}
		}

		// --- Barrel / keg dispenser (ItemCharger): give one drink, not a full stack ---
		// ItemCharger.InteractServerRpc fills an empty mug (or consumes supply ingredients)
		// into a drink/food. It builds the result with `new Item(itemData)` — whose ctor
		// defaults amount to maxStack — and in one branch explicitly does
		// `outItem.amount = itemData.maxStack`. Vanilla drink maxStack was 1 so a tap gave
		// one; with stacking on, every tap yields 10/99. We rewrite the result amount to 1
		// for food/drink while leaving maxStack high, so taps still merge into a stack.
		[HarmonyPatch(typeof(ItemCharger), "InteractServerRpc")]
		[HarmonyTranspiler]
		private static IEnumerable<CodeInstruction> Fix_BarrelDispense(IEnumerable<CodeInstruction> instructions)
		{
			ConstructorInfo itemCtor = AccessTools.Constructor(typeof(Item), new[] { typeof(ItemData) });
			MethodInfo makeOne = AccessTools.Method(typeof(WorldSourceStackFix), nameof(MakeOne));
			FieldInfo maxStackFld = AccessTools.Field(typeof(ItemData), nameof(ItemData.maxStack));
			FieldInfo amountFld = AccessTools.Field(typeof(Item), nameof(Item.amount));
			MethodInfo dispenseAmt = AccessTools.Method(typeof(WorldSourceStackFix), nameof(DispenseAmount));

			List<CodeInstruction> code = new List<CodeInstruction>(instructions);
			int ctorSwaps = 0, amountSwaps = 0;
			for (int i = 0; i < code.Count; i++)
			{
				// `new Item(itemData)` -> MakeOne(itemData): same stack shape (ItemData -> Item).
				if (code[i].opcode == OpCodes.Newobj && code[i].operand as ConstructorInfo == itemCtor)
				{
					code[i] = new CodeInstruction(OpCodes.Call, makeOne);
					ctorSwaps++;
				}
				// The `ldfld ItemData::maxStack` feeding `stfld Item::amount` (not the
				// `maxStack > 1` guard, which is followed by a comparison) -> DispenseAmount.
				else if (code[i].opcode == OpCodes.Ldfld && code[i].operand as FieldInfo == maxStackFld
					&& i + 1 < code.Count && code[i + 1].opcode == OpCodes.Stfld
					&& code[i + 1].operand as FieldInfo == amountFld)
				{
					code[i] = new CodeInstruction(OpCodes.Call, dispenseAmt);
					amountSwaps++;
				}
			}
			if (ctorSwaps == 0 && amountSwaps == 0)
			{
				StackableFoodPlugin.Log.LogWarning(
					"WorldSourceStackFix: no dispense sites found in ItemCharger.InteractServerRpc; " +
					"barrels may still hand out full stacks.");
			}
			return code;
		}

		// The literal barrel/keg "dispenser" (ItemDispenser, e.g. InteractiveObject.Type
		// .BarrelDispencer). TakeServerRpc builds the drink with `new Item(itemData)` and only
		// overrides the amount when `givenItemAmount != 0`. Barrels are configured with
		// givenItemAmount == 0 ("a full stack"), so the override is skipped and the ctor's
		// maxStack default leaks through — every tap yields 10/99. Swapping the ctor to
		// MakeOne makes that fall-through give one for food/drink (maxStack stays high, so
		// taps still merge). A configured givenItemAmount (e.g. 5) still wins, unchanged.
		[HarmonyPatch(typeof(ItemDispenser), "TakeServerRpc")]
		[HarmonyTranspiler]
		private static IEnumerable<CodeInstruction> Fix_ItemDispenser(IEnumerable<CodeInstruction> instructions)
		{
			ConstructorInfo itemCtor = AccessTools.Constructor(typeof(Item), new[] { typeof(ItemData) });
			MethodInfo makeOne = AccessTools.Method(typeof(WorldSourceStackFix), nameof(MakeOne));

			int swaps = 0;
			List<CodeInstruction> code = new List<CodeInstruction>(instructions);
			for (int i = 0; i < code.Count; i++)
			{
				if (code[i].opcode == OpCodes.Newobj && code[i].operand as ConstructorInfo == itemCtor)
				{
					code[i] = new CodeInstruction(OpCodes.Call, makeOne);
					swaps++;
				}
			}
			if (swaps == 0)
			{
				StackableFoodPlugin.Log.LogWarning(
					"WorldSourceStackFix: no `new Item(itemData)` found in ItemDispenser.TakeServerRpc; " +
					"barrel dispensers may still hand out full stacks.");
			}
			return code;
		}

		/// <summary>Builds an item like `new Item(data)` but gives food/drink amount 1.</summary>
		public static Item MakeOne(ItemData data)
		{
			Item it = new Item(data);
			if (data != null && ServingStackFix.IsServable(data.type))
			{
				it.amount = 1;
			}
			return it;
		}

		/// <summary>Replacement for a raw `itemData.maxStack` read used as a produced amount.</summary>
		public static ushort DispenseAmount(ItemData data)
		{
			if (data == null)
			{
				return 1;
			}
			return ServingStackFix.IsServable(data.type) ? (ushort)1 : data.maxStack;
		}
	}
}
