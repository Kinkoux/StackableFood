# Stackable Food

Plated food no longer hogs one inventory slot each. Cooked dishes now stack so
your bags and chests stay tidy.

## Stack size

- **Standalone:** food stacks up to **10** per slot (configurable, 2–99).
- **With [ItemStackerFix](https://thunderstore.io/c/ale-and-tale-tavern/p/Travys_Mods/ItemStackerFix/) installed:** the cap is automatically raised to **99**, matching that mod's stacking system. Shop prices and loot amounts stay vanilla (handled by ItemStackerFix).

The mod detects ItemStackerFix automatically — no setup needed. Install both and
food goes to 99; install this one alone and food goes to 10.

## Configuration

`BepInEx/config/KinkoCraft.StackableFood.cfg`

- `StandaloneMaxStack` (default `10`) — stack size used when ItemStackerFix is **not** installed. Ignored when it is (forced to 99).
- `IncludeDrinks` (default `false`) — also make drinks (beer, juice, etc.) stackable, not just plated food.

## Compatibility

Works alongside other mods. Only changes the `maxStack` of food item data
(`FoodSpoon` / `FoodFork`, plus `Drink` if enabled), so it does not touch
recipes, cooking, or anything else.

Placing a stacked dish on a serving table only puts one plate down and keeps the
rest in your inventory (the base serving logic would otherwise delete the whole
stack).

## Install

Requires [BepInEx](https://thunderstore.io/c/ale-and-tale-tavern/p/BepInEx/BepInExPack/).
Drop `KinkoCraft.StackableFood.dll` into `BepInEx/plugins`, or install with a mod manager.
