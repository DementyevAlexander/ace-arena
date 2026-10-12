# Hades room extraction: format notes

This covers the Hades macOS Steam build (`buildinfo.txt` 38284, 2022-02-07). Only geometry and gameplay
metadata is extracted. No art, audio or code is copied.

## Files

| File | Purpose |
|------|---------|
| `parse_thing_bin.py` | Parser for `Content/macOS/Maps/*.thing_bin` (SGB1). Usable as a CLI (`python3 -I parse_thing_bin.py X.thing_bin -o X.json`) or as a module. |
| `sjson.py` | Parser for SGG relaxed JSON (`Game/Obstacles`, `Game/Units`, `Game/Weapons`, `Game/Projectiles`). |
| `luadata.py` | Reads the table literals in `Scripts/*.lua` without running any Lua. References become `"@EnemySets.X"`. |
| `typedb.py` | Merges obstacle and unit types, resolving `InheritFrom`. |
| `geom.py`, `png.py` | Polygon transforms, grid flood fill, contour tracing, Douglas-Peucker simplification, and a PNG writer (no PIL needed). |
| `extract_rooms.py` | `python3 -I extract_rooms.py <Content> <outDir> Room1 Room2 ...` writes `<Room>.json`, `index.json` and `preview/<Room>.png`. |
| `validate.py` | `python3 -I validate.py <Content>` re-runs the format and convention checks described below. |

## 1. `thing_bin` (SGB1 v12) record format

All values are little-endian. A string is stored as `u8 present` (0 = null, 1 = present) followed by `u32 len` and `len` bytes, with no terminator.
The header is `"SGB1" | u32 version (=12) | u32 thingCount`. Each thing record follows:

| # | Field | Type | Meaning / evidence | Confidence |
|---|-------|------|--------------------|-----------|
| 1 | recordTag | u32 | Always 1. | high (constant) |
| 2 | flags | u32 bitfield | `0x14416` typical. **bit16 (0x10000) = Active**: it is off for FishingPoint, InspectPoint, and the boss-phase impassables in groups `Phase3Add`/`Phase4Add`, all of which scripts activate later. bit24 is set on candles, flames and lights (probably a render flag). | medium |
| 3 | ambient | f32 | -1 means "inherit from type". Type sjson also has `Ambient`. | medium |
| 4 | angle | f32 | Degrees, map space. See section 2. | high |
| 5 | attachedIds | u32 count + u32[] | `AttachedIDs` (ids of other things in the same map). | high |
| 6 | attachToId | u32 | `AttachToID`, 0 = none. | high |
| 7 | 3 x u8 | bools | The 3rd byte is 1 on 87% of things. Meaning unknown and not needed. | low |
| 8 | color | u8[4] RGBA | Tint. ffffffff typical, other values on lights. | high |
| 9 | comments | str | Only seen in dev maps (`"Spawner"`). | medium |
| 10 | triStateA | u32 enum | 0 = Undefined, 1 = False, 2 = True. Per-instance **Collision override** (see below). | medium-high |
| 11 | dataType | u32 enum | 1 = Obstacle, 2 = Unit (`_PlayerUnit`, Breakable*, traps, LavaTile). The legacy text format spells it `"Unit"`/`"Obstacle"`. | high |
| 12 | triStateB | u32 enum | Same encoding. Set on walls, fences, fog and flames. Probably an occlusion or shadow override. Not used. | low |
| 13 | flipHorizontal | u8 | Mirrors the collision polygon in x. | high |
| 14 | flipVertical | u8 | Rare (triangle tiles, rivers). Applied as a y mirror. | medium |
| 15 | groups | u32 n + {i32 id, str name}[n] | Editor layers (`Standing`, `Impassable`, `SpawnPoints`, `Traps`, `ExitDoors`, `Scripting`, `Breakables`, ...). Ids match `Maps/*.map_text` ThingGroups (181,248 refs checked, 23 orphans). `Default` = -3. | high |
| 16 | u8 + 3 x f32 | hue / saturation / value | Small values such as -0.15 that look like colour grading. | medium |
| 17 | id | u32 | Unique per map (checked on all maps). RoomData Lua refers to these ids (`HeroEndPoint = 50059`, `RemoteTrapChains`). | high |
| 18 | 2 x u8 | pad | Always 0. | - |
| 19 | x, y | f32, f32 | Location. | high |
| 20 | name | str | Obstacle or unit **type name**, the key into the Obstacles/Units sjson. | high |
| 21 | offsetZ | f32 | Height above the floor, for example capitals on top of pillars (633). | high |
| 22 | unk1 | f32 | 1.0 typical, 0.7–1.3 on some art. Possibly ParallaxAmount. Not geometric. | low |
| 23 | points | u32 n + {f32 x, f32 y}[n] | Per-instance collision polygon override. Only 2 things in all biome maps use it. | high |
| 24 | scale | f32 | **Multiplier on the type's `Thing.Scale`** (see below). | high |
| 25 | skewAngle, skewScale | f32, f32 | 0 or 180, and about 1.0. Shadow/skew rendering. | medium |
| 26 | unk6, unk7 | f32 | Always 0 in shipping maps. | - |
| 27 | tallness | f32 | 0 = inherit the type's `Tallness`. | medium |
| 28 | unk9 | u32 | 0, or 2 on `GroundFogIso`. | - |

**Validation.** 167 of 177 maps parse exactly to EOF with no slack. The 10 that do not are 8 legacy
pre-SGB1 maps (`D_Traversal01-06` and `RoomTreasure01-02`, which use 7-bit string enums) and 2 dev maps
(`TestDarren`, `TestMichael`). None of these are combat rooms. `map_text` `NumThings` is 0 in every file, so it
cannot be used for count checks. The group-id cross-check above is used instead.

The engine binary lists the field names `DataType, Frozen, Selectable, Active, ActivateAtRange,
ActivationRange, Tallness, SkewAngle, SkewScale, AttachToID, Invert, Clutter, CreatesShadows, StopsLight,
Comments, Collision, CausesOcclusion, ...`. These names agree with the fields found above, but the exact
mapping of each flag bit and tri-state is inferred, not proven.

### Collision polygon of a thing

The polygon comes from `Points` on the instance, or else from the resolved type `Thing.Points`
(`Game/Obstacles/*.sjson` or `Game/Units/*.sjson`, following `InheritFrom` with a deep merge). It is then transformed in this order:

1. Scale by `instance.scale * type.Thing.Scale` (the type scale defaults to 1).
2. If `flipHorizontal`, set x = -x. If `flipVertical`, set y = -y.
3. Rotate by `angle` counter-clockwise **on screen** (y down): `x' = x cos a + y sin a`, `y' = -x sin a + y cos a`.
4. Translate by (x, y).

`validate.py` tested these steps by counting logic points (spawn points, HeroEnd, loot points and breakables)
that land inside solid polygons. A correct convention should give close to zero.

| Hypothesis | Points inside solids |
|------------|----------------------|
| No scale | 33,673 |
| Instance scale only | 526 |
| **Instance scale × type scale** | **135** |
| Flip byte B instead of A | 219 |
| No flip | 219 |
| Opposite rotation | 244 |
| triStateA=1 not treated as "collision off" | 345 |

The remaining 135 hits are mostly breakables pushed against walls and points on pillar edges.

Solid means the type's `Thing.StopsUnits` (default true) is set, triStateA is not 1, the thing is Active,
offsetZ < 200, and the thing is not a "soft" destructible (`Life.DieOnCollision` with a required velocity below 500,
for example spider webs, ghosts and the small Asphodel stalagmites, which crumble when touched).

## 2. Coordinate convention

Map coordinates are in **screen plane (projected) space**, with x to the right and **y down** (towards the camera).
The floor is foreshortened **2:1 vertically**. Evidence:

* `_Impassable_Tile64Iso` (the block-out tile) has Points (0,±32), (±64,0): a 128×64 diamond, which is a ground square seen in 2:1 iso.
* Round objects have 2:1 collision footprints. The Breakable pot is a 64×32 diamond. The player `_PlayerUnit` is an octagon 96 wide × 48 tall.
  `_Impassable_Oval` is about 680×320.
* Blast radii carry a y-squash. `BlastCubeExplosion` has `DamageRadiusScaleY = 0.5`. The gas traps use 0.58.
  Lua proximity checks pass `ScaleY = 0.5` (`NotifyWithinDistanceAny(... ScaleY = 0.5)` in EnemyAI.lua and DeathLoopData.lua). So the engine's
  default distance is isotropic **in map space**, and designers opt in to "ground circles".
* Room walls are always iso diagonals (slope ±0.5). Thin rotated blockers (`_Impassable_Long*`) use angles
  of 297°, 63° and 117°, which are 90° minus 26.57° = atan(0.5). So **rotation is applied in screen space**, not in ground space.
* `EnemyAI.lua` has a "Snap isometric" block that turns attackers to 210° or 330° (down-left or down-right). Together with the rotation test above,
  this shows that angle 0 is +x and angles increase counter-clockwise on screen, so 90° points up the screen, away from the camera.

**Transform to a flat ground plane (Unity):** `X = x`, `Z = -2*y`, using the same units. An orthographic camera
pitched **30°** (sin 30° = 0.5) reproduces the Hades view. To align the iso walls with the axes, also rotate 45°:
`u = (x + 2y)/√2`, `v = (2y − x)/√2`. Iso tiles become 90.5-unit squares. A direction angle `a` becomes
`normalize(cos a, 2 sin a)` in (X, Z).

**Gameplay caveat (medium confidence):** physics, speeds and ranges are isotropic in *map* space unless a ScaleY is given.
The hero `Speed = 540` and dash range (`RushMaxRangeOverride = 535`) therefore cover the same *screen* distance in every direction.
On a true ground plane that means vertical motion covers twice the ground distance. To keep the Hades feel, either simulate in map
space, or scale movement along Z by 2.

Rooms can be **mirrored horizontally at runtime**, depending on `EntranceDirection` and the previous room's exit
(RoomManager.lua `flipMap`, `FlipHorizontalChance`). The JSON stores the unflipped layout.

## 3. Walkable area

The data has no navmesh. The engine blocks units with collision polygons, which come from three sources:

* **Invisible block-out shapes** (`kind: "impassable"`): `_Impassable_Tile64Iso`, `_Impassable_Oval*`, `_Impassable_Long*`,
  `_Impassable_Triangle*`, and the `*UnitsOnly*` variants, which have `StopsProjectiles = false` (pits and ledges that shots fly over).
  These provide most of the room outline.
* **Art obstacles with Points** (walls, pillars, rocks, statues, fences): `kind: "obstacle"`. `destructible: true` means the thing
  has HP (`Life.Invulnerable = false`) and blocks until destroyed.
* **Exit doors** (their polygons).

`extract_rooms.py` builds the walkable area as follows:

1. Rasterize all solid polygons, plus active lava, on a 16-unit grid in the iso-aligned (u,v) frame. Thin edges are also stamped so that 10-unit-thin walls are not skipped.
2. Flood-fill from HeroEnd, all spawn points and loot points.
3. Trace the boundary of the reachable cells, simplify it (Douglas-Peucker, 12-unit tolerance) and map it back to x,y.

The output is `walkable: [{outer, holes[], area}]`. Outer loops have positive shoelace area in map coordinates and holes negative.
No room leaks to the grid border.

Several Asphodel rooms return more than one region. These are islands that are only connected through magma.

Obstacles that do not touch the reachable floor (within 48 units) are dropped as background. `walkableStats.droppedFarObstacles` gives the count.

The **safe** walkable area excludes Asphodel magma (`LavaTile`, `LavaTileTriangle01/02`).
Magma is `StopsUnits = false`: it can be crossed, but it deals damage (see `hazards[]` and `hazardParams`). The magma field is not bounded by impassables,
so a flood fill through lava leaks to infinity. That is why lava is treated as a hazard border.

## 4. Classification rules (per room JSON)

| JSON key | Source |
|----------|--------|
| `heroStart` | `HeroEnd`, the in-room landing point. RoomData `HeroEndPoint` refers to its id. |
| `heroEntrance` | `HeroStart`, outside the room behind the entry. |
| `spawnPoints` | `EnemyPoint` (any), `EnemyPointMelee`, `EnemyPointRanged`, `EnemyPointSupport`, as `role`. Active only. |
| `exits` | Types that inherit `ExitDoor` in ObstacleData.lua: `TartarusDoor03b`, `AsphodelBoat01b`, `ElysiumExitDoor`, `StyxDoor01`, `StyxWarpDoor`, `TravelDoor03`, ... The reward preview is shown at the door. Each exit is paired with the nearest `HeroExit` point. Styx wing rooms use an inactive `StyxWarpDoor` that is revealed after combat. Some rooms have more door slots than `NumExits`; the game picks among them. |
| `lootPoints` | `LootPoint`, where the room reward spawns. |
| `obstacles` | Collision things, see section 1. Each has `solid`, `blocksUnits`, `blocksProjectiles`, `active`, `invisible`, `destructible` and `tallnessUnscaled`. |
| `traps` | `SpikeTrap`, `DartTrap` + `DartTrapEmitter`, `AxeTrap` + `AxeTrapTrigger`, `GasTrapPassive`, `PoisonTrap`, `PhalanxTrap`, `SawTrap`, `ArcherTrap`, `BlastCubeFused`, `BlastCubeFusedRegenerating`, and `TrapPadDisabled`/`TrapFissureDisabled` placeholders. `polygon` is the trigger pad or body. `firesEmitterId` / `triggersChains` describe trap links. |
| `trapTypes` | Per-type parameters from `EnemyData.lua`, `Weapons/*.sjson` and `Projectiles/*.sjson`. |
| `breakables` | `Breakable`, `BreakableAsphodel`, `BreakableElysium`, `BreakableStyx` and `BreakableHighValue`. Pots: 1 HP, block movement until broken, and die on high-velocity collision. |
| `hazards` | Asphodel magma polygons. |
| `cameraClampPoints`, `secretDoorPoints` | Camera bounds hints, and slots where secret, Chaos or shrine doors can spawn. |
| `roomData` | From `RoomData*.lua`, with inheritance resolved: entrance direction, `NumExits`, `TrapOptions`, `RemoteTrapChains`, legal encounters with min/max waves, type counts, active-enemy caps and the enemy set (`EnemySets.lua`), and `debugOnly`. |
| `zoom` | `ZoomFraction` (1.0 = default; Elysium rooms use 0.8 because they are larger). |

Things that are ignored: all decoration without collision (terrain decals, lights, fog, FX, ambience generators),
`CameraIntro*`, `InvisibleTarget`, and units placed in the map for scripts.

**Trap randomization:** rooms that inherit `RandomizeTrapTypes` or `RandomizeTrapTypesAll` swap `TrapPadDisabled` and
`TrapFissureDisabled` at runtime into one of the biome's `TrapOptions`: Tartarus uses `SpikeTrap` and the others use `GasTrapPassive`.
`RandomizeTrapTypes` activates a random 0–100% of them.

## 5. Trap and hazard parameters (base data)

The damage values below are base `DamageLow`/`DamageHigh`. `damageMultiplierVsEnemies` is the `NonPlayerMultiplier` that applies when a trap hits enemies.
Pact/Heat and difficulty modifiers are applied at runtime and are not included. Distances are map units; use ScaleY to convert to ground.

| Type | Trigger | Timing | Effect |
|------|---------|--------|--------|
| SpikeTrap (pad 302×150 diamond) | Unit within 100 (ScaleY 0.5) | 0.2 s windup, 2.0 s cooldown | 5 damage on the pad polygon. ×20 vs enemies. |
| DartTrap (pad) + DartTrapEmitter (wall) | Pad within 100 | 0.2 s windup, 2.0 s cooldown | The closest emitter within 2000 fires a dart: speed 2500, range 4000, 3 damage, stopped by obstacles. |
| AxeTrap (+ AxeTrapTrigger) | Remote trigger (`RemoteTrapChains` lists axe ids) or the axe's own pad | 0.3 s windup, 1.0 s post, 0.5 s cooldown | 30 damage. Gains unit collision while swinging. |
| GasTrapPassive (Asphodel/Elysium fissure) | Passive, wakes after 5–15 s | 3.0 s windup, 15–20 s cooldown | Gas cloud with radius 550 (ScaleY 0.58), 0 direct damage. |
| PoisonTrap (Styx) | Passive, wakes after 5–15 s | 1.5 s windup, 15 s cooldown | Poison cloud with radius 425 (ScaleY 0.58). The poison effect is applied by script. |
| PhalanxTrap (Elysium spear statue) | Within 100 (ScaleY 0.7) plus a 120 offset; group trigger | 0.5 s windup, 1.5 s fire, 2.0 s cooldown | Spear thrust: range 150, 12 damage. ×5 vs enemies. Polygon rotates with the angle. |
| SawTrap (Styx) | Fires along its facing (`TargetOffsetForward` 100) | 0.5 s windup, 0.3 s fire, 2.5 s cooldown | Saw projectile: speed 700, 20 damage, blocked by obstacles. |
| ArcherTrap (Elysium) | Fires forward | 2.0 s windup, 3.0 s cooldown | Arrow: speed 500, 20 damage. |
| BlastCubeFused (Tartarus/Asphodel) | Fuse lights when damaged | 1.5 s fuse | Explosion radius 400 (ScaleY 0.5), 10 damage. ×50 vs enemies. Solid until it explodes. |
| BlastCubeFusedRegenerating (Elysium) | Fuse lights when hit | 2.0 s fuse, re-arms after 6 s | Radius 450, 15 damage. ×33 vs enemies. |
| LavaTile (Asphodel magma hazard) | Point-in-polygon on the unit position | Ticks every 0.19 s | 0.5 damage per consecutive hit (ramps within a 0.8 s window). Pathfinders avoid it. |

## 6. Uncertainties

* The flag bits and tri-states are inferred from correlation: bit16 = Active, and triStateA = Collision override, which is supported by the validation table.
  The other bools (boolA–D, triStateB, unk1, unk6/7/9) are not identified, but they are not needed for geometry.
* It is unverified whether the engine applies `flipVertical` to collision. It is rare.
* The `offsetZ >= 200` rule ("non-solid") is a heuristic for raised art.
* Speed and range isotropy in map space is inferred from the data (ScaleY opt-ins), not measured in-game.
* Walkable outlines are 16-unit raster contours. Edges that are not iso-aligned (vertical or horizontal in map space) show small stair-steps.
* `NumExits` can be smaller than the number of door things. Unused doors stay closed or decorative at runtime.
* Encounter data lists *candidate* encounters and enemy pools. Waves are generated procedurally (`Generated*`), so there is no fixed per-room spawn list.
