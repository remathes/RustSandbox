# Changelog

## 1.2.0 (2026-10-11)

### New
- **Loot** page and `RustSandboxLoot` plugin 1.1.0. Every crate, barrel and NPC body is listed with Rust's own items and the real chance of each one, worked out from the game's loot tables. For each one you can keep Rust's loot (the default), replace it with your own list, or add to it. Each item has an amount range, a chance %, and a blueprint option. Also:
  - a loot multiplier
  - scrap per crate
  - how many crates Patrol Heli and Bradley drop
  - **Refill crates on the map**, which applies changes right away
- **Skins**: `RustSandboxSkins` plugin 1.1.0. `/skins` lets players pick skins for their clothes, weapons, tools and deployables. Players only see skins they own on Steam plus Facepunch's free ones, because Facepunch's server rules forbid handing out paid skins people don't own. A config switch turns this check off at your own risk.
- **Cooking skill** (Skills 1.6.0): Big Portions (a chance of extra cooked meat and fish), Master Brewer (a chance of extra teas) and Steeped (longer tea buffs). There are now ten skills.
- **Quest giver outfits** (Quests 1.3.0): `/questgiver outfit <id>` dresses a quest giver in what you're wearing, skins included.

### Changed
- **Repair Recycler** (1.2.0): the bench and recycler now sit together in an L-shaped corner layout. New settings for repair mode (vanilla or full repair), recycler output % and recycler speed. Vanilla is the default for all three.
- The in-game `/help` guide has pages for the Repair Recycler and Skins, and lists the cooking skill.

### Fixed
- Smelting gave 1 sulfur (and other outputs) per ore when the smelting settings were changed (Tweaks 1.4.1).
- The "Tool Time" starter quest rewarded `salvaged.axe`, which doesn't exist. It now gives `axe.salvaged`, and existing configs are corrected.

## 1.1.0 (2026-10-10)

### New
- **Repair Recycler** (`RustSandboxRecycler` plugin): a single deployable item that places a recycler with a repair bench built in. It can be crafted with `/recycler craft` or given by admins.

## 1.0.0 (2026-10-10)

First public release.
