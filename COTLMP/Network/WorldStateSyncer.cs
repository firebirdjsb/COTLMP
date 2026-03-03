/*
 * PROJECT:     Cult of the Lamb Multiplayer Mod
 * LICENSE:     MIT (https://spdx.org/licenses/MIT)
 * PURPOSE:     Capture host world state and apply received state on clients
 * COPYRIGHT:   Copyright 2025 COTLMP Contributors
 */

/* IMPORTS ********************************************************************/

using COTLMP.Data;
using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;

/* CLASSES & CODE *************************************************************/

namespace COTLMP.Network
{
    /**
     * @brief
     * Static helpers that bridge between the in-game world objects
     * (followers, structures, enemies, items, resources) and the
     * serializable WorldStateSnapshot used for network sync.
     *
     * All methods are designed to run on the Unity main thread.
     */
    internal static class WorldStateSyncer
    {
        /** Debounce: true while a client room-change transition is playing. */
        private static bool _roomChangeInProgress;

        /* ---- Per-snapshot caches (host only) ----
           FindObjectsOfType is very expensive.  We populate these once
           at the start of CaptureSnapshot and reuse them across all
           Capture* methods so we only pay the cost once per heartbeat. */
        private static Health[]   _cachedHealth;
        private static EnemyAI[]  _cachedEnemies;
        private static UnitObject[] _cachedUnits;
        private static Critter[]  _cachedCritters;

        /** Stagger counter — we rotate which expensive sections are
         *  captured each heartbeat so a single frame is never overloaded. */
        private static int _captureFrame;

        /* ------------------------------------------------------------------ */
        /* Host-side: capture                                                   */
        /* ------------------------------------------------------------------ */

        /**
         * @brief
         * Reads the current game world into a WorldStateSnapshot.
         * Called periodically on the host.
         */
        public static WorldStateSnapshot CaptureSnapshot()
        {
            var snapshot = new WorldStateSnapshot
            {
                SceneName = SceneManager.GetActiveScene().name
            };

            try { snapshot.TimeOfDay = TimeManager.CurrentGameTime; }  catch { }
            try { snapshot.CurrentDay = TimeManager.CurrentDay; }      catch { }

            /* Populate per-snapshot caches once so Capture* methods
               never call FindObjectsOfType individually. */
            try { _cachedHealth   = UnityEngine.Object.FindObjectsOfType<Health>(); }   catch { _cachedHealth   = null; }
            try { _cachedEnemies  = UnityEngine.Object.FindObjectsOfType<EnemyAI>(); }  catch { _cachedEnemies  = null; }

            /* These two are only needed every other frame (staggered) */
            int frame = _captureFrame++;
            if (frame % 2 == 0)
            {
                try { _cachedUnits    = UnityEngine.Object.FindObjectsOfType<UnitObject>(); } catch { _cachedUnits = null; }
                try { _cachedCritters = UnityEngine.Object.FindObjectsOfType<Critter>(); }    catch { _cachedCritters = null; }
            }

            /* Cheap captures — every frame */
            CaptureDungeonState(snapshot);
            CaptureEquipment(snapshot);
            CaptureWeather(snapshot);
            CaptureCultStats(snapshot);
            CaptureFollowers(snapshot);
            CaptureStructures(snapshot);

            /* Expensive captures — stagger across two heartbeats.
               Frame 0: enemies + resources
               Frame 1: NPCs + critters + dropped items */
            if (frame % 2 == 0)
            {
                CaptureEnemies(snapshot);
                CaptureResources(snapshot);
            }
            else
            {
                CaptureDroppedItems(snapshot);
                CaptureNpcs(snapshot);
                CaptureCritters(snapshot);
            }

            /* Clear caches so they are not held across frames */
            _cachedHealth   = null;
            _cachedEnemies  = null;
            _cachedUnits    = null;
            _cachedCritters = null;

            return snapshot;
        }

        /* ---- dungeon state ---- */

        private static void CaptureDungeonState(WorldStateSnapshot snapshot)
        {
            try
            {
                var dm = DataManager.Instance;
                if (dm == null) return;

                if (dm.LastDungeonSeeds != null && dm.LastDungeonSeeds.Count > 0)
                    snapshot.DungeonSeed = dm.LastDungeonSeeds[dm.LastDungeonSeeds.Count - 1];

                /* Dungeon room coordinates — BiomeGenerator tracks which
                   room the host is in so the client can follow. */
                try
                {
                    if (MMBiomeGeneration.BiomeGenerator.Instance != null)
                    {
                        snapshot.DungeonRoomX = MMBiomeGeneration.BiomeGenerator.Instance.CurrentX;
                        snapshot.DungeonRoomY = MMBiomeGeneration.BiomeGenerator.Instance.CurrentY;
                    }
                }
                catch { }

                try
                {
                    snapshot.DungeonFloor = GameManager.CurrentDungeonFloor;
                    snapshot.DungeonLayer = GameManager.CurrentDungeonLayer;
                }
                catch { }
            }
            catch { }
        }

        /* ---- equipment ---- */

        private static void CaptureEquipment(WorldStateSnapshot snapshot)
        {
            try
            {
                /* Weapon: PlayerFarming.currentWeapon is an EquipmentType */
                try
                {
                    if (PlayerFarming.Instance != null)
                        snapshot.EquippedWeaponType = (int)PlayerFarming.Instance.currentWeapon;
                }
                catch { }

                /* Curse: PlayerFarming.currentCurse is an EquipmentType */
                try
                {
                    if (PlayerFarming.Instance != null)
                        snapshot.EquippedCurseType = (int)PlayerFarming.Instance.currentCurse;
                }
                catch { }

                /* Relics / trinkets: DataManager.Instance.PlayerRunTrinkets
                   may not be available in all game versions; use reflection
                   for activeTrinkets as a fallback. */
                try
                {
                    var dm = DataManager.Instance;
                    if (dm != null)
                    {
                        var fi = dm.GetType().GetField("activeTrinkets",
                            System.Reflection.BindingFlags.Public |
                            System.Reflection.BindingFlags.NonPublic |
                            System.Reflection.BindingFlags.Instance);
                        if (fi != null)
                        {
                            var trinkets = fi.GetValue(dm) as System.Collections.IList;
                            if (trinkets != null && trinkets.Count > 0)
                            {
                                var list = new List<WorldStateSnapshot.RelicEntry>();
                                foreach (var trinket in trinkets)
                                {
                                    try
                                    {
                                        var tt = trinket.GetType();
                                        var cardProp = tt.GetProperty("TarotCard") ?? tt.GetProperty("CardType");
                                        System.Reflection.MemberInfo lvlProp =
                                            (System.Reflection.MemberInfo)tt.GetProperty("UpgradeIndex")
                                            ?? tt.GetField("UpgradeIndex",
                                                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
                                        int cardVal = 0, lvlVal = 0;
                                        if (cardProp != null) cardVal = (int)cardProp.GetValue(trinket);
                                        if (lvlProp is System.Reflection.PropertyInfo lp) lvlVal = (int)lp.GetValue(trinket);
                                        else if (lvlProp is System.Reflection.FieldInfo lf) lvlVal = (int)lf.GetValue(trinket);
                                        list.Add(new WorldStateSnapshot.RelicEntry { Type = cardVal, Level = lvlVal });
                                    }
                                    catch { }
                                }
                                snapshot.Relics = list.ToArray();
                            }
                        }
                    }
                }
                catch { }
            }
            catch { }
        }

        /* ---- weather ---- */

        private static void CaptureWeather(WorldStateSnapshot snapshot)
        {
            try
            {
                var dm = DataManager.Instance;
                if (dm == null) return;

                snapshot.WeatherType      = (int)dm.WeatherType;
                snapshot.WeatherStrength  = (int)dm.WeatherStrength;
                snapshot.WeatherDuration  = dm.WeatherDuration;
                snapshot.WeatherStartTime = dm.WeatherStartingTime;
            }
            catch { }
        }

        /* ---- cult stats (faith, hunger, sickness) ---- */

        private static void CaptureCultStats(WorldStateSnapshot snapshot)
        {
            try
            {
                var dm = DataManager.Instance;
                if (dm == null) return;

                snapshot.CultFaith    = dm.CultFaith;
                snapshot.StaticFaith  = dm.StaticFaith;
                snapshot.HungerBar    = dm.HungerBarCount;
                snapshot.IllnessBar   = dm.IllnessBarCount;
                snapshot.IllnessBarMax = dm.IllnessBarDynamicMax;
            }
            catch { }
        }

        /* ---- NPCs (insects, animals, ambient creatures) ---- */

        private static void CaptureNpcs(WorldStateSnapshot snapshot)
        {
            try
            {
                var list = new List<WorldStateSnapshot.NpcEntry>();

                /* UnitObject covers insects, animals, and ambient creatures
                   that are not EnemyAI.  Use cached list and skip anything
                   already tracked as an enemy or player. */
                var units = _cachedUnits;
                if (units != null)
                {
                    foreach (var unit in units)
                    {
                        try
                        {
                            if (unit == null) continue;
                            var go = unit.gameObject;
                            if (go.GetComponent<EnemyAI>() != null)       continue;
                            if (go.GetComponent<PlayerFarming>() != null) continue;
                            if (go.GetComponent<Follower>() != null)      continue;

                            var entry = new WorldStateSnapshot.NpcEntry
                            {
                                InstanceID = go.GetInstanceID(),
                                TypeName   = go.name ?? "",
                                X          = go.transform.position.x,
                                Y          = go.transform.position.y,
                                IsDead     = !go.activeInHierarchy
                            };

                            try
                            {
                                var hp = go.GetComponent<Health>();
                                if (hp != null)
                                {
                                    entry.HP      = hp.HP;
                                    entry.TotalHP = hp.totalHP;
                                    entry.IsDead  = entry.IsDead || (hp.HP <= 0f);
                                }
                            }
                            catch { }

                            list.Add(entry);
                        }
                        catch { continue; }
                    }
                }

                snapshot.Npcs = list.ToArray();
            }
            catch
            {
                snapshot.Npcs = Array.Empty<WorldStateSnapshot.NpcEntry>();
            }
        }

        /* ---- critters (spiders, birds, bees, butterflies) ---- */

        private static void CaptureCritters(WorldStateSnapshot snapshot)
        {
            try
            {
                var list = new List<WorldStateSnapshot.CritterEntry>();

                /* CritterSpider.Spiders is a static list of all live spiders */
                try
                {
                    if (CritterSpider.Spiders != null)
                    {
                        foreach (var spider in CritterSpider.Spiders)
                        {
                            if (spider == null) continue;
                            list.Add(new WorldStateSnapshot.CritterEntry
                            {
                                InstanceID = spider.gameObject.GetInstanceID(),
                                TypeName   = spider.gameObject.name ?? "CritterSpider",
                                X          = spider.transform.position.x,
                                Y          = spider.transform.position.y,
                                IsDead     = !spider.gameObject.activeInHierarchy
                            });
                        }
                    }
                }
                catch { }

                /* Scan cached Critter components for other critter types
                   (birds, bees, squirrels, etc.) */
                try
                {
                    var allCritters = _cachedCritters;
                    if (allCritters != null)
                    {
                        foreach (var critter in allCritters)
                        {
                            if (critter == null) continue;
                            var go = critter.gameObject;
                            /* Skip spiders already captured above */
                            if (go.GetComponent<CritterSpider>() != null) continue;

                            list.Add(new WorldStateSnapshot.CritterEntry
                            {
                                InstanceID = go.GetInstanceID(),
                                TypeName   = go.name ?? "Critter",
                                X          = go.transform.position.x,
                                Y          = go.transform.position.y,
                                IsDead     = !go.activeInHierarchy
                            });
                        }
                    }
                }
                catch { }

                snapshot.Critters = list.ToArray();
            }
            catch
            {
                snapshot.Critters = Array.Empty<WorldStateSnapshot.CritterEntry>();
            }
        }

        /* ---- followers ---- */

        private static void CaptureFollowers(WorldStateSnapshot snapshot)
        {
            try
            {
                var list = new List<WorldStateSnapshot.FollowerEntry>();

                if (FollowerBrain.AllBrains != null)
                {
                    foreach (var brain in FollowerBrain.AllBrains)
                    {
                        try
                        {
                            if (brain == null) continue;

                            var entry = new WorldStateSnapshot.FollowerEntry
                            {
                                ID   = -1,
                                Name = ""
                            };

                            /* Stable identity from FollowerBrainInfo */
                            try
                            {
                                entry.ID   = brain.Info.ID;
                                entry.Name = brain.Info.Name ?? "";
                                entry.Role = (int)brain.Info.FollowerRole;
                            }
                            catch { }

                            if (entry.ID < 0) continue;

                            /* Position: try to find the scene Follower by ID */
                            try
                            {
                                Follower sceneFollower = FollowerManager.FindFollowerByID(entry.ID);
                                if (sceneFollower != null)
                                {
                                    entry.X = sceneFollower.transform.position.x;
                                    entry.Y = sceneFollower.transform.position.y;

                                    /* Facing angle + sprite flip */
                                    try
                                    {
                                        if (sceneFollower.State != null)
                                            entry.FacingAngle = sceneFollower.State.facingAngle;
                                    }
                                    catch { }

                                    try
                                    {
                                        if (sceneFollower.Spine != null
                                            && sceneFollower.Spine.Skeleton != null)
                                            entry.SpineScaleX = sceneFollower.Spine.Skeleton.ScaleX;
                                    }
                                    catch { }

                                    /* Body animation (track 1 in Spine) */
                                    try
                                    {
                                        if (sceneFollower.Spine != null
                                            && sceneFollower.Spine.AnimationState != null)
                                        {
                                            var track = sceneFollower.Spine.AnimationState.GetCurrent(1);
                                            if (track?.Animation != null)
                                                entry.Animation = track.Animation.Name;
                                        }
                                    }
                                    catch { }
                                }
                            }
                            catch { }

                            /* Current task */
                            try { entry.TaskType = (int)brain.CurrentTaskType; }
                            catch { }

                            list.Add(entry);
                        }
                        catch { continue; }
                    }
                }

                snapshot.Followers = list.ToArray();
            }
            catch
            {
                snapshot.Followers = Array.Empty<WorldStateSnapshot.FollowerEntry>();
            }
        }

        /* ---- structures ---- */

        private static void CaptureStructures(WorldStateSnapshot snapshot)
        {
            try
            {
                var list = new List<WorldStateSnapshot.StructureEntry>();

                /* Capture structures at the current player location */
                FollowerLocation location = FollowerLocation.Base;
                try { location = PlayerFarming.Location; } catch { }

                List<StructureBrain> brains = null;
                try { brains = StructureManager.StructuresAtLocation(location); } catch { }

                if (brains != null)
                {
                    foreach (var sb in brains)
                    {
                        try
                        {
                            if (sb == null || sb.Data == null) continue;
                            list.Add(new WorldStateSnapshot.StructureEntry
                            {
                                ID          = sb.Data.ID,
                                Type        = (int)sb.Data.Type,
                                X           = sb.Data.Position.x,
                                Y           = sb.Data.Position.y,
                                IsCollapsed = sb.Data.IsCollapsed,
                                IsAflame    = sb.Data.IsAflame
                            });
                        }
                        catch { continue; }
                    }
                }

                snapshot.Structures = list.ToArray();
            }
            catch
            {
                snapshot.Structures = Array.Empty<WorldStateSnapshot.StructureEntry>();
            }
        }

        /* ---- enemies ---- */

        private static void CaptureEnemies(WorldStateSnapshot snapshot)
        {
            try
            {
                var list = new List<WorldStateSnapshot.EnemyEntry>();

                var enemies = _cachedEnemies;
                if (enemies != null)
                {
                    foreach (var ai in enemies)
                    {
                        try
                        {
                            if (ai == null) continue;
                            var go = ai.gameObject;

                            var entry = new WorldStateSnapshot.EnemyEntry
                            {
                                InstanceID = go.GetInstanceID(),
                                TypeName   = go.name ?? "",
                                X          = go.transform.position.x,
                                Y          = go.transform.position.y,
                                IsDead     = !go.activeInHierarchy
                            };

                            /* Health component lives on the same object or a parent */
                            try
                            {
                                var hp = go.GetComponent<Health>();
                                if (hp == null) hp = go.GetComponentInParent<Health>();
                                if (hp != null)
                                {
                                    entry.HP      = hp.HP;
                                    entry.TotalHP = hp.totalHP;
                                    entry.IsDead  = entry.IsDead || (hp.HP <= 0f);
                                }
                            }
                            catch { }

                            list.Add(entry);
                        }
                        catch { continue; }
                    }
                }

                snapshot.Enemies = list.ToArray();
            }
            catch
            {
                snapshot.Enemies = Array.Empty<WorldStateSnapshot.EnemyEntry>();
            }
        }

        /* ---- dropped items ---- */

        private static void CaptureDroppedItems(WorldStateSnapshot snapshot)
        {
            try
            {
                var list = new List<WorldStateSnapshot.DroppedItemEntry>();

                /* PickUp.PickUps is a static List<PickUp> maintained by the game */
                if (PickUp.PickUps != null)
                {
                    foreach (var pu in PickUp.PickUps)
                    {
                        try
                        {
                            if (pu == null) continue;
                            list.Add(new WorldStateSnapshot.DroppedItemEntry
                            {
                                InstanceID = pu.gameObject.GetInstanceID(),
                                Type       = (int)pu.type,
                                Quantity   = pu.Quantity,
                                X          = pu.transform.position.x,
                                Y          = pu.transform.position.y
                            });
                        }
                        catch { continue; }
                    }
                }

                snapshot.DroppedItems = list.ToArray();
            }
            catch
            {
                snapshot.DroppedItems = Array.Empty<WorldStateSnapshot.DroppedItemEntry>();
            }
        }

        /* ---- resources (trees, rubble, interactable objects) ---- */

        private static void CaptureResources(WorldStateSnapshot snapshot)
        {
            try
            {
                var list = new List<WorldStateSnapshot.ResourceEntry>();

                /* Scan cached Health components from the scene.
                   Resources like trees/rocks typically have Health attached
                   and no EnemyAI or PlayerFarming component. */
                var allHealth = _cachedHealth;
                if (allHealth != null)
                {
                    foreach (var h in allHealth)
                    {
                        try
                        {
                            if (h == null) continue;
                            var go = h.gameObject;

                            /* Skip players and enemies (tracked elsewhere) */
                            if (go.GetComponent<PlayerFarming>() != null) continue;
                            if (go.GetComponent<EnemyAI>() != null)       continue;

                            /* Only include if it looks like a resource / interaction */
                            if (go.GetComponent<Interaction>() == null)    continue;

                            list.Add(new WorldStateSnapshot.ResourceEntry
                            {
                                InstanceID  = go.GetInstanceID(),
                                TypeName    = go.name ?? "",
                                X           = go.transform.position.x,
                                Y           = go.transform.position.y,
                                HP          = h.HP,
                                TotalHP     = h.totalHP,
                                IsDestroyed = h.HP <= 0f || !go.activeInHierarchy
                            });
                        }
                        catch { continue; }
                    }
                }

                snapshot.Resources = list.ToArray();
            }
            catch
            {
                snapshot.Resources = Array.Empty<WorldStateSnapshot.ResourceEntry>();
            }
        }

        /* ------------------------------------------------------------------ */
        /* Client-side: apply                                                   */
        /* ------------------------------------------------------------------ */

        /**
         * @brief
         * Applies a received snapshot to the local game world.
         * Only acts when the client is in the same scene as the host.
         */
        public static void ApplySnapshot(WorldStateSnapshot snapshot)
        {
            if (snapshot == null) return;

            /* Scene guard: only apply if we are in the same scene */
            string local = SceneManager.GetActiveScene().name;
            if (!string.Equals(local, snapshot.SceneName, StringComparison.Ordinal))
                return;

            /* Populate apply-side caches once so Apply* methods never
               call FindObjectsOfType individually. */
            try { _cachedEnemies  = UnityEngine.Object.FindObjectsOfType<EnemyAI>(); }  catch { _cachedEnemies  = null; }
            try { _cachedHealth   = UnityEngine.Object.FindObjectsOfType<Health>(); }   catch { _cachedHealth   = null; }
            try { _cachedUnits    = UnityEngine.Object.FindObjectsOfType<UnitObject>(); } catch { _cachedUnits = null; }
            try { _cachedCritters = UnityEngine.Object.FindObjectsOfType<Critter>(); }  catch { _cachedCritters = null; }

            try { ApplyTime(snapshot); }         catch { }
            try { ApplyDungeonState(snapshot); } catch { }
            try { ApplyEquipment(snapshot); }    catch { }
            try { ApplyWeather(snapshot); }      catch { }
            try { ApplyCultStats(snapshot); }    catch { }
            try { ApplyFollowers(snapshot); }    catch { }
            try { ApplyStructures(snapshot); }   catch { }
            try { ApplyEnemies(snapshot); }      catch { }
            try { ApplyDroppedItems(snapshot); }  catch { }
            try { ApplyResources(snapshot); }    catch { }
            try { ApplyNpcs(snapshot); }         catch { }
            try { ApplyCritters(snapshot); }     catch { }

            _cachedEnemies  = null;
            _cachedHealth   = null;
            _cachedUnits    = null;
            _cachedCritters = null;
        }

        /* ---- time of day ---- */

        /**
         * @brief
         * Syncs the client's game clock to the host so day/night cycles,
         * follower schedules and time-gated events stay in lockstep.
         */
        private static void ApplyTime(WorldStateSnapshot snapshot)
        {
            if (!InternalData.IsMultiplayerSession || InternalData.IsHost) return;

            try
            {
                if (TimeManager.CurrentDay != snapshot.CurrentDay)
                    TimeManager.CurrentDay = snapshot.CurrentDay;

                float drift = Mathf.Abs(TimeManager.CurrentGameTime - snapshot.TimeOfDay);
                if (drift > 5f)
                    TimeManager.CurrentGameTime = snapshot.TimeOfDay;
            }
            catch { }
        }

        /* ---- dungeon state ---- */

        /**
         * @brief
         * Syncs the host's dungeon generation seed to the client so both
         * produce identical room layouts when entering a dungeon.
         */
        private static void ApplyDungeonState(WorldStateSnapshot snapshot)
        {
            if (!InternalData.IsMultiplayerSession || InternalData.IsHost) return;
            if (snapshot.DungeonSeed == 0) return;

            try
            {
                var dm = DataManager.Instance;
                if (dm == null) return;

                /* Inject the host's seed into LastDungeonSeeds so
                   BiomeGenerator and GenerateRoom pick it up. */
                if (dm.LastDungeonSeeds == null)
                    dm.LastDungeonSeeds = new System.Collections.Generic.List<int>();
                if (dm.LastDungeonSeeds.Count == 0 || dm.LastDungeonSeeds[dm.LastDungeonSeeds.Count - 1] != snapshot.DungeonSeed)
                {
                    dm.LastDungeonSeeds.Add(snapshot.DungeonSeed);
                    Plugin.Logger?.LogInfo($"[WorldSync] Dungeon seed set to {snapshot.DungeonSeed}");
                }

                /* Also overwrite DataManager.RandomSeed (a System.Random)
                   and seed Unity's RNG so GenerateRoom.GenerateRandomSeed
                   and any Random.Range calls produce identical results. */
                DataManager.RandomSeed = new System.Random(snapshot.DungeonSeed);
                DataManager.UseDataManagerSeed = true;
                UnityEngine.Random.InitState(snapshot.DungeonSeed);
            }
            catch { }

            /* Sync dungeon floor / layer counters */
            try
            {
                if (snapshot.DungeonFloor > 0)
                    GameManager.CurrentDungeonFloor = snapshot.DungeonFloor;
                if (snapshot.DungeonLayer > 0)
                    GameManager.CurrentDungeonLayer = snapshot.DungeonLayer;
            }
            catch { }

            /* Follow the host into the correct dungeon room.
               BiomeGenerator.ChangeRoom repositions the camera and
               activates the correct room prefab within the same scene.
               Wrap it in an MMTransition fade so the switch is not jarring. */
            try
            {
                var bg = MMBiomeGeneration.BiomeGenerator.Instance;
                if (bg != null && !_roomChangeInProgress)
                {
                    if (bg.CurrentX != snapshot.DungeonRoomX || bg.CurrentY != snapshot.DungeonRoomY)
                    {
                        int targetX = snapshot.DungeonRoomX;
                        int targetY = snapshot.DungeonRoomY;
                        _roomChangeInProgress = true;

                        Plugin.Logger?.LogInfo(
                            $"[WorldSync] Following host room ({bg.CurrentX},{bg.CurrentY}) -> ({targetX},{targetY})");

                        try
                        {
                            MMTools.MMTransition.Play(
                                MMTools.MMTransition.TransitionType.ChangeRoom,
                                MMTools.MMTransition.Effect.BlackFade,
                                MMTools.MMTransition.NO_SCENE, 0.5f, "",
                                () =>
                                {
                                    MMBiomeGeneration.BiomeGenerator.ChangeRoom(targetX, targetY);
                                    _roomChangeInProgress = false;
                                },
                                null);
                        }
                        catch
                        {
                            /* Fallback: change room without transition */
                            MMBiomeGeneration.BiomeGenerator.ChangeRoom(targetX, targetY);
                            _roomChangeInProgress = false;
                        }
                    }
                }
            }
            catch { _roomChangeInProgress = false; }
        }

        /* ---- equipment ---- */

        /**
         * @brief
         * Syncs the host's equipped weapon, curse, and relics to the
         * client's DataManager so dungeon weapon-checks pass and the
         * UI reflects the correct loadout.
         */
        private static void ApplyEquipment(WorldStateSnapshot snapshot)
        {
            if (!InternalData.IsMultiplayerSession || InternalData.IsHost) return;

            try
            {
                /* Weapon: set on the local lamb */
                if (snapshot.EquippedWeaponType >= 0)
                {
                    try
                    {
                        if (PlayerFarming.Instance != null)
                            PlayerFarming.Instance.currentWeapon = (EquipmentType)snapshot.EquippedWeaponType;
                    }
                    catch { }
                }

                /* Curse */
                if (snapshot.EquippedCurseType >= 0)
                {
                    try
                    {
                        if (PlayerFarming.Instance != null)
                            PlayerFarming.Instance.currentCurse = (EquipmentType)snapshot.EquippedCurseType;
                    }
                    catch { }
                }

                /* Also set weapon/curse on the coop avatar so
                   GetAllPlayersWearingWeapons() returns true
                   and the dungeon start room doors can open. */
                try
                {
                    if (PlayerFarming.players != null)
                    {
                        foreach (var pf in PlayerFarming.players)
                        {
                            if (pf == null || pf.isLamb) continue;
                            if (snapshot.EquippedWeaponType >= 0)
                                pf.currentWeapon = (EquipmentType)snapshot.EquippedWeaponType;
                            if (snapshot.EquippedCurseType >= 0)
                                pf.currentCurse = (EquipmentType)snapshot.EquippedCurseType;
                        }
                    }
                }
                catch { }
            }
            catch { }
        }

        /* ---- weather ---- */

        /**
         * @brief
         * Syncs the host's weather state to the client so rain, snow, wind,
         * and storms are visually identical on both machines.
         */
        private static void ApplyWeather(WorldStateSnapshot snapshot)
        {
            if (!InternalData.IsMultiplayerSession || InternalData.IsHost) return;

            try
            {
                var dm = DataManager.Instance;
                if (dm == null) return;

                var hostType     = (WeatherSystemController.WeatherType)snapshot.WeatherType;
                var hostStrength = (WeatherSystemController.WeatherStrength)snapshot.WeatherStrength;

                /* Update DataManager fields so the game's own weather tick
                   does not immediately override what we set. */
                dm.WeatherType         = hostType;
                dm.WeatherStrength     = hostStrength;
                dm.WeatherDuration     = snapshot.WeatherDuration;
                dm.WeatherStartingTime = snapshot.WeatherStartTime;

                /* Push the change into the active weather controller */
                var wsc = WeatherSystemController.Instance;
                if (wsc != null)
                {
                    if (wsc.CurrentWeatherType != hostType
                        || wsc.CurrentWeatherStrength != hostStrength)
                    {
                        wsc.SetWeather(hostType, hostStrength, 4f, true, true);
                    }
                }
            }
            catch { }
        }

        /* ---- cult stats (faith, hunger, sickness) ---- */

        /**
         * @brief
         * Syncs the host's cult faith, hunger bar, and illness bar to the
         * client so the HUD and follower AI reflect the real state.
         */
        private static void ApplyCultStats(WorldStateSnapshot snapshot)
        {
            if (!InternalData.IsMultiplayerSession || InternalData.IsHost) return;

            try
            {
                var dm = DataManager.Instance;
                if (dm == null) return;

                dm.CultFaith           = snapshot.CultFaith;
                dm.StaticFaith         = snapshot.StaticFaith;
                dm.HungerBarCount      = snapshot.HungerBar;
                dm.IllnessBarCount     = snapshot.IllnessBar;
                dm.IllnessBarDynamicMax = snapshot.IllnessBarMax;
            }
            catch { }
        }

        /* ---- followers ---- */

        /** Cached target positions for followers so lerp continues between heartbeats. */
        private static readonly Dictionary<int, Vector3> _followerTargets
            = new Dictionary<int, Vector3>();
        private static readonly Dictionary<int, float> _followerAngles
            = new Dictionary<int, float>();
        private static readonly Dictionary<int, float> _followerScaleX
            = new Dictionary<int, float>();

        /**
         * @brief
         * Called every frame (from PlayerSync.Update) to continue lerping
         * followers toward their target positions between heartbeats.
         */
        public static void TickFollowerInterpolation()
        {
            if (!InternalData.IsMultiplayerSession || InternalData.IsHost) return;
            if (_followerTargets.Count == 0) return;

            foreach (var kvp in _followerTargets)
            {
                try
                {
                    Follower f = FollowerManager.FindFollowerByID(kvp.Key);
                    if (f == null) continue;

                    Vector3 target = kvp.Value;
                    Vector3 oldPos = f.transform.position;
                    float dist = Vector3.Distance(oldPos, target);
                    if (dist > 8f)
                        f.transform.position = target;
                    else if (dist > 0.01f)
                        f.transform.position = Vector3.Lerp(
                            oldPos, target, 12f * Time.deltaTime);

                    /* Facing direction — set Spine.Skeleton.ScaleX so the
                       follower sprite faces the correct direction.  Use the
                       movement delta when walking, or the host's ScaleX when
                       stationary.  This mirrors Follower.FacePosition(). */
                    try
                    {
                        if (f.Spine != null && f.Spine.Skeleton != null)
                        {
                            float dx = target.x - oldPos.x;
                            if (Mathf.Abs(dx) > 0.005f)
                            {
                                /* Moving: face movement direction */
                                f.Spine.Skeleton.ScaleX = dx < 0f ? 1f : -1f;
                            }
                            else if (_followerScaleX.TryGetValue(kvp.Key, out float hostScale)
                                     && Mathf.Abs(hostScale) > 0.01f)
                            {
                                /* Stationary: use the exact ScaleX from the host */
                                f.Spine.Skeleton.ScaleX = hostScale;
                            }
                        }
                    }
                    catch { }

                    if (_followerAngles.TryGetValue(kvp.Key, out float ang))
                    {
                        if (f.State != null)
                        {
                            f.State.facingAngle = ang;
                            f.State.LookAngle   = ang;
                        }
                    }
                }
                catch { }
            }
        }

        /**
         * @brief
         * Clears cached follower positions and the room-change debounce.
         * Call on scene transitions so stale data does not linger.
         */
        public static void ResetTransientState()
        {
            _followerTargets.Clear();
            _followerAngles.Clear();
            _followerScaleX.Clear();
            _roomChangeInProgress = false;
        }

        private static void ApplyFollowers(WorldStateSnapshot snapshot)
        {
            if (snapshot.Followers == null || snapshot.Followers.Length == 0) return;

            bool isClient = InternalData.IsMultiplayerSession && !InternalData.IsHost;

            foreach (var entry in snapshot.Followers)
            {
                try
                {
                    /* On the client, ensure the follower exists in the local
                       save data so the game can spawn it.  This handles
                       followers recruited by the host after the initial
                       save-data transfer. */
                    if (isClient)
                        EnsureFollowerExists(entry);

                    Follower f = FollowerManager.FindFollowerByID(entry.ID);
                    if (f == null) continue;

                    /* Store the target position; the per-frame
                       TickFollowerInterpolation() will smoothly lerp
                       toward it every frame, not just on heartbeat. */
                    Vector3 target = new Vector3(entry.X, entry.Y, f.transform.position.z);
                    _followerTargets[entry.ID] = target;
                    _followerAngles[entry.ID]  = entry.FacingAngle;
                    _followerScaleX[entry.ID]  = entry.SpineScaleX;

                    /* Body animation (track 1 in the follower's Spine) */
                    try
                    {
                        if (!string.IsNullOrEmpty(entry.Animation)
                            && f.Spine != null && f.Spine.AnimationState != null)
                        {
                            var current = f.Spine.AnimationState.GetCurrent(1);
                            if (current?.Animation == null
                                || current.Animation.Name != entry.Animation)
                            {
                                var anim = f.Spine.Skeleton?.Data?.FindAnimation(entry.Animation);
                                if (anim != null)
                                    f.Spine.AnimationState.SetAnimation(1, anim, true);
                            }
                        }
                    }
                    catch { }

                    /* On client, disable interaction components so followers
                       only bother the host player. */
                    if (isClient)
                        SuppressFollowerInteraction(f);
                }
                catch { continue; }
            }
        }

        /**
         * @brief
         * Ensures a follower from the host's snapshot exists in the local
         * save data and brain list.  If the follower is missing (e.g. the
         * host recruited it after the client joined), a minimal
         * FollowerInfo is injected into DataManager so the game can
         * spawn the follower at the current location.
         */
        private static void EnsureFollowerExists(WorldStateSnapshot.FollowerEntry entry)
        {
            try
            {
                if (entry.ID < 0) return;
                if (DataManager.Instance?.Followers == null) return;

                /* Check save data */
                bool inSave = false;
                foreach (var info in DataManager.Instance.Followers)
                {
                    if (info != null && info.ID == entry.ID)
                    {
                        inSave = true;
                        break;
                    }
                }

                if (!inSave)
                {
                    var newInfo          = new FollowerInfo();
                    newInfo.ID           = entry.ID;
                    newInfo.Name         = !string.IsNullOrEmpty(entry.Name) ? entry.Name : "Follower";
                    newInfo.FollowerRole = (FollowerRole)entry.Role;
                    DataManager.Instance.Followers.Add(newInfo);
                }

                /* Ensure a brain exists for this follower */
                bool hasBrain = false;
                if (FollowerBrain.AllBrains != null)
                {
                    foreach (var brain in FollowerBrain.AllBrains)
                    {
                        if (brain?.Info != null && brain.Info.ID == entry.ID)
                        {
                            hasBrain = true;
                            break;
                        }
                    }
                }

                if (!hasBrain)
                {
                    FollowerInfo saveInfo = null;
                    foreach (var info in DataManager.Instance.Followers)
                    {
                        if (info != null && info.ID == entry.ID)
                        {
                            saveInfo = info;
                            break;
                        }
                    }
                    if (saveInfo != null)
                        new FollowerBrain(saveInfo);
                }
            }
            catch { /* non-critical: follower will be missing until next sync */ }
        }

        /**
         * @brief
         * Disables Interaction components and trigger colliders on a
         * follower so it cannot initiate or receive interactions on the
         * client.  Called every heartbeat cycle; checks are cheap because
         * most components will already be disabled after the first call.
         */
        private static void SuppressFollowerInteraction(Follower f)
        {
            try
            {
                foreach (var interaction in f.GetComponents<Interaction>())
                {
                    if (interaction != null && interaction.enabled)
                        interaction.enabled = false;
                }

                foreach (var col in f.GetComponentsInChildren<Collider2D>())
                {
                    if (col != null && col.isTrigger && col.enabled)
                        col.enabled = false;
                }
            }
            catch { }
        }

        /* ---- structures ---- */

        private static void ApplyStructures(WorldStateSnapshot snapshot)
        {
            if (snapshot.Structures == null || snapshot.Structures.Length == 0) return;

            FollowerLocation loc = FollowerLocation.Base;
            try { loc = PlayerFarming.Location; } catch { }

            List<StructureBrain> local = null;
            try { local = StructureManager.StructuresAtLocation(loc); } catch { }
            if (local == null) return;

            /* Build a lookup by ID for the host structures */
            var hostMap = new Dictionary<int, WorldStateSnapshot.StructureEntry>();
            foreach (var se in snapshot.Structures)
                hostMap[se.ID] = se;

            foreach (var sb in local)
            {
                try
                {
                    if (sb == null || sb.Data == null) continue;
                    if (!hostMap.TryGetValue(sb.Data.ID, out var hostEntry)) continue;

                    /* Sync collapsed / aflame state */
                    if (hostEntry.IsCollapsed && !sb.Data.IsCollapsed)
                    {
                        try { sb.Collapse(true, true, false); } catch { }
                    }
                    if (hostEntry.IsAflame && !sb.Data.IsAflame)
                    {
                        try { sb.SetAflame(true); } catch { }
                    }
                }
                catch { continue; }
            }
        }

        /* ---- enemies ---- */

        /**
         * @brief
         * Matches client-side enemies to host-side data using a proximity
         * heuristic (same type name within a small radius). Updates HP and
         * kills enemies the host has killed.
         */
        private static void ApplyEnemies(WorldStateSnapshot snapshot)
        {
            if (snapshot.Enemies == null || snapshot.Enemies.Length == 0) return;

            var localEnemies = _cachedEnemies;
            if (localEnemies == null) return;

            foreach (var localAI in localEnemies)
            {
                try
                {
                    if (localAI == null) continue;
                    var go = localAI.gameObject;
                    string localName = go.name ?? "";

                    /* Find closest matching host entry by name + proximity */
                    float bestDist = float.MaxValue;
                    WorldStateSnapshot.EnemyEntry best = default;
                    bool found = false;

                    foreach (var he in snapshot.Enemies)
                    {
                        /* Match by object name prefix (strip "(Clone)" etc.) */
                        if (!localName.StartsWith(he.TypeName.Split('(')[0].TrimEnd(),
                            StringComparison.OrdinalIgnoreCase))
                            continue;

                        float dx = go.transform.position.x - he.X;
                        float dy = go.transform.position.y - he.Y;
                        float dist = dx * dx + dy * dy;
                        if (dist < bestDist)
                        {
                            bestDist = dist;
                            best = he;
                            found = true;
                        }
                    }

                    if (!found) continue;

                    /* Apply health via DealDamage so hit/death effects
                       (blood, knockback, death animation) fire properly. */
                    var hp = go.GetComponent<Health>();
                    if (hp == null) hp = go.GetComponentInParent<Health>();
                    if (hp != null && best.HP < hp.HP && hp.HP > 0f)
                    {
                        float delta = hp.HP - best.HP;
                        try
                        {
                            hp.DealDamage(delta, go, go.transform.position,
                                false, Health.AttackTypes.Melee, true, (Health.AttackFlags)0);
                        }
                        catch { hp.HP = best.HP; }
                    }

                    /* Kill if host says dead */
                    if (best.IsDead && go.activeInHierarchy)
                    {
                        if (hp != null && hp.HP > 0f)
                        {
                            try
                            {
                                hp.DealDamage(hp.HP + 1f, go, go.transform.position,
                                    false, Health.AttackTypes.Melee, true,
                                    Health.AttackFlags.ForceKill);
                            }
                            catch { hp.HP = 0f; }
                        }
                    }
                }
                catch { continue; }
            }
        }

        /* ---- dropped items ---- */

        private static void ApplyDroppedItems(WorldStateSnapshot snapshot)
        {
            /* For now we track destruction: if the host has fewer items,
               some were picked up.  We don't spawn new items because that
               requires the game's object pool system. */
            if (snapshot.DroppedItems == null) return;

            /* Build a set of host-side instance positions for quick lookup */
            var hostPositions = new HashSet<long>();
            foreach (var di in snapshot.DroppedItems)
            {
                /* Quantise to a rough grid so floating-point drift is tolerated */
                long key = ((long)(int)(di.X * 10f) << 32) | (uint)(int)(di.Y * 10f);
                hostPositions.Add(key);
            }

            /* If PickUp.PickUps is available, check for orphan items */
            try
            {
                if (PickUp.PickUps == null) return;
                for (int i = PickUp.PickUps.Count - 1; i >= 0; i--)
                {
                    try
                    {
                        var pu = PickUp.PickUps[i];
                        if (pu == null) continue;
                        long key = ((long)(int)(pu.transform.position.x * 10f) << 32)
                                 | (uint)(int)(pu.transform.position.y * 10f);
                        if (!hostPositions.Contains(key))
                        {
                            /* Item no longer exists on host – destroy locally */
                            pu.gameObject.SetActive(false);
                        }
                    }
                    catch { continue; }
                }
            }
            catch { }
        }

        /* ---- resources ---- */

        /**
         * @brief
         * Matches client-side resources (trees, rocks, etc.) to host-side
         * data using a name + proximity heuristic.  Uses Health.DealDamage()
         * instead of directly setting HP so that all visual effects fire:
         * Tree.OnHit skin changes, hit VFX, camera shake, and Tree.OnDie
         * stump/fall animations.
         */
        private static void ApplyResources(WorldStateSnapshot snapshot)
        {
            if (snapshot.Resources == null || snapshot.Resources.Length == 0) return;

            var allHealth = _cachedHealth;
            if (allHealth == null) return;

            foreach (var h in allHealth)
            {
                try
                {
                    if (h == null) continue;
                    var go = h.gameObject;
                    if (go.GetComponent<PlayerFarming>() != null) continue;
                    if (go.GetComponent<EnemyAI>() != null)       continue;
                    if (go.GetComponent<Interaction>() == null)    continue;

                    string localName = go.name ?? "";

                    /* Find closest matching host resource by name + proximity */
                    float bestDist = float.MaxValue;
                    WorldStateSnapshot.ResourceEntry best = default;
                    bool found = false;

                    foreach (var hr in snapshot.Resources)
                    {
                        if (!string.IsNullOrEmpty(hr.TypeName)
                            && !localName.StartsWith(hr.TypeName.Split('(')[0].TrimEnd(),
                                StringComparison.OrdinalIgnoreCase))
                            continue;

                        float dx = go.transform.position.x - hr.X;
                        float dy = go.transform.position.y - hr.Y;
                        float dist = dx * dx + dy * dy;
                        if (dist < bestDist)
                        {
                            bestDist = dist;
                            best     = hr;
                            found    = true;
                        }
                    }

                    if (!found) continue;
                    /* Only match within a small radius */
                    if (bestDist > 4f) continue;

                    /* If the host's HP is lower than ours, deal the delta
                       via DealDamage so OnHit/OnDie effects fire properly. */
                    if (best.HP < h.HP && h.HP > 0f)
                    {
                        float delta = h.HP - best.HP;
                        try
                        {
                            h.DealDamage(delta, go, go.transform.position,
                                false, Health.AttackTypes.Melee, true, (Health.AttackFlags)0);
                        }
                        catch
                        {
                            /* Fallback if DealDamage throws */
                            h.HP = best.HP;
                        }
                    }

                    /* Force-kill if host says destroyed and still alive locally */
                    if (best.IsDestroyed && h.HP > 0f && go.activeInHierarchy)
                    {
                        try
                        {
                            h.DealDamage(h.HP + 1f, go, go.transform.position,
                                false, Health.AttackTypes.Melee, true,
                                Health.AttackFlags.ForceKill);
                        }
                        catch
                        {
                            h.HP = 0f;
                        }
                    }
                }
                catch { continue; }
            }
        }

        /* ---- NPCs (insects, animals, ambient creatures) ---- */

        /**
         * @brief
         * Matches client-side NPCs (insects, animals, etc.) to host-side
         * data using a proximity heuristic.  Syncs position and death state
         * so the client's ambient scene matches the host's.
         */
        private static void ApplyNpcs(WorldStateSnapshot snapshot)
        {
            if (snapshot.Npcs == null || snapshot.Npcs.Length == 0) return;

            var units = _cachedUnits;
            if (units == null) return;

            foreach (var unit in units)
            {
                try
                {
                    if (unit == null) continue;
                    var go = unit.gameObject;
                    if (go.GetComponent<EnemyAI>() != null)       continue;
                    if (go.GetComponent<PlayerFarming>() != null) continue;
                    if (go.GetComponent<Follower>() != null)      continue;

                    string localName = go.name ?? "";

                    float bestDist = float.MaxValue;
                    WorldStateSnapshot.NpcEntry best = default;
                    bool found = false;

                    foreach (var hn in snapshot.Npcs)
                    {
                        if (!localName.StartsWith(hn.TypeName.Split('(')[0].TrimEnd(),
                            StringComparison.OrdinalIgnoreCase))
                            continue;

                        float dx = go.transform.position.x - hn.X;
                        float dy = go.transform.position.y - hn.Y;
                        float dist = dx * dx + dy * dy;
                        if (dist < bestDist)
                        {
                            bestDist = dist;
                            best     = hn;
                            found    = true;
                        }
                    }

                    if (!found) continue;

                    /* Lerp position */
                    Vector3 target = new Vector3(best.X, best.Y, go.transform.position.z);
                    float d = Vector3.Distance(go.transform.position, target);
                    if (d > 8f)
                        go.transform.position = target;
                    else
                        go.transform.position = Vector3.Lerp(
                            go.transform.position, target, 12f * Time.deltaTime);

                    /* Health — use DealDamage for hit/death effects */
                    var hp = go.GetComponent<Health>();
                    if (hp != null && best.HP < hp.HP && hp.HP > 0f)
                    {
                        float delta = hp.HP - best.HP;
                        try
                        {
                            hp.DealDamage(delta, go, go.transform.position,
                                false, Health.AttackTypes.Melee, true, (Health.AttackFlags)0);
                        }
                        catch { hp.HP = best.HP; }
                    }

                    /* Kill if host says dead */
                    if (best.IsDead && go.activeInHierarchy)
                    {
                        if (hp != null && hp.HP > 0f)
                        {
                            try
                            {
                                hp.DealDamage(hp.HP + 1f, go, go.transform.position,
                                    false, Health.AttackTypes.Melee, true,
                                    Health.AttackFlags.ForceKill);
                            }
                            catch { hp.HP = 0f; }
                        }
                    }
                }
                catch { continue; }
            }
        }

        /* ---- critters (spiders, birds, bees, butterflies) ---- */

        /**
         * @brief
         * Matches client-side critters to host-side data using a proximity
         * heuristic.  Syncs position and death state so spiders, birds etc.
         * are in the same positions on both machines.
         */
        private static void ApplyCritters(WorldStateSnapshot snapshot)
        {
            if (snapshot.Critters == null || snapshot.Critters.Length == 0) return;

            /* Apply spiders from the static list */
            try
            {
                if (CritterSpider.Spiders != null)
                {
                    foreach (var spider in CritterSpider.Spiders)
                    {
                        if (spider == null) continue;
                        var go = spider.gameObject;
                        string localName = go.name ?? "";

                        float bestDist = float.MaxValue;
                        WorldStateSnapshot.CritterEntry best = default;
                        bool found = false;

                        foreach (var hc in snapshot.Critters)
                        {
                            if (!localName.StartsWith(hc.TypeName.Split('(')[0].TrimEnd(),
                                StringComparison.OrdinalIgnoreCase))
                                continue;

                            float dx = go.transform.position.x - hc.X;
                            float dy = go.transform.position.y - hc.Y;
                            float dist = dx * dx + dy * dy;
                            if (dist < bestDist)
                            {
                                bestDist = dist;
                                best     = hc;
                                found    = true;
                            }
                        }

                        if (!found) continue;

                        Vector3 target = new Vector3(best.X, best.Y, go.transform.position.z);
                        float d = Vector3.Distance(go.transform.position, target);
                        if (d > 8f)
                            go.transform.position = target;
                        else if (d > 0.01f)
                            go.transform.position = Vector3.Lerp(
                                go.transform.position, target, 10f * Time.deltaTime);

                        if (best.IsDead && go.activeInHierarchy)
                            go.SetActive(false);
                    }
                }
            }
            catch { }

            /* Apply remaining critters (birds, bees, etc.) */
            try
            {
                var allCritters = _cachedCritters;
                if (allCritters == null) return;

                foreach (var critter in allCritters)
                {
                    if (critter == null) continue;
                    var go = critter.gameObject;
                    if (go.GetComponent<CritterSpider>() != null) continue;

                    string localName = go.name ?? "";

                    float bestDist = float.MaxValue;
                    WorldStateSnapshot.CritterEntry best = default;
                    bool found = false;

                    foreach (var hc in snapshot.Critters)
                    {
                        if (!localName.StartsWith(hc.TypeName.Split('(')[0].TrimEnd(),
                            StringComparison.OrdinalIgnoreCase))
                            continue;

                        float dx = go.transform.position.x - hc.X;
                        float dy = go.transform.position.y - hc.Y;
                        float dist = dx * dx + dy * dy;
                        if (dist < bestDist)
                        {
                            bestDist = dist;
                            best     = hc;
                            found    = true;
                        }
                    }

                    if (!found) continue;

                    Vector3 target = new Vector3(best.X, best.Y, go.transform.position.z);
                    float d = Vector3.Distance(go.transform.position, target);
                    if (d > 8f)
                        go.transform.position = target;
                    else if (d > 0.01f)
                        go.transform.position = Vector3.Lerp(
                            go.transform.position, target, 10f * Time.deltaTime);

                    if (best.IsDead && go.activeInHierarchy)
                        go.SetActive(false);
                }
            }
            catch { }
        }
    }
}

/* EOF */
