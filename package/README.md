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
- `IncludeDrinks` (default `true`) — make drinks (beer, juice, etc.) stackable too. Set to `false` to leave drinks unstacked.

## Compatibility

Works alongside other mods. Only changes the `maxStack` of food item data
(`FoodSpoon` / `FoodFork`, plus `Drink` if enabled), so it does not touch
recipes, cooking, or anything else.

Serving a stacked dish — onto a serving table, by hand to a customer, or via a
waiter helper — only consumes one plate and keeps the rest of the stack (the base
serving logic would otherwise delete the whole stack). Holding the serve key on a
serving table fills every free tray it can from your stacks instead of just one.

Selling a food/drink stack at the shop pays for the whole stack (per-unit price ×
amount). The base price formula assumed food never stacks, so it would otherwise
pay for roughly one item while taking the entire stack.

Taking a drink/food from an infinite source (a barrel/keg placed in the world)
gives one, not a full stack. The source's default amount is its max stack size,
which used to be 1 for drinks.

## Install

Requires [BepInEx](https://thunderstore.io/c/ale-and-tale-tavern/p/BepInEx/BepInExPack/).
Drop `KinkoCraft.StackableFood.dll` into `BepInEx/plugins`, or install with a mod manager.
