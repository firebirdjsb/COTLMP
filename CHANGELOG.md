# COTLMP Changelog

## v0.2.0 — World State Sync Overhaul (June 2025)

A major update to how the host and client keep their game worlds in sync. Covers weather, cult stats, dungeon rooms, follower visuals, resource destruction effects, critter/insect sync, donation box fixes, and a big performance pass to eliminate host-side lag.

---

### 🌧️ Weather Sync

The host's weather is now synced to the client in real time. Rain, snow, wind, and storms all match between host and client.

**What changed:**
- Added `WeatherType`, `WeatherStrength`, `WeatherDuration`, and `WeatherStartTime` fields to the world-state snapshot.
- On the host: reads `DataManager.Instance.WeatherType/WeatherStrength/WeatherDuration/WeatherStartingTime` each heartbeat.
- On the client: writes those values back into `DataManager` and calls `WeatherSystemController.Instance.SetWeather(...)` with a 4-second transition so weather changes fade in smoothly instead of popping.

---

### ⛪ Faith, Hunger & Sickness Sync

The host's cult stats (faith bar, hunger bar, illness bar) are now synced so the client's HUD matches reality.

**What changed:**
- Added `CultFaith`, `StaticFaith`, `HungerBar`, `IllnessBar`, and `IllnessBarMax` to the snapshot.
- Host captures from `DataManager.Instance.CultFaith`, `.StaticFaith`, `.HungerBarCount`, `.IllnessBarCount`, `.IllnessBarDynamicMax`.
- Client writes them back into `DataManager` so the UI bars and follower AI decisions reflect the host's actual state.

---

### 🕷️ Critter & Insect Sync (Spiders, Birds, Bees, etc.)

Ambient creatures now sync between host and client so both players see spiders, birds, bees, and butterflies in the same places.

**What changed:**
- Added a new `CritterEntry` struct with `InstanceID`, `TypeName`, `X`, `Y`, `IsDead`.
- Host captures all `CritterSpider.Spiders` (static list) plus all other `Critter` components in the scene.
- Client matches local critters to host data by name prefix + proximity, lerps positions, and deactivates dead ones.

---

### 🌲 Resource Destruction Effects (Trees, Rocks, Rubble)

Previously, when the host (or a host follower) chopped a tree, the client would see it silently vanish. Now the client sees the actual hit animations, skin changes, VFX, camera shake, and death/stump effects — exactly like on the host.

**What changed:**
- Expanded `ResourceEntry` to include `TypeName`, `HP`, and `TotalHP` (was previously just `IsDestroyed`).
- Host now captures the current HP of every resource, not just whether it's dead.
- Client uses `Health.DealDamage()` instead of directly setting `HP = 0`. This triggers the game's built-in effect pipeline:
  - `Tree.OnHit` → skin swap to `normal-chop1` / `normal-chop2`, hit animation, `BiomeConstants.EmitHitVFX`
  - `Tree.OnDie` → stump skin, fall rotation, camera shake, particle stop
  - Works for rocks, rubble, and any other `Health + Interaction` object
- Same `DealDamage` approach applied to enemies and NPCs for consistent hit/death effects across the board.

---

### 🚪 Dungeon Room Following

The client now follows the host through dungeon rooms within the same scene (not just scene changes).

**What changed:**
- Added `DungeonRoomX`, `DungeonRoomY`, `DungeonFloor`, `DungeonLayer` to the snapshot.
- Host captures `BiomeGenerator.Instance.CurrentX/CurrentY` and `GameManager.CurrentDungeonFloor/CurrentDungeonLayer`.
- Client detects when the host's room coords differ and calls `BiomeGenerator.ChangeRoom(x, y)` wrapped in an `MMTransition.Play(ChangeRoom, BlackFade, ...)` for a smooth fade.
- Debounce flag (`_roomChangeInProgress`) prevents stacking transitions from rapid heartbeats.
- On scene load, the host immediately sends a world-state heartbeat alongside the scene-change message so the dungeon seed arrives before `BiomeGenerator.Start()` runs.

---

### 🧑‍🤝‍🧑 Follower Facing Direction Fix

Followers on the client now face the correct direction when performing actions, walking, and standing idle.

**What changed:**
- Added `SpineScaleX` field to `FollowerEntry` (the Spine skeleton's horizontal flip value).
- Host captures `Spine.Skeleton.ScaleX` from each follower every heartbeat.
- Client's `TickFollowerInterpolation()` (runs every frame) sets `Spine.Skeleton.ScaleX` directly:
  - When the follower is moving: faces the movement direction.
  - When stationary: uses the exact `ScaleX` value from the host.
- Previously only `State.facingAngle` was set, which is an internal variable that doesn't actually flip the sprite. The game uses `Spine.Skeleton.ScaleX` (via `Follower.FacePosition()`).

---

### 💰 Donation Box Desync Fix

Picking up coins from the donation chest no longer causes the host to get stuck following the client's avatar.

**What changed:**
- Added a Harmony prefix on `Interaction.OnInteract` that blocks the call when the `StateMachine` belongs to the coop avatar (`!pf.isLamb`). This prevents the donation box (and all other interactions) from storing the network avatar as the active player.
- Added a Harmony prefix on `PlayerFarming.SetMainPlayer` that blocks switching the main player to the coop avatar. Multiple game systems (donation boxes, podiums, cutscenes) call this, and it was causing the host's camera and controls to follow the wrong player.

---

### ⚡ Host Performance Optimization

The host was experiencing significant lag spikes while hosting. The main cause was 9+ `FindObjectsOfType<T>()` calls every 100ms.

**What changed:**
- **Reduced heartbeat rate**: `WorldStateInterval` changed from `0.1f` (10 Hz) to `0.5f` (2 Hz). Position sync remains at 20 Hz.
- **Centralized caching**: All `FindObjectsOfType` calls consolidated into 4 shared caches (`_cachedHealth`, `_cachedEnemies`, `_cachedUnits`, `_cachedCritters`) populated once at the start of `CaptureSnapshot()` and `ApplySnapshot()`. Previously each Capture/Apply method called `FindObjectsOfType` independently.
- **Staggered captures**: Expensive sections alternate across heartbeats — frame 0 captures enemies + resources, frame 1 captures NPCs + critters + dropped items. No single heartbeat does all the heavy work.
- **Cache cleanup**: All caches are nulled at the end of both `CaptureSnapshot()` and `ApplySnapshot()` so they don't hold references across frames.

---

### 🔧 Dungeon Seed Sync (BiomeGenerator + GenerateRoom)

Both `BiomeGenerator.Start()` and `GenerateRoom.Start()` are now patched so the client uses the host's dungeon seed.

**What changed:**
- New `DungeonSyncPatches` class with manual Harmony patches (applied at runtime via `Plugin.cs` because the target types are nested/internal).
- `SeedClientBiome` prefix on `BiomeGenerator.Start()` — overwrites `DataManager.RandomSeed` and `UnityEngine.Random.InitState()` with the host's seed before the game reads it.
- `SeedClientDungeon` prefix on `GenerateRoom.Start()` — same seed injection for per-room generation.
- `AutoOpenClientRoomBarriers` postfix on `GenerateRoom.Start()` — opens room barriers on the client so they're not stuck behind locked doors.

---

### 📋 Follower Smooth Interpolation

Follower movement on the client is now smooth instead of jumping between positions every heartbeat.

**What changed:**
- Added `_followerTargets`, `_followerAngles`, `_followerScaleX` dictionaries that cache the latest host positions/facing between heartbeats.
- `ApplyFollowers()` stores target positions instead of directly lerping.
- New `TickFollowerInterpolation()` method runs every frame (called from `PlayerSync.Update()`) and smoothly lerps each follower toward their target at `12 * deltaTime`.
- `ResetTransientState()` clears all caches on scene transitions.

---

### 📦 Snapshot Format

Format version bumped from **3 → 7** across these changes:
- v4: `DungeonRoomX/Y`, `DungeonFloor/Layer`
- v5: Weather fields, cult stat fields, `CritterEntry` array
- v6: `ResourceEntry` expanded with `TypeName`, `HP`, `TotalHP`
- v7: `FollowerEntry.SpineScaleX`

All fields are binary-serialized with `BinaryWriter`/`BinaryReader` and GZip compressed before network transmission.

---

### Files Changed (vs. main)

| File | Lines Added | Lines Removed | Summary |
|------|------------|---------------|---------|
| `CoopPatches.cs` | ~370 | ~1 | Interaction blocker, SetMainPlayer blocker, DungeonSyncPatches (seed sync, barrier opener, weapon podium), PickUp safety finalizer |
| `PlayerSync.cs` | ~12 | ~1 | Heartbeat interval 0.1→0.5, per-frame follower tick, scene-load cache reset, immediate heartbeat on scene change |
| `WorldStateSnapshot.cs` | ~200 | ~5 | Weather/cult/critter/resource HP fields, SpineScaleX, format v7, full serialize/deserialize |
| `WorldStateSyncer.cs` | ~940 | ~50 | All capture/apply methods for weather, cult stats, critters, DealDamage for resources/enemies/NPCs, FindObjectsOfType caching, staggered captures, follower ScaleX facing |
| `Plugin.cs` | ~5 | 0 | Wires up `DungeonSyncPatches.ApplyManualPatches()` at startup |
