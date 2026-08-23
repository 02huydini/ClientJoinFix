using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using KrokoshaCasualtiesMP;
using KrokoshaCasualtiesUtils;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using UnityEngine;

namespace ClientJoinFix {
    [BepInPlugin("02dumbass.clientjoinfix", "ClientJoinFix", "0.3.24")]
    [BepInDependency("KrokoshaCasualtiesMP")]
    [BepInDependency("noah.casualtiesunknown.multiplayerspritereplacer", BepInDependency.DependencyFlags.SoftDependency)]
    public class ClientJoinFix : BaseUnityPlugin {
        const float SCAN_INTERVAL = 5f;
        const int MAX_PERIODIC_SCANS = 12;
        float timer;
        internal static ManualLogSource _log;
        static MethodInfo msrQueueRefresh;
        static MethodInfo msrOnPlayerJoined;
        static bool msrOnPlayerJoinedMissLogged;
        static readonly Dictionary<int, string> lastNudgedName = new Dictionary<int, string>();
        static readonly HashSet<NetBody> emptyNameBodies = new HashSet<NetBody>();
        static readonly HashSet<int> pendingJoinClientIds = new HashSet<int>();
        static bool joinRepairDirty;
        static int periodicScansRemaining;
        internal static long emptyStringHoverHits;
        bool wasReady;
        static readonly MethodInfo _setNbPlr = AccessTools.PropertySetter(typeof(NetBody), "plr");
        internal static long guardedNullBodyPackets;
        internal static long eventTriggeredRepairs;
        internal static bool JoinInProgress => pendingJoinClientIds.Count > 0;
        internal static int PendingJoinCount => pendingJoinClientIds.Count;
        internal static int PeriodicScansRemaining => periodicScansRemaining;
        internal static void MarkJoinRepairDirty() { joinRepairDirty = true; }
        public void Awake() {
            _log = Logger;
            Updater.ApplyPendingIfAny(Logger);
            var harmony = new Harmony("clientjoinfix");
            harmony.PatchAll(typeof(ClientJoinFix).Assembly);
            foreach (var m in harmony.GetPatchedMethods()) DebugBridge.Log("Patched", $"{m.DeclaringType?.Name}.{m.Name}");
            NetPlayer.OnPlayerJoined += OnPlayerJoinedHandler;
            StartCoroutine(Updater.CheckForUpdate(Logger));
        }
        internal static bool Ready() {
            if (Net.is_server) return false;
            if (!Util.IsWorldInstantiated()) return false;
            return true;
        }
        internal static void DrawRepairButtonMenuSlot() {
            DebugBridge.DrawRepairButton(Ready());
        }
        static void OnPlayerJoinedHandler(NetPlayer p) {
            if (!Ready()) return;
            eventTriggeredRepairs++;
            if (p != null) pendingJoinClientIds.Add(p.clientId);
            periodicScansRemaining = MAX_PERIODIC_SCANS;
            DebugBridge.Log("OnPlayerJoined", $"clientId={p?.clientId}, playername={p?.playername}. {pendingJoinClientIds.Count} join(s) now tracked pending, periodic scan armed for {MAX_PERIODIC_SCANS} scans. Running immediate repair.");
            Repair();
            NudgeSpriteReplacer();
        }
        void Update() {
            bool r = Ready();
            if (r && !wasReady) {
                lastNudgedName.Clear();
                pendingJoinClientIds.Clear();
                joinRepairDirty = false;
                periodicScansRemaining = 0;
                DebugBridge.Log("SessionReset", "Fresh session detected (Ready() false->true), sprite-nudge dedup cache and join-tracking cleared.");
            }
            wasReady = r;
            DebugBridge.Heartbeat(Time.deltaTime);
            if (!Ready()) return;
            if (joinRepairDirty) {
                joinRepairDirty = false;
                DebugBridge.Log("CoalescedRepair", $"Draining coalesced body-init repair, {pendingJoinClientIds.Count} join(s) still pending.");
                Repair();
                NudgeSpriteReplacer();
            }
            if (periodicScansRemaining <= 0) return;
            timer += Time.deltaTime;
            if (timer < SCAN_INTERVAL) return;
            timer = 0f;
            if (!AnyBodylessPlayer()) {
                DebugBridge.Log("PeriodicScan", $"No bodyless players remain, turning periodic scan off early ({periodicScansRemaining} scan(s) unused).");
                periodicScansRemaining = 0;
                return;
            }
            periodicScansRemaining--;
            DebugBridge.Log("PeriodicScan", $"Running scan ({MAX_PERIODIC_SCANS - periodicScansRemaining}/{MAX_PERIODIC_SCANS}), {periodicScansRemaining} remaining before auto-off.");
            Repair();
            NudgeSpriteReplacer();
            if (periodicScansRemaining == 0) DebugBridge.Log("PeriodicScan", "Scan window exhausted (1 minute used), turning off until next join.");
        }
        void OnGUI() {
            DebugBridge.DrawFlash();
        }
        internal static bool AnyBodylessPlayer() {
            if (Net.is_server) return false;
            if (!Util.IsWorldInstantiated()) return false;
            foreach (NetPlayer plr in NetPlayer.ClientIdToPlayerDict.Values) {
                if (plr == null || plr.is_local) continue;
                if (plr.body == null) return true;
            }
            return false;
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
                    DebugBridge.Log("Repair", $"Linked client {plr.clientId} -> body {nb.name} (plr had no body).");
                }
                if (nb.plr != plr) {
                    _setNbPlr.Invoke(nb, new object[] { plr });
                    DebugBridge.Log("Repair", $"Linked body {nb.name} -> client {plr.clientId} (body had no plr).");
                }
                if (pendingJoinClientIds.Count > 0 && plr.body != null && nb.plr == plr && pendingJoinClientIds.Remove(plr.clientId)) {
                    DebugBridge.Log("JoinResolved", $"clientId={plr.clientId} fully loaded in (body linked both directions). {pendingJoinClientIds.Count} join(s) still pending.");
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
            DebugBridge.Log("Repair", $"Roster: {total} remote players known, {missingBody} had no body, {foundNb} had a matching NetBody, {fixedNow} fixed, {staleKeys.Count} stale dict entries purged, {orphanBodies} player-bodies exist with zero matching NetPlayer (unfixable client-side).");
            if (total == 0) DebugBridge.Warn("ZeroRoster", "ClientIdToPlayerDict has zero remote players. This is NOT a link/dictionary problem — the other clients never got registered as NetPlayer objects on this machine at all. Relinking cannot fix that; the roster/join packet itself is missing or not being applied.");
            if (orphanBodies > 0) DebugBridge.Warn("OrphanBodies", $"{orphanBodies} player body object(s) exist locally with no owning NetPlayer at all. A NetBody without a NetPlayer cannot be relinked from the client; this needs the identity/registration packet resent, which only the host can do.");
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
            string name = plr.playername;
            if (lastNudgedName.TryGetValue(plr.clientId, out string prevName) && prevName == name) return;
            lastNudgedName[plr.clientId] = name;
            if (msrOnPlayerJoined == null) {
                foreach (Assembly asm in AppDomain.CurrentDomain.GetAssemblies()) {
                    Type t = asm.GetType("SpriteNetwork");
                    if (t == null) continue;
                    msrOnPlayerJoined = t.GetMethod("OnPlayerJoined", BindingFlags.Public | BindingFlags.Static);
                    if (msrOnPlayerJoined != null) break;
                }
                if (msrOnPlayerJoined == null && !msrOnPlayerJoinedMissLogged) {
                    msrOnPlayerJoinedMissLogged = true;
                    DebugBridge.Warn("MSRMissing", "SpriteNetwork.OnPlayerJoined not found via reflection (MultiplayerSpriteReplacer absent or version mismatch). Sprite sync for late joiners will rely on MSR's own logic only.");
                }
            }
            if (msrOnPlayerJoined != null) {
                DebugBridge.Log("MSRNudge", $"Nudging SpriteNetwork.OnPlayerJoined for clientId={plr.clientId}, name={name} (prev={prevName ?? "none"}).");
                msrOnPlayerJoined.Invoke(null, new object[] { plr });
            }
        }
        internal static string ResolveRealName(NetBody nb, string fallback) {
            if (nb == null || nb.plr == null) return fallback;
            string realName = nb.plr.playername;
            return !string.IsNullOrEmpty(realName) && realName != "EMPTY_STRING" ? realName : fallback;
        }
        internal static void ReportEmptyStringName(NetBody nb) {
            if (nb == null) return;
            if (!emptyNameBodies.Add(nb)) return;
            emptyStringHoverHits++;
            DebugBridge.TriggerFlash();
            bool hasPlr = nb.plr != null;
            string realName = hasPlr ? nb.plr.playername : null;
            bool realNameUsable = hasPlr && !string.IsNullOrEmpty(realName) && realName != "EMPTY_STRING";
            int? cid = hasPlr ? nb.plr.clientId : (int?)null;
            bool isLocal = hasPlr && nb.plr.is_local;
            DebugBridge.Log("EMPTY_STRING", $"body={nb.name}, is_player={nb.is_player}, hasPlr={hasPlr}, plr.clientId={(cid.HasValue ? cid.Value.ToString() : "n/a")}, plr.is_local={isLocal}, plr.playername={(realName ?? "n/a")}, realNameUsable={realNameUsable}.");
            DebugBridge.LinkSnapshot("EMPTY_STRING-before-fix");
            if (realNameUsable) {
                DebugBridge.Log("EMPTY_STRING", $"body={nb.name}: underlying plr.playername is already correct ({realName}), body's own cached name is stale. Forcing set_bodyname({realName}) and correcting the getter's own return value directly, since the getter clearly doesn't just read bodyname (Repair() only fixes plr<->body links and doesn't touch either).");
                nb.bodyname = realName;
            } else if (Ready()) {
                DebugBridge.Log("EMPTY_STRING", $"body={nb.name}: no usable plr.playername yet, falling back to Repair() in case the link itself is the problem.");
                Repair();
            }
            DebugBridge.LinkSnapshot("EMPTY_STRING-after-fix");
        }
        internal static void ClearEmptyStringName(NetBody nb) {
            if (nb == null) return;
            emptyNameBodies.Remove(nb);
        }
    }
    [HarmonyPatch(typeof(NetBody), "OnFoundNetPlayerInitFinish")]
    static class Patch_NetBody_OnFoundNetPlayerInitFinish {
        static void Postfix(NetBody __instance) {
            int? cid = __instance != null && __instance.plr != null ? __instance.plr.clientId : (int?)null;
            DebugBridge.Log("OnFoundNetPlayerInitFinish", $"body={__instance?.name}, plr.clientId={(cid.HasValue ? cid.Value.ToString() : "null")}");
            if (!ClientJoinFix.JoinInProgress) return;
            DebugBridge.Log("CoalescedRepair", $"Body-init fired while a join is pending — marking repair dirty instead of running it inline.");
            ClientJoinFix.MarkJoinRepairDirty();
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
    [HarmonyPatch(typeof(NetBody), "get_playername")]
    static class Patch_NetBody_get_playername_Diag {
        static void Postfix(NetBody __instance, ref string __result) {
            if (__result == "EMPTY_STRING") {
                ClientJoinFix.ReportEmptyStringName(__instance);
                __result = ClientJoinFix.ResolveRealName(__instance, __result);
            } else ClientJoinFix.ClearEmptyStringName(__instance);
        }
    }
    [HarmonyPatch(typeof(NetBody), "get_bodyname")]
    static class Patch_NetBody_get_bodyname_Diag {
        static void Postfix(NetBody __instance, ref string __result) {
            if (__result == "EMPTY_STRING") {
                ClientJoinFix.ReportEmptyStringName(__instance);
                __result = ClientJoinFix.ResolveRealName(__instance, __result);
            } else ClientJoinFix.ClearEmptyStringName(__instance);
        }
    }
    [HarmonyPatch(typeof(ServerMain), "HeyPlayerJustJoinedGiveHimASpawnLocationOkay")]
    static class Patch_ServerMain_HeyPlayerJustJoinedGiveHimASpawnLocationOkay {
        static void Postfix(knetid clientId) {
            if (!Net.is_server) return;
            int cidInt = clientId;
            DebugBridge.Log("HostSideResync", $"new client {cidInt} joined. Forcing full roster resync + registry re-registration for all connected players.");
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
    [HarmonyPatch(typeof(UIMainMenu), "_GUI_DrawSideMenuWithPlayerList")]
    static class Patch_UIMainMenu_DrawSideMenuWithPlayerList_RepairButtonSlot {
        static readonly MethodInfo endHorizontal = AccessTools.Method(typeof(GUILayout), "EndHorizontal", Type.EmptyTypes);
        static readonly MethodInfo injectedSlot = AccessTools.Method(typeof(ClientJoinFix), nameof(ClientJoinFix.DrawRepairButtonMenuSlot));
        static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions) {
            bool injected = false;
            foreach (CodeInstruction instr in instructions) {
                yield return instr;
                if (!injected && instr.Calls(endHorizontal)) { yield return new CodeInstruction(OpCodes.Call, injectedSlot); injected = true; }
            }
        }
    }

    static class DebugBridge {
        static Type t;
        static bool resolved;
        static MethodInfo miLog, miWarn, miLinkSnapshot, miTriggerFlash, miDrawFlash, miDrawRepairButton, miHeartbeat;
        static void Resolve() {
            if (resolved) return;
            resolved = true;
            t = Type.GetType("ClientJoinFix.ClientJoinFixDebug");
            if (t == null) { ClientJoinFix._log?.LogInfo("[ClientJoinFix] ClientJoinFixDebug.cs not present in this build, falling back to plain logging and inline repair button."); return; }
            var flags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
            miLog = t.GetMethod("Log", flags);
            miWarn = t.GetMethod("Warn", flags);
            miLinkSnapshot = t.GetMethod("LinkSnapshot", flags);
            miTriggerFlash = t.GetMethod("TriggerFlash", flags);
            miDrawFlash = t.GetMethod("DrawEmptyStringFlashOverlay", flags);
            miDrawRepairButton = t.GetMethod("DrawRepairButton", flags);
            miHeartbeat = t.GetMethod("Heartbeat", flags);
        }
        internal static void Heartbeat(float deltaTime) {
            Resolve();
            if (miHeartbeat != null) miHeartbeat.Invoke(null, new object[] { deltaTime });
        }
        internal static void Log(string tag, string msg) {
            Resolve();
            if (miLog != null) { miLog.Invoke(null, new object[] { tag, msg }); return; }
            ClientJoinFix._log?.LogInfo($"[ClientJoinFix][{tag}] {msg}");
        }
        internal static void Warn(string tag, string msg) {
            Resolve();
            if (miWarn != null) { miWarn.Invoke(null, new object[] { tag, msg }); return; }
            ClientJoinFix._log?.LogWarning($"[ClientJoinFix][{tag}] {msg}");
        }
        internal static void LinkSnapshot(string tag) {
            Resolve();
            if (miLinkSnapshot != null) miLinkSnapshot.Invoke(null, new object[] { tag });
        }
        internal static void TriggerFlash() {
            Resolve();
            if (miTriggerFlash != null) miTriggerFlash.Invoke(null, null);
        }
        internal static void DrawFlash() {
            Resolve();
            if (miDrawFlash != null) miDrawFlash.Invoke(null, null);
        }
        internal static void DrawRepairButton(bool ready) {
            Resolve();
            if (miDrawRepairButton != null) { miDrawRepairButton.Invoke(null, new object[] { ready }); return; }
            if (!ready) return;
            if (GUILayout.Button("Repair Player Links")) {
                ClientJoinFix.Repair();
                ClientJoinFix.NudgeSpriteReplacer();
            }
        }
    }
}