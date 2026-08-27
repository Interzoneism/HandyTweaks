# Handy Tweaks

A collection of quality-of-life tweaks for [Vintage Story](https://www.vintagestory.at/) focused on better item and inventory handling.

**Mod ID:** `handytweaks` | **Side:** Universal (client + server) | **Author:** Interzoner

---

> ### 🙏 A Sincere Apology
>
> I am genuinely, deeply sorry. This entire mod was written with AI assistance, and I am fully aware that means the code may read like a fever dream authored by a very confident robot that has never actually played Vintage Story. There are Harmony patches nested inside Harmony patches. There are reflection calls hunting for fields by name-fragment in a loop. There is a `ThreadStatic` variable tracking drop context across a stack depth counter. It works — probably — but if you are trying to read it, I am so, so sorry. Please accept my humblest apologies for any suffering caused. You deserved better. We all did.

---

## Features

### 🔥 Fast Pickup Plus
When you break a block, any items that drop nearby are automatically collected for you after a short configurable delay. No more frantically clicking on drops before they scatter.

- Detects the freshly-spawned items within a configurable radius of the broken block
- Respects server-side "sneak to pick up" settings
- Integrates with **Discard Mode** so discarded items are never auto-collected
- Only activates briefly after a block is broken — it does not auto-vacuum your surroundings

**Config (`FastPickup`):**
| Key | Default | Description |
|---|---|---|
| `Enabled` | `true` | Enable/disable the feature |
| `FreshDropRadiusBlocks` | `4.0` | Radius (in blocks) to scan for fresh drops after breaking a block |
| `PickupDelayMs` | `150` | Milliseconds to wait before collecting fresh drops (0 = instant) |

---

### 🗑️ Discard Mode
Lets you throw away items without immediately picking them back up. When Discard Mode is active, any item you drop (and all identical items on the ground) will be blocked from re-entering your inventory until you turn it off.

- Toggle with the `/htdiscard toggle` command or the **B** hotkey (client-side)
- Blocked items are tracked by item code, so the whole stack type stays on the ground
- Sneaking bypasses the block so you can still pick up items intentionally (configurable)
- Automatically clears the blocklist when you turn Discard Mode off

**Commands:**
| Command | Description |
|---|---|
| `/htdiscard on` | Enable Discard Mode |
| `/htdiscard off` | Disable Discard Mode and clear the blocklist |
| `/htdiscard toggle` | Toggle Discard Mode (also bound to **B**) |
| `/htdiscard status` | Show current state and how many item types are blocked |

**Config (`DiscardMode`):**
| Key | Default | Description |
|---|---|---|
| `Enabled` | `true` | Enable/disable the feature |
| `AllowSneakBypass` | `true` | Sneaking allows picking up blocked items |

---

### 🖱️ Right-Click Pickup (Inventory Friendly)
Makes right-click block pickup smarter. By default, Vintage Story's right-click-to-pick-up behavior can deposit items into your active slot even when you don't want it to. This feature patches that behavior to check your existing inventory first.

- Optionally requires you to already have a matching item in your inventory before pickup is allowed
- Can allow pickup when your active hotbar slot is empty
- Supports handbook alternate drops for matching

**Config (`RightClickPickup`):**
| Key | Default | Description |
|---|---|---|
| `Enabled` | `true` | Enable/disable the feature |
| `PickupWithoutMatchingStack` | `true` | Allow pickup even if you don't have a matching stack |
| `AllowWhenActiveSlotEmpty` | `true` | Allow pickup when the active hotbar slot is empty |
| `IncludeHandbookAlternates` | `true` | Include handbook alternate drops when checking for inventory matches |

---

### 🚀 Throw Far
Multiplies the velocity of items you throw on the ground, so they actually go somewhere instead of landing at your feet.

**Config (`ThrowFar`):**
| Key | Default | Description |
|---|---|---|
| `Enabled` | `true` | Enable/disable the feature |
| `ThrowVelocityMultiplier` | `1.5` | How much faster thrown items travel (1.0 = vanilla) |

---

### 🍖 Offhand Conditional Hunger *(disabled by default)*
Applies a hunger rate penalty when you perform actions with an item in your offhand slot. Intended for modpack makers who want to add a cost to using the offhand.

- Configurable trigger conditions: sprinting, left-clicking, right-clicking
- Penalty is applied as a percentage increase to the base hunger rate
- Can require an item to be in the offhand slot before applying the penalty
- Penalty lingers for a configurable duration after the trigger condition ends

**Config (`OffhandConditionalHunger`):**
| Key | Default | Description |
|---|---|---|
| `Enabled` | `false` | Enable/disable the feature |
| `PenaltyPercent` | `40` | Hunger rate increase as a percentage (40 = +40%) |
| `PenaltyDurationSeconds` | `5.0` | How many seconds the penalty persists after trigger ends |
| `TriggerOnSprint` | `true` | Apply penalty while sprinting |
| `TriggerOnLeftClick` | `true` | Apply penalty while holding left mouse button |
| `TriggerOnRightClick` | `true` | Apply penalty while holding right mouse button |
| `RequireOffhandItem` | `true` | Only apply penalty if something is in the offhand slot |

---

## Installation

1. Download the latest `.zip` from the [releases page](../../releases) or from [the VS mod DB](https://mods.vintagestory.at/).
2. Place it in your Vintage Story `Mods` folder.
3. Launch the game. A default config file (`handytweaks.json`) will be generated in your `ModConfig` folder.
4. Edit the config to taste and restart, or use `/htdiscard` in-game.

## Building from Source

The project uses [Cake](https://cakebuild.net/) for builds.

```sh
# Linux / macOS
./build.sh

# Windows
./build.ps1
```

You will need the Vintage Story game binaries on your machine and the path configured in the project file.

## Compatibility

| VS Version | Mod Version |
|---|---|
| 1.20.x | 1.0.26 |
| 1.21.x | 1.2.6 |

## License

See repository for license details.
