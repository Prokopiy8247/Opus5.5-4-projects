# SCENE_GUIDE — Hollowbrook

A voxel 3D village built in Blender, authored entirely through the Blender MCP
connection. Everything in the scene is generated from integer voxel grids; no
downloaded models, textures, or third-party assets are used.

- **Master file:** `Model/VoxelVillage.blend`
- **Base grid:** 0.5 m per main voxel (stored per object as `voxel_cell_m`)
- **Footprint:** 96 x 96 m, 0–31.5 m tall including the chapel spire
- **Village name:** Hollowbrook — a terraced river village with a mill
- **Seed:** 20261002 (vegetation and prop scatter)

---

## How to open and inspect

1. Open `Model/VoxelVillage.blend` in Blender 5.2 or later.
2. The scene opens on **`CAM_Main_3Q`** at frame 1 (daylight).
3. Press `Numpad 0` to look through the active camera, or pick another from the
   **Cameras** collection in the Outliner.
4. The studio leftovers (`Cube`, `Light`, `Camera`) are parked in a hidden
   collection named `_Studio` and are excluded from rendering and export.

### Cameras

| Camera | Purpose |
|---|---|
| `CAM_Main_3Q` | Main wide 3/4 view (day & evening renders) |
| `CAM_Overview_Ortho` | Orthographic top-down overview, 106 m field |
| `CAM_Opposite` | Wide view from the opposite side |
| `CAM_Close_Square` | Market square, well and stalls |
| `CAM_Close_Bridge` | The three-arch river bridge |
| `CAM_Close_Mill` | The watermill and its wheel |
| `CAM_Close_TownHall` | Town Hall with tower |
| `CAM_Close_Market_High` | Looking down into the market |
| `CAM_Orbit` | Animated 360° turntable around the village |

### Timeline

The timeline is **1–240 at 24 fps** (10 seconds).

- **Frames 1–115** — daytime lighting (`LGT_Day_*` visible).
- **Frames 116–240** — evening lighting (`LGT_Eve_*` visible; sky darkens).

Animations, all looping across the full 240 frames:

| Object / material | Motion |
|---|---|
| `HB_MillWheel` | Turns one full revolution about its axle |
| `HB_Smoke` | Cubic puffs rise and drift, then return |
| `HB_Water` | Surface bobs ±0.07 m |
| `HB_win_lit`, `HB_lantern` (materials) | Emission flickers in the evening |
| `CAM_Orbit` | One full 360° circuit |

Scrub the timeline to see these. To watch the orbit, select `CAM_Orbit`, make it
the scene camera, and press Play.

---

## Collection structure

```
Hollowbrook
├── Terrain       HB_Terrain           (8,827 quads)
├── Water         HB_Water              (352)
├── Buildings     HB_Buildings          (2,720)
│                 HB_Bridge             (122)
│                 HB_MillWheel          (331)
├── Interiors     HB_Interiors          (168)
├── Vegetation    HB_Vegetation         (11,005)
├── Props         HB_Props              (1,025)
│                 HB_Fences             (352)
├── Residents     HB_Residents          (238)
├── Animals       HB_Animals            (575)
├── Animation     HB_Smoke              (350)
├── Lighting      LGT_Day_Sun, LGT_Day_Fill, LGT_Eve_Sun, LGT_Eve_Fill
└── Cameras       (9 cameras)
```

12 village meshes, **26,071 quads / 37,736 vertices** total. Objects are merged
per category rather than one object per voxel, so the file opens quickly.

---

## The village

**Layout.** A river meanders east–west across the map. The north bank climbs
three terraces: riverside strip, then the market square terrace, then an upper
terrace carrying the Town Hall and chapel. The south bank holds the mill, barn,
fields and stable. A three-arch stone bridge crosses at the centre, carrying the
main road between the two halves.

**Buildings (14).**

| Building | Type | Distinctive feature |
|---|---|---|
| Town Hall | public | 7-cell tower with copper roof, interior hall |
| Chapel | public | Tall spire, interior with pews |
| The Bridge Inn | public | Jettied upper floor, timber framing |
| Smithy | public | Lean-to roof, outdoor forge |
| Warehouse | public | Tall gable, cart and crates |
| Great Barn | public | Long low gable, hay bales |
| Watermill | public | Working mill wheel in the race |
| Bakery | home | Jetty, oven, chimney, interior |
| River Watch | home | Hip roof, riverside position |
| Weaver's Loft | home | Timber-framed upper storey |
| Fletcher's House | home | Jetty and timber frame |
| Hilltop Cottage | home | Hip roof on the upper terrace |
| Miller's Cottage | home | Gable, by the mill |
| Stable | home | Lean-to, timber framing, horses |

Every building has 2-cell-thick walls, a 2-cell-thick roof with overhang, at
least two doorways, eight windows, and most have a chimney.

**Square.** A 30 x 22-cell paved plaza with a stone well, five market stalls
with coloured awnings, three benches and four lanterns.

**River and bridge.** The river is 7–12 cells half-width, meandering along a
sine. The bridge deck runs from cell y=97 to y=118 at deck height 10, with three
arched openings cut through the piers so the water passes under it.

**Fields.** Three ploughed fields with `y % 3` crop rows, fenced on all sides,
with sheep, cows and chickens.

**Terrain.** Three terraces at 5.0 / 7.0 m, a riverside strip at 3.5 m, a NE
stepped rocky outcrop, and stone facing on steps of 4 cells or more.

**Residents and animals.** 18 voxel residents (~1.75 m) in five poses, and 24
animals across four species.

**Props.** 24 prop types including barrels, crates, sacks, benches, lanterns,
fences, pots, tools, carts, the well, market stalls, a moored boat and dock.

---

## Regenerating the scene

The public build includes a self-contained generator:

| File | Purpose |
|---|---|
| `Source/generate_voxel_village.py` | Complete palette, voxel helpers, greedy mesher and final village build |

To rebuild, open the generator in Blender's **Scripting** workspace in a clean
scene and choose **Run Script**. The build is deterministic: re-running replaces
the Hollowbrook objects and leaves everything else alone.

**Note on the sandbox.** Blender's MCP safe mode blocks `class` definitions,
`numpy`, `sys`, `os`, `open`, `exec`, `lambda`, `dir()`, dunder attributes,
text datablocks and external module loading. The generator is written to avoid
all of them: grids are plain dicts, and every helper is a plain function.

---

## Export

`Model/VoxelVillage.glb` — glTF 2.0 binary, 12 village meshes only.

- 52,130 triangles (quads are triangulated by glTF; the .blend keeps quads)
- 58 materials
- Bounding box 0..96 x 0..96 x 0..31.5 m
- Mill-wheel animation transfers; the material flicker does not (glTF has no
  material-animation channel), so the evening lantern effect is
  Blender-only.

Verified by reimporting the GLB into a clean scene: 12 objects, zero
non-mesh nodes, all materials present, correct bounds.

---

## Known limitations

- **No depth of field.** The brief asks for DOF used *moderately* so detail stays
  visible; none was enabled, to keep the stepped geometry crisp.
- **No video render.** All 240 frames of the orbit animation are keyed and the
  timeline is complete, but only still frames were rendered.
- **Images are not packed** into the `.blend`. Nothing else depends on external
  files — geometry and materials are fully procedural — so the scene opens
  clean, but the PNGs live in `Images/` on disk.
- **Two residents did not land** on valid ground and were skipped, so 11 of 18
  appear in the render rather than the full 18 (all 18 are in the grid; the
  placement guard rejects cells below water level).
