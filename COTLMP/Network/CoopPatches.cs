// Cult of the Lamb Multiplayer Mod
// Licensed under MIT (https://spdx.org/licenses/MIT)
//
// Harmony patches that stop the game's coop lifecycle from removing
// or fighting with network-controlled avatars during multiplayer.

using COTLMP.Data;
using HarmonyLib;
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using UnityEngine;

namespace COTLMP.Network
{
    // During multiplayer, the game must not remove the coop player slot.
    // Vanilla code detects "no second controller" and fires removal
    // animations, faith rewards, difficulty resets, and HidePlayer in
    // a tight loop every frame. These patches short-circuit those paths.
    [HarmonyPatch]
    internal static class CoopPatches
    {
        // Block the game's built-in movement and state processing on the
        // network-controlled coop avatar. Without this, the game's follow-
        // the-leader logic constantly fights our network-driven position,
        // causing the remote player to snap to the host during interactions
        // like the donation box.
        [HarmonyPatch(typeof(PlayerFarming), "Update")]
        [HarmonyPrefix]
        private static bool BlockCoopAvatarUpdate(PlayerFarming __instance)
        {
            if (!InternalData.IsMultiplayerSession) return true;

            // The local player (lamb) should update normally
            if (__instance.isLamb) return true;

            // Block the game's update for the network-controlled coop avatar.
            // We drive position, animation, state, and health from the network
            // in RemotePlayerInfo.Tick(), so the game's own logic is not needed
            // and only causes desyncs.
            return false;
        }

        // Blocks Interaction.OnInteract when the colliding StateMachine
        // belongs to the coop avatar.  This prevents donation boxes,
        // chests, and other interactions from storing the coop avatar
        // as the active player, which causes SetMainPlayer to switch
        // the camera/control to the network avatar and desync the host.
        [HarmonyPatch(typeof(Interaction), nameof(Interaction.OnInteract))]
        [HarmonyPrefix]
        private static bool BlockCoopAvatarInteraction(StateMachine state)
        {
            if (!InternalData.IsMultiplayerSession) return true;
            if (state == null) return true;

            var pf = state.GetComponent<PlayerFarming>();
            if (pf != null && !pf.isLamb)
                return false; // block: coop avatar must not interact

            return true;
        }

        // Prevents SetMainPlayer from ever switching the active player
        // to the coop avatar during multiplayer.  Multiple game systems
        // (donation box, interaction podiums, cutscenes) call this and
        // it causes the host camera to follow the network avatar.
        //
        // SetMainPlayer has three overloads — we patch the StateMachine
        // and PlayerFarming variants (the Collider2D one resolves to
        // one of the other two internally).
        [HarmonyPatch(typeof(PlayerFarming), nameof(PlayerFarming.SetMainPlayer),
            new Type[] { typeof(StateMachine) })]
        [HarmonyPrefix]
        private static bool BlockSetMainPlayerToCoopSM(StateMachine state)
        {
            if (!InternalData.IsMultiplayerSession) return true;
            if (state == null) return true;

            var pf = state.GetComponent<PlayerFarming>();
            if (pf != null && !pf.isLamb)
                return false;

            return true;
        }

        [HarmonyPatch(typeof(PlayerFarming), nameof(PlayerFarming.SetMainPlayer),
            new Type[] { typeof(PlayerFarming) })]
        [HarmonyPrefix]
        private static bool BlockSetMainPlayerToCoopPF(PlayerFarming playerFarming)
        {
            if (!InternalData.IsMultiplayerSession) return true;
            if (playerFarming == null) return true;

            if (!playerFarming.isLamb)
                return false;

            return true;
        }

        [HarmonyPatch(typeof(PlayerFarming), nameof(PlayerFarming.SetMainPlayer),
            new Type[] { typeof(UnityEngine.Collider2D) })]
        [HarmonyPrefix]
        private static bool BlockSetMainPlayerToCoopCol(UnityEngine.Collider2D collider2D)
        {
            if (!InternalData.IsMultiplayerSession) return true;
            if (collider2D == null) return true;

            var pf = collider2D.GetComponentInParent<PlayerFarming>();
            if (pf != null && !pf.isLamb)
                return false;

            return true;
        }

        // Blocks CoopManager.RemoveCoopPlayer so the remote player's
        // despawn animation, faith reward, and Rewired reset never fire.
        [HarmonyPatch(typeof(CoopManager), nameof(CoopManager.RemoveCoopPlayer))]
        [HarmonyPrefix]
        private static bool BlockRemoveCoopPlayer()
        {
            return !InternalData.IsMultiplayerSession;
        }

        // Same deal for the static variant.
        [HarmonyPatch(typeof(CoopManager), nameof(CoopManager.RemoveCoopPlayerStatic))]
        [HarmonyPrefix]
        private static bool BlockRemoveCoopPlayerStatic()
        {
            return !InternalData.IsMultiplayerSession;
        }

        // Blocks the menu-driven removal path (controller disconnect).
        // Fires when the game thinks a controller was disconnected.
        [HarmonyPatch(typeof(CoopManager), nameof(CoopManager.RemovePlayerFromMenu))]
        [HarmonyPrefix]
        private static bool BlockRemovePlayerFromMenu()
        {
            return !InternalData.IsMultiplayerSession;
        }

        // Blocks ClearCoopMode which destroys all coop players.
        [HarmonyPatch(typeof(CoopManager), nameof(CoopManager.ClearCoopMode))]
        [HarmonyPrefix]
        private static bool BlockClearCoopMode()
        {
            return !InternalData.IsMultiplayerSession;
        }

        // Blocks the temporary hide that disables the Spine renderer
        // and deactivates the gameObject.
        [HarmonyPatch(typeof(CoopManager), nameof(CoopManager.HideCoopPlayerTemporarily))]
        [HarmonyPrefix]
        private static bool BlockHideCoopPlayerTemporarily()
        {
            return !InternalData.IsMultiplayerSession;
        }

        // Blocks PlayerFarming.HidePlayer so the avatar stays in the
        // players list. Allow hiding the local lamb (cutscenes, etc.)
        // but never hide the network-controlled coop slot.
        [HarmonyPatch(typeof(PlayerFarming), nameof(PlayerFarming.HidePlayer))]
        [HarmonyPrefix]
        private static bool BlockHidePlayer(PlayerFarming playerFarming)
        {
            if (!InternalData.IsMultiplayerSession) return true;

            if (playerFarming != null && !playerFarming.isLamb)
                return false;

            return true;
        }

        // Replaces WaitTillPlayersRady with an empty coroutine during
        // multiplayer. The original expects Rewired input from a physical
        // second controller and NullRefs when the slot is network-driven.
        // We set __result to a valid IEnumerator because returning false
        // with null causes "routine is null" from StartCoroutine.
        [HarmonyPatch(typeof(CoopManager), "WaitTillPlayersRady")]
        [HarmonyPrefix]
        private static bool BlockWaitTillPlayersRady(ref IEnumerator __result)
        {
            if (!InternalData.IsMultiplayerSession) return true;
            __result = EmptyCoroutine();
            return false;
        }

        private static IEnumerator EmptyCoroutine()
        {
            yield break;
        }

        // Swallows the NullRef that happens when RefreshCoopPlayerRewired
        // tries to access the Rewired input player for slot 1. The network-
        // driven avatar has no physical controller, so GetPlayer(1) NullRefs.
        // The local lamb (slot 0) is set up correctly before the crash, so
        // swallowing this is safe.
        [HarmonyPatch(typeof(CoopManager), nameof(CoopManager.RefreshCoopPlayerRewired))]
        [HarmonyFinalizer]
        private static Exception SafeRefreshCoopPlayerRewired(Exception __exception)
        {
            if (InternalData.IsMultiplayerSession && __exception != null)
                return null;
            return __exception;
        }

        // After the coop system spawns a second player, strip any camera-
        // targeting components from the new avatar so the camera stays on
        // the local lamb.
        [HarmonyPatch(typeof(CoopManager), nameof(CoopManager.SpawnCoopPlayer))]
        [HarmonyPostfix]
        private static void AfterCoopSpawn()
        {
            if (!InternalData.IsMultiplayerSession) return;

            try
            {
                if (PlayerFarming.players == null) return;
                foreach (var pf in PlayerFarming.players)
                {
                    if (pf == null || pf.isLamb) continue;

                    // Strip camera-follow components so the camera ignores this avatar
                    foreach (var mb in pf.GetComponents<UnityEngine.MonoBehaviour>())
                    {
                        if (mb != null && mb.GetType().Name.Contains("CameraFollow"))
                            UnityEngine.Object.Destroy(mb);
                    }

                    // Disable trigger colliders (safety net, LinkAvatar also does this)
                    foreach (var col in pf.GetComponentsInChildren<UnityEngine.Collider2D>())
                    {
                        if (col.isTrigger)
                            col.enabled = false;
                    }

                    // Remove the coop avatar from camera targets immediately
                    try
                    {
                        if (pf.CameraBone != null)
                        {
                            var cam = GameManager.GetInstance()?.CamFollowTarget;
                            cam?.RemoveTarget(pf.CameraBone);
                        }
                    }
                    catch { }
                }
            }
            catch { /* scene not ready */ }
        }

        // Prevents the game from adding the coop avatar's camera bone
        // to CameraFollowTarget.targets.  The game re-adds camera targets
        // every frame from multiple paths (PlayerFarming.Update,
        // CameraFollowTarget.LateUpdate init, GameManager.AddToCamera)
        // so stripping them after spawn is not enough.
        [HarmonyPatch(typeof(CameraFollowTarget), nameof(CameraFollowTarget.AddTarget))]
        [HarmonyPrefix]
        private static bool BlockCoopCameraTarget(GameObject g)
        {
            if (!InternalData.IsMultiplayerSession) return true;
            if (g == null) return true;

            var pf = g.GetComponentInParent<PlayerFarming>();
            if (pf != null && !pf.isLamb)
                return false;

            return true;
        }

        // Safety-net: before CameraFollowTarget computes the frame,
        // remove any target that belongs to a non-lamb PlayerFarming.
        // This catches targets that slipped past BlockCoopCameraTarget
        // (e.g. direct list manipulation, serialisation, or a path we
        // haven't patched).
        [HarmonyPatch(typeof(CameraFollowTarget), "LateUpdate")]
        [HarmonyPrefix]
        private static void StripNonLambCameraTargets(CameraFollowTarget __instance)
        {
            if (!InternalData.IsMultiplayerSession) return;
            if (__instance.targets == null || __instance.targets.Count == 0) return;

            for (int i = __instance.targets.Count - 1; i >= 0; i--)
            {
                var t = __instance.targets[i];
                if (t == null || t.gameObject == null) continue;

                var pf = t.gameObject.GetComponentInParent<PlayerFarming>();
                if (pf != null && !pf.isLamb)
                    __instance.targets.RemoveAt(i);
            }
        }
    }

    // Prevents clients from triggering door/scene transitions.
    // Only the host may walk through doors; clients follow via
    // the SceneChange network message.
    //
    // On the host, every door prefix also verifies the entering
    // collider belongs to the local lamb.  The coop avatar (remote
    // player) carries the "Player" tag and a PlayerFarming component,
    // so without this check it would fire the same door trigger,
    // creating an infinite host→client loading loop.
    //
    // Kept in a separate class so that if a target method is renamed
    // in a game update, CoopPatches still loads fine.
    [HarmonyPatch]
    internal static class DoorPatches
    {
        // Returns true only when the collider belongs to the local
        // lamb player.  The coop avatar shares the "Player" tag so
        // a simple tag check is not enough.
        private static bool IsLambCollider(Collider2D col)
        {
            if (col == null) return false;
            var pf = col.GetComponentInParent<PlayerFarming>();
            if (pf == null)
                pf = col.gameObject.GetComponent<PlayerFarming>();
            return pf != null && pf.isLamb;
        }

        // Shared gate: client always blocked, host requires lamb collider.
        private static bool AllowDoor(Collider2D col)
        {
            if (!InternalData.IsMultiplayerSession) return true;
            if (!InternalData.IsHost) return false;
            return IsLambCollider(col);
        }

        // Door (dungeon room-to-room transitions)
        [HarmonyPatch(typeof(Door), "OnTriggerEnter2D")]
        [HarmonyPrefix]
        private static bool BlockDoorTrigger(Collider2D __0)
        {
            return AllowDoor(__0);
        }

        // DoorRoomDoor (door-room to dungeon run)
        [HarmonyPatch(typeof(Inteaction_DoorRoomDoor), "OnTriggerEnter2D")]
        [HarmonyPrefix]
        private static bool BlockDoorRoomDoorTrigger(Collider2D __0)
        {
            return AllowDoor(__0);
        }

        // BaseDungeonDoor (base to dungeon entry)
        // The IsLambCollider check prevents the coop avatar from
        // triggering the door on the host, which was the root cause
        // of the host↔client loading loop.  The vanilla black-fade
        // transition is kept intact.
        [HarmonyPatch(typeof(Interaction_BaseDungeonDoor), "OnTriggerEnter2D")]
        [HarmonyPrefix]
        private static bool BlockBaseDungeonDoorTrigger(Collider2D __0)
        {
            return AllowDoor(__0);
        }

        // BaseDoor (base to world map)
        [HarmonyPatch(typeof(Interaction_BaseDoor), "OnTriggerEnter2D")]
        [HarmonyPrefix]
        private static bool BlockBaseDoorTrigger(Collider2D __0)
        {
            return AllowDoor(__0);
        }

        // BiomeDoor (biome transitions)
        [HarmonyPatch(typeof(Interaction_BiomeDoor), "OnTriggerEnter2D")]
        [HarmonyPrefix]
        private static bool BlockBiomeDoorTrigger(Collider2D __0)
        {
            return AllowDoor(__0);
        }

        // DungeonDoor (dungeon activation trigger)
        [HarmonyPatch(typeof(DungeonDoor), "OnTriggerEnter2D")]
        [HarmonyPrefix]
        private static bool BlockDungeonDoorTrigger(Collider2D __0)
        {
            return AllowDoor(__0);
        }
    }

    // Suppresses client-side follower AI so follower positions,
    // animations and task assignments are driven entirely by the
    // host's world-state heartbeat.
    //
    // Without this, TimeManager.Simulate() ticks every FollowerBrain
    // on the client independently — followers choose different tasks,
    // walk to different positions, and fight the host snapshot data
    // every 2 seconds, causing constant visual desyncs.
    //
    // Kept in a separate class so an isolated failure does not
    // prevent DoorPatches or CoopPatches from loading.
    [HarmonyPatch]
    internal static class FollowerSyncPatches
    {
        // Block Follower.Tick on clients.  Tick is the main entry
        // called by TimeManager.Simulate(); it reassigns tasks via
        // HardSwapToTask and drives movement through Brain.Tick →
        // CurrentTask.Tick.  Skipping it makes the client's followers
        // purely visual puppets driven by the host snapshot.
        [HarmonyPatch(typeof(Follower), nameof(Follower.Tick))]
        [HarmonyPrefix]
        private static bool BlockFollowerTickOnClient()
        {
            if (!InternalData.IsMultiplayerSession) return true;
            if (InternalData.IsHost) return true;
            return false;
        }

        // Block FollowerBrain.CheckChangeTask on clients as a safety
        // net.  Even with Follower.Tick blocked, the brain's own
        // CheckChangeTask can fire from FollowerBrain.Tick during
        // state transitions or idle fallbacks.
        [HarmonyPatch(typeof(FollowerBrain), "CheckChangeTask")]
        [HarmonyPrefix]
        private static bool BlockCheckChangeTaskOnClient()
        {
            if (!InternalData.IsMultiplayerSession) return true;
            if (InternalData.IsHost) return true;
            return false;
        }
    }

    // Save-game patches for multiplayer.
    //
    // Client: blocks all saves so the client never overwrites the host's
    // authoritative data (the client uses a disposable temp slot).
    //
    // Host: after every save, re-captures and compresses the save file
    // then broadcasts it to all clients to keep their temp slot current.
    [HarmonyPatch]
    internal static class SavePatches
    {
        // Blocks SaveAndLoad.Save() on clients. The client's DataManager
        // is only a mirror of the host's save.
        [HarmonyPatch(typeof(SaveAndLoad), "Save", new Type[0])]
        [HarmonyPrefix]
        private static bool BlockClientSave()
        {
            if (InternalData.IsMultiplayerSession && !InternalData.IsHost)
                return false;
            return true;
        }

        // Blocks the overload SaveAndLoad.Save(string) on clients.
        [HarmonyPatch(typeof(SaveAndLoad), "Save", new Type[] { typeof(string) })]
        [HarmonyPrefix]
        private static bool BlockClientSaveWithFilename()
        {
            if (InternalData.IsMultiplayerSession && !InternalData.IsHost)
                return false;
            return true;
        }

        // After the host saves, read the save file back, compress it,
        // and broadcast it to all clients so their temp slot stays in sync.
        [HarmonyPatch(typeof(SaveAndLoad), "Save", new Type[0])]
        [HarmonyPostfix]
        private static void HostResyncAfterSave()
        {
            if (!InternalData.IsHost) return;
            if (PlayerSync.ActiveClient == null || !PlayerSync.ActiveClient.IsConnected) return;

            try
            {
                string savesDir = Path.Combine(Application.persistentDataPath, "saves");
                string slotName = SaveAndLoad.MakeSaveSlot(SaveAndLoad.SAVE_SLOT);
                string mpPath   = Path.Combine(savesDir, Path.ChangeExtension(slotName, ".mp"));
                string jsonPath = Path.Combine(savesDir, slotName);
                string savePath = File.Exists(mpPath) ? mpPath : jsonPath;

                if (!File.Exists(savePath)) return;

                byte[] raw        = File.ReadAllBytes(savePath);
                byte[] compressed = Compress(raw);
                PlayerSync.ActiveClient.SendSaveResync(compressed);

                Plugin.Logger?.LogInfo($"[SavePatches] Broadcast save resync ({raw.Length} -> {compressed.Length} bytes)");
            }
            catch (Exception e)
            {
                Plugin.Logger?.LogWarning($"[SavePatches] Save resync failed: {e.Message}");
            }
        }

        private static byte[] Compress(byte[] raw)
        {
            using (var output = new MemoryStream())
            {
                using (var gz = new GZipStream(output, CompressionMode.Compress, true))
                    gz.Write(raw, 0, raw.Length);
                return output.ToArray();
            }
        }
    }

    // Dungeon-specific patches for multiplayer.
    //
    // 1) Sync the dungeon generation seed so clients produce the same
    //    room layout as the host.
    // 2) Auto-open room barriers / doors on clients so players are
    //    never stuck behind a weapon-check or puzzle-gate that only
    //    fires on the host.
    // 3) Allow all players to pick up items (weapons, curses, relics)
    //    from pedestals, not just the host.
    //
    // Kept in a separate class so a single patch failure does not
    // prevent CoopPatches / DoorPatches from loading.
    //
    // NOTE: GenerateRoom is MMRoomGeneration+GenerateRoom (a nested
    // type).  We cannot use [HarmonyPatch(typeof(...))] at compile
    // time because the parent type's dependencies fail to load via
    // reflection in some environments.  Instead we apply manual
    // patches at runtime in a static constructor.
    internal static class DungeonSyncPatches
    {
        /* ---- runtime patch wiring --------------------------------------- */

        internal static void ApplyManualPatches(HarmonyLib.Harmony harmony)
        {
            /* ---- Patch BiomeGenerator.Start for dungeon seed sync ---- */
            try
            {
                /* BiomeGenerator.Start() is where the master dungeon seed
                   is read: this.Seed = DataManager.RandomSeed.Next(...)
                   We need our prefix to run BEFORE that line so we can
                   overwrite DataManager.RandomSeed with the host's seed. */
                var bgType = typeof(MMBiomeGeneration.BiomeGenerator);
                var bgStart = bgType.GetMethod("Start",
                    System.Reflection.BindingFlags.Instance |
                    System.Reflection.BindingFlags.Public |
                    System.Reflection.BindingFlags.NonPublic);
                if (bgStart != null)
                {
                    var prefix = new HarmonyMethod(typeof(DungeonSyncPatches),
                        nameof(SeedClientBiome));
                    harmony.Patch(bgStart, prefix: prefix);
                    Plugin.Logger?.LogInfo("[DungeonSync] Patched BiomeGenerator.Start");
                }
            }
            catch (Exception e)
            {
                Plugin.Logger?.LogWarning($"[DungeonSync] BiomeGenerator patch failed: {e.Message}");
            }

            /* ---- Patch GenerateRoom.Start for per-room seed sync ---- */
            try
            {
                /* Resolve MMRoomGeneration+GenerateRoom at runtime */
                var genRoomType = System.Type.GetType("MMRoomGeneration+GenerateRoom, Assembly-CSharp");
                if (genRoomType == null)
                {
                    /* Fallback: scan all loaded assemblies */
                    foreach (var asm in System.AppDomain.CurrentDomain.GetAssemblies())
                    {
                        genRoomType = asm.GetType("MMRoomGeneration+GenerateRoom");
                        if (genRoomType != null) break;
                    }
                }

                if (genRoomType != null)
                {
                    var startMethod = genRoomType.GetMethod("Start",
                        System.Reflection.BindingFlags.Instance |
                        System.Reflection.BindingFlags.Public |
                        System.Reflection.BindingFlags.NonPublic);

                    if (startMethod != null)
                    {
                        var prefix = new HarmonyMethod(typeof(DungeonSyncPatches),
                            nameof(SeedClientDungeon));
                        var postfix = new HarmonyMethod(typeof(DungeonSyncPatches),
                            nameof(AutoOpenClientRoomBarriers));
                        harmony.Patch(startMethod, prefix: prefix, postfix: postfix);
                        Plugin.Logger?.LogInfo("[DungeonSync] Patched GenerateRoom.Start");
                    }
                }
            }
            catch (Exception e)
            {
                Plugin.Logger?.LogWarning($"[DungeonSync] Manual patch failed: {e.Message}");
            }

            /* Patch Inteaction_DoorRoomDoor for weapon-check bypass */
            try
            {
                /* Try multiple candidate method names for the door-ready
                   check: CanOpenDoors, TryToOpenDoors.  The exact name
                   varies by game version. */
                var doorType = typeof(Inteaction_DoorRoomDoor);
                string[] candidates = { "CanOpenDoors", "TryToOpenDoors", "ReadyToOpenDoors" };
                foreach (var name in candidates)
                {
                    var m = doorType.GetMethod(name,
                        System.Reflection.BindingFlags.Instance |
                        System.Reflection.BindingFlags.Public |
                        System.Reflection.BindingFlags.NonPublic);
                    if (m != null && m.ReturnType == typeof(bool))
                    {
                        var postfix = new HarmonyMethod(typeof(DungeonSyncPatches),
                            nameof(ForceClientDoorReady));
                        harmony.Patch(m, postfix: postfix);
                        Plugin.Logger?.LogInfo($"[DungeonSync] Patched {name} on DoorRoomDoor");
                        break;
                    }
                }
            }
            catch (Exception e)
            {
                Plugin.Logger?.LogWarning($"[DungeonSync] DoorRoomDoor patch failed: {e.Message}");
            }

            /* Patch Interaction_WeaponSelectionPodium.GetAllPlayersWearingWeapons()
               This is the REAL weapon-check: it loops all PlayerFarming.players
               and returns false if any has currentWeapon == None.  The coop avatar
               has no weapon, so the dungeon start room doors never open.
               On clients, force it to return true. */
            try
            {
                var podiumType = typeof(Interaction_WeaponSelectionPodium);
                var m = podiumType.GetMethod("GetAllPlayersWearingWeapons",
                    System.Reflection.BindingFlags.Instance |
                    System.Reflection.BindingFlags.Public |
                    System.Reflection.BindingFlags.NonPublic);
                if (m != null)
                {
                    var postfix = new HarmonyMethod(typeof(DungeonSyncPatches),
                        nameof(ForceAllPlayersArmed));
                    harmony.Patch(m, postfix: postfix);
                    Plugin.Logger?.LogInfo("[DungeonSync] Patched GetAllPlayersWearingWeapons");
                }
            }
            catch (Exception e)
            {
                Plugin.Logger?.LogWarning($"[DungeonSync] WeaponPodium patch failed: {e.Message}");
            }
            /* Patch PickUp.OnTriggerEnter2D finalizer for safe item pickup */
            try
            {
                var pickupTrigger = typeof(PickUp).GetMethod("OnTriggerEnter2D",
                    System.Reflection.BindingFlags.Instance |
                    System.Reflection.BindingFlags.Public |
                    System.Reflection.BindingFlags.NonPublic);
                if (pickupTrigger != null)
                {
                    var finalizer = new HarmonyMethod(typeof(DungeonSyncPatches),
                        nameof(SafePickUp));
                    harmony.Patch(pickupTrigger, finalizer: finalizer);
                }
            }
            catch { }
        }

        /* ---- BiomeGenerator seed sync ----------------------------------- */

        // Prefix on BiomeGenerator.Start — must run BEFORE the line:
        //   this.Seed = DataManager.RandomSeed.Next(-2147483647, int.MaxValue);
        // so we replace DataManager.RandomSeed with one seeded from the host.
        private static void SeedClientBiome()
        {
            if (!InternalData.IsMultiplayerSession) return;
            if (InternalData.IsHost) return;

            try
            {
                var dm = DataManager.Instance;
                if (dm == null) return;

                if (dm.LastDungeonSeeds != null && dm.LastDungeonSeeds.Count > 0)
                {
                    int seed = dm.LastDungeonSeeds[dm.LastDungeonSeeds.Count - 1];
                    if (seed != 0)
                    {
                        DataManager.RandomSeed = new System.Random(seed);
                        DataManager.UseDataManagerSeed = true;
                        UnityEngine.Random.InitState(seed);
                        Plugin.Logger?.LogInfo($"[DungeonSync] Client BiomeGenerator seeded to {seed}");
                    }
                }
            }
            catch { }
        }

        /* ---- Dungeon seed sync ------------------------------------------- */

        // Prefix on GenerateRoom.Start — seed the client's RNG
        private static void SeedClientDungeon()
        {
            if (!InternalData.IsMultiplayerSession) return;
            if (InternalData.IsHost) return;

            try
            {
                var dm = DataManager.Instance;
                if (dm == null) return;

                /* DataManager.RandomSeed is the System.Random used by
                   dungeon generation.  Reseed it and Unity's RNG so
                   GenerateRoom.GenerateRandomSeed() produces the same
                   Seed value as on the host. */
                if (dm.LastDungeonSeeds != null && dm.LastDungeonSeeds.Count > 0)
                {
                    int seed = dm.LastDungeonSeeds[dm.LastDungeonSeeds.Count - 1];
                    if (seed != 0)
                    {
                        DataManager.RandomSeed = new System.Random(seed);
                        DataManager.UseDataManagerSeed = true;
                        UnityEngine.Random.InitState(seed);
                        Plugin.Logger?.LogInfo($"[DungeonSync] Client seeded RNG to {seed}");
                    }
                }
            }
            catch { }
        }

        /* ---- Room barrier / weapon-check bypass -------------------------- */

        // Postfix on GenerateRoom.Start — schedule barrier auto-open
        // The __instance parameter is typed as MonoBehaviour so the
        // patch signature does not require the nested type at compile time.
        private static void AutoOpenClientRoomBarriers(MonoBehaviour __instance)
        {
            if (!InternalData.IsMultiplayerSession) return;
            if (InternalData.IsHost) return;

            try
            {
                Plugin.MonoInstance?.StartCoroutine(ForceOpenBarriersWhenClear(__instance));
            }
            catch { }
        }

        private static IEnumerator ForceOpenBarriersWhenClear(MonoBehaviour room)
        {
            // Let the room finish initialising (enemies spawn, etc.)
            yield return new WaitForSeconds(1.5f);

            // Poll until the room is cleared or the object is destroyed
            for (int attempt = 0; attempt < 120; attempt++)
            {
                if (room == null) yield break;

                bool hasLivingEnemy = false;
                try
                {
                    var enemies = room.GetComponentsInChildren<EnemyAI>(false);
                    if (enemies != null)
                    {
                        foreach (var ai in enemies)
                        {
                            if (ai == null) continue;
                            var hp = ai.GetComponent<Health>();
                            if (hp != null && hp.HP > 0f)
                            {
                                hasLivingEnemy = true;
                                break;
                            }
                        }
                    }
                }
                catch { }

                if (!hasLivingEnemy)
                {
                    // Room is clear — call the game's own RoomCompleted
                    // which fires OnRoomCompleted events and opens barriers
                    try { RoomLockController.RoomCompleted(false, true); }
                    catch { }

                    // Fallback: also try RoomLockController.OpenAll()
                    try { RoomLockController.OpenAll(); }
                    catch { }

                    yield break;
                }

                yield return new WaitForSeconds(0.5f);
            }
        }

        /* ---- Weapon / curse check bypass --------------------------------- */

        // Applied manually to Inteaction_DoorRoomDoor.CanOpenDoors (or
        // TryToOpenDoors) — forces the result to true on clients.
        private static void ForceClientDoorReady(ref bool __result)
        {
            if (!InternalData.IsMultiplayerSession) return;
            if (InternalData.IsHost) return;
            __result = true;
        }

        // Postfix on Interaction_WeaponSelectionPodium.GetAllPlayersWearingWeapons.
        // The original checks EVERY PlayerFarming in the players list.
        // The network coop avatar has currentWeapon == None, so it returns false
        // and the dungeon start room doors stay locked.
        // During multiplayer (both host and client), force it to only consider
        // the local lamb (isLamb == true) — or just return true if the lamb
        // has a weapon, ignoring the coop avatar.
        private static void ForceAllPlayersArmed(ref bool __result)
        {
            if (!InternalData.IsMultiplayerSession) return;

            try
            {
                // Check only the local lamb
                if (PlayerFarming.Instance != null
                    && PlayerFarming.Instance.currentWeapon != EquipmentType.None)
                {
                    __result = true;
                }
            }
            catch { }
        }

        /* ---- Allow item pick-up for all players -------------------------- */

        // Finalizer: if PickUp.OnTriggerEnter2D throws for the coop
        // avatar (e.g. because Rewired player is null), silently
        // swallow the error so the item still gets collected.
        private static Exception SafePickUp(Exception __exception)
        {
            if (InternalData.IsMultiplayerSession && __exception != null)
                return null;
            return __exception;
        }
    }
}
