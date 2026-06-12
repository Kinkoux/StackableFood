using System.Linq;
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
		public const string Version = "1.0.1";

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
}
