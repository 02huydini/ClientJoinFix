using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using KrokoshaCasualtiesMP;
using KrokoshaCasualtiesUtils;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;

namespace ClientJoinFix {
    [BepInPlugin("clientjoinfix", "KrokMP Client join fix", "0.2.0")]
    [BepInDependency("KrokoshaCasualtiesMP")]
    [BepInDependency("noah.casualtiesunknown.multiplayerspritereplacer", BepInDependency.DependencyFlags.SoftDependency)]
    public class ClientJoinFix : BaseUnityPlugin {
        const float SCAN_INTERVAL = 5f;
        const float HEARTBEAT_INTERVAL = 15f;
        float timer;
        float heartbeatTimer;
        internal static ManualLogSource _log;
        static MethodInfo msrQueueRefresh;
        static MethodInfo msrOnPlayerJoined;
        static bool msrOnPlayerJoinedMissLogged;
        static readonly HashSet<int> nudgedClientIds = new HashSet<int>();
        bool wasReady;
        static readonly MethodInfo _setNbPlr = AccessTools.PropertySetter(typeof(NetBody), "plr");
        internal static long guardedNullBodyPackets;
        internal static long eventTriggeredRepairs;
        public static readonly string ReleaseURL = "https://api.github.com/repos/02huydini/ClientJoinFix/releases/latest";
        public static readonly string PLUGIN_VERSION = "0.2.0";
        public void Awake() {
            _log = Logger;
            string tag = "ClientJoinFix";
            Updater.ApplyPendingIfAny(Logger, tag);
            var harmony = new Harmony("clientjoinfix");
            harmony.PatchAll(typeof(ClientJoinFix).Assembly);
            // foreach (var m in harmony.GetPatchedMethods()) _log.LogInfo($"[ClientJoinFix] Patched: {m.DeclaringType?.Name}.{m.Name}");
            NetPlayer.OnPlayerJoined += OnPlayerJoinedHandler;
            StartCoroutine(Updater.CheckForUpdate(Logger, ReleaseURL, PLUGIN_VERSION, tag));
        }
        static bool Ready() {
            if (Net.is_server) return false;
            if (!Util.IsWorldInstantiated()) return false;
            return true;
        }
        static void OnPlayerJoinedHandler(NetPlayer p) {
            if (!Ready()) return;
            eventTriggeredRepairs++;
            // _log?.LogInfo($"[ClientJoinFix] OnPlayerJoined fired: clientId={p?.clientId}, playername={p?.playername}. Running immediate repair.");
            Repair();
            NudgeSpriteReplacer();
        }
        void Update() {
            bool r = Ready();
            if (r && !wasReady) {
                nudgedClientIds.Clear();
                // _log?.LogInfo("[ClientJoinFix] Fresh session detected (Ready() false->true), sprite-nudge dedup cache cleared.");
            }
            wasReady = r;
            heartbeatTimer += Time.deltaTime;
            if (heartbeatTimer >= HEARTBEAT_INTERVAL) {
                heartbeatTimer = 0f;
                int orphanBodies = 0;
                if (Ready()) orphanBodies = NetBody.all_instances.Count(b => b != null && b.is_player && b.plr == null);
                // _log?.LogInfo($"[ClientJoinFix] Heartbeat: is_server={Net.is_server}, worldInstantiated={Util.IsWorldInstantiated()}, ready={Ready()}, knownPlayers={NetPlayer.ClientIdToPlayerDict.Count}, orphanPlayerBodies={orphanBodies}, guardedNullBodyPackets={guardedNullBodyPackets}, eventTriggeredRepairs={eventTriggeredRepairs}");
            }
            if (!Ready()) return;
            timer += Time.deltaTime;
            if (timer < SCAN_INTERVAL) return;
            timer = 0f;
            Repair();
            NudgeSpriteReplacer();
        }
        void OnGUI() {
            if (!Ready()) return;
            if (GUI.Button(new Rect(10, 10, 190, 30), "Repair Player Links")) {
                Repair();
                NudgeSpriteReplacer();
            }
        }
        internal static void Repair() {
            if (Net.is_server) return;
            if (!Util.IsWorldInstantiated()) return;
            int total = 0, missingBody = 0, foundNb = 0, fixedNow = 0;
            foreach (NetPlayer plr in NetPlayer.ClientIdToPlayerDict.Values) {
                if (plr == null || plr.is_local) continue;
                total++;
                bool hadNb = NetBody.TryGetNetBodyFromId(plr.clientId, out NetBody nb) && nb != null;
                if (hadNb) foundNb++;
                if (plr.body == null) missingBody++;
                if (!hadNb) continue;
                if (plr.body == null) {
                    plr.body = nb.body;
                    fixedNow++;
                    _log?.LogInfo($"[ClientJoinFix] Linked client {plr.clientId} -> body {nb.name} (plr had no body).");
                }
                if (nb.plr != plr) {
                    _setNbPlr.Invoke(nb, new object[] { plr });
                    _log?.LogInfo($"[ClientJoinFix] Linked body {nb.name} -> client {plr.clientId} (body had no plr).");
                }
                if (plr.body != null) NetPlayer.BodyToPlayerDict[plr.body] = plr;
                if (plr.body != null) NudgeSpriteReplacerForPlayer(plr);
            }
            var staleKeys = NetPlayer.BodyToPlayerDict.Keys.Where(k => k == null).ToList();
            foreach (var k in staleKeys) NetPlayer.BodyToPlayerDict.Remove(k);
            int orphanBodies = 0;
            foreach (NetBody nb in NetBody.all_instances) {
                if (nb == null || !nb.is_player) continue;
                if (nb.plr != null) continue;
                orphanBodies++;
            }
            // _log?.LogInfo($"[ClientJoinFix] Roster: {total} remote players known, {missingBody} had no body, {foundNb} had a matching NetBody, {fixedNow} fixed, {staleKeys.Count} stale dict entries purged, {orphanBodies} player-bodies exist with zero matching NetPlayer (unfixable client-side).");
            // if (total == 0) _log?.LogWarning("[ClientJoinFix] ClientIdToPlayerDict has zero remote players. This is NOT a link/dictionary problem — the other clients never got registered as NetPlayer objects on this machine at all. Relinking cannot fix that; the roster/join packet itself is missing or not being applied.");
            // if (orphanBodies > 0) _log?.LogWarning($"[ClientJoinFix] {orphanBodies} player body object(s) exist locally with no owning NetPlayer at all. A NetBody without a NetPlayer cannot be relinked from the client; this needs the identity/registration packet resent, which only the host can do.");
        }
        internal static void NudgeSpriteReplacer() {
            if (msrQueueRefresh == null) {
                foreach (Assembly asm in AppDomain.CurrentDomain.GetAssemblies()) {
                    Type t = asm.GetType("SpriteApplication");
                    if (t == null) continue;
                    msrQueueRefresh = t.GetMethod("QueueRefresh", BindingFlags.Public | BindingFlags.Static);
                    if (msrQueueRefresh != null) break;
                }
            }
            if (msrQueueRefresh != null) msrQueueRefresh.Invoke(null, null);
        }
        internal static void NudgeSpriteReplacerForPlayer(NetPlayer plr) {
            if (plr == null) return;
            if (!nudgedClientIds.Add(plr.clientId)) return;
            if (msrOnPlayerJoined == null) {
                foreach (Assembly asm in AppDomain.CurrentDomain.GetAssemblies()) {
                    Type t = asm.GetType("SpriteNetwork");
                    if (t == null) continue;
                    msrOnPlayerJoined = t.GetMethod("OnPlayerJoined", BindingFlags.Public | BindingFlags.Static);
                    if (msrOnPlayerJoined != null) break;
                }
                if (msrOnPlayerJoined == null && !msrOnPlayerJoinedMissLogged) {
                    msrOnPlayerJoinedMissLogged = true;
                    // _log?.LogWarning("[ClientJoinFix] SpriteNetwork.OnPlayerJoined not found via reflection (MultiplayerSpriteReplacer absent or version mismatch). Sprite sync for late joiners will rely on MSR's own logic only.");
                }
            }
            if (msrOnPlayerJoined != null) {
                // _log?.LogInfo($"[ClientJoinFix] Nudging SpriteNetwork.OnPlayerJoined for clientId={plr.clientId}.");
                msrOnPlayerJoined.Invoke(null, new object[] { plr });
            }
        }
    }
    [HarmonyPatch(typeof(NetBody), "OnFoundNetPlayerInitFinish")]
    static class Patch_NetBody_OnFoundNetPlayerInitFinish {
        static void Postfix(NetBody __instance) {
            int? cid = __instance != null && __instance.plr != null ? __instance.plr.clientId : (int?)null;
            // ClientJoinFix._log?.LogInfo($"[ClientJoinFix] OnFoundNetPlayerInitFinish fired: body={__instance?.name}, plr.clientId={(cid.HasValue ? cid.Value.ToString() : "null")}");
            ClientJoinFix.Repair();
            ClientJoinFix.NudgeSpriteReplacer();
        }
    }
    [HarmonyPatch(typeof(NetBody), "SetBodyPosition", new Type[] { typeof(Vector2) })]
    static class Patch_NetBody_SetBodyPosition_Guard {
        static bool Prefix(NetBody __instance) {
            if (__instance != null && __instance.body != null) return true;
            ClientJoinFix.guardedNullBodyPackets++;
            return false;
        }
    }
    [HarmonyPatch(typeof(ServerMain), "HeyPlayerJustJoinedGiveHimASpawnLocationOkay")]
    static class Patch_ServerMain_HeyPlayerJustJoinedGiveHimASpawnLocationOkay {
        static void Postfix(knetid clientId) {
            if (!Net.is_server) return;
            int cidInt = clientId;
            // ClientJoinFix._log?.LogInfo($"[ClientJoinFix] Host: new client {cidInt} joined. Forcing full roster resync + registry re-registration for all connected players.");
            foreach (NetPlayer plr in ServerMain.AllPlayersExceptHost) {
                if (plr == null) continue;
                if (!plr.TryGetNetBody(out NetBody nb) || nb == null) continue;
                nb.Server_RemindPlayersCurrentState(true, true);
                NetObjectRegistry.Server_EnsureItemIsNetworkRegistered(nb.gameObject);
            }
            if (NetPlayer.TryGetPlayerFromClientId(clientId, out NetPlayer newPlr) && newPlr != null && newPlr.TryGetNetBody(out NetBody newNb) && newNb != null) {
                NetObjectRegistry.Server_EnsureItemIsNetworkRegistered(newNb.gameObject);
            }
        }
    }
}