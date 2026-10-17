// TheatricalNukeProbe.cs — M0 probe harness for the Theatrical Nuke plugin.
//
// Source-verified against decompiled Assembly-CSharp (2026-10-17). NOT COMPILED here: there is no
// .NET toolchain in the build environment. Treat it as a starting point, not a shipped artefact.
// Test server only — the supply probe patches SupplySignal.Explode for every supply signal.
//
// Install: copy to carbon/plugins/TheatricalNukeProbe.cs, then `c.reload TheatricalNukeProbe`.
// Commands (admin only, in chat):
//   /probe spawn <prefab>       R8  — is this prefab an entity, and can it be spawned?
//   /probe missile              R1  — the missile prefab specifically
//   /probe bus                  R8b — the nuclear_warhead prefab specifically
//   /probe cctv                 R2  — CCTV parented to you; prints its bookmark identifier
//   /probe cctvstop             R2  — remove it
//   /probe siren [x y z]        R6  — pulse the air-raid sound via Effect.server.Run (run 1: REJECTED by engine)
//   /probe spawn <path>         R6b — spawn an asset path as an entity; prints components (AudioSource?)
//   /probe marker               R7  — spawn the generic-radius map marker at your feet
//   /probe markerstop           R7  — remove it
//   /probe plume                R9  — 4 neutered FireBalls parented to you
//   /probe plumestop            R9  — remove them
//   /probe fx <prefab> [x y z]  R4  — broadcast an effect at a point (default: 2 km ahead)
//   /probe ddraw                R3  — draw test primitives for 30 s
//   /probe supply on|off        R5  — suppress SupplySignal.Explode (vanilla cargo plane)
//
// If the compiler complains about HarmonyLib (R5 only), delete the R5 region and its command case.

using System;
using System.Collections.Generic;
using System.Linq;
using Carbon.Plugins;
using HarmonyLib;
using Oxide.Plugins;
using UnityEngine;

namespace Carbon.Plugins;

[Info("TheatricalNukeProbe", "Theatrical Nuke", "0.2.0")]
[Description("M0 probes: prefab spawnability, CCTV parenting, siren reach, map marker, plumes.")]
public class TheatricalNukeProbe : CarbonPlugin
{
    private const string MissilePrefab =
        "assets/content/structures/nuclear_missile_silo/nuclear_silo_missile.prefab";

    private const string BusPrefab =
        "assets/bundled/prefabs/modding/events/twitch/nuclear_warhead.prefab";

    private const string SirenPrefab =
        "assets/bundled/prefabs/modding/sound/air_raid/airraidsound.prefab";

    private const string PlumePrefab =
        "assets/prefabs/npc/m2bradley/oilfireball2.prefab";

    private const string CctvPrefab =
        "assets/prefabs/deployable/cctvcamera/cctv.static.prefab";

    private const string MarkerPrefab =
        "assets/prefabs/tools/map/genericradiusmarker.prefab";

    private static TheatricalNukeProbe _instance;
    private static bool _suppressSupply;

    private readonly List<BaseEntity> _temporary = new List<BaseEntity>();

    private CCTV_RC _cctv;
    private Harmony _harmony;

    private void Loaded()
    {
        _instance = this;
        Puts("TheatricalNukeProbe v0.2 loaded.");
        Puts("Commands: /probe spawn <prefab> | missile | bus | cctv | cctvstop | siren [x y z] | " +
             "marker | markerstop | plume | plumestop | fx <prefab> [x y z] | ddraw | supply on|off");
    }

    private void Unload()
    {
        try { _harmony?.UnpatchAll("theatricalnuke.probe"); }
        catch (Exception ex) { Puts("unpatch failed: " + ex.Message); }

        _harmony = null;
        _suppressSupply = false;

        ClearTemporary();
        _instance = null;
    }

    [ChatCommand("probe")]
    private void CmdProbe(BasePlayer player, string command, string[] args)
    {
        if (player == null)
        {
            Puts("Run /probe in-game.");
            return;
        }

        if (!player.IsAdmin)
        {
            player.ChatMessage("Admins only.");
            return;
        }

        if (args == null || args.Length == 0)
        {
            player.ChatMessage("/probe spawn <prefab> | missile | bus | cctv | cctvstop | " +
                               "siren [x y z] | marker | markerstop | plume | plumestop | " +
                               "fx <prefab> [x y z] | ddraw | supply on|off");
            return;
        }

        try
        {
            switch (args[0].ToLowerInvariant())
            {
                case "spawn":
                    if (args.Length < 2) { player.ChatMessage("/probe spawn <prefab>"); break; }
                    ProbeSpawn(player, args[1]);
                    break;

                case "missile":
                    ProbeSpawn(player, MissilePrefab);
                    break;

                case "bus":
                    ProbeSpawn(player, BusPrefab);
                    break;

                case "cctv":
                    ProbeCctv(player);
                    break;

                case "cctvstop":
                    if (_cctv != null && !_cctv.IsDestroyed) _cctv.Kill();
                    _cctv = null;
                    player.ChatMessage("R2: CCTV removed.");
                    break;

                case "siren":
                    ProbeSiren(player, args);
                    break;

                case "marker":
                    ProbeMarker(player);
                    break;

                case "markerstop":
                    ClearTemporary();
                    player.ChatMessage("R7: temporary entities removed.");
                    break;

                case "plume":
                    ProbePlume(player);
                    break;

                case "plumestop":
                    ClearTemporary();
                    player.ChatMessage("R9: temporary entities removed.");
                    break;

                case "fx":
                    ProbeFx(player, args);
                    break;

                case "ddraw":
                    ProbeDdraw(player);
                    break;

                case "supply":
                    ProbeSupply(player, args.Length > 1 &&
                                          args[1].Equals("on", StringComparison.OrdinalIgnoreCase));
                    break;

                default:
                    player.ChatMessage("Unknown probe: " + args[0]);
                    break;
            }
        }
        catch (Exception ex)
        {
            Puts("probe threw: " + ex);
            player.ChatMessage("Probe threw; see console.");
        }
    }

    // ── R8 / R1: generic prefab spawnability ─────────────────────────────────────────────────────
    private void ProbeSpawn(BasePlayer player, string prefabPath)
    {
        Puts("=== spawn probe: " + prefabPath + " ===");

        GameObject prefab = null;

        try
        {
            prefab = GameManager.server.FindPrefab(prefabPath);
        }
        catch (Exception ex)
        {
            Puts("FindPrefab threw: " + ex);
        }

        if (prefab == null)
        {
            Puts("FindPrefab -> NULL (the server cannot load this asset path)");
            player.ChatMessage("FindPrefab returned null. Wrong path for this build, or not a server asset.");
            return;
        }

        Puts("FindPrefab -> OK: " + prefab.name);

        try
        {
            var names = prefab.GetComponents<Component>()
                .Select(c => c == null ? "<missing>" : c.GetType().Name)
                .ToArray();
            Puts("components: " + string.Join(", ", names));

            var entityComponent = prefab.GetComponent<BaseEntity>();
            Puts("BaseEntity component -> " +
                 (entityComponent == null ? "NONE (effect/decorative prefab)" : entityComponent.GetType().FullName));
        }
        catch (Exception ex)
        {
            Puts("component dump threw: " + ex);
        }

        BaseEntity spawned = null;

        try
        {
            var pos = player.transform.position + player.transform.forward * 8f + Vector3.up * 2f;
            spawned = GameManager.server.CreateEntity(prefabPath, pos, Quaternion.identity);
        }
        catch (Exception ex)
        {
            Puts("CreateEntity threw: " + ex);
        }

        if (spawned == null)
        {
            Puts("CreateEntity -> NULL (not spawnable as an entity; try /probe fx for an effect)");
            player.ChatMessage("Not spawnable as an entity. If it is a sound/particle, use /probe fx.");
            return;
        }

        Puts("CreateEntity -> " + spawned.GetType().FullName);

        try
        {
            spawned.Spawn();
            _temporary.Add(spawned);
            Puts("Spawn() -> OK, net.id=" + (spawned.net == null ? 0u : spawned.net.ID));
            player.ChatMessage("Spawned " + spawned.GetType().Name + " in front of you (use markerstop to clear).");

            timer.Once(60f, () =>
            {
                try { if (spawned != null && !spawned.IsDestroyed) spawned.Kill(); }
                catch { }
            });
        }
        catch (Exception ex)
        {
            Puts("Spawn() threw: " + ex);
        }
    }

    // ── R2: CCTV parenting ────────────────────────────────────────────────────────────────────────
    private void ProbeCctv(BasePlayer player)
    {
        Puts("=== R2: CCTV parenting ===");

        if (_cctv != null && !_cctv.IsDestroyed) _cctv.Kill();
        _cctv = null;

        var pos = player.transform.position + player.transform.forward * 3f + Vector3.up * 1.5f;
        var forward = (player.transform.position - pos).normalized;

        CCTV_RC cctv = null;

        try
        {
            cctv = GameManager.server.CreateEntity(CctvPrefab, pos, Quaternion.LookRotation(forward, Vector3.up)) as CCTV_RC;
        }
        catch (Exception ex)
        {
            Puts("CreateEntity threw: " + ex);
        }

        if (cctv == null)
        {
            Puts("CreateEntity -> NULL, or the prefab is not a CCTV_RC");
            player.ChatMessage("R2: could not spawn CCTV. See console.");
            return;
        }

        try
        {
            cctv.Spawn();
            _cctv = cctv;
            Puts("CCTV spawned: " + cctv.GetType().FullName + " net.id=" + (cctv.net == null ? 0u : cctv.net.ID));
            Puts("ConsumptionAmount -> " + SafeInt(cctv.ConsumptionAmount));

            var identifier = ((IRemoteControllable)cctv).GetIdentifier();
            Puts("identifier -> " + identifier);
            player.ChatMessage("R2: CCTV id = " + identifier);
            player.ChatMessage("Add it at a computer station, then walk around and watch the feed.");
        }
        catch (Exception ex)
        {
            Puts("spawn/identifier threw: " + ex);
        }

        try
        {
            cctv.SetParent(player, worldPositionStays: true);
            Puts("SetParent(player, worldPositionStays: true) -> OK");
            player.ChatMessage("R2: camera is parented to you — the feed should move as you walk.");
        }
        catch (Exception ex)
        {
            Puts("SetParent threw: " + ex);
        }
    }

    // ── R6: siren reach + latecomer ──────────────────────────────────────────────────────────────
    private void ProbeSiren(BasePlayer player, string[] args)
    {
        Vector3 pos;

        if (args.Length >= 4 &&
            float.TryParse(args[1], out var x) &&
            float.TryParse(args[2], out var y) &&
            float.TryParse(args[3], out var z))
        {
            pos = new Vector3(x, y, z);
        }
        else
        {
            pos = player.transform.position + player.transform.forward * 400f;
        }

        var distance = Vector3.Distance(player.transform.position, pos);
        Puts($"=== R6: siren '{SirenPrefab}' at {pos} (distance {distance:0} m) ===");
        Puts("Pulsing every 8 s for 40 s. Listen, then move far away and back.");

        try
        {
            Effect.server.Run(SirenPrefab, pos, Vector3.up, null, true);
            Puts("pulse 1 submitted to Effect.server.Run (no return value: check the console for 'unknown effect' lines)");
        }
        catch (Exception ex)
        {
            Puts("Effect.server.Run threw: " + ex);
            return;
        }

        timer.Repeat(8f, 4, () =>
        {
            try
            {
                Effect.server.Run(SirenPrefab, pos, Vector3.up, null, true);
                Puts("siren pulse submitted (check for 'unknown effect' lines above)");
            }
            catch (Exception ex)
            {
                Puts("siren pulse threw: " + ex.Message);
            }
        });

        player.ChatMessage($"R6: siren at {distance:0} m, pulsing for 40 s. Note how far you can hear it.");
    }

    // ── R7: map marker ───────────────────────────────────────────────────────────────────────────
    private void ProbeMarker(BasePlayer player)
    {
        Puts("=== R7: generic radius marker ===");

        var pos = player.transform.position + player.transform.forward * 5f;

        MapMarkerGenericRadius marker = null;

        try
        {
            marker = GameManager.server.CreateEntity(MarkerPrefab, pos, Quaternion.identity) as MapMarkerGenericRadius;
        }
        catch (Exception ex)
        {
            Puts("CreateEntity threw: " + ex);
        }

        if (marker == null)
        {
            Puts("CreateEntity -> NULL, or the prefab is not a MapMarkerGenericRadius");
            player.ChatMessage("R7: marker spawn failed. See console.");
            return;
        }

        try
        {
            marker.radius = 60f;
            marker.color1 = new Color(1f, 0.20f, 0.85f, 1f);
            marker.color2 = new Color(1f, 0.20f, 0.85f, 1f);
            marker.alpha = 0.55f;
            marker.Spawn();
            marker.SendUpdate();
            _temporary.Add(marker);

            Puts("marker spawned, net.id=" + (marker.net == null ? 0u : marker.net.ID));
            player.ChatMessage("R7: purple 60 m circle should appear at your feet on the map — for EVERY player, not just you.");
            player.ChatMessage("Ask a second player to confirm they see it.");
        }
        catch (Exception ex)
        {
            Puts("marker spawn threw: " + ex);
        }
    }

    // ── R9: FireBall plumes ──────────────────────────────────────────────────────────────────────
    private void ProbePlume(BasePlayer player)
    {
        Puts("=== R9: FireBall plumes (oilfireball2) ===");

        var origin = player.transform.position + player.transform.forward * 4f + Vector3.up * 3f;

        for (var i = 0; i < 4; i++)
        {
            try
            {
                var offset = new Vector3((i - 1.5f) * 0.8f, 0f, 0f);
                var fire = GameManager.server.CreateEntity(PlumePrefab, origin + offset, Quaternion.identity) as FireBall;

                if (fire == null)
                {
                    Puts("CreateEntity -> NULL or not a FireBall");
                    break;
                }

                // Neuter: no damage, no spreading, long life so it does not vanish mid-test.
                fire.damagePerSecond = 0f;
                fire.radius = 0f;
                fire.spreadSubEntity = null;
                fire.canMerge = false;
                fire.lifeTimeMin = 300f;
                fire.lifeTimeMax = 300f;
                fire.ignoreNPC = true;

                fire.Spawn();
                fire.SetParent(player, worldPositionStays: true);
                _temporary.Add(fire);

                Puts($"plume {i + 1}: spawned and parented, {fire.GetType().Name}");
            }
            catch (Exception ex)
            {
                Puts("plume threw: " + ex);
            }
        }

        player.ChatMessage("R9: 4 plumes parented to you. Do they follow you? Do they look like booster flames?");
        player.ChatMessage("Check: did anything catch fire or take damage? Use /probe plumestop to clear.");
    }

    // ── R3: debug drawing ────────────────────────────────────────────────────────────────────────
    private void ProbeDdraw(BasePlayer player)
    {
        Puts("=== R3: ddraw ===");

        var p = player.transform.position;

        try
        {
            player.SendConsoleCommand("ddraw.line", 30f, Color.red, p, p + Vector3.up * 5f);
            player.SendConsoleCommand("ddraw.sphere", 30f, Color.cyan, p + player.transform.forward * 5f, 1f);
            player.SendConsoleCommand("ddraw.text", 30f, Color.white, p + Vector3.up * 6f, "PROBE");
            Puts("ddraw.line/sphere/text sent (30 s).");
            player.ChatMessage("R3: drawn for 30 s. Now type 'ddraw.clear' in F1 and report what happens.");
        }
        catch (Exception ex)
        {
            Puts("ddraw threw: " + ex);
        }
    }

    // ── R4: broadcast effect ─────────────────────────────────────────────────────────────────────
    private void ProbeFx(BasePlayer player, string[] args)
    {
        if (args.Length < 2)
        {
            player.ChatMessage("/probe fx <prefab> [x y z]");
            return;
        }

        var prefab = args[1];
        Vector3 pos;

        if (args.Length >= 5 &&
            float.TryParse(args[2], out var x) &&
            float.TryParse(args[3], out var y) &&
            float.TryParse(args[4], out var z))
        {
            pos = new Vector3(x, y, z);
        }
        else
        {
            pos = player.transform.position + player.transform.forward * 2000f;
        }

        var distance = Vector3.Distance(player.transform.position, pos);
        Puts($"=== R4: broadcast '{prefab}' at {pos} (distance {distance:0} m) ===");

        try
        {
            Effect.server.Run(prefab, pos, Vector3.up, null, true);
            Puts("Effect.server.Run(..., broadcast: true) submitted (no return value: check for 'unknown effect' lines above)");
            player.ChatMessage($"R4: submitted at {distance:0} m. Silence + an 'unknown effect' console line means the path is not in the effect pool."); 
        }
        catch (Exception ex)
        {
            Puts("Effect.server.Run threw: " + ex);
        }
    }

    // ── R5: supply-signal suppression ────────────────────────────────────────────────────────────
    private void ProbeSupply(BasePlayer player, bool enabled)
    {
        _suppressSupply = enabled;

        if (enabled)
        {
            if (_harmony == null)
            {
                _harmony = new Harmony("theatricalnuke.probe");
                _harmony.PatchAll(GetType().Assembly);
            }

            Puts("R5: SupplySignal.Explode patch installed.");
            player.ChatMessage("R5: ON — throw a supply signal. No cargo plane should spawn.");
        }
        else
        {
            try { _harmony?.UnpatchAll("theatricalnuke.probe"); }
            catch (Exception ex) { Puts("unpatch threw: " + ex); }

            _harmony = null;
            Puts("R5: patch removed.");
            player.ChatMessage("R5: OFF — vanilla supply signals restored.");
        }
    }

    [HarmonyPatch(typeof(SupplySignal), nameof(SupplySignal.Explode))]
    private static class SupplySignalExplodePatch
    {
        private static bool Prefix(SupplySignal __instance)
        {
            if (!_suppressSupply) return true;

            _instance?.Puts("[R5] SupplySignal.Explode intercepted; vanilla cargo plane suppressed.");

            try { __instance.Kill(); }
            catch { }

            return false;
        }
    }

    // ── helpers ──────────────────────────────────────────────────────────────────────────────────
    private void ClearTemporary()
    {
        foreach (var entity in _temporary.ToArray())
        {
            try { if (entity != null && !entity.IsDestroyed) entity.Kill(); }
            catch { }
        }

        _temporary.Clear();
    }

    private static string SafeInt(Func<int> value)
    {
        try { return value().ToString(); }
        catch (Exception ex) { return "threw: " + ex.GetType().Name; }
    }
}
