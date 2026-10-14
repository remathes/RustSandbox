# Maps

Maps in this folder ship with Rust Sandbox and show up in every server's map library (Settings → World → Custom map). The manager hosts them at `/mapfiles/<name>.map`, sets `server.levelurl` for you, and copies the map into the server folder so the server never has to download its own map.

Each map can have two optional files next to it with the same name:

- `Name.jpg` or `Name.png`: the live-map image, north up, with a 500 px ocean border around the island like Rust's own map images.
- `Name.json`: `name`, `description`, `worldSize`, `monuments` (name, x, z) and `landSpots` (dry-land [x, z] points the demo server's simulated players gather around).

Maps you upload in the manager go to the data folder's `maps` folder instead, not here.

## Maui

`Maui.map` is the default map for new servers and the demo server: a 3000 m island built from Maui's real terrain (Haleakalā, the West Maui mountains and Kahoʻolawe), with jungle on the wet north-east side and desert on the dry south-west. Made in RustEdit with roads, rocks and 15 monuments (Outpost, Ferry Terminal, 2 Harbors, 3 Fishing Villages, Abandoned Military Base, Missile Silo, HQM Quarry, The Dome, 3 Water Wells and a Lighthouse).

Terrain data: [AWS Terrain Tiles](https://registry.opendata.aws/terrain-tiles/) (Mapzen), which include USGS 3DEP/NED and SRTM elevation data. Heights are scaled down so Haleakalā is about 250 m tall.
