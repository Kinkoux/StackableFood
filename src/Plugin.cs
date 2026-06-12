using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using BepInEx;
using BepInEx.Bootstrap;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;

namespace KinkoCraft.StackableFood
{
	internal static class PluginMeta
	{
		public const string Guid = "KinkoCraft.StackableFood";
		public const string Name = "Stackable Food";
		public const string Version = "1.0.2";

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
				false,
				"Also make drinks (beer, juice, etc.) stackable, not just plated food.");

			Harmony harmony = new Harmony(PluginMeta.Guid);
			harmony.PatchAll(typeof(FoodStackPatch));
			harmony.PatchAll(typeof(ServingStackFix));

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
	/// Fixes vanilla data loss when a stacked dish is placed on a serving table slot.
	///
	/// ServingTableSlot.ServeSingleDishServerRpc puts a single dish on the slot but
	/// removes the WHOLE source item with ContainerNet.RemoveItemById — so a stack of N
	/// loses all N while only 1 ends up on the table. We transpile that one call site to
	/// our helper, which instead decrements the stack by one and only removes the item
	/// when its last unit is served. Nothing else in the method changes.
	/// </summary>
	public class ServingStackFix
	{
		[HarmonyPatch(typeof(ServingTableSlot), "ServeSingleDishServerRpc")]
		[HarmonyTranspiler]
		private static IEnumerable<CodeInstruction> SwapRemoveWithDecrement(IEnumerable<CodeInstruction> instructions)
		{
			MethodInfo removeById = AccessTools.Method(typeof(ContainerNet), nameof(ContainerNet.RemoveItemById));
			MethodInfo replacement = AccessTools.Method(typeof(ServingStackFix), nameof(ServeOne));

			int swapped = 0;
			foreach (CodeInstruction ins in instructions)
			{
				if ((ins.opcode == OpCodes.Callvirt || ins.opcode == OpCodes.Call)
					&& ins.operand as MethodInfo == removeById)
				{
					yield return new CodeInstruction(OpCodes.Call, replacement);
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
					"ServingStackFix: RemoveItemById call not found in ServeSingleDishServerRpc; " +
					"stacked dishes may still be lost on serving tables.");
			}
		}

		/// <summary>
		/// Removes a single unit of the dish identified by <paramref name="itemId"/>:
		/// decrements a stack of more than one, or removes the item outright when it is
		/// the last one. Matches the (ContainerNet, uint) -> bool shape of the call it
		/// replaces, so the surrounding IL is unchanged.
		/// </summary>
		public static bool ServeOne(ContainerNet cont, uint itemId)
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
	}
}
