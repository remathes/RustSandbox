# Changelog

## 1.3.0 (2026-10-14)

### New
- **Maui map**: a 3000 m island built from Maui's real terrain (Haleakalā, the West Maui mountains and Kahoʻolawe) in RustEdit, with jungle on the wet north-east side, desert on the dry south-west, roads, rocks, an Outpost and 14 other monuments. It ships with the app and is now the default map for the demo server and new servers, ready to run. The demo's simulated players stay on Maui's land, and an existing demo server still on the old procedural demo map moves to Maui.
- **Custom maps** (Server settings → World → Custom map): a map library with a picture of each map. Upload your own `.map` files, pick one per server, and the manager does the rest: it hosts the file for players at `/mapfiles/<name>.map`, sets `server.levelurl`, and copies the map into the server folder so the server never has to download its own map. The live map and the server tiles use the map's own image. Set a *Map download address* (your public IP and port 5080) so friends outside your network can download it, or paste a map URL hosted elsewhere.
- **NPCs** page and `RustSandboxNpcs` plugin 1.0.0: an easier take on BotReSpawn. Pick a preset (scientists, heavy scientists, bandits, raiders, elite guards, wolf pack, bears, boars, big cats), how many, and where: any monument from a list, a place you stand on in game (`/npcs add <preset> [count]`), raider patrols that walk the roads, and Few / Some / Many bandit camps in the empty stretches. Each preset has built-in outfits and weapons picked at random per NPC, so no Kits plugin is needed, plus health, damage, aim and weapon-drop chance. One Easy / Normal / Hard switch scales them all. The plugin warns if `nav_disable`, `nav_wait`, `ai.think` or `ai.npc_enable` would stop NPCs working.
- **Wolf companion** (`RustSandboxPet` plugin 1.2.0): `/pet` calls a wolf that runs at your side, matching your pace, and can't be killed. It tanks for you: anything that comes after you within 25 m becomes its target, animals near it turn on the wolf instead of you, and scientists hold their fire while it's on them. It also goes for whatever you attack, and never players unless the config allows it. Its killing bites count as yours for quests and skills.
- **Companion skill** (Skills 1.7.0, with Pet 1.2.0): fights the wolf helps with and things it fetches earn Companion XP. Its perks are Howl (`/pet howl`: you hit harder and heal a little for 30 s), Loyal Friend (the wolf gets you up when you're downed), Pack Hunter (more meat, leather and bone from animals it helped kill), Big Pack (more backpack slots) and Keen Nose (`/pet sniff` lists nearby animals, NPCs and crates with direction and distance). There are now eleven skills. New-style animals such as wolves now give Combat XP too, and the skills menu fits any number of skills. It carries a backpack (`/pet bag`, or look at it and press E) that is kept when it's dismissed and across restarts. Middle mouse sends it after what you look at: animals and NPCs get attacked, while dropped items, crates, barrels, bodies and plants get fetched into the backpack. With nothing in sight, it collects the loot around you. `/pet keys` shows how to bind the commands to keys.
- **Weather** page and `RustSandboxWeather` plugin 1.0.0: random weather that uses Rust's own sky, clouds and fades. Turn it on or off, and pick a weather mix (Mostly clear, Varied, Stormy, Rust's own, or Custom with a % for each kind of weather). You can also change the weather right now for a set time (clear, overcast, fog, rain, storm or dust). Players type `/weather` for the current weather and what's coming next, and can get a chat line when it turns. Admins can use `/weather storm` and similar commands in game.
- **Pets** (Pet 1.4.0): pick your animal with `/pet choose` (or just `/pet bear`): wolf, tiger or panther are fighters, and bear, polar bear, boar or crocodile are tanks with more health and less damage taken. `/pet roar` (`bind v pet.roar`; `/pet howl` still works) gives a fighter's owner +15% damage (more with the Howl perk) and health back every second for 30 s. A tank's roar pulls the enemies around it onto the pet and cuts the damage you take by 30%. The backpack comes along whichever animal you pick. Each animal's health, bite damage, toughness and bite speed are in the config.
- **Pets can go down** (Pet 1.4.0): pets can now be hurt. At no health a pet lies down and waits. Look at it and press E with a bandage, syringe or medkit (in your hand, or anywhere in your inventory) to get it up with 30%, 60% or 100% health. `/pet heal` does the same, and E with a medical item in hand also heals a hurt pet. If you leave a downed pet for 10 minutes it goes home and rests for 5 before you can call it again. Pets heal slowly once out of a fight.
- **Pet buffs on the HUD** (Event HUD 1.4.0): while a roar is going, a tile next to the coordinates shows ROAR (amber) or GUARD (blue) with the seconds left. A red PET DOWN tile shows while your pet is waiting to be patched up. It can be turned off with "Pet buffs next to the coordinates".
- **World options** (Server settings → World): customise how procedural maps are generated using Rust's own world config. You can set the biome mix (desert, temperate, snow, arctic, jungle) and the starter/medium/hard zones, switch roads, trails, rivers, power lines, rails and underwater labs on or off, and untick monuments you don't want. Changes apply on the next map wipe.

### Changed
- **Plugin performance** (Plugins page → Performance tab): how much server time each plugin uses and its slowest hooks, so you can spot the plugin that is lagging the server.
- **Check for updates** on the Plugins page now covers our own Rust Sandbox plugins too. It compares each installed one with the copy built into the manager and offers **Update to x.y.z** (or **Update all**) when the server's copy is older. Other plugins are still checked against uMod.
- **Finding NPCs** (NPCs 1.1.0): `/npcs list` now shows every group nearest first with its map grid and how many are alive, `/npcs go <number>` teleports you near one, and `/npcs show` marks the NPCs within 1 km on screen. The NPCs page has an **On the map now** table listing every spawned group (spots, road patrols, camps) with its grid. Help lists the new commands.
- **Wolf patrol** (Pet 1.3.0): `/pet patrol` (or `bind k pet.patrol`) lets the wolf roam on its own 8 to 30 m from you, so it isn't underfoot while you build. It still jumps in when something attacks you, and `/pet patrol` again or `/pet` brings it back to your side. Both distances are in the config. Help 1.2.1 lists the command.
- **Skills menu** (Skills 1.8.0): the skills are now tabs across the top, each with its name, level and a small XP bar. The chosen skill shows its level and a full XP bar above its perks, and the perk list scrolls so it never runs off the screen.
- **Apple Picker** (Skills 1.8.0): a new Woodcutting perk. Each rank adds an 8% chance (up to 40%) of 1 to 3 apples when a tree you're cutting falls.
- In-game `/help` (Help 1.2.0) has Wolf companion and Weather pages, and admin weather and NPC commands. New pages are added to existing configs automatically. The page list now fits any number of pages, and the Skins page text matches the owned-skins rule.

### Fixed
- Pet 1.4.2: pets rubber-banded chasing hit-and-run animals (panthers bite, bolt and circle back). Now they let a fleeing enemy go past 15 m, come back to you, and meet it when it returns within 12 m, and they re-path less often on long chases.
- Event HUD 1.4.1 and Pet 1.4.1: the ROAR / GUARD / PET DOWN tile never showed, and the Howl perk's extra heal on a roar never applied. Both asked another plugin about the player by their Steam ID in a form the other plugin didn't recognise, so the answer was always empty.
- Pet 1.4.1 and Tweaks 1.4.3: the sort buttons now appear on the pet's backpack (the pet plugin asks Tweaks to show them once the backpack is open), and the buttons stay hidden when sorting is turned off.
- Help 1.2.6, Skills 1.8.5, Skins 1.1.2, Quests 1.3.2 and Daily Rewards 1.1.3: menus looked blurry when first opened and only sharpened after a click. The window is now drawn a second time a moment after it opens, which is what the click did.
- Tweaks 1.4.2: the pet's backpack now gets the Sort box, Quick stack and Take all buttons like other boxes.
- Skills 1.8.5: the Companion perks talked about "your wolf". They now say "your pet", the Howl perk is called Roar to match `/pet roar`, and the level line reads "Level 2 (max 30)" instead of "Level 2 of 30".
- Repair Recycler 1.2.1: the repair bench sat level with the front of the recycler, so the corner looked more like a T. It now sits 0.5 m further back, making an L that opens towards the recycler's buttons. Stations already placed move on the next restart or plugin reload. "Corner: move the bench forward/back" still fine-tunes it.
- Pet 1.3.3: on patrol the wolf walked slowly and pushed into walls. It now trots (runs for longer hops), picks spots a short hop away with nothing built in between, and gives up on a blocked spot after 2.5 s instead of 6.
- Skills 1.8.4: Apple Picker never gave apples, and felling a tree often gave no felling XP. Both waited for the tree's finish bonus, which not every tree hands out. They now happen when the tree you were chopping falls.
- Event HUD 1.3.3: oil rig crates only showed once someone started hacking them. Waiting crates are now listed (for example "Large Oil Rig M2 waiting"), and a rig's tile turns amber while its crate is waiting. The setting is "Show oil rig crates before anyone hacks them".
- Skills 1.8.3, Help 1.2.4, Skins 1.1.1, Quests 1.3.1 and Daily Rewards 1.1.2: clicking a tab, page or button sometimes grabbed the mouse or moved it to the middle of the screen. Every click rebuilt the whole window, including the backdrop that frees the mouse. Now only the window inside it is redrawn.
- Skills 1.8.2 and Help 1.2.3: the mouse wheel didn't scroll the perk and command lists (only dragging the bar did). The lists now have a background that catches the wheel, and scroll a bit further per notch.
- Help 1.2.2: commands like `/questgiver add <name>` showed a stray `<noparse>`. Pages with more than 9 commands (Admin) cut the rest off, and the footer line overlapped the last row. The command list now scrolls and sits above the footer.
- NPCs 1.1.1: `/npcs go` landed you 25 m from scientists, close enough to be shot at once. It now lands 160 m away (60 m for animals), avoids water, and `/npcs go <number> <metres>` picks your own distance.
- Pet 1.3.2: the wolf attacked farm animals. It now leaves cows, bulls, sheep, rams and chickens alone (config: "Leaves farm animals alone").
- Pet 1.3.1: the wolf could walk slowly alongside a scientist that was backing away and never bite. It now aims ahead of a moving target at full sprint, bites from a little further out, and lunges if it has been just out of reach for over a second.
- NPCs 1.0.1: road patrols (and wild camps away from monuments) never spawned and logged "Can't find walkable ground". Rust now uses its own navmesh (RustNav) instead of Unity's, and the plugin was still asking Unity's. It now checks Rust's navmesh.
- Skills 1.8.1: the Survival perk **Regeneration** almost never healed. Rust counts cold, hunger, thirst, bleeding and radiation as being attacked, so any of them reset its 10-second out-of-combat timer. It now only waits after you hit something or something hits you.
- Event HUD (1.3.2): the Cargo Ship's locked crates showed as one line each. They're now a single "Cargo Ship" line that follows the ship and shows how many crates are waiting, hacking or unlocked. The line goes away when the ship leaves the map.
- Event HUD: locked crates on Deep Sea ghost ships were listed as "Monument A-1". They now show as "Ghost ship · Deep Sea".
- Event HUD: locked crates at monuments now show the monument's name (e.g. "Airfield M9 waiting"), not just "Monument".

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
