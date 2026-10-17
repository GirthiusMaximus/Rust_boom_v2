// TheatricalNukeProbe.cs — M0 probe harness for the Theatrical Nuke plugin.
//
// Source-verified against decompiled Assembly-CSharp (2026-10-17). NOT COMPILED here: there is no
// .NET toolchain in the build environment. Treat it as a starting point, not a shipped artefact.
// Test server only — R5 patches SupplySignal.Explode for every supply signal while enabled.
//
// Install: copy to carbon/plugins/TheatricalNukeProbe.cs, then `c.reload TheatricalNukeProbe`.
// Commands (admin only, in chat):
//   /probe missile              R1 — does the missile prefab exist and can it be spawned?
//   /probe cctv                 R2 — spawn a CCTV parented to you; prints its bookmark identifier
//   /probe cctvstop             R2 — remove it
//   /probe fx <prefab> [x y z]  R4 — broadcast an effect at a point (default: 2 km ahead)
//   /probe ddraw                R3 — draw test primitives for 30 s
//   /probe supply on|off        R5 — suppress SupplySignal.Explode (vanilla cargo plane)
//
// If the compiler complains about HarmonyLib (R5 only), delete the region marked R5 and everything
// below it that references Harmony; R1–R4 do not need it.

using System;
using System.Linq;
using Carbon.Plugins;
using HarmonyLib;
using Oxide.Plugins;
using UnityEngine;

namespace Carbon.Plugins;

[Info("TheatricalNukeProbe", "Theatrical Nuke", "0.1.0")]
[Description("M0 probes: missile prefab, CCTV parenting, broadcast FX, supply-signal suppression.")]
public class TheatricalNukeProbe : CarbonPlugin
{
    private const string MissilePrefab =
        "assets/content/structures/nuclear_missile_silo/nuclear_silo_missile.prefab";

    private const string CctvPrefab =
        "assets/prefabs/deployable/cctvcamera/cctv.static.prefab";

    private static TheatricalNukeProbe _instance;
    private static bool _suppressSupply;

    private CCTV_RC _cctv;
    private Harmony _harmony;

    private void Loaded()
    {
        _instance = this;
        Puts("TheatricalNukeProbe loaded. Commands: /probe missile | cctv | cctvstop | fx <prefab> [x y z] | ddraw | supply on|off");
    }

    private void Unload()
    {
        try { _harmony?.UnpatchAll("theatricalnuke.probe"); }
        catch (Exception ex) { Puts("unpatch failed: " + ex.Message); }

        _harmony = null;
        _suppressSupply = false;

        try { if (_cctv != null && !_cctv.IsDestroyed) _cctv.Kill(); }
        catch { }

        _cctv = null;
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
            player.ChatMessage("/probe missile | cctv | cctvstop | fx <prefab> [x y z] | ddraw | supply on|off");
            return;
        }

        try
        {
            switch (args[0].ToLowerInvariant())
            {
                case "missile":
                    ProbeMissile(player);
                    break;

                case "cctv":
                    ProbeCctv(player);
                    break;

                case "cctvstop":
                    if (_cctv != null && !_cctv.IsDestroyed) _cctv.Kill();
                    _cctv = null;
                    player.ChatMessage("R2: CCTV removed.");
                    break;

                case "fx":
                    ProbeFx(player, args);
                    break;

                case "ddraw":
                    ProbeDdraw(player);
                    break;

                case "supply":
                    ProbeSupply(player, args.Length > 1 && args[1].Equals("on", StringComparison.OrdinalIgnoreCase));
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

    // ── R1: missile prefab ────────────────────────────────────────────────────────────────────────
    private void ProbeMissile(BasePlayer player)
    {
        Puts("=== R1: missile prefab ===");
        Puts("path: " + MissilePrefab);

        GameObject prefab = null;

        try
        {
            prefab = GameManager.server.FindPrefab(MissilePrefab);
        }
        catch (Exception ex)
        {
            Puts("FindPrefab threw: " + ex);
        }

        if (prefab == null)
        {
            Puts("FindPrefab -> NULL (the server could not load this asset path)");
        }
        else
        {
            Puts("FindPrefab -> OK: " + prefab.name);

            var names = prefab.GetComponents<Component>()
                .Select(c => c == null ? "<missing>" : c.GetType().Name)
                .ToArray();

            Puts("components: " + string.Join(", ", names));

            var entity = prefab.GetComponent<BaseEntity>();
            Puts("BaseEntity component -> " + (entity == null ? "NONE (decorative prefab)" : entity.GetType().FullName));
        }

        BaseEntity spawned = null;

        try
        {
            var pos = player.transform.position + player.transform.forward * 6f + Vector3.up * 2f;
            spawned = GameManager.server.CreateEntity(MissilePrefab, pos, Quaternion.identity);
        }
        catch (Exception ex)
        {
            Puts("CreateEntity threw: " + ex);
        }

        if (spawned == null)
        {
            Puts("CreateEntity -> NULL (cannot be spawned as an entity with this prefab)");
            player.ChatMessage("R1: prefab not spawnable as an entity. See console.");
            return;
        }

        Puts("CreateEntity -> " + spawned.GetType().FullName);

        try
        {
            spawned.Spawn();
            Puts("Spawn() -> OK, net.id=" + (spawned.net == null ? 0u : spawned.net.ID));
            player.ChatMessage("R1: spawned " + spawned.GetType().Name + " in front of you (removed in 30 s).");

            timer.Once(30f, () =>
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
            Puts("IsStatic -> " + SafeBool(cctv.IsStatic));
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

    // ── R3: debug drawing ─────────────────────────────────────────────────────────────────────────
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
            player.ChatMessage("R3: primitives drawn for 30 s. Now type 'ddraw.clear' in F1 and report what happens.");
        }
        catch (Exception ex)
        {
            Puts("ddraw threw: " + ex);
        }
    }

    // ── R4: broadcast effect ──────────────────────────────────────────────────────────────────────
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
            Puts("Effect.server.Run(..., broadcast: true) -> sent");
            player.ChatMessage($"R4: played at {distance:0} m. Listen; then repeat from the far corner.");
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
    private static string SafeBool(Func<bool> value)
    {
        try { return value().ToString(); }
        catch (Exception ex) { return "threw: " + ex.GetType().Name; }
    }

    private static string SafeInt(Func<int> value)
    {
        try { return value().ToString(); }
        catch (Exception ex) { return "threw: " + ex.GetType().Name; }
    }
}
