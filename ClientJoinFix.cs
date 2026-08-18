using BepInEx;
using KrokoshaCasualtiesMP;
using KrokoshaCasualtiesUtils;
using UnityEngine;

[BepInPlugin("clientjoinfix", "KrokMP Client Join Fix", "1.0.0")]
[BepInDependency("KrokoshaCasualtiesMP")]
public class ClientJoinFixPlugin : BaseUnityPlugin {
    const float SCAN_INTERVAL = 10f;
    float timer;
    bool Ready() {
        if (Net.is_server) return false;
        if (!Util.IsWorldInstantiated()) return false;
        return true;
    }
    void Update() {
        if (!Ready()) return;
        timer += Time.deltaTime;
        if (timer < SCAN_INTERVAL) return;
        timer = 0f;
        RepairMissingLinks();
    }
    void OnGUI() {
        if (!Ready()) return;
        if (GUI.Button(new Rect(10, 10, 190, 30), "Repair Player Links")) RepairMissingLinks();
    }
    void RepairMissingLinks() {
        foreach (NetPlayer plr in NetPlayer.ClientIdToPlayerDict.Values) {
            if (plr.is_local) continue;
            if (plr.body != null) continue;
            if (!NetBody.TryGetNetBodyFromId(plr.clientId, out NetBody body)) continue;
            plr.body = body.body;
            NetPlayer.BodyToPlayerDict[body.body] = plr;
            Logger.LogInfo($"[PlayerLinkRepair] Linked client {plr.clientId} -> body {body.name}.");
        }
    }
}