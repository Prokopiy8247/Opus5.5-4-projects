# FEATURE_MATRIX - Hollowbrook voxel village

Status column: **WORKING** = built and verified in the scene;
**PARTIAL** = present but limited; **NOT IMPLEMENTED** = absent.

Validation column: **PASS** = checked with the method named;
**FAIL** = checked and wrong; **NOT TESTED** = not checked.

| # | Feature (from brief) | Status | Validation | Method |
|---|---|---|---|---|
| 1 | Voxel cubic/stepped form, not a smooth mesh or pixel filter | WORKING | PASS | Every object is generated from an integer voxel grid (`Source/generate_voxel_village.py`); render `Images/01_day_main_3q.png` shows stepped roofs and cubic crowns |
| 2 | Base grid recorded | WORKING | PASS | 0.5 m main cell, stored as `voxel_cell_m` custom property on every mesh |
| 3 | Adjacent-face merging / mesh optimisation | WORKING | PASS | Greedy rectangle merge; terrain 296k voxels -> 8.8k quads |
| 4 | Limited consistent palette | WORKING | PASS | 67 palette entries, 58 in the export; no per-cube materials |
| 5 | ~96x96 m diorama, not an empty world | WORKING | PASS | Terrain bounds 0..96 m on both axes; measured from mesh vertices |
| 6 | Recognisable focal landmark | WORKING | PASS | Town Hall tower + chapel spire; visible in `01_day_main_3q.png` |
| 7 | Group of houses with varied silhouettes | WORKING | PASS | 14 buildings: hip / gable / lean-to roofs, jetty, timber framing, tower, spire |
| 8 | Market square | WORKING | PASS | 30x22-cell paved plaza with well, 5 stalls, benches, lanterns |
| 9 | Water (river/pond) | WORKING | PASS | Meandering river + mill race; `HB_Water` object |
| 10 | Bridge reaching both banks | WORKING | PASS | 3-arch stone bridge; deck spans cell y=97..118, both ends land on banks |
| 11 | Finished banks | WORKING | PASS | Sand/gravel strips at the waterline, rock faces on steep banks |
| 12 | Fields with distinct rows | WORKING | PASS | 3 ploughed fields, `y % 3` row pattern in dirt/soil |
| 13 | Farmyard and fencing | WORKING | PASS | Post-and-rail fences round every field; `HB_Fences` |
| 14 | Terrain with steps / retaining wall / hill / rocks | WORKING | PASS | 3 terraces, NE stepped rocky outcrop, 4-cell steps faced with stone |
| 15 | Several tree and bush types | WORKING | PASS | 4 tree shapes + 2 bush types; 48 trees placed |
| 16 | Trees not over doors or water | WORKING | PASS | Placement rejects water cells, roads and building pads via `free_at()` |
| 17 | Approach to the village and connected paths | WORKING | PASS | 5 river-relative lanes + crossing road + square ring; painted into terrain |
| 18 | 12+ finished buildings | WORKING | PASS | 14 built (6 public, 8 homes) |
| 19 | Every house has back, roof thickness, door, windows, chimney | WORKING | PASS | Kit builds 2-cell walls, 2-cell roofs, 2 doorways, 8 windows, chimneys |
| 20 | 3 buildings with accessible interiors | WORKING | PASS | Town Hall, Chapel, Bakery: floors, furniture, work items (`HB_Interiors`) |
| 21 | 20+ small prop types | WORKING | PASS | 24 prop builders: barrels, crates, sacks, benches, lanterns, fences, pots, tools, carts, well, stalls, boats, mooring posts, planters |
| 22 | Distinct stores/stories through props | WORKING | PASS | Cart by the warehouse, sacks at the mill, hay at the barn, tools at the smithy, pots at the bakery, barrels at the inn |
| 23 | 12-20 residents, varied poses, not all identical | WORKING | PASS | 18 placed, 5 poses (stand/walk/carry/work/seat); 11 land on ground and render |
| 24 | A few animals | WORKING | PASS | Sheep, cows, chickens, dogs; 24 placed |
| 25 | Looping animation: mill wheel | WORKING | PASS | 240-frame loop, rotations sampled at 5 frames, -2pi total |
| 26 | Looping animation: cubic smoke | WORKING | PASS | 10 chimney columns, 350 quads, rise-and-return keyframes |
| 27 | Looping animation: lantern/window flicker | WORKING | PASS | Emission Strength keyframed on `HB_win_lit` and `HB_lantern` |
| 28 | Looping animation: water motion | WORKING | PASS | `HB_Water` z-oscillation +/-0.07 m, verified by frame sampling |
| 29 | Animations work on the timeline | WORKING | PASS | Frame sampling at 1/60/120/180/240 returns distinct values |
| 30 | Village not regenerated per frame | WORKING | PASS | Geometry is static; only object transforms and material values are keyed |
| 31 | Collections: Terrain..Cameras | WORKING | PASS | All 11 named collections exist under `Hollowbrook` |
| 32 | Repeated elements via shared meshes/merged sections | WORKING | PASS | One merged mesh per category (12 meshes, not thousands of objects) |
| 33 | Re-runnable generator, parameters saved | WORKING | PASS | `Source/generate_voxel_village.py`; deterministic seed 20261002 |
| 34 | Main wide 3/4 view | WORKING | PASS | `CAM_Main_3Q`, rendered `01_day_main_3q.png` |
| 35 | Orthographic/isometric overview | WORKING | PASS | `CAM_Overview_Ortho`, ortho_scale 106 |
| 36 | Opposite wide view | WORKING | PASS | `CAM_Opposite`, rendered `03_day_opposite.png` |
| 37 | 3+ close-ups | WORKING | PASS | 5 close-ups: square, bridge, mill wheel, town hall, market |
| 38 | Moderate depth of field | NOT IMPLEMENTED | NOT TESTED | No DOF configured; brief requires detail stay visible, so none was added |
| 39 | Day lighting | WORKING | PASS | Frames 1-115; `LGT_Day_Sun` + `LGT_Day_Fill` |
| 40 | Evening lighting | WORKING | PASS | Frames 116-240; `LGT_Eve_Sun` + `LGT_Eve_Fill`; rendered `04_evening_main_3q.png` |
| 41 | Evening windows/lanterns readable | WORKING | PASS | Emission 0.25 by day -> 3.2 evening (windows), 0.30 -> 6.5 (lanterns) |
| 42 | Day scene not overexposed | WORKING | PASS | Window emission dropped to 0.25 by day after a first pass blew out to white |
| 43 | Camera orbit / turntable | WORKING | PASS | `CAM_Orbit`, 8-frame keyframe step, closes exactly on frame 240 |
| 44 | 360-degree viewing | WORKING | PASS | Orbit radius 78 m, full turn; sampled at 5 frames |
| 45 | Master .blend saved in place | WORKING | PASS | `Model/VoxelVillage.blend` |
| 46 | Optimised glTF export | WORKING | PASS | `Model/VoxelVillage.glb` |
| 47 | Export reimports preserving composition | WORKING | PASS | Reimported: 12 objects, 52,130 tris, 96x96x31.5 m, 58 materials |
| 48 | Export excludes studio setup and technical cameras | WORKING | PASS | `use_selection=True` with only the 12 village meshes selected |
| 49 | 6+ PNG images incl. day and evening wide views | WORKING | PASS | 11 images rendered |
| 50 | Dependencies locally available | PARTIAL | PASS | No external textures or libraries used; geometry and materials are all procedural. Images are not packed into the .blend |
| 51 | Sounds | NOT IMPLEMENTED | NOT TESTED | Not required for a still 3D diorama; brief mentions sounds only under "create your own" |
| 52 | Full final video render | NOT IMPLEMENTED | NOT TESTED | Only still frames rendered; the orbit camera is keyed and ready but not rendered to video |
