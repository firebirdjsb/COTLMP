// Cult of the Lamb Multiplayer Mod
// Licensed under MIT (https://spdx.org/licenses/MIT)
//
// Harmony patches that stop the game's coop lifecycle from removing
// or fighting with network-controlled avatars during multiplayer.

using COTLMP.Data;
using HarmonyLib;
using System;
using System.Collections;
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
}
