// TheatricalNukeProbe.cs — M0 probe harness, v0.11.13.
//
// v0.2 compiled and ran on the live test server (run 1 + run 2 console output), so the
// calls it used are proven. New in v0.3 and NOT yet compiled: BaseEntity.SetFlag,
// BaseEntity.Flags.On, SendNetworkUpdateImmediate, Invoke(Action,float) inside the R5
// prefix, UnityEngine.Object.FindObjectsOfType.
// Compile-checked in run 3: SetFlag, Flags.On, IsStatic() and GetIdentifier() are public.
// AudioSource is NOT referenced by type (AudioModule is not in the plugin reference set); it is read by reflection.
// If Carbon reports a compile error, those lines are the first suspects.
//
// PROTOCOL (v0.8, one session log per session):
//   /probe start                  open the session log. Required before any probe.
//   /probe <ID> [args]            start a probe. Everything it does is written to the session log.
//   /probe <ID> end               end it and run cleanup. No observation yet, so it is queued and the box opens on /probe end.
//   /probe <ID> end <words>       end it with the observation typed in chat (no box).
//   /probe end                    collect every outstanding observation in order (box for each), then close the session.
//   /probe obs <words>            chat fallback: give the next outstanding observation without the box.
//   Box: type in the field, press Submit (saves and opens the next). X closes without saving; the item stays queued.
//   /probe abort | status | help
// IDs: R0 R1 R1b R1c R1e R1f R1g R1h R1i R1j R1k R1m R1n R1o R1p R1q R1r R1s R1t R2 R3 R4 R5 R6 R6b R6c R7 R8 R9 R10
// Quote any asset path that contains a space:  /probe R6b "assets/... air raid/airraidsound.prefab" 0 1 0
//
// Every probe auto-ends after its timeout and runs its cleanup list in reverse.
// Logs:
//   carbon/logs/TheatricalNukeProbe/session_<yyyyMMdd-HHmmss>.log   every probe in a session, one file
//   carbon/logs/TheatricalNukeProbe/probe-errors.log                 every caught exception (full stack)
//
// Test server only. R5 patches SupplySignal.Explode while armed.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using Carbon.Components;
using Carbon.Plugins;
using HarmonyLib;
using Oxide.Game.Rust.Cui;
using Oxide.Plugins;
using UnityEngine;

namespace Carbon.Plugins;

[Info("TheatricalNukeProbe", "Theatrical Nuke", "0.11.13")]
[Description("M0 probes R0-R10: one probe per command, start/end banners, per-run log file, cleanup.")]
public class TheatricalNukeProbe : CarbonPlugin
{
    private const string Version = "0.11.13";
    private const string LogDirectory = "carbon/logs/TheatricalNukeProbe";
    private const string HarmonyId = "theatricalnuke.probe";

    private const string MissilePrefab = "assets/content/structures/nuclear_missile_silo/nuclear_silo_missile.prefab";
    private const string BusPrefab = "assets/bundled/prefabs/modding/events/twitch/nuclear_warhead.prefab";
    // Folder name contains a SPACE ("air raid"), confirmed by the R4 run-2 result. Run 1 used an underscore and failed.
    private const string SirenPrefab = "assets/bundled/prefabs/modding/sound/air raid/airraidsound.prefab";
    private const string CctvPrefab = "assets/prefabs/deployable/cctvcamera/cctv.static.prefab";
    private const string MarkerPrefab = "assets/prefabs/tools/map/genericradiusmarker.prefab";
    private const string PlumePrefab = "assets/prefabs/npc/m2bradley/oilfireball2.prefab";
    // Bare networked entity (BaseEntity only, per Entities.md). Used as an invisible carrier in R1b.
    private const string CarrierPrefab = "assets/content/structures/rocket_crane/rocket_crane_lift_trigger.prefab";
    private const string ObsBoxName = "TNProbeObs";
    private const string ObsCmdText = "theatricalnuke.probe.obs.text";
    private const string ObsCmdSubmit = "theatricalnuke.probe.obs.submit";
    private const string ObsCmdClose = "theatricalnuke.probe.obs.close";
    private const string ObsCloseName = "TNProbeObsClose";
    private const string ObsInputName = "TNProbeObsInput";
    private const string ObsSubmitName = "TNProbeObsSubmit";
    private const string ObsAddName = "TNProbeObsAdd";
    private const string ObsCmdAdd = "theatricalnuke.probe.obs.add";
    private int _partCount;
    private const int MaxObservationWords = 1000;
    private const int MaxObservationChars = 6000;
    private const float TickSeconds = 5f;

    private static readonly string[] KnownIds = { "R0", "R1", "R2", "R3", "R4", "R5", "R6", "R6b", "R6c", "R7", "R8", "R9", "R10", "R1b", "R1c", "R1e", "R1f", "R1g", "R1h", "R1i", "R1j", "R1k", "R1m", "R1n", "R1o", "R1p", "R1q", "R1r", "R1s", "R1t", "BOXTEST", "R1u" };
    private static readonly float[] MarkerRadii = { 0.01f, 0.025f, 0.05f, 0.1f, 0.25f };

    private static TheatricalNukeProbe _instance;
    private static bool _r5Armed;
    private static float _r5SmokeSeconds = 15f;

    private ProbeRun _active;
    private Harmony _harmony;

    // Session and observation state.
    private SessionLog _session;
    // FIFO of probes that ended without an observation. The box always works on the head.
    private readonly List<ProbeRun> _pendingObs = new List<ProbeRun>();
    private bool _boxOpen;
    private ulong _boxOwner;
    private string _boxText = string.Empty;
    private bool _submitPending;
    private bool _closeSessionAfterCollect;
    private readonly List<string> _sessionSummary = new List<string>();

    // ── lifecycle ────────────────────────────────────────────────────────────────────────────────
    private void Loaded()
    {
        _instance = this;
        Puts($"TheatricalNukeProbe v{Version} loaded. Start with /probe start, then /probe <ID>, /probe <ID> end, /probe end.");
    }

    private void Unload()
    {
        try
        {
            if (_active != null) EndProbe(_active, "aborted: plugin unloading", null, null, false);
            if (_boxOpen)
            {
                foreach (var player in BasePlayer.activePlayerList.ToList())
                {
                    if (player.userID == _boxOwner) CuiHelper.DestroyUi(player, ObsBoxName);
                }
            }
            if (_session != null) CloseSession("plugin unloading", null);
        }
        catch (Exception ex)
        {
            ErrorLog(null, "Unload", ex);
        }
        finally
        {
            _r5Armed = false;
            try { _harmony?.UnpatchAll(HarmonyId); }
            catch (Exception ex) { ErrorLog(null, "Unload unpatch", ex); }
            _harmony = null;
            _instance = null;
        }
    }

    // ── command surface ──────────────────────────────────────────────────────────────────────────
    [ChatCommand("probe")]
    private void CmdProbe(BasePlayer player, string command, string[] args)
    {
        try
        {
            if (player == null)
            {
                Puts("[PROBE] Run /probe in-game as an admin.");
                return;
            }

            if (!player.IsAdmin)
            {
                player.ChatMessage("Admins only.");
                return;
            }

            var typed = args == null || args.Length == 0 ? "(none)" : string.Join(" | ", args);
            SessionLine("CMD", $"operator={player.userID} '{player.displayName}' typed: /probe {typed}");

            if (args == null || args.Length == 0 || args[0].Equals("help", StringComparison.OrdinalIgnoreCase))
            {
                Help(player);
                return;
            }

            var first = args[0];

            if (first.Equals("status", StringComparison.OrdinalIgnoreCase))
            {
                var session = _session == null ? "no session open (start with /probe start)" : $"session open: {_session.Path}";
                var running = _active == null ? "no probe running" : $"{_active.Id} running";
                var pending = _pendingObs.Count == 0
                    ? "no observations outstanding"
                    : $"{_pendingObs.Count} observation(s) outstanding, next is {_pendingObs[0].Id}";
                var box = _boxOpen ? "; observation box open" : string.Empty;
                player.ChatMessage($"{session}; {running}; {pending}{box}.");
                return;
            }

            if (first.Equals("start", StringComparison.OrdinalIgnoreCase))
            {
                StartSession(player);
                return;
            }

            if (first.Equals("end", StringComparison.OrdinalIgnoreCase))
            {
                if (args.Length > 1)
                {
                    player.ChatMessage("To end a probe: /probe <ID> end.  To end the session: /probe end (no other words).");
                    return;
                }

                EndSession(player);
                return;
            }

            if (first.Equals("obs", StringComparison.OrdinalIgnoreCase))
            {
                // Chat fallback for the observation box: /probe obs <text>. Collects the next outstanding observation.
                var text = string.Join(" ", args.Skip(1));
                if (IsBlank(text))
                {
                    player.ChatMessage("Usage: /probe obs <your observation>. Collects the next outstanding observation.");
                    return;
                }

                CollectHead(player, text, "chat command");
                return;
            }

            if (first.Equals("outstanding", StringComparison.OrdinalIgnoreCase) || first.Equals("oustanding", StringComparison.OrdinalIgnoreCase))
            {
                // Reopens the observation boxes for every outstanding observation, in sequence. The session stays open.
                if (args.Length > 1)
                {
                    player.ChatMessage("Usage: /probe outstanding (no other words).");
                    return;
                }

                if (_pendingObs.Count == 0)
                {
                    player.ChatMessage("No observation is outstanding.");
                    return;
                }

                if (_active != null)
                {
                    player.ChatMessage($"{_active.Id} is still running. End it with /probe {_active.Id} end, then run /probe outstanding.");
                    return;
                }

                if (_boxOpen)
                {
                    player.ChatMessage($"The box is already open for {_pendingObs[0].Id}. Submit it to move to the next one.");
                    return;
                }

                SessionLine("COLLECT", $"/probe outstanding: {_pendingObs.Count} observation(s), starting with {_pendingObs[0].Id}");
                player.ChatMessage($"{_pendingObs.Count} observation(s) outstanding. Opening {_pendingObs[0].Id} first; each submit opens the next.");
                OpenNextObservationBox(player);
                return;
            }

            if (first.Equals("more", StringComparison.OrdinalIgnoreCase))
            {
                // Appends to the last collected observation, for text the box cut off. Logged in full as OBSERVE-MORE.
                var more = string.Join(" ", args.Skip(1));
                if (IsBlank(more)) { player.ChatMessage("Usage: /probe more <rest of the observation>"); return; }
                if (_lastCollectedRun == null) { player.ChatMessage("No observation has been collected yet. Use /probe obs <text> for the first part."); return; }
                Line(_lastCollectedRun, "OBSERVE-MORE", EscapeText(more));
                SessionLine("COLLECT", $"{_lastCollectedRun.Id} more text appended: characters={more.Length} text=\"{EscapeText(more)}\"");
                player.ChatMessage($"Appended {more.Length} characters to {_lastCollectedRun.Id}.");
                return;
            }

            if (first.Equals("abort", StringComparison.OrdinalIgnoreCase))
            {
                if (_active == null) player.ChatMessage("No probe running.");
                else EndProbe(_active, "aborted by operator", null, player, false);
                return;
            }

            var id = NormaliseId(first);
            if (id == null)
            {
                player.ChatMessage($"Unknown probe '{first}'. Known: {string.Join(" ", KnownIds)}");
                return;
            }

            var rest = args.Skip(1).ToArray();

            if (rest.Length > 0 && rest[0].Equals("end", StringComparison.OrdinalIgnoreCase))
            {
                if (_active == null || !_active.Id.Equals(id, StringComparison.Ordinal))
                {
                    player.ChatMessage($"{id} is not running.");
                    return;
                }

                var chatObservation = string.Join(" ", rest.Skip(1));

                if (chatObservation.Length == 0)
                {
                    // No words after end: queue the observation and open the box to collect it.
                    EndProbe(_active, "ended by operator", null, player, true);
                }
                else
                {
                    EndProbe(_active, "ended by operator (observation typed in chat)", chatObservation, player, false);
                }

                return;
            }

            if (_session == null)
            {
                player.ChatMessage("No session is open. Start one with /probe start first.");
                return;
            }

            if (_pendingObs.Count > 0)
            {
                player.ChatMessage($"{_pendingObs.Count} observation(s) outstanding, next {_pendingObs[0].Id}. Use /probe end to collect them before starting a probe.");
                return;
            }

            if (_active != null)
            {
                player.ChatMessage($"{_active.Id} is still running. End it with /probe {_active.Id} end, or /probe abort.");
                return;
            }

            StartProbe(id, rest, player);
        }
        catch (Exception ex)
        {
            ErrorLog(_active, "CmdProbe", ex);
            if (player != null) player.ChatMessage("Probe error. See carbon/logs/TheatricalNukeProbe/probe-errors.log");
        }
    }

    private void Help(BasePlayer player)
    {
        player.ChatMessage("Session: /probe start (open the log)   /probe end (collect outstanding observations, then close)   /probe status");
        player.ChatMessage("Probe: /probe <ID> [args]   End: /probe <ID> end (queues the observation and opens the box)");
        player.ChatMessage("Observation without the box: /probe obs <your text>");
        player.ChatMessage("/probe outstanding: reopens every outstanding observation box in sequence (also /probe oustanding).");
        player.ChatMessage("Probes: R0 R1 R1b R1c R1e R1f R1g R1h R1i R1j R1k R1m R1n R1o R1p R1q R1r R1s R1t R2 R3 R4 R5 R6 R6b R6c R7 R8 R9 R10");
        player.ChatMessage("R4 <path> [x y z]   R5 [smoke seconds]   R6 [metres]   R6b [path] [x y z]   R6c [path]");
        player.ChatMessage("R7 [radius ...] (default 0.01 0.025 0.05 0.1 0.25)   R7 move [radius] [m/s] (moving marker)");
        player.ChatMessage("R9 [spacing] [static|follow]   R1b (carrier test)   R1c (manifest search)   R1e (child toggle on the warhead bus)   R1f (silo parent test)");
        player.ChatMessage("Silo avenues: R1g / R1h need 'go' (effect route, no cleanup)   R1i scene instances   R1j server prefab audit   R1k networked child scan   R1m BaseEntity injection   R1n ClientEntity (killable, moves)   R1o effect parented to a bus (killable)   R1p Harmony discovery (read-only)   R1q Harmony prefab-id swap on one bus (approved, server-side)   R1r prefab id before Spawn, no Harmony   R1s wider Harmony write-path interception   R1t silo prefab via CreateEntity   BOXTEST observation box capture test   R1u final in-situ inspection and every spawn method");
        player.ChatMessage("Also: /probe abort");
    }

    private static string NormaliseId(string input)
    {
        foreach (var known in KnownIds)
        {
            if (known.Equals(input, StringComparison.OrdinalIgnoreCase)) return known;
        }

        return null;
    }

    // ── session ──────────────────────────────────────────────────────────────────────────────────
    private void StartSession(BasePlayer player)
    {
        if (_session != null)
        {
            player.ChatMessage($"A session is already open: {_session.Path}");
            return;
        }

        var started = DateTime.UtcNow;
        _session = new SessionLog
        {
            Path = Path.Combine(LogDirectory, $"session_{started:yyyyMMdd-HHmmss}.log"),
            StartUtc = started,
            OperatorId = player.userID,
        };
        _sessionSummary.Clear();
        ResetBox();

        SessionLine("BANNER", "########## PROBE SESSION START ##########");
        SessionLine("INFO", $"plugin=TheatricalNukeProbe v{Version}");
        SessionLine("INFO", $"utc={started:o} local={LocalNow(started)}");
        SessionLine("INFO", $"operator={player.userID} '{player.displayName}' pos={Pos(player.transform.position)} yaw={player.transform.eulerAngles.y:0.0}");
        SessionLine("INFO", $"server hostname='{ConVar.Server.hostname}' level='{ConVar.Server.level}' worldsize={ConVar.Server.worldsize} m");
        SessionLine("INFO", $"players online={BasePlayer.activePlayerList.Count}");
        SessionLine("PROTOCOL", "one probe at a time; every probe writes into this file; /probe <ID> end queues the observation; /probe end collects outstanding observations, then closes the session.");

        player.ChatMessage($"Probe session started. Log: {_session.Path}");
    }

    // /probe end: with nothing outstanding, closes the session. Otherwise collects outstanding observations
    // one at a time (box for each, or /probe obs <text>) and closes the session when the last one is saved.
    private void EndSession(BasePlayer player)
    {
        if (_session == null)
        {
            player?.ChatMessage("No session is open.");
            return;
        }

        if (_active != null)
        {
            player?.ChatMessage($"{_active.Id} is still running. End it with /probe {_active.Id} end, or /probe abort.");
            return;
        }

        if (_pendingObs.Count == 0)
        {
            CloseSession("operator /probe end", player);
            return;
        }

        _closeSessionAfterCollect = true;
        SessionLine("COLLECT", $"/probe end with {_pendingObs.Count} observation(s) outstanding; collecting in sequence, starting with {_pendingObs[0].Id}");

        if (_boxOpen)
        {
            player?.ChatMessage($"{_pendingObs.Count} observation(s) outstanding. The box is open for {_pendingObs[0].Id}. Submit it, or close it with X and use /probe end again.");
            return;
        }

        player?.ChatMessage($"{_pendingObs.Count} observation(s) outstanding. Collecting now, starting with {_pendingObs[0].Id}.");
        OpenNextObservationBox(player);
    }

    private void CloseSession(string reason, BasePlayer player)
    {
        var closing = _session;
        if (closing == null) return;

        // Anything still outstanding is recorded as missing. Normally empty: /probe end only closes when nothing is outstanding.
        foreach (var run in _pendingObs.ToList())
        {
            Line(run, "MISSING", "no observation collected before the session closed");
            CompleteProbeLog(run);
        }

        _pendingObs.Clear();

        var duration = (DateTime.UtcNow - closing.StartUtc).TotalSeconds;
        SessionLine("INFO", $"reason={reason}; utc={DateTime.UtcNow:o}; duration={duration:0.0}s; probes run={closing.ProbesRun}");

        if (_sessionSummary.Count == 0) SessionLine("SUMMARY", "no probes run");
        foreach (var line in _sessionSummary) SessionLine("SUMMARY", line);

        SessionLine("BANNER", "########## PROBE SESSION END ##########");

        _session = null;
        _sessionSummary.Clear();
        ResetBox();
        _closeSessionAfterCollect = false;
        player?.ChatMessage($"Session closed. Log: {closing.Path}");
    }

    private static string LocalNow(DateTime utc)
    {
        try
        {
            var zone = FindUkZone();
            return TimeZoneInfo.ConvertTimeFromUtc(utc, zone).ToString("yyyy-MM-dd HH:mm:ss") + " (" + zone.Id + ")";
        }
        catch (Exception ex)
        {
            return "unavailable: " + ex.GetType().Name + ": " + ex.Message;
        }
    }

    private static TimeZoneInfo FindUkZone()
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById("Europe/London");
        }
        catch (Exception)
        {
            return TimeZoneInfo.FindSystemTimeZoneById("GMT Standard Time");
        }
    }

    // ── run lifecycle ────────────────────────────────────────────────────────────────────────────
    private void StartProbe(string id, string[] a, BasePlayer op)
    {
        var started = DateTime.UtcNow;
        var run = new ProbeRun
        {
            Id = id,
            OperatorId = op.userID,
            StartUtc = started,
            Args = a ?? new string[0],
            LogPath = _session.Path,
        };

        _active = run;
        _session.ProbesRun++;
        Banner(run, "START", $"v{Version} args=[{string.Join(" | ", run.Args)}] operator={op.userID} utc={started:o}");
        Line(run, "DEBUG", $"raw args: {run.Args.Length} item(s): '{string.Join("' '", run.Args)}'");
        SnapshotEnvironment(run, op, "start");

        try
        {
            switch (id)
            {
                case "R0": ProbeR0(run, op); break;
                case "R1": ProbeR1(run, op); break;
                case "R1b": ProbeR1b(run, op); break;
                case "R1c": ProbeR1c(run, op); break;
                case "R1e": ProbeR1e(run, op); break;
                case "R1g": ProbeR1g(run, op); break;
                case "R1n": ProbeR1n(run, op); break;
                case "R1p": ProbeR1p(run, op); break;
                case "R1q": ProbeR1q(run, op); break;
                case "R1r": ProbeR1r(run, op); break;
                case "R1s": ProbeR1s(run, op); break;
                case "R1t": ProbeR1t(run, op); break;
                case "BOXTEST": ProbeBoxTest(run, op); break;
                case "R1u": ProbeR1u(run, op); break;
                case "R1o": ProbeR1o(run, op); break;
                case "R1h": ProbeR1h(run, op); break;
                case "R1i": ProbeR1i(run, op); break;
                case "R1j": ProbeR1j(run, op); break;
                case "R1k": ProbeR1k(run, op); break;
                case "R1m": ProbeR1m(run, op); break;
                case "R1f": ProbeR1f(run, op); break;
                case "R2": ProbeR2(run, op); break;
                case "R3": ProbeR3(run, op); break;
                case "R4": ProbeR4(run, op); break;
                case "R5": ProbeR5(run, op); break;
                case "R6": ProbeR6(run, op); break;
                case "R6b": ProbeR6b(run, op); break;
                case "R6c": ProbeR6c(run, op); break;
                case "R7": ProbeR7(run, op); break;
                case "R8": ProbeR8(run, op); break;
                case "R9": ProbeR9(run, op); break;
                case "R10":
                    run.TimeoutSeconds = 5f;
                    Line(run, "NOTE", "R10 is not implemented yet. It needs a safe-zone test spec (Q17).");
                    break;
                default:
                    Line(run, "NOTE", "no handler for this id");
                    break;
            }
        }
        catch (Exception ex)
        {
            ErrorLog(run, "probe body", ex);
            Line(run, "ERROR", "probe body threw; see probe-errors.log. Cleanup still runs when you end it.");
        }

        var timeout = run.TimeoutSeconds;
        timer.Once(timeout, () =>
        {
            if (!run.Ended) EndProbe(run, $"auto-ended after {timeout:0} s timeout", null, null, false);
        });

        ScheduleTick(run, op);

        op.ChatMessage($"{id} running. Session log: {run.LogPath}. End with: /probe {id} end");
    }

    // Ends a probe: runs cleanup. With an observation it completes now. Without one it is queued,
    // and /probe end (or the box opened here when openBox is true) collects it.
    private void EndProbe(ProbeRun run, string outcome, string observation, BasePlayer op, bool openBox)
    {
        if (run == null || run.Ended) return;
        run.Ended = true;
        run.Outcome = outcome;
        if (ReferenceEquals(_active, run)) _active = null;

        try
        {
            if (!IsBlank(observation))
            {
                run.Observed = true;
                Line(run, "OBSERVE", observation);
            }

            SnapshotEnvironment(run, op, "end");
            run.CleanupStatus = RunCleanup(run);

            if (run.Observed)
            {
                CompleteProbeLog(run);
                op?.ChatMessage($"{run.Id} ended. Cleanup: {run.CleanupStatus}. Observation saved.");
                return;
            }

            _pendingObs.Add(run);
            Banner(run, "END-PENDING", $"outcome={run.Outcome}; cleanup={run.CleanupStatus}; observation=PENDING; queue position {_pendingObs.Count}");
            Line(run, "PENDING", "no observation yet. Queued for collection by /probe end.");
            op?.ChatMessage($"{run.Id} ended. Cleanup: {run.CleanupStatus}. {_pendingObs.Count} observation(s) outstanding.");

            if (openBox && op != null) OpenNextObservationBox(op);
        }
        catch (Exception ex)
        {
            ErrorLog(run, "EndProbe", ex);
            if (!run.Completed && !run.Observed && !_pendingObs.Contains(run)) _pendingObs.Add(run);
        }
    }

    private void CompleteProbeLog(ProbeRun run)
    {
        if (run.Completed) return;
        run.Completed = true;

        var duration = (DateTime.UtcNow - run.StartUtc).TotalSeconds;
        var observed = run.Observed ? "yes" : "NO";
        Banner(run, "END", $"outcome={run.Outcome}; cleanup={run.CleanupStatus}; duration={duration:0.0}s; observation={observed}; results={run.Results.Count}; errors={run.ErrorCount}");
        _sessionSummary.Add($"{run.Id} outcome={run.Outcome} cleanup={run.CleanupStatus} duration={duration:0.0}s observation={observed} errors={run.ErrorCount}");
    }

    private string RunCleanup(ProbeRun run)
    {
        var failed = 0;
        Line(run, "DEBUG", $"cleanup starting: {run.Cleanup.Count} item(s), reverse order");

        for (var i = run.Cleanup.Count - 1; i >= 0; i--)
        {
            var item = run.Cleanup[i];
            Line(run, "DEBUG", $"cleanup item {run.Cleanup.Count - i}/{run.Cleanup.Count}: {item.Label}");

            try
            {
                var detail = item.Run();
                Line(run, "CLEANUP", $"OK   {item.Label}: {detail}");
            }
            catch (Exception ex)
            {
                failed++;
                Line(run, "CLEANUP", $"FAIL {item.Label}: {ex.GetType().Name}: {ex.Message}");
                ErrorLog(run, "cleanup " + item.Label, ex);
            }
        }

        run.Cleanup.Clear();
        return failed == 0 ? "OK" : $"{failed} FAILED";
    }

    private static void Track(ProbeRun run, BaseEntity entity, string label)
    {
        run.Tracked.Add(Tuple.Create(label, entity));
        run.Cleanup.Add(new CleanupItem
        {
            Label = "kill " + label,
            Run = () =>
            {
                if (entity == null) return "already null";
                if (entity.IsDestroyed) return "already destroyed";
                entity.Kill();
                return "killed; IsDestroyed=" + entity.IsDestroyed;
            },
        });
    }

    // ── verbose state: environment snapshots and a periodic tick ─────────────────────────────────
    private void SnapshotEnvironment(ProbeRun run, BasePlayer op, string phase)
    {
        Line(run, "ENV", $"{phase}: utc={DateTime.UtcNow:o} hostname='{ConVar.Server.hostname}' level='{ConVar.Server.level}' worldsize={ConVar.Server.worldsize} players={BasePlayer.activePlayerList.Count}");

        if (op != null && !op.IsDestroyed)
        {
            Line(run, "ENV", $"{phase}: operator id={op.userID} name='{op.displayName}' admin={op.IsAdmin} pos={Pos(op.transform.position)} yaw={op.transform.eulerAngles.y:0.0} pitch={op.transform.eulerAngles.x:0.0}");
        }
        else
        {
            Line(run, "ENV", $"{phase}: operator not available");
        }

        Line(run, "ENV", $"{phase}: tracked={run.Tracked.Count} cleanupItems={run.Cleanup.Count} r5Armed={_r5Armed} r5SmokeSeconds={_r5SmokeSeconds:0.0}");
    }

    private void ScheduleTick(ProbeRun run, BasePlayer op)
    {
        timer.Once(TickSeconds, () => Tick(run, op));
    }

    private void Tick(ProbeRun run, BasePlayer op)
    {
        try
        {
            if (run.Ended) return;
            run.TickCount++;

            var sb = new StringBuilder();
            sb.Append($"#{run.TickCount} t=+{(DateTime.UtcNow - run.StartUtc).TotalSeconds:0.0}s");

            if (op != null && !op.IsDestroyed)
            {
                sb.Append($" operator={Pos(op.transform.position)} yaw={op.transform.eulerAngles.y:0.0}");
            }
            else
            {
                sb.Append(" operator=unavailable");
            }

            foreach (var item in run.Tracked)
            {
                var entity = item.Item2;
                if (entity == null) sb.Append($" | {item.Item1}=null");
                else if (entity.IsDestroyed) sb.Append($" | {item.Item1}=destroyed");
                else sb.Append($" | {item.Item1}={Pos(entity.transform.position)} net={(entity.net != null ? entity.net.ID : 0u)}");
            }

            Line(run, "TICK", sb.ToString());
            ScheduleTick(run, op);
        }
        catch (Exception ex)
        {
            ErrorLog(run, "tick", ex);
        }
    }

    // ── observation queue and box (CUI) ──────────────────────────────────────────────────────────
    // Outstanding observations are collected strictly in order. The box shows the head of the queue.
    // Submit: the box text is saved for the head, then the next one opens. X closes the box and keeps the item queued.
    private void OpenNextObservationBox(BasePlayer op)
    {
        if (op == null || _pendingObs.Count == 0 || _boxOpen) return;

        var head = _pendingObs[0];
        _boxOpen = true;
        _boxOwner = op.userID;
        _boxText = string.Empty;
        _submitPending = false;
        _partCount = 0;

        CuiHelper.DestroyUi(op, ObsBoxName);
        SendObservationBox(op, head.Id, _pendingObs.Count, string.Empty);
        SessionLine("BOX", $"observation box opened for {head.Id} (operator {op.userID}); {_pendingObs.Count} outstanding; limit {MaxObservationWords} words / {MaxObservationChars} characters");
    }

    private void CloseBox(BasePlayer op, string reason)
    {
        var wasOpen = _boxOpen;
        ResetBox();
        if (op != null) CuiHelper.DestroyUi(op, ObsBoxName);
        if (wasOpen) SessionLine("BOX", $"observation box closed: {reason}");
    }

    private void ResetBox()
    {
        _boxOpen = false;
        _boxOwner = 0;
        _boxText = string.Empty;
        _submitPending = false;
    }

    // Saves one observation for the head of the queue, then opens the next box or closes the session.
    private void CollectHead(BasePlayer op, string text, string source)
    {
        if (_pendingObs.Count == 0)
        {
            op?.ChatMessage("No observation is outstanding.");
            return;
        }

        var run = _pendingObs[0];

        if (IsBlank(text))
        {
            op?.ChatMessage($"The observation for {run.Id} is empty. Type something, then Submit.");
            return;
        }

        var words = CountWords(text);
        if (words > MaxObservationWords)
        {
            SessionLine("BOX", $"observation for {run.Id} rejected: {words} words exceeds {MaxObservationWords}");
            op?.ChatMessage($"Observation is {words} words; the limit is {MaxObservationWords}. Shorten it and submit again.");
            return;
        }

        _pendingObs.RemoveAt(0);
        _lastCollectedRun = run;
        if (_boxOpen) CloseBox(op, "submitted");

        run.Observed = true;
        SessionLine("COLLECT", $"{run.Id} observation via {source}: words={words} characters={text.Length}");
        // Echo what was captured, so the operator can see a cut and send the rest with /probe more.
        op?.ChatMessage($"{run.Id} captured {words} words, {text.Length} characters. If the observation is incomplete, send the rest with /probe more <text>.");
        Line(run, "OBSERVE", EscapeText(text));
        CompleteProbeLog(run);

        if (_pendingObs.Count > 0)
        {
            op?.ChatMessage($"{run.Id} saved. Next: {_pendingObs[0].Id} ({_pendingObs.Count} outstanding).");
            OpenNextObservationBox(op);
        }
        else
        {
            op?.ChatMessage($"{run.Id} saved. All observations collected.");
            if (_closeSessionAfterCollect) CloseSession("all observations collected", op);
        }
    }

    private void TrySubmitBox(BasePlayer player)
    {
        if (!_submitPending || !_boxOpen || IsBlank(_boxText)) return;

        _submitPending = false;
        CollectHead(player, _boxText, "observation box");
    }

    // The input field sends its text when it loses focus. The Submit click can arrive first, so wait briefly for the text.
    private void AfterSubmitWait(BasePlayer player, ProbeRun head)
    {
        try
        {
            if (!_submitPending || !_boxOpen) return;
            if (_pendingObs.Count == 0 || !ReferenceEquals(_pendingObs[0], head)) return;

            if (IsBlank(_boxText))
            {
                _submitPending = false;
                SessionLine("BOX", "submit rejected: no text arrived from the input field within 0.6 s");
                player?.ChatMessage("No text has reached the server. Click in the box, type, then press Submit. If typing still fails, use /probe obs <text>.");
                return;
            }

            TrySubmitBox(player);
        }
        catch (Exception ex)
        {
            ErrorLog(null, "observation submit wait", ex);
        }
    }

    private static bool IsBlank(string text) => string.IsNullOrWhiteSpace(text);

    // Builds the observation box with Carbon's LUI (CarbonMod.MD 10.3). The input sends its value as a command on
    // submit (Rust.MD: command + " " + value). prefill is the text kept by earlier parts, so the operator continues from it.
    private void SendObservationBox(BasePlayer player, string probeId, int outstanding, string prefill)
    {
        using var cui = new CUI(CuiHandler);

        var title = "Observation for " + probeId + " (" + outstanding + " outstanding). Type. Add part keeps the text and reopens it; Submit saves all of it. X closes without saving.";
        var root = cui.v2.CreateParent(CUI.ClientPanels.Overlay, LuiPosition.Full, ObsBoxName);
        var overlay = cui.v2.CreatePanel(root, LuiPosition.Full, LuiOffset.None, "0 0 0 0.92");
        overlay.AddCursor();
        overlay.AddKeyboard();

        var box = cui.v2.CreatePanel(overlay, new LuiPosition(0.22f, 0.16f, 0.78f, 0.86f), LuiOffset.None, "0.05 0.05 0.05 0.98");
        cui.v2.CreateText(box, new LuiPosition(0.03f, 0.89f, 0.88f, 0.98f), LuiOffset.None, 15, "1 1 1 1", title, TextAnchor.UpperLeft);

        var close = cui.v2.CreateButton(box, new LuiPosition(0.92f, 0.90f, 0.98f, 0.98f), LuiOffset.None, ObsCmdClose, "0.6 0.1 0.1 1");
        cui.v2.CreateText(close, LuiPosition.None, LuiOffset.None, 16, "1 1 1 1", "X");

        var input = cui.v2.CreateInput(box, new LuiPosition(0.03f, 0.22f, 0.97f, 0.86f), LuiOffset.None, "1 1 1 1", prefill, 14, ObsCmdText,
            charLimit: MaxObservationChars, alignment: TextAnchor.UpperLeft);
        input.SetInputAutoFocus(true);
        input.SetInputKeyboard(true);

        var add = cui.v2.CreateButton(box, new LuiPosition(0.03f, 0.03f, 0.32f, 0.12f), LuiOffset.None, ObsCmdAdd, "0.2 0.35 0.65 1");
        cui.v2.CreateText(add, LuiPosition.None, LuiOffset.None, 14, "1 1 1 1", "Add part (keep typing)");

        var submit = cui.v2.CreateButton(box, new LuiPosition(0.35f, 0.03f, 0.65f, 0.12f), LuiOffset.None, ObsCmdSubmit, "0.15 0.55 0.2 1");
        cui.v2.CreateText(submit, LuiPosition.None, LuiOffset.None, 16, "1 1 1 1", "Submit observation");

        cui.v2.SendUi(player);
    }

    [ConsoleCommand(ObsCmdAdd)]
    private void ObservationAddCommand(ConsoleSystem.Arg arg)
    {
        try
        {
            var player = arg.Player();
            if (player == null) return;

            if (!_boxOpen || player.userID != _boxOwner)
            {
                player.ChatMessage("No observation box is open for you. Use /probe end.");
                return;
            }

            SessionLine("BOX", "add part pressed");
            // The field sends its text when focus leaves, so wait briefly for that text, as Submit does.
            timer.Once(0.6f, () => CommitPart(player));
        }
        catch (Exception ex)
        {
            ErrorLog(null, "observation add command", ex);
        }
    }

    private void CommitPart(BasePlayer player)
    {
        try
        {
            if (!_boxOpen || player == null || player.userID != _boxOwner || _pendingObs.Count == 0) return;

            var head = _pendingObs[0];
            var kept = _boxText ?? string.Empty;
            _partCount++;
            SessionLine("BOX", $"PART {_partCount} kept for {head.Id}: characters={kept.Length} lines={CountLines(kept)} text=\"{EscapeText(kept)}\"");

            CuiHelper.DestroyUi(player, ObsBoxName);
            SendObservationBox(player, head.Id, _pendingObs.Count, kept);
            _boxText = kept;
            player.ChatMessage($"Part {_partCount} kept ({kept.Length} characters). Keep typing, then Add part again or Submit.");
        }
        catch (Exception ex)
        {
            ErrorLog(null, "observation add part", ex);
        }
    }

    [ConsoleCommand(ObsCmdText)]
    private void ObservationTextCommand(ConsoleSystem.Arg arg)
    {
        try
        {
            var player = arg.Player();
            if (player == null || player.userID != _boxOwner) return;

            var text = JoinArgs(arg);
            if (!_boxOpen)
            {
                // Text that arrives after the box closed is kept in the session log, against the last observation.
                SessionLine("BOX", $"LATE text after submit: characters={text.Length} lines={CountLines(text)} text=\"{EscapeText(text)}\"");
                if (_lastCollectedRun != null) Line(_lastCollectedRun, "OBSERVE-LATE", EscapeText(text));
                return;
            }

            _boxText = text;
            // Every arrival is logged in full, so no text the operator typed is lost from the session file.
            SessionLine("BOX", $"input field sent text: words={CountWords(_boxText)} characters={_boxText.Length} lines={CountLines(_boxText)} text=\"{EscapeText(_boxText)}\"");

            if (_submitPending) TrySubmitBox(player);
        }
        catch (Exception ex)
        {
            ErrorLog(null, "observation text command", ex);
        }
    }

    [ConsoleCommand(ObsCmdSubmit)]
    private void ObservationSubmitCommand(ConsoleSystem.Arg arg)
    {
        try
        {
            var player = arg.Player();
            if (player == null) return;

            if (!_boxOpen || player.userID != _boxOwner)
            {
                player.ChatMessage("No observation box is open for you. Use /probe end.");
                return;
            }

            _submitPending = true;
            SessionLine("BOX", $"submit pressed: stored words={CountWords(_boxText)} characters={_boxText.Length}");

            if (IsBlank(_boxText))
            {
                var head = _pendingObs.Count > 0 ? _pendingObs[0] : null;
                timer.Once(0.6f, () => AfterSubmitWait(player, head));
                return;
            }

            TrySubmitBox(player);
        }
        catch (Exception ex)
        {
            ErrorLog(null, "observation submit command", ex);
        }
    }

    [ConsoleCommand(ObsCmdClose)]
    private void ObservationCloseCommand(ConsoleSystem.Arg arg)
    {
        try
        {
            var player = arg.Player();
            if (player == null || !_boxOpen || player.userID != _boxOwner) return;

            var head = _pendingObs.Count > 0 ? _pendingObs[0].Id : "the next probe";
            CloseBox(player, "closed without submitting");
            player.ChatMessage($"Closed without saving. {head} still needs an observation: /probe end collects it again, or use /probe obs <text>.");
        }
        catch (Exception ex)
        {
            ErrorLog(null, "observation close command", ex);
        }
    }

    private static string JoinArgs(ConsoleSystem.Arg arg)
    {
        var parts = new List<string>();
        for (var i = 0; i < 5000 && arg.HasArgs(i + 1); i++)
        {
            parts.Add(arg.GetString(i));
        }

        return string.Join(" ", parts);
    }

    private static int CountLines(string text)
    {
        return IsBlank(text) ? 0 : text.Split('\n').Length;
    }

    // Keeps every character visible in one log line: line breaks and carriage returns become escapes.
    private static string EscapeText(string text)
    {
        return (text ?? string.Empty).Replace("\r", "\\r").Replace("\n", "\\n");
    }

    private ProbeRun _lastCollectedRun;

    private static int CountWords(string text)
    {
        return IsBlank(text) ? 0 : text.Split((char[])null, StringSplitOptions.RemoveEmptyEntries).Length;
    }

    // ── logging: one session file receives every probe ───────────────────────────────────────────
    private void SessionLine(string tag, string message)
    {
        var text = $"[{DateTime.UtcNow:HH:mm:ss.fff}] [SESSION] {tag,-8} {message}";
        Puts(text);
        if (_session != null) Append(_session.Path, text);
    }

    private void Line(ProbeRun run, string tag, string message)
    {
        var text = $"[{DateTime.UtcNow:HH:mm:ss.fff}] [PROBE {run.Id}] {tag,-8} {message}";
        Puts(text);
        Append(run.LogPath, text);
        if (tag == "RESULT") run.Results.Add(message);
    }

    private void Banner(ProbeRun run, string phase, string detail)
    {
        var text = $"########## PROBE {run.Id} {phase} ########## {detail}";
        Puts(text);
        Append(run.LogPath, text);
    }

    private void ErrorLog(ProbeRun run, string where, Exception ex)
    {
        try
        {
            var text = $"[{DateTime.UtcNow:o}] [{run?.Id ?? "-"}] {where}: {ex}";
            Puts("[PROBE ERROR] " + text);
            Append(Path.Combine(LogDirectory, "probe-errors.log"), text);

            if (run != null) run.ErrorCount++;
            var target = run?.LogPath ?? _session?.Path;
            if (target != null)
            {
                // Every line carries the probe tag, so the error reads as part of that probe in the session file.
                var tag = run != null ? $"[PROBE {run.Id}]" : "[SESSION]";
                var stamp = DateTime.UtcNow.ToString("HH:mm:ss.fff");
                Append(target, $"[{stamp}] {tag} ERROR    {where}: {ex.GetType().Name}: {ex.Message}");
                foreach (var frame in (ex.ToString() ?? string.Empty).Split('\n'))
                {
                    if (frame.Trim().Length == 0) continue;
                    Append(target, $"[{stamp}] {tag} ERROR      {frame.TrimEnd()}");
                }
            }
        }
        catch
        {
            // Logging must never throw into the server loop.
        }
    }

    private static void Append(string path, string text)
    {
        try
        {
            Directory.CreateDirectory(LogDirectory);
            File.AppendAllText(path, text + Environment.NewLine, Encoding.UTF8);
        }
        catch (Exception ex)
        {
            Debug.LogWarning("[TheatricalNukeProbe] could not write " + path + ": " + ex.Message);
        }
    }

    // ── helpers ──────────────────────────────────────────────────────────────────────────────────
    private static float? ParseFloat(string[] a, int i)
    {
        if (a == null || i < 0 || i >= a.Length) return null;
        return float.TryParse(a[i], out var v) ? v : (float?)null;
    }

    private static Vector3? ParseVec(string[] a, int start)
    {
        if (a == null || a.Length < start + 3) return null;
        var x = ParseFloat(a, start);
        var y = ParseFloat(a, start + 1);
        var z = ParseFloat(a, start + 2);
        if (x == null || y == null || z == null) return null;
        return new Vector3(x.Value, y.Value, z.Value);
    }

    private static bool IsNumber(string s) => float.TryParse(s, out _);

    private static Vector3 InFront(BasePlayer p, float metres, float up = 0f) =>
        p.transform.position + p.transform.forward * metres + Vector3.up * up;

    // AudioSource lives in UnityEngine.AudioModule, which is not in Carbon's plugin reference set
    // (compile error CS1069 in run 3). We find AudioSource components by type name and read their
    // properties by reflection, so no extra assembly reference is needed.
    private static IEnumerable<Component> AudioComponents(GameObject go)
    {
        return go.GetComponentsInChildren<Component>(true)
            .Where(c => c != null && c.GetType().Name == "AudioSource");
    }

    private static string Reflect(object target, string property)
    {
        try
        {
            var p = target.GetType().GetProperty(property);
            return p == null ? "n/a" : Convert.ToString(p.GetValue(target, null));
        }
        catch (Exception ex)
        {
            return "err:" + ex.GetType().Name;
        }
    }

    private void DumpAudio(ProbeRun run, GameObject go, string label)
    {
        if (go == null)
        {
            Line(run, "AUDIO", $"{label}: no object to inspect");
            return;
        }

        var sources = AudioComponents(go).ToArray();
        Line(run, "AUDIO", $"{label}: {sources.Length} AudioSource component(s) on {go.name} (reflected)");

        foreach (var s in sources)
        {
            Line(run, "AUDIO",
                $"  on '{s.gameObject.name}' clip={Reflect(s, "clip")} playOnAwake={Reflect(s, "playOnAwake")} " +
                $"loop={Reflect(s, "loop")} volume={Reflect(s, "volume")} minDist={Reflect(s, "minDistance")} " +
                $"maxDist={Reflect(s, "maxDistance")} isPlaying={Reflect(s, "isPlaying")}");
        }
    }

    // ── R0: environment ──────────────────────────────────────────────────────────────────────────
    private void ProbeR0(ProbeRun run, BasePlayer p)
    {
        run.TimeoutSeconds = 600f;
        Line(run, "STEP", $"probe plugin v{Version}; server UTC {DateTime.UtcNow:o}");
        Line(run, "STEP", $"operator position {p.transform.position}");
        Line(run, "STEP", "Paste into the end observation: c.version output, Rust build banner, map name and seed, test or live.");
    }

    // ── R1: missile prefab ───────────────────────────────────────────────────────────────────────
    private void ProbeR1(ProbeRun run, BasePlayer p)
    {
        run.TimeoutSeconds = 120f;
        var go = GameManager.server.FindPrefab(MissilePrefab);
        if (go == null)
        {
            Line(run, "RESULT", "FindPrefab returned null — wrong path for this build");
            return;
        }

        Line(run, "STEP", $"FindPrefab OK: {go.name}");
        var names = go.GetComponents<Component>().Select(c => c == null ? "<missing>" : c.GetType().Name);
        Line(run, "STEP", "root components: " + string.Join(", ", names));
        Line(run, "STEP", "BaseEntity on root: " + (go.GetComponent<BaseEntity>() != null));

        BaseEntity e = null;
        try
        {
            e = GameManager.server.CreateEntity(MissilePrefab, InFront(p, 8f, 2f), Quaternion.identity);
        }
        catch (Exception ex)
        {
            Line(run, "STEP", $"CreateEntity threw {ex.GetType().Name}: {ex.Message}");
        }

        if (e == null)
        {
            Line(run, "RESULT", "CreateEntity returned null: missile prefab is not spawnable as an entity (matches run 1)");
            return;
        }

        Track(run, e, "missile entity");
        e.Spawn();
        Line(run, "RESULT", $"CreateEntity returned {e.GetType().Name}; spawned 8 m in front. Look at it and report.");
    }

    // ── R2: CCTV identifiers and parenting ───────────────────────────────────────────────────────
    // Run 3 showed the default identifier COBALT1 is shared by many monument cameras (32 on this server).
    // PoweredRemoteControlEntity.Spawn() replaces a "#ID" token with the entity's net id, so a template
    // "NUKE#ID" gives a unique identifier. Set before Spawn(). IsValidIdentifier: alphanumeric, <= 32 chars.
    private void ProbeR2(ProbeRun run, BasePlayer p)
    {
        run.TimeoutSeconds = 600f;

        var existing = UnityEngine.Object.FindObjectsOfType<CCTV_RC>();
        var ids = existing.Where(c => c != null).Select(c => ((IRemoteControllable)c).GetIdentifier()).ToList();
        var dups = ids.GroupBy(x => x).Where(g => g.Count() > 1).OrderByDescending(g => g.Count())
            .Select(g => $"{g.Key} x{g.Count()}").ToArray();
        Line(run, "STEP", $"survey: {ids.Count} CCTV_RC, {ids.Distinct().Count()} distinct identifiers; duplicated: " +
                          (dups.Length == 0 ? "none" : string.Join(", ", dups)));

        var spawnAt = InFront(p, 3f, 1.5f);
        var cctv = GameManager.server.CreateEntity(CctvPrefab, spawnAt,
            Quaternion.LookRotation((p.transform.position - spawnAt).normalized, Vector3.up)) as CCTV_RC;
        if (cctv == null)
        {
            Line(run, "RESULT", "CreateEntity returned null or not a CCTV_RC");
            return;
        }

        Line(run, "STEP", $"prefab identifier before spawn='{cctv.rcIdentifier}'; setting template 'NUKE#ID'");
        cctv.rcIdentifier = "NUKE#ID";
        cctv.Spawn();
        Track(run, cctv, "cctv");

        var own = ((IRemoteControllable)cctv).GetIdentifier();
        var occurrences = ids.Count(x => x == own) + 1;
        Line(run, "RESULT", $"own identifier={own}; valid={ComputerStation.IsValidIdentifier(own)}; " +
                            $"occurrences including this camera={occurrences}");

        cctv.SetParent(p, worldPositionStays: true);
        Line(run, "STEP", "parented to operator (worldPositionStays: true)");
        Line(run, "STEP", $"NEXT: at a computer station add '{own}' as a bookmark. Walk, jump, turn. Report the feed and which camera it shows.");
    }

    // ── R3: debug drawing ────────────────────────────────────────────────────────────────────────
    private void ProbeR3(ProbeRun run, BasePlayer p)
    {
        run.TimeoutSeconds = 60f;
        var origin = p.transform.position;

        p.SendConsoleCommand("ddraw.line", 30f, Color.red, origin, origin + Vector3.up * 5f);
        p.SendConsoleCommand("ddraw.sphere", 30f, Color.cyan, origin + p.transform.forward * 5f, 1f);
        p.SendConsoleCommand("ddraw.text", 30f, Color.white, origin + Vector3.up * 6f, "R3 PROBE");
        Line(run, "STEP", "drew line, sphere, text (30 s duration)");

        run.Cleanup.Add(new CleanupItem
        {
            Label = "ddraw.clear",
            Run = () =>
            {
                p.SendConsoleCommand("ddraw.clear");
                return "sent";
            },
        });

        timer.Once(10f, () =>
        {
            if (run.Ended) return;
            p.SendConsoleCommand("ddraw.clear");
            Line(run, "STEP", "sent ddraw.clear at +10 s; report whether the lines vanished");
        });
    }

    // ── R4: generic broadcast effect ─────────────────────────────────────────────────────────────
    private void ProbeR4(ProbeRun run, BasePlayer p)
    {
        run.TimeoutSeconds = 30f;
        var path = run.Args.Length > 0 && !IsNumber(run.Args[0]) ? run.Args[0] : SirenPrefab;
        var offset = path == (run.Args.Length > 0 ? run.Args[0] : null) ? 1 : 0;
        var pos = ParseVec(run.Args, offset) ?? InFront(p, 2000f);
        var distance = Vector3.Distance(p.transform.position, pos);

        Line(run, "STEP", $"effect path='{path}' at {pos} ({distance:0} m from operator)");
        Effect.server.Run(path, pos, Vector3.up, null, true);
        Line(run, "RESULT", "submitted to Effect.server.Run (returns nothing). Check the console for 'unknown effect' within 2 s, then report what you heard.");
    }

    // ── R5: supply signal, keep smoke, suppress the cargo plane ─────────────────────────────────
    private void ProbeR5(ProbeRun run, BasePlayer p)
    {
        run.TimeoutSeconds = 300f;
        _r5SmokeSeconds = Math.Max(1f, ParseFloat(run.Args, 0) ?? 15f);
        Line(run, "STEP", $"smoke will end {_r5SmokeSeconds:0} s after the throw (vanilla is 210 s; configured value per Q3 is 15 s)");

        _harmony = new Harmony(HarmonyId);
        _harmony.PatchAll(GetType().Assembly);
        _r5Armed = true;

        run.Cleanup.Add(new CleanupItem { Label = "disarm R5 prefix", Run = () => { _r5Armed = false; return "disarmed"; } });
        run.Cleanup.Add(new CleanupItem
        {
            Label = "unpatch " + HarmonyId,
            Run = () =>
            {
                _harmony?.UnpatchAll(HarmonyId);
                _harmony = null;
                return "unpatched";
            },
        });

        Line(run, "STEP", "armed. Throw one Supply Signal. Expect: smoke at the landing point, NO cargo plane, signal stays until its 210 s FinishUp.");
        Line(run, "STEP", "Report: smoke yes/no, plane yes/no, signal lifetime. Each intercept is logged below as INTERCEPT.");
    }

    // Called from the prefix when R5 is armed.
    private void OnR5Intercept(SupplySignal signal)
    {
        var run = _active;
        if (run == null || !run.Id.Equals("R5", StringComparison.Ordinal)) return;
        Line(run, "INTERCEPT", $"SupplySignal at {signal.transform.position}; cargo plane suppressed; Flags.On set; FinishUp in {_r5SmokeSeconds:0} s");
    }

    // ── R6: siren through Effect.server.Run (run 1 and run 2 were negative) ─────────────────────
    private void ProbeR6(ProbeRun run, BasePlayer p)
    {
        run.TimeoutSeconds = 60f;
        var distance = ParseFloat(run.Args, 0) ?? 1f;
        var pos = InFront(p, distance);

        Line(run, "STEP", $"Effect.server.Run(SirenPrefab) at {distance:0.0} m, single pulse");
        Effect.server.Run(SirenPrefab, pos, Vector3.up, null, true);
        Line(run, "RESULT", "submitted. Check the console for 'unknown effect'. Report whether you heard it and at what distance.");
    }

    // ── R6b: siren as a spawned entity (the route the working siren plugin appears to use) ──────
    private void ProbeR6b(ProbeRun run, BasePlayer p)
    {
        run.TimeoutSeconds = 120f;

        var hasPath = run.Args.Length > 0 && !IsNumber(run.Args[0]);
        var path = hasPath ? run.Args[0] : SirenPrefab;
        var pos = ParseVec(run.Args, hasPath ? 1 : 0) ?? InFront(p, 1f, 1f);

        var go = GameManager.server.FindPrefab(path);
        if (go == null)
        {
            Line(run, "RESULT", $"FindPrefab null for '{path}'. Check the path and quoting.");
            return;
        }

        Line(run, "STEP", $"FindPrefab OK: {go.name} (path='{path}')");
        DumpAudio(run, go, "prefab");

        var entity = GameManager.server.CreateEntity(path, pos, Quaternion.identity);
        if (entity == null)
        {
            Line(run, "RESULT", "CreateEntity returned null: not an entity; this route fails for this asset.");
            return;
        }

        Track(run, entity, "siren entity");
        entity.Spawn();
        Line(run, "STEP", $"spawned {entity.GetType().Name} at {pos}; net id={(entity.net != null ? entity.net.ID : 0u)}");
        DumpAudio(run, entity.gameObject, "live");

        timer.Once(2f, () => AudioCheck(run, entity, "+2 s"));
        timer.Once(10f, () => AudioCheck(run, entity, "+10 s"));
        Line(run, "STEP", "NEXT: listen at 1 m, then walk out to 200, 800, 1900 m and report where it stops. /probe R6b end <observation>");
    }

    private void AudioCheck(ProbeRun run, BaseEntity entity, string when)
    {
        if (run.Ended) return;
        if (entity == null || entity.IsDestroyed)
        {
            Line(run, "AUDIO", $"{when}: entity is gone");
            return;
        }

        var playing = AudioComponents(entity.gameObject).Count(s => Reflect(s, "isPlaying") == "True");
        Line(run, "AUDIO", $"{when}: AudioSources currently playing = {playing}");
    }

    // ── R7: marker radius sweep (units and visibility) ───────────────────────────────────────────
    // ── R7 move: one marker of a chosen radius travels west to east across the whole map ────────
    // Usage: /probe R7 move [radius, default 0.1] [speed m/s, default 25]
    // The marker starts at the west edge (worldsize / 2 - 20 m) and moves along z = 0 to the east edge, one step per second.
    private void ProbeR7Move(ProbeRun run, BasePlayer p)
    {
        var radius = (run.Args.Length > 1 ? ParseFloat(run.Args, 1) : null) ?? 0.1f;
        var speed = (run.Args.Length > 2 ? ParseFloat(run.Args, 2) : null) ?? 25f;

        if (radius <= 0f || radius > 60f || speed <= 0f || speed > 200f)
        {
            Line(run, "RESULT", $"invalid radius={radius} or speed={speed}. Radius 0-60, speed 0-200 m/s.");
            return;
        }

        var size = (float)ConVar.Server.worldsize;
        var half = size / 2f - 20f;
        var y = p.transform.position.y;
        var start = new Vector3(-half, y, 0f);
        var end = new Vector3(half, y, 0f);
        var seconds = (end.x - start.x) / speed;
        run.TimeoutSeconds = seconds + 60f;

        var marker = GameManager.server.CreateEntity(MarkerPrefab, start, Quaternion.identity) as MapMarkerGenericRadius;
        if (marker == null)
        {
            Line(run, "RESULT", "CreateEntity returned null or not a MapMarkerGenericRadius.");
            return;
        }

        marker.color1 = new Color(1f, 0.20f, 0.85f, 1f);
        marker.color2 = new Color(1f, 0.20f, 0.85f, 1f);
        marker.alpha = 0.55f;
        marker.radius = radius;
        marker.Spawn();
        marker.SendUpdate();
        Track(run, marker, "moving marker");

        Line(run, "STEP", $"moving marker spawned at {Pos(start)} radius={radius} speed={speed} m/s");
        Line(run, "STEP", $"map worldsize={size} m; path x from {-half:0} to {half:0} along z=0; expected {seconds:0} s");
        ScheduleMove(run, marker, start, end, speed, 0f);
    }

    private void ScheduleMove(ProbeRun run, MapMarkerGenericRadius marker, Vector3 start, Vector3 end, float speed, float elapsed)
    {
        timer.Once(1f, () =>
        {
            try
            {
                if (run.Ended || marker == null || marker.IsDestroyed) return;

                var t = elapsed + 1f;
                var x = Mathf.Min(start.x + speed * t, end.x);
                var pos = new Vector3(x, start.y, start.z);
                marker.transform.position = pos;
                marker.SendNetworkUpdateImmediate();
                Line(run, "MOVE", $"t=+{t:0.0}s marker at {Pos(pos)}");

                if (x < end.x)
                {
                    ScheduleMove(run, marker, start, end, speed, t);
                }
                else
                {
                    Line(run, "RESULT", $"marker reached the east edge at t=+{t:0.0}s. Report: smooth or jerky, lag behind you, whether the whole map tinted, and whether it stayed visible throughout.");
                }
            }
            catch (Exception ex)
            {
                ErrorLog(run, "R7 move step", ex);
            }
        });
    }

    private void ProbeR7(ProbeRun run, BasePlayer p)
    {
        if (run.Args.Length > 0 && run.Args[0].Equals("move", StringComparison.OrdinalIgnoreCase))
        {
            ProbeR7Move(run, p);
            return;
        }

        var radii = ParseRadii(run);
        if (radii.Length == 0)
        {
            Line(run, "RESULT", "no valid radius given; nothing spawned");
            return;
        }
        run.TimeoutSeconds = 20f * radii.Length + 60f;

        var marker = GameManager.server.CreateEntity(MarkerPrefab, InFront(p, 5f), Quaternion.identity) as MapMarkerGenericRadius;
        if (marker == null)
        {
            Line(run, "RESULT", "CreateEntity returned null or not a MapMarkerGenericRadius");
            return;
        }

        marker.color1 = new Color(1f, 0.20f, 0.85f, 1f);
        marker.color2 = new Color(1f, 0.20f, 0.85f, 1f);
        marker.alpha = 0.55f;
        marker.radius = radii[0];
        marker.Spawn();
        marker.SendUpdate();
        Track(run, marker, "marker");
        Line(run, "STEP", $"marker spawned at {Pos(marker.transform.position)}; radius={radii[0]} sent. Check your map now.");
        Line(run, "STEP", $"sweep: {string.Join(", ", radii)} m, 20 s per step. Report circle size vs map grid cells.");

        ScheduleRadius(run, marker, radii, 1);
    }

    private void ScheduleRadius(ProbeRun run, MapMarkerGenericRadius marker, float[] radii, int index)
    {
        if (index >= radii.Length) return;

        timer.Once(20f, () =>
        {
            try
            {
                if (run.Ended || marker == null || marker.IsDestroyed) return;

                var r = radii[index];
                marker.radius = r;
                marker.SendUpdate();
                Line(run, "STEP", $"radius={r} sent (step {index + 1}/{radii.Length}). Report circle size vs map grid cells, and whether the whole map is tinted.");
                ScheduleRadius(run, marker, radii, index + 1);
            }
            catch (Exception ex)
            {
                ErrorLog(run, "R7 radius step", ex);
            }
        });
    }

    // Parses the radius list from args. Default is the run 4 sweep. Invalid values are logged and skipped.
    private float[] ParseRadii(ProbeRun run)
    {
        if (run.Args.Length == 0) return MarkerRadii;

        var list = new List<float>();
        foreach (var token in run.Args)
        {
            if (float.TryParse(token, out var value) && value > 0f && value <= 60f && list.Count < 10)
            {
                list.Add(value);
            }
            else
            {
                Puts($"[PROBE R7] ignored radius argument '{token}' (need a number from 0.01 to 60, at most 10 values)");
            }
        }

        return list.ToArray();
    }

    // ── R1c: manifest search (read-only, no spawns) ─────────────────────────────────────────────
    // Lists every bundled asset path that looks like the silo, missile or warhead. For networked entity
    // prefabs it logs the child hierarchy, so we can see whether an entity already carries a missile child.
    private void ProbeR1c(ProbeRun run, BasePlayer p)
    {
        run.TimeoutSeconds = 120f;
        var keywords = new[] { "nuclear", "silo", "missile", "warhead", "icbm", "nuke" };

        var manifest = GameManifest.Current;
        if (manifest == null)
        {
            Line(run, "RESULT", "GameManifest.Current is null. Cannot search.");
            return;
        }

        var entityPaths = new HashSet<string>(manifest.entities ?? new string[0], StringComparer.OrdinalIgnoreCase);
        var allPaths = GameManifest.pathToGuid.Keys.ToList();
        Line(run, "SCAN", $"manifest: {allPaths.Count} asset path(s), {entityPaths.Count} networked entity path(s)");
        Line(run, "SCAN", $"keywords: {string.Join(", ", keywords)}");

        var matches = allPaths
            .Where(path => keywords.Any(k => path.IndexOf(k, StringComparison.OrdinalIgnoreCase) >= 0))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToList();
        Line(run, "SCAN", $"matches: {matches.Count}");

        var networkedCount = 0;
        foreach (var path in matches)
        {
            var networked = entityPaths.Contains(path);
            Line(run, "MATCH", $"{(networked ? "ENTITY" : "asset ")} {path}");

            if (!networked) continue;

            networkedCount++;
            DumpPrefabHierarchy(run, path);
        }

        Line(run, "RESULT", $"{matches.Count} match(es), {networkedCount} networked entity prefab(s). " +
                            "Read the ENTITY lines and their child names. A child named like the missile on an entity is the lead to test.");
    }

    private void DumpPrefabHierarchy(ProbeRun run, string path)
    {
        try
        {
            var go = GameManager.server.FindPrefab(path);
            if (go == null)
            {
                Line(run, "DETAIL", $"  FindPrefab returned null for {path}");
                return;
            }

            var rootTypes = go.GetComponents<Component>().Where(c => c != null).Select(c => c.GetType().Name);
            Line(run, "DETAIL", $"  root '{go.name}' active={go.activeSelf} components=[{string.Join(", ", rootTypes)}]");

            var transforms = go.GetComponentsInChildren<Transform>(true);
            var shown = 0;
            foreach (var t in transforms)
            {
                if (t == null || t == go.transform) continue;

                if (shown >= 60)
                {
                    Line(run, "DETAIL", $"  ... {transforms.Length - 1 - shown} more child transform(s) not listed");
                    break;
                }

                var childTypes = t.GetComponents<Component>().Where(c => c != null).Select(c => c.GetType().Name);
                Line(run, "DETAIL", $"  child '{t.name}' active={t.gameObject.activeSelf} [{string.Join(", ", childTypes)}]");
                shown++;
            }
        }
        catch (Exception ex)
        {
            ErrorLog(run, "R1c hierarchy " + path, ex);
        }
    }

    // ── R1 avenues (run 7): every route we know that could put nuclear_silo_missile.prefab in front of a client ─
    // Avenue                              Probe     Server handle / cleanup
    // entity spawn                        R1        CreateEntity (null, run 1 and run 6)
    // server child under a carrier        R1b, R1f  yes, but the child is not networked (invisible)
    // effect route, one shot              R1g       NONE. Client object may persist. Needs "go".
    // effect route, stepped ascent        R1h       NONE. Client objects may persist. Needs "go".
    // scene instance already in world     R1i       read-only
    // server prefab hierarchy audit       R1j       read-only
    // networked entity with a missile child  R1k    read-only
    // BaseEntity injected into a copy     R1m       yes (Track)

    private static readonly string[] MissileChildWords = { "missile", "silo", "icbm", "warhead", "rocket" };

    // R1g: one Effect.server.Run of the silo missile at 14 m in front, ground level. Clients instantiate
    // any bundled prefab for an effect (run 6: the siren appeared on the client). Gated by "go" because
    // the effect has no server handle, so cleanup cannot remove a client object.
    private void ProbeR1g(ProbeRun run, BasePlayer p)
    {
        run.TimeoutSeconds = 60f;
        if (!HasGo(run)) return;

        var pos = InFront(p, 14f, 0f);
        Line(run, "STEP", $"Effect.server.Run('{MissilePrefab}') at {Pos(pos)} ({Dist(pos, p):0.0} m from operator), normal up, useRpc true");
        Effect.server.Run(MissilePrefab, pos, Vector3.up, null, true);
        Line(run, "NOTE", "Effect route has no server handle: no entity, no Kill(). A client object may persist after this probe. Cleanup cannot remove it; a server restart clears it.");
        Line(run, "RESULT", "Check the console for 'unknown effect' or 'EffectRecycle' lines within 2 s. Operator: stand 30 m away and look 14 m in front at about 0 m. " +
                            "Report: is the silo missile body visible? Its shape, orientation, colour, and whether it is the full silo missile. Walk around it and report any change.");
    }

    // R1h: the same effect route, six spawns 10 m apart up a vertical line, one second apart. Tests whether
    // repeated effect spawns read as a rising missile. Gated by "go". Each spawn is a permanent client object.
    private void ProbeR1h(ProbeRun run, BasePlayer p)
    {
        run.TimeoutSeconds = 60f;
        if (!HasGo(run)) return;

        var basePos = InFront(p, 14f, 0f);
        Line(run, "STEP", $"stepped ascent: 6 effect spawns of '{MissilePrefab}' from {Pos(basePos)} upward, 10 m apart, 1 s apart");
        Line(run, "NOTE", "Each spawn is a permanent client object with no server handle. Expect 6 persistent copies until a server restart.");

        for (var i = 0; i < 6; i++)
        {
            var step = i;
            timer.Once(step * 1f, () =>
            {
                try
                {
                    if (run.Ended) return;
                    var pos = basePos + Vector3.up * (step * 10f);
                    Effect.server.Run(MissilePrefab, pos, Vector3.up, null, true);
                    Line(run, "STEP", $"stepped ascent {step + 1}/6: effect submitted at {Pos(pos)} (height +{step * 10f:0} m)");
                }
                catch (Exception ex)
                {
                    ErrorLog(run, "R1h step " + step, ex);
                }
            });
        }

        Line(run, "RESULT", "Operator: look 14 m in front and watch for 8 s. Report: one missile, a column of copies, or nothing. Give the heights you can see.");
    }

    // R1i: searches every transform in the scene for 'missile'. Silo monuments are in the scene (R1f), so an
    // in-world missile would already be on clients. Read-only. Logs root, path, active state and component names.
    private void ProbeR1i(ProbeRun run, BasePlayer p)
    {
        run.TimeoutSeconds = 30f;
        var transforms = UnityEngine.Object.FindObjectsOfType<Transform>();
        var hits = new List<Transform>();
        foreach (var t in transforms)
        {
            if (t == null) continue;
            if (t.name != null && t.name.IndexOf("missile", StringComparison.OrdinalIgnoreCase) >= 0) hits.Add(t);
        }

        Line(run, "SCENE", $"scanned {transforms.Length} transform(s); names containing 'missile': {hits.Count}");
        foreach (var t in hits.OrderBy(h => Dist(h.position, p)))
        {
            var root = t.root != null ? t.root.name : "none";
            var comps = t.GetComponents<Component>().Where(c => c != null).Select(c => c.GetType().Name);
            Line(run, "SCENE", $"'{t.name}' root='{root}' path={PathUnder(t, t.root)} pos={Pos(t.position)} dist={Dist(t.position, p):0.0} m " +
                               $"activeSelf={t.gameObject.activeSelf} activeInHierarchy={t.gameObject.activeInHierarchy} components=[{string.Join(", ", comps)}]");
        }

        Line(run, "RESULT", $"{hits.Count} in-scene transform(s) named missile. Read the SCENE lines. If one is the silo missile and is visible, we can already see it. " +
                            "Report whether any visible missile is near the silo monuments.");
    }

    // R1j: hierarchy of the server copy of the silo missile prefab, with component names. Read-only.
    // Run 5 and R1f reported the server copy as a single transform with a MeshCollider. This confirms it.
    private void ProbeR1j(ProbeRun run, BasePlayer p)
    {
        run.TimeoutSeconds = 30f;
        var go = GameManager.server.FindPrefab(MissilePrefab);
        if (go == null)
        {
            Line(run, "RESULT", "FindPrefab returned null for the silo missile.");
            return;
        }

        var rootComps = go.GetComponents<Component>().Where(c => c != null).Select(c => c.GetType().Name);
        Line(run, "STEP", $"root '{go.name}' active={go.activeSelf} components=[{string.Join(", ", rootComps)}]");

        var transforms = go.GetComponentsInChildren<Transform>(true);
        Line(run, "STEP", $"transforms including root: {transforms.Length}; children: {transforms.Length - 1}");
        foreach (var t in transforms)
        {
            if (t == null || t == go.transform) continue;
            var comps = t.GetComponents<Component>().Where(c => c != null).Select(c => c.GetType().Name);
            Line(run, "DETAIL", $"child '{t.name}' path={PathUnder(t, go.transform)} active={t.gameObject.activeSelf} [{string.Join(", ", comps)}]");
        }

        var allComps = go.GetComponentsInChildren<Component>(true).Where(c => c != null).Select(c => c.GetType().Name).ToList();
        Line(run, "STEP", $"component types in whole hierarchy: {allComps.Count}; distinct: {string.Join(", ", allComps.Distinct())}");
        Line(run, "RESULT", transforms.Length <= 1
            ? "Server copy is a stub: root only. The client prefab's visual children are unknown to the server."
            : $"Server copy has {transforms.Length - 1} child transform(s). Read the DETAIL lines for the body mesh.");
    }

    // R1p: Harmony discovery for the network write path. Read-only, installs no patch.
    // Question: which server-side methods build the outgoing entity record (prefab id, position, snapshot)?
    // Output tells us what a Harmony patch could change. Server-side patches cannot change the client's prefab.
    private void ProbeR1p(ProbeRun run, BasePlayer p)
    {
        run.TimeoutSeconds = 30f;
        Line(run, "STEP", $"HarmonyLib type loaded: {(FindType("HarmonyLib.Harmony") != null ? "yes" : "no")}");

        var pool = FindType("StringPool");
        var get = pool?.GetMethod("Get", new[] { typeof(string) });
        if (get != null)
        {
            var id = get.Invoke(null, new object[] { MissilePrefab });
            Line(run, "STEP", $"StringPool id for '{MissilePrefab}' = {id}");
        }
        else
        {
            Line(run, "STEP", "StringPool.Get(string) not found by reflection.");
        }

        var flags = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
        var keys = new[] { "Save", "Spawn", "Snapshot", "Prefab", "Write", "Network", "Send", "Position", "Create", "Load" };
        foreach (var t in new[] { typeof(BaseNetworkable), typeof(BaseEntity) })
        {
            foreach (var f in t.GetFields(flags).Where(f => f.Name.IndexOf("refab", StringComparison.OrdinalIgnoreCase) >= 0))
                Line(run, "DETAIL", $"{t.Name} field {f.FieldType.Name} {f.Name}");

            foreach (var m in t.GetMethods(flags).Where(m => !m.IsSpecialName && keys.Any(k => m.Name.IndexOf(k, StringComparison.OrdinalIgnoreCase) >= 0)))
            {
                var args = string.Join(", ", m.GetParameters().Select(a => a.ParameterType.Name + " " + a.Name));
                Line(run, "DETAIL", $"{t.Name}.{m.Name}({args}) -> {m.ReturnType.Name}");
            }
        }

        Line(run, "RESULT", "Discovery only. No patch installed. Send the DETAIL lines for BaseNetworkable and BaseEntity so the Harmony target can be chosen.");
    }

    // ── R1q: Harmony swap of the outgoing prefab id on one carrier's snapshot (approved) ───────────
    // A networked nuclear warhead bus is created, then its prefab id is swapped to the silo missile's id
    // while it serializes its snapshot (Harmony prefix on BaseNetworkable.SendAsSnapshot). The postfix
    // restores the bus id straight away. Server-side only; the client's prefab files are unchanged.
    private const string R1qHarmonyId = HarmonyId + ".r1q";
    private static FieldInfo _r1qPrefabField;
    private static uint _r1qSiloId;
    private static BaseEntity _r1qCarrier;
    private static bool _r1qArmed;
    private static int _r1qSwaps, _r1qRestores, _r1qErrors;
    private Harmony _r1qHarmony;

    private void ProbeR1q(ProbeRun run, BasePlayer p)
    {
        run.TimeoutSeconds = 30f;
        _r1qSwaps = 0; _r1qRestores = 0; _r1qErrors = 0;
        _r1qArmed = false;
        _r1qCarrier = null;

        var pool = FindType("StringPool");
        var get = pool?.GetMethod("Get", new[] { typeof(string) });
        if (get == null) { Line(run, "RESULT", "StringPool.Get(string) not found by reflection. Not run."); return; }
        _r1qSiloId = (uint)get.Invoke(null, new object[] { MissilePrefab });

        _r1qPrefabField = typeof(BaseNetworkable).GetField("prefabID", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (_r1qPrefabField == null) { Line(run, "RESULT", "BaseNetworkable.prefabID field not found. Not run."); return; }

        var target = typeof(BaseNetworkable)
            .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
            .FirstOrDefault(m => m.Name == "SendAsSnapshot" && m.GetParameters().Length == 4);
        if (target == null) { Line(run, "RESULT", "BaseNetworkable.SendAsSnapshot (4 parameters) not found. Not run."); return; }

        var bus = GameManager.server.CreateEntity(BusPrefab, InFront(p, 8f, 2f), Quaternion.identity);
        if (bus == null) { Line(run, "RESULT", $"CreateEntity returned null for {BusPrefab}. Not run."); return; }

        var originalId = (uint)_r1qPrefabField.GetValue(bus);
        Line(run, "STEP", $"carrier created {bus.GetType().Name}; bus prefabID={originalId}; swap target silo prefabID={_r1qSiloId}");

        _r1qHarmony = new Harmony(R1qHarmonyId);
        _r1qHarmony.Patch(target,
            prefix: new HarmonyMethod(typeof(TheatricalNukeProbe).GetMethod(nameof(R1qPrefix), BindingFlags.NonPublic | BindingFlags.Static)),
            postfix: new HarmonyMethod(typeof(TheatricalNukeProbe).GetMethod(nameof(R1qPostfix), BindingFlags.NonPublic | BindingFlags.Static)));
        Line(run, "STEP", "Harmony prefix and postfix applied to BaseNetworkable.SendAsSnapshot");

        _r1qCarrier = bus;
        _r1qArmed = true;
        bus.Spawn();
        Track(run, bus, "r1q carrier bus");

        run.Cleanup.Add(new CleanupItem
        {
            Label = "R1q disarm and unpatch",
            Run = () =>
            {
                _r1qArmed = false;
                _r1qCarrier = null;
                try { _r1qHarmony?.UnpatchAll(R1qHarmonyId); } catch (Exception) { _r1qErrors++; }
                return $"unpatched; swaps={_r1qSwaps} restores={_r1qRestores} errors={_r1qErrors}";
            },
        });

        Line(run, "RESULT", "Operator: look 8 m in front at about 2 m. Report what appears where the bus was, its height, its shape, and whether it looks like the upright silo missile.");
    }

    private static void R1qPrefix(BaseNetworkable __instance, ref uint __state)
    {
        R1qSwap(__instance, ref __state);
    }

    private static void R1qSwap(BaseNetworkable __instance, ref uint __state)
    {
        __state = 0;
        try
        {
            if (!_r1qArmed || __instance == null || !ReferenceEquals(__instance, _r1qCarrier)) return;
            __state = (uint)_r1qPrefabField.GetValue(__instance);
            _r1qPrefabField.SetValue(__instance, _r1qSiloId);
            _r1qSwaps++;
        }
        catch (Exception) { _r1qErrors++; }
    }

    private static void R1qPostfix(BaseNetworkable __instance, uint __state)
    {
        try
        {
            if (__state == 0 || __instance == null) return;
            _r1qPrefabField.SetValue(__instance, __state);
            _r1qRestores++;
        }
        catch (Exception) { _r1qErrors++; }
    }

    // ── R1r: silo prefab id set on the bus BEFORE Spawn, no Harmony (approved) ──────────────────────
    // Tests whether the client draws its body from the id itself. Server-side only, no patch.
    private void ProbeR1r(ProbeRun run, BasePlayer p)
    {
        run.TimeoutSeconds = 30f;
        var silo = R1SiloId(run);
        var field = R1PrefabIdField();
        if (silo == 0 || field == null) return;

        var bus = GameManager.server.CreateEntity(BusPrefab, InFront(p, 8f, 2f), Quaternion.identity);
        if (bus == null) { Line(run, "RESULT", $"CreateEntity returned null for {BusPrefab}. Not run."); return; }

        var before = (uint)field.GetValue(bus);
        field.SetValue(bus, silo);
        Line(run, "STEP", $"carrier prefabID {before} -> {(uint)field.GetValue(bus)} (silo) before Spawn");
        Line(run, "STEP", $"InvalidateNetworkCache: {R1Invoke(bus, "InvalidateNetworkCache")}");

        bus.Spawn();
        Line(run, "STEP", $"SendNetworkUpdateImmediate: {R1Invoke(bus, "SendNetworkUpdateImmediate")}");
        Track(run, bus, "r1r bus with silo prefab id");
        Line(run, "RESULT", "Operator: look 8 m in front at about 2 m. Report what appears where the bus was, its height and shape, and whether it looks like the upright silo missile.");
    }

    // ── R1s: wider Harmony interception of the network write paths (approved) ───────────────────
    // Patches SendAsSnapshot and ToStreamForNetwork (swap while the carrier serializes) and
    // CanUseNetworkCache (logs whether the cache path is taken). Counts show which path the server used.
    private const string R1sHarmonyId = HarmonyId + ".r1s";
    private static int _r1sSnapshotCalls, _r1sStreamCalls, _r1sCacheTrue, _r1sCacheFalse;
    private Harmony _r1sHarmony;

    private void ProbeR1s(ProbeRun run, BasePlayer p)
    {
        run.TimeoutSeconds = 30f;
        _r1sSnapshotCalls = 0; _r1sStreamCalls = 0; _r1sCacheTrue = 0; _r1sCacheFalse = 0;
        _r1qSwaps = 0; _r1qRestores = 0; _r1qErrors = 0;

        var silo = R1SiloId(run);
        var field = R1PrefabIdField();
        if (silo == 0 || field == null) return;
        _r1qSiloId = silo;
        _r1qPrefabField = field;

        var snapshot = R1Method(typeof(BaseNetworkable), "SendAsSnapshot", 4);
        var stream = R1Method(typeof(BaseNetworkable), "ToStreamForNetwork", 2);
        var cache = R1Method(typeof(BaseEntity), "CanUseNetworkCache", 1);
        if (snapshot == null || stream == null || cache == null)
        {
            Line(run, "RESULT", $"Target missing: SendAsSnapshot={snapshot != null} ToStreamForNetwork={stream != null} CanUseNetworkCache={cache != null}. Not run.");
            return;
        }

        var bus = GameManager.server.CreateEntity(BusPrefab, InFront(p, 8f, 2f), Quaternion.identity);
        if (bus == null) { Line(run, "RESULT", $"CreateEntity returned null for {BusPrefab}. Not run."); return; }

        _r1sHarmony = new Harmony(R1sHarmonyId);
        _r1sHarmony.Patch(snapshot,
            prefix: R1Hm(nameof(R1sSnapshotPrefix)), postfix: R1Hm(nameof(R1qPostfix)));
        _r1sHarmony.Patch(stream,
            prefix: R1Hm(nameof(R1sStreamPrefix)), postfix: R1Hm(nameof(R1qPostfix)));
        _r1sHarmony.Patch(cache, postfix: R1Hm(nameof(R1sCachePostfix)));
        Line(run, "STEP", "Harmony patches applied: SendAsSnapshot, ToStreamForNetwork, CanUseNetworkCache");

        _r1qCarrier = bus;
        _r1qArmed = true;
        field.SetValue(bus, silo);
        Line(run, "STEP", $"InvalidateNetworkCache: {R1Invoke(bus, "InvalidateNetworkCache")}");
        bus.Spawn();
        Line(run, "STEP", $"SendNetworkUpdateImmediate: {R1Invoke(bus, "SendNetworkUpdateImmediate")}");
        Track(run, bus, "r1s carrier bus");

        run.Cleanup.Add(new CleanupItem
        {
            Label = "R1s disarm and unpatch",
            Run = () =>
            {
                _r1qArmed = false;
                _r1qCarrier = null;
                try { _r1sHarmony?.UnpatchAll(R1sHarmonyId); } catch (Exception) { _r1qErrors++; }
                return $"unpatched; snapshotCalls={_r1sSnapshotCalls} streamCalls={_r1sStreamCalls} cacheTrue={_r1sCacheTrue} cacheFalse={_r1sCacheFalse} swaps={_r1qSwaps} restores={_r1qRestores} errors={_r1qErrors}";
            },
        });

        Line(run, "RESULT", "Operator: look 8 m in front at about 2 m. Report what appears where the bus was, its height and shape, and whether it looks like the upright silo missile.");
    }

    private static void R1sSnapshotPrefix(BaseNetworkable __instance, ref uint __state)
    {
        if (ReferenceEquals(__instance, _r1qCarrier)) _r1sSnapshotCalls++;
        R1qSwap(__instance, ref __state);
    }

    private static void R1sStreamPrefix(BaseNetworkable __instance, ref uint __state)
    {
        if (ReferenceEquals(__instance, _r1qCarrier)) _r1sStreamCalls++;
        R1qSwap(__instance, ref __state);
    }

    private static void R1sCachePostfix(BaseNetworkable __instance, ref bool __result)
    {
        if (!ReferenceEquals(__instance, _r1qCarrier)) return;
        if (__result) _r1sCacheTrue++; else _r1sCacheFalse++;
    }

    // Shared helpers for R1r and R1s.
    private uint R1SiloId(ProbeRun run)
    {
        var pool = FindType("StringPool");
        var get = pool?.GetMethod("Get", new[] { typeof(string) });
        if (get == null) { Line(run, "RESULT", "StringPool.Get(string) not found by reflection. Not run."); return 0; }
        return (uint)get.Invoke(null, new object[] { MissilePrefab });
    }

    private static FieldInfo R1PrefabIdField()
    {
        return typeof(BaseNetworkable).GetField("prefabID", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
    }

    private static MethodInfo R1Method(Type type, string name, int parameterCount)
    {
        return type.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
            .FirstOrDefault(m => m.Name == name && m.GetParameters().Length == parameterCount);
    }

    private static HarmonyMethod R1Hm(string name)
    {
        return new HarmonyMethod(typeof(TheatricalNukeProbe).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static));
    }

    private static string R1Invoke(object target, string name)
    {
        try
        {
            var m = target.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (m == null || m.GetParameters().Length != 0) return "missing";
            m.Invoke(target, null);
            return "called";
        }
        catch (Exception ex) { return "error " + ex.GetType().Name; }
    }

    // ── R1t: the silo missile prefab created directly as a networked entity (no copy, no ID swap) ──
    // CreateEntity is the normal server path for a networked prefab. If it returns null, the silo prefab is
    // not a networked entity prefab and the route is closed. If it returns an entity, it is spawned for observation
    // and killed in cleanup.
    private void ProbeR1t(ProbeRun run, BasePlayer p)
    {
        run.TimeoutSeconds = 30f;
        var e = GameManager.server.CreateEntity(MissilePrefab, InFront(p, 8f, 2f), Quaternion.identity);
        if (e == null)
        {
            Line(run, "RESULT", "CreateEntity returned null for the silo missile prefab. The prefab is not a networked entity prefab. Route closed.");
            return;
        }

        Line(run, "STEP", $"CreateEntity returned {e.GetType().Name} prefabID={(R1PrefabIdField()?.GetValue(e))}");
        e.Spawn();
        Track(run, e, "r1t silo prefab entity");
        Line(run, "RESULT", "Operator: look 8 m in front at about 2 m. Report what appears, its height and shape, and whether it looks like the upright silo missile.");
    }

    // ── BOXTEST: observation box capture test (no game action, no cleanup) ───────────────────────
    // The operator types or pastes a numbered string into the box. The session log then shows exactly
    // where any cut happens, and the length of the text that reached the probe.
    private void ProbeBoxTest(ProbeRun run, BasePlayer p)
    {
        var test = string.Join(" ", Enumerable.Range(1, 40).Select(i => i.ToString("D4")));
        Line(run, "STEP", $"test string: 0001 to 0040, {test.Length} characters");
        Line(run, "RESULT", "Operator: type 0001 0002 ... 0040 into the observation box (or paste it), then Submit. The log will show where any cut happens. Expected length: " + test.Length + " characters.");

        // Ends the run at once and opens the box, so no /probe end is needed. Cleanup has nothing to undo (no game action).
        EndProbe(run, "box test: observation box opened for typed text", null, p, true);
    }

    // ── R1u: final attempt. Inspect the in-situ silo missile, list every spawn-like method, try each one. ──
    // Read-only inspection first. Then each method that takes (prefab path, position, rotation) is invoked once.
    // Anything it creates is tracked and killed in cleanup. Local Instantiate clones are destroyed at once.
    private void ProbeR1u(ProbeRun run, BasePlayer p)
    {
        run.TimeoutSeconds = 90f;
        var flags = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
        var gm = GameManager.server;

        // A. Discovery: spawn-like members on the types that can create networked or local objects.
        var types = new[] { typeof(GameManager), typeof(BaseEntity), typeof(BaseNetworkable), typeof(Effect), typeof(UnityEngine.Object), typeof(GameObject) };
        foreach (var t in types)
        {
            var names = t.GetMethods(flags)
                .Where(m => !m.IsSpecialName && (m.Name.StartsWith("Create") || m.Name.StartsWith("Spawn") || m.Name.StartsWith("Instantiate") || m.Name.Contains("Prefab") || m.Name.StartsWith("Run")))
                .Take(150);
            foreach (var m in names)
                Line(run, "DETAIL", $"{t.Name}.{m.Name}({string.Join(", ", m.GetParameters().Select(a => a.ParameterType.Name))}) {(m.IsStatic ? "static" : "instance")}");
        }

        // B. The in-situ silo missile, nearest to the monument position.
        var anchor = new Vector3(-2074.65f, 0.10f, 343.15f);
        Transform best = null;
        var bestDistance = float.MaxValue;
        foreach (var t in UnityEngine.Object.FindObjectsOfType<Transform>())
        {
            if (t == null || !t.name.Equals("nuclear_silo_missile", StringComparison.OrdinalIgnoreCase)) continue;
            var d = Vector3.Distance(t.position, anchor);
            if (d < bestDistance) { bestDistance = d; best = t; }
        }

        if (best == null)
        {
            Line(run, "RESULT", "No transform named nuclear_silo_missile found in the scene.");
        }
        else
        {
            Line(run, "STEP", $"in-situ silo missile found {bestDistance:0.00} m from the monument anchor");
            InspectTransform(run, best, "in-situ");
        }

        var root = gm?.FindPrefab(MissilePrefab);
        if (root == null) Line(run, "STEP", "FindPrefab(silo path) returned null");
        else InspectTransform(run, root.transform, "server prefab root");

        // C. Spawn attempts.
        var pos = InFront(p, 8f, 2f);
        if (gm == null) { Line(run, "RESULT", "GameManager.server is null. No spawn attempted."); return; }

        var creators = typeof(GameManager).GetMethods(flags)
            .Where(m => !m.IsSpecialName && (m.Name.StartsWith("Create") || m.Name.StartsWith("Spawn")))
            .Where(m => { var ps = m.GetParameters(); return ps.Length >= 3 && ps[0].ParameterType == typeof(string) && ps[1].ParameterType == typeof(Vector3) && ps[2].ParameterType == typeof(Quaternion); })
            .ToList();
        Line(run, "STEP", $"GameManager methods taking (string, Vector3, Quaternion): {creators.Count}");

        foreach (var m in creators)
        {
            try
            {
                var ps = m.GetParameters();
                var args = new object[ps.Length];
                args[0] = MissilePrefab;
                args[1] = pos;
                args[2] = Quaternion.identity;
                for (var i = 3; i < ps.Length; i++) args[i] = ps[i].HasDefaultValue ? ps[i].DefaultValue : null;

                var result = m.Invoke(gm, args);
                Line(run, "STEP", $"{m.Name} returned {(result == null ? "null" : result.GetType().Name)}");
                if (result is BaseEntity entity)
                {
                    entity.Spawn();
                    Track(run, entity, "r1u " + m.Name);
                    Line(run, "STEP", $"{m.Name}: entity spawned 8 m in front and tracked for cleanup");
                }
                else if (result is GameObject go)
                {
                    var comps = go.GetComponents<Component>().Where(c => c != null).Select(c => c.GetType().Name);
                    Line(run, "DETAIL", $"{m.Name}: GameObject '{go.name}' components=[{string.Join(", ", comps)}]; destroyed (local only)");
                    UnityEngine.Object.Destroy(go);
                }
            }
            catch (Exception ex)
            {
                ErrorLog(run, "R1u " + m.Name, ex);
            }
        }

        try
        {
            if (best != null)
            {
                var clone = UnityEngine.Object.Instantiate(best.gameObject);
                Line(run, "STEP", $"local Instantiate of the in-situ object: '{clone.name}' (local only, not networked); destroyed now");
                UnityEngine.Object.Destroy(clone);
            }
            if (root != null)
            {
                var clone2 = UnityEngine.Object.Instantiate(root);
                Line(run, "STEP", $"local Instantiate of the server prefab root: '{clone2.name}' (local only); destroyed now");
                UnityEngine.Object.Destroy(clone2);
            }
        }
        catch (Exception ex)
        {
            ErrorLog(run, "R1u local instantiate", ex);
        }

        Line(run, "STEP", "Effect route skipped: the client refuses the silo prefab as an effect (no EffectRecycle, client console 16:46:56).");
        Line(run, "RESULT", "Operator: look 8 m in front at about 2 m. Report anything that appeared, its height and shape, and whether it looks like the upright silo missile.");
    }

    // Logs every attribute of a transform and its children that could matter for spawning or drawing it.
    private void InspectTransform(ProbeRun run, Transform t, string label)
    {
        var names = new List<string>();
        for (var c = t; c != null; c = c.parent) names.Insert(0, c.name);
        Line(run, "STEP", $"{label}: path={string.Join("/", names)} activeInHierarchy={t.gameObject.activeInHierarchy} activeSelf={t.gameObject.activeSelf} layer={LayerMask.LayerToName(t.gameObject.layer)} tag={t.tag} isStatic={t.gameObject.isStatic}");
        Line(run, "STEP", $"{label}: world={t.position} local={t.localPosition} lossyScale={t.lossyScale} localScale={t.localScale}");
        var own = t.GetComponents<Component>().Where(c => c != null).Select(c => c.GetType().Name);
        Line(run, "STEP", $"{label}: own components=[{string.Join(", ", own)}]");
        Line(run, "STEP", $"{label}: descendant transforms={t.GetComponentsInChildren<Transform>(true).Length - 1}");

        foreach (var mf in t.GetComponentsInChildren<MeshFilter>(true))
            Line(run, "DETAIL", $"{label}: MeshFilter '{mf.name}' mesh={(mf.sharedMesh != null ? mf.sharedMesh.name + " vertices=" + mf.sharedMesh.vertexCount : "null")} activeInHierarchy={mf.gameObject.activeInHierarchy}");
        foreach (var r in t.GetComponentsInChildren<Renderer>(true))
            Line(run, "DETAIL", $"{label}: {r.GetType().Name} '{r.name}' enabled={r.enabled} material={(r.sharedMaterial != null ? r.sharedMaterial.name : "null")} shader={(r.sharedMaterial != null && r.sharedMaterial.shader != null ? r.sharedMaterial.shader.name : "null")}");
        foreach (var col in t.GetComponentsInChildren<Collider>(true))
            Line(run, "DETAIL", $"{label}: {col.GetType().Name} '{col.name}' enabled={col.enabled} isTrigger={col.isTrigger}");
        foreach (var n in t.GetComponentsInChildren<BaseNetworkable>(true))
            Line(run, "DETAIL", $"{label}: networked component {n.GetType().Name} on '{n.name}' prefabID={R1PrefabIdField()?.GetValue(n)}");
        foreach (var a in t.GetComponentsInChildren<Animator>(true))
            Line(run, "DETAIL", $"{label}: Animator on '{a.name}' enabled={a.enabled}");
        foreach (var ps in t.GetComponentsInChildren<ParticleSystem>(true))
            Line(run, "DETAIL", $"{label}: ParticleSystem on '{ps.name}' playing={ps.isPlaying}");
    }

    // R1k: every networked entity prefab (manifest.entities), scanned for a child whose name looks like a missile.
    // Read-only, no spawns. Any hit is a candidate carrier that already holds a missile-like child on clients.
    private void ProbeR1k(ProbeRun run, BasePlayer p)
    {
        run.TimeoutSeconds = 240f;
        var manifest = GameManifest.Current;
        if (manifest == null || manifest.entities == null)
        {
            Line(run, "RESULT", "GameManifest.Current or its entity list is null. Cannot scan.");
            return;
        }

        var entityPaths = manifest.entities.Where(s => !string.IsNullOrEmpty(s)).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(s => s, StringComparer.OrdinalIgnoreCase).ToList();
        Line(run, "SCAN", $"networked entity paths: {entityPaths.Count}; child words: {string.Join(", ", MissileChildWords)}");

        var scanned = 0;
        var withChildren = 0;
        var hits = 0;
        foreach (var path in entityPaths)
        {
            try
            {
                var go = GameManager.server.FindPrefab(path);
                scanned++;
                if (go == null) continue;

                var transforms = go.GetComponentsInChildren<Transform>(true);
                if (transforms.Length > 1) withChildren++;

                foreach (var t in transforms)
                {
                    if (t == null || t == go.transform) continue;
                    if (!MissileChildWords.Any(w => t.name.IndexOf(w, StringComparison.OrdinalIgnoreCase) >= 0)) continue;

                    hits++;
                    Line(run, "HIT", $"{path} :: child '{t.name}' path={PathUnder(t, go.transform)} active={t.gameObject.activeSelf}");
                }
            }
            catch (Exception ex)
            {
                ErrorLog(run, "R1k " + path, ex);
            }
        }

        Line(run, "RESULT", $"scanned {scanned} entity prefab(s); {withChildren} have children; {hits} missile-like child hit(s). " +
                            "Note: the server copy of the silo missile is a stub (R1j), so this scan sees only what the server prefab exposes.");
    }

    // R1m: component injection. A server copy of the silo missile gets a BaseEntity component added, then Spawn().
    // Not an API we have verified in Carbon or Rust source; this probe checks what the runtime does. Cleanup removes
    // the copy, and the entity is killed if it spawned.
    private void ProbeR1m(ProbeRun run, BasePlayer p)
    {
        run.TimeoutSeconds = 60f;
        // Run 7 (v0.11.1): Kill() on the injected entity threw NullReferenceException inside the game's
        // LookupPrefab, because AddComponent does not assign a prefab ID. The entity may remain on the server.
        // Gated behind "go" until the injection route is understood.
        if (!(run.Args.Length > 0 && run.Args[0].Equals("go", StringComparison.OrdinalIgnoreCase)))
        {
            Line(run, "RESULT", "Not run. Run 7 showed the injected entity cannot be killed (NullReferenceException in LookupPrefab) and may stay on the server. Run again with 'go': /probe R1m go");
            return;
        }

        var prefab = GameManager.server.FindPrefab(MissilePrefab);
        if (prefab == null)
        {
            Line(run, "RESULT", "FindPrefab returned null for the silo missile.");
            return;
        }

        var pos = InFront(p, 14f, 1f);
        GameObject copy = null;
        try
        {
            copy = UnityEngine.Object.Instantiate(prefab, pos, Quaternion.identity);
            Line(run, "STEP", $"instantiated copy '{copy.name}' at {Pos(pos)}");
        }
        catch (Exception ex)
        {
            ErrorLog(run, "R1m instantiate", ex);
            Line(run, "RESULT", $"Instantiate threw {ex.GetType().Name}: {ex.Message}");
            return;
        }

        var holder = copy;
        run.Cleanup.Add(new CleanupItem
        {
            Label = "destroy injected copy",
            Run = () =>
            {
                if (holder == null) return "already null";
                UnityEngine.Object.Destroy(holder);
                return "destroyed";
            },
        });

        try
        {
            var entity = copy.AddComponent<BaseEntity>();
            Line(run, "STEP", $"AddComponent<BaseEntity> returned {(entity != null ? "non-null" : "null")}");
            if (entity == null)
            {
                Line(run, "RESULT", "AddComponent gave null. Injection route is closed.");
                return;
            }

            try
            {
                entity.Spawn();
                // Deliberately not tracked: killing it threw in run 7. The server entity is left for a restart.
                Line(run, "NOTE", $"injected entity net id {(entity.net != null ? entity.net.ID : 0u)} is NOT killed by cleanup; expect it on the server until restart");
                Line(run, "STEP", $"Spawn() returned; IsDestroyed={entity.IsDestroyed} net={(entity.net != null ? entity.net.ID : 0u)}");
                Line(run, "RESULT", "Injected entity spawned. Operator: look 14 m in front at about 1 m. Report whether the silo missile body is visible.");
            }
            catch (Exception ex)
            {
                ErrorLog(run, "R1m spawn", ex);
                Line(run, "RESULT", $"Spawn threw {ex.GetType().Name}: {ex.Message}. Route closed unless the message suggests otherwise.");
            }
        }
        catch (Exception ex)
        {
            ErrorLog(run, "R1m AddComponent", ex);
            Line(run, "RESULT", $"AddComponent<BaseEntity> threw {ex.GetType().Name}: {ex.Message}");
        }
    }

    private bool HasGo(ProbeRun run)
    {
        if (run.Args.Length > 0 && run.Args[0].Equals("go", StringComparison.OrdinalIgnoreCase)) return true;
        Line(run, "RESULT", "Not run. This route leaves client objects with no server handle, so cleanup cannot remove them. " +
                            "Run it again with 'go': /probe " + run.Id + " go");
        return false;
    }

    // ── R1n: Carbon ClientEntity (client-only networked entity with a kill handle) ─────────────────
    // Source: Carbon.Common src/Carbon/Components/ClientEntity.cs (namespace Carbon.Components).
    // Create(prefabName, position, rotation, proto, netId, group) -> SpawnAll(IList<Connection>) -> Position +
    // SendNetworkUpdate_Position to move -> KillAll(mode) to remove -> Dispose().
    // Reached by reflection so the plugin compiles even if Carbon.Components is not in the reference set.
    // Phases: spawn now; rise 40 m over 10 s (0.5 s steps); kill at 20 s. Cleanup kills and disposes if still alive.
    private void ProbeR1n(ProbeRun run, BasePlayer p)
    {
        run.TimeoutSeconds = 40f;
        var type = FindType("Carbon.Components.ClientEntity");
        if (type == null)
        {
            Line(run, "RESULT", "Carbon.Components.ClientEntity not found in any loaded assembly. Route closed on this build.");
            return;
        }

        Line(run, "STEP", $"type {type.FullName} in assembly {type.Assembly.GetName().Name}");
        foreach (var m in type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
        {
            Line(run, "DEBUG", $"member {Sig(m)}");
        }

        var create = type.GetMethod("Create", BindingFlags.Public | BindingFlags.Static);
        if (create == null)
        {
            Line(run, "RESULT", "Create method not found by reflection. Route closed on this build.");
            return;
        }

        var pos = InFront(p, 14f, 0f);
        var args = BuildArgs(create, MissilePrefab, pos, Quaternion.identity);
        Line(run, "STEP", $"ClientEntity.Create('{MissilePrefab}', {Pos(pos)}) with {args.Length} argument(s)");

        object entity;
        try
        {
            entity = create.Invoke(null, args);
        }
        catch (Exception ex)
        {
            var inner = ex.InnerException ?? ex;
            ErrorLog(run, "R1n Create", inner);
            Line(run, "RESULT", $"Create threw {inner.GetType().Name}: {inner.Message}");
            return;
        }

        if (entity == null)
        {
            Line(run, "RESULT", "Create returned null. Carbon logs a warning when the prefab is not in its string pool. Check the console for 'ClientEntity creation failed'.");
            return;
        }

        var netId = type.GetProperty("NetID")?.GetValue(entity);
        var prefabId = type.GetProperty("Prefab")?.GetValue(entity);
        Line(run, "STEP", $"created ClientEntity netId={netId} prefabId={prefabId} (0 means the client gets no prefab)");

        var spawnAll = type.GetMethod("SpawnAll", BindingFlags.Public | BindingFlags.Instance);
        var killAll = type.GetMethod("KillAll", BindingFlags.Public | BindingFlags.Instance);
        var dispose = type.GetMethod("Dispose", BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null);
        var setPos = type.GetProperty("Position");
        var sendPos = type.GetMethod("SendNetworkUpdate_Position", BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null);

        if (spawnAll == null)
        {
            Line(run, "RESULT", "SpawnAll not found by reflection. Created entity is left for cleanup.");
        }
        else
        {
            try
            {
                var list = BuildConnectionList(spawnAll, p);
                spawnAll.Invoke(entity, new object[] { list });
                Line(run, "STEP", "SpawnAll sent to the operator's connection");
            }
            catch (Exception ex)
            {
                var inner = ex.InnerException ?? ex;
                ErrorLog(run, "R1n SpawnAll", inner);
                Line(run, "STEP", $"SpawnAll threw {inner.GetType().Name}: {inner.Message}");
            }
        }

        run.Cleanup.Add(new CleanupItem
        {
            Label = "R1n ClientEntity KillAll + Dispose",
            Run = () =>
            {
                var notes = new List<string>();
                if (killAll != null)
                {
                    killAll.Invoke(entity, new object[] { DefaultArg(killAll.GetParameters(), 0) });
                    notes.Add("KillAll");
                }

                if (dispose != null)
                {
                    dispose.Invoke(entity, null);
                    notes.Add("Dispose");
                }

                return notes.Count == 0 ? "no kill or dispose method found" : string.Join("+", notes);
            },
        });

        if (setPos != null && sendPos != null)
        {
            var baseline = pos;
            for (var i = 1; i <= 20; i++)
            {
                var step = i;
                timer.Once(step * 0.5f, () =>
                {
                    try
                    {
                        if (run.Ended) return;
                        var next = baseline + Vector3.up * (step * 2f);
                        setPos.SetValue(entity, next);
                        sendPos.Invoke(entity, null);
                        if (step % 4 == 0) Line(run, "STEP", $"R1n rise step {step}/20: Position={Pos(next)} (+{step * 2f:0} m)");
                    }
                    catch (Exception ex)
                    {
                        var inner = ex.InnerException ?? ex;
                        ErrorLog(run, "R1n move", inner);
                    }
                });
            }
        }
        else
        {
            Line(run, "NOTE", "Position setter or SendNetworkUpdate_Position not found; no movement phase.");
        }

        timer.Once(20f, () =>
        {
            try
            {
                if (run.Ended) return;
                Line(run, "STEP", "R1n kill phase at 20 s: KillAll (the operator should see the body disappear).");
                if (killAll != null) killAll.Invoke(entity, new object[] { DefaultArg(killAll.GetParameters(), 0) });
                Line(run, "RESULT", "Kill sent. Operator: report whether the silo missile appeared, moved up with the steps, and disappeared at about 20 s. Report what it looked like.");
            }
            catch (Exception ex)
            {
                var inner = ex.InnerException ?? ex;
                ErrorLog(run, "R1n kill", inner);
            }
        });

        Line(run, "RESULT", "Operator: look 14 m in front at about 1 m. Expect the silo missile to appear, rise about 40 m over 10 s, and vanish at 20 s. Report each.");
    }

    // R1o: Effect.server.Run with a BaseEntity parent (the 7-argument overload reported on the Oxide forum).
    // A networked warhead bus is spawned 14 m in front and the effect is attached to it. The bus is then killed at
    // 15 s. If the effect vanishes with it, the effect has a kill route. Reflection finds the overload, so no compile risk.
    private void ProbeR1o(ProbeRun run, BasePlayer p)
    {
        run.TimeoutSeconds = 40f;
        // Effect.server is a nested static type, not a value. It cannot be assigned to a variable, so reflect on the type.
        var serverType = typeof(Effect).GetNestedType("server", BindingFlags.Public | BindingFlags.NonPublic);
        if (serverType == null)
        {
            Line(run, "RESULT", "Effect has no nested type 'server' in this build. Route closed.");
            return;
        }

        Line(run, "STEP", $"Effect.server resolved to nested type {serverType.FullName}");
        var overloads = serverType.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance).Where(m => m.Name == "Run").ToList();
        foreach (var m in overloads) Line(run, "DEBUG", $"Effect.server overload {Sig(m)}");

        var target = overloads.FirstOrDefault(m =>
        {
            var ps = m.GetParameters();
            return ps.Length >= 2 && ps[0].ParameterType == typeof(string) && ps[1].ParameterType == typeof(BaseEntity);
        });

        if (target == null)
        {
            Line(run, "RESULT", "No Effect.server.Run overload takes (string, BaseEntity, ...) in this build. Route closed.");
            return;
        }

        var pos = InFront(p, 14f, 1f);
        var bus = GameManager.server.CreateEntity(BusPrefab, pos, Quaternion.identity);
        if (bus == null)
        {
            Line(run, "RESULT", $"CreateEntity returned null for {BusPrefab}. Probe not run.");
            return;
        }

        bus.Spawn();
        Track(run, bus, "parent bus");
        Line(run, "STEP", $"parent bus spawned at {Pos(pos)} (net id {(bus.net != null ? bus.net.ID : 0u)})");

        var ps2 = target.GetParameters();
        var args = new object[ps2.Length];
        for (var i = 0; i < ps2.Length; i++)
        {
            var t = ps2[i].ParameterType;
            if (i == 0) args[i] = MissilePrefab;
            else if (i == 1) args[i] = bus;
            else if (t == typeof(Vector3)) args[i] = i == 2 ? Vector3.zero : Vector3.up;
            else if (t == typeof(bool)) args[i] = true;
            else if (t == typeof(uint)) args[i] = 0u;
            else if (ps2[i].HasDefaultValue) args[i] = ps2[i].DefaultValue;
            else args[i] = null;
        }

        Line(run, "STEP", $"invoking {Sig(target)} with the silo missile path and the bus as parent");
        try
        {
            // Static method: target is null.
            target.Invoke(null, args);
            Line(run, "STEP", "Effect submitted with a parent");
        }
        catch (Exception ex)
        {
            var inner = ex.InnerException ?? ex;
            ErrorLog(run, "R1o invoke", inner);
            Line(run, "RESULT", $"Invoke threw {inner.GetType().Name}: {inner.Message}");
            return;
        }

        timer.Once(15f, () =>
        {
            try
            {
                if (run.Ended) return;
                if (bus == null || bus.IsDestroyed)
                {
                    Line(run, "NOTE", "bus already gone before the kill step");
                    return;
                }

                bus.Kill();
                Line(run, "STEP", $"R1o kill at 15 s: parent bus killed; IsDestroyed={bus.IsDestroyed}");
                Line(run, "RESULT", "Operator: did the silo missile vanish with the bus at 15 s? Report yes, no, or it stayed.");
            }
            catch (Exception ex)
            {
                ErrorLog(run, "R1o kill", ex);
            }
        });

        Line(run, "RESULT", "Operator: look 14 m in front at about 1 m. The silo missile should appear, then vanish when the parent bus is killed at 15 s.");
    }

    private static Type FindType(string fullName)
    {
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            try
            {
                var t = asm.GetType(fullName);
                if (t != null) return t;
            }
            catch (Exception)
            {
                // Some dynamic assemblies throw on GetType. Skip them.
            }
        }

        return null;
    }

    private static string Sig(MethodInfo m) =>
        $"{m.ReturnType.Name} {m.Name}({string.Join(", ", m.GetParameters().Select(x => x.ParameterType.Name + " " + x.Name + (x.HasDefaultValue ? " = default" : string.Empty)))})";

    // Fills a parameter list by position. The first three are supplied; the rest take their declared defaults.
    private static object[] BuildArgs(MethodInfo m, string path, Vector3 pos, Quaternion rot)
    {
        var ps = m.GetParameters();
        var args = new object[ps.Length];
        for (var i = 0; i < ps.Length; i++)
        {
            if (i == 0) args[i] = path;
            else if (i == 1) args[i] = pos;
            else if (i == 2) args[i] = rot;
            else args[i] = ps[i].HasDefaultValue ? ps[i].DefaultValue : null;
        }

        return args;
    }

    private static object DefaultArg(ParameterInfo[] ps, int index)
    {
        if (ps.Length <= index) return null;
        return ps[index].HasDefaultValue ? ps[index].DefaultValue : Enum.ToObject(ps[index].ParameterType, 0);
    }

    // Builds an IList<Connection> (the element type comes from the SpawnAll parameter) with the operator's connection.
    private static object BuildConnectionList(MethodInfo spawnAll, BasePlayer p)
    {
        var listType = spawnAll.GetParameters()[0].ParameterType;
        var elem = listType.IsGenericType ? listType.GetGenericArguments()[0] : typeof(object);
        var list = Activator.CreateInstance(typeof(List<>).MakeGenericType(elem));
        list.GetType().GetMethod("Add").Invoke(list, new object[] { p.net.connection });
        return list;
    }

    // ── R1f: in-world silo search, then a server-side silo copy under a different networked parent ─
    // Part 1 lists transforms in the scene named like the silo or missile (the monument, if present).
    // Part 2 spawns the warhead bus as a normal networked parent, puts a server-side copy of the silo
    // missile under it, and asks the operator to look. Expected: the copy is NOT visible (as in R1b).
    private void ProbeR1f(ProbeRun run, BasePlayer p)
    {
        run.TimeoutSeconds = 180f;

        var missile = GameManager.server.FindPrefab(MissilePrefab);
        if (missile == null)
        {
            Line(run, "RESULT", "FindPrefab returned null for the missile.");
            return;
        }

        var transforms = UnityEngine.Object.FindObjectsOfType<Transform>();
        var hits = 0;
        foreach (var t in transforms)
        {
            if (t == null) continue;
            var name = t.name ?? string.Empty;
            if (name.IndexOf("silo", StringComparison.OrdinalIgnoreCase) < 0 &&
                name.IndexOf("missile", StringComparison.OrdinalIgnoreCase) < 0) continue;

            hits++;
            if (hits <= 40)
            {
                var parentName = t.parent != null ? t.parent.name : "none";
                Line(run, "SCENE", $"'{name}' pos={Pos(t.position)} dist={Dist(t.position, p):0.0} m " +
                                   $"activeSelf={t.gameObject.activeSelf} activeInHierarchy={t.gameObject.activeInHierarchy} parent='{parentName}'");
            }
        }

        Line(run, "SCENE", $"scanned {transforms.Length} transform(s); matches={hits}" +
                           (hits > 40 ? " (first 40 listed)" : string.Empty));

        var busPos = InFront(p, 14f, 2f);
        var bus = GameManager.server.CreateEntity(BusPrefab, busPos, Quaternion.identity);
        if (bus == null)
        {
            Line(run, "RESULT", $"CreateEntity returned null for {BusPrefab}. Part 2 not run.");
            return;
        }

        bus.Spawn();
        Track(run, bus, "networked parent");
        Line(run, "STEP", $"networked parent {bus.GetType().Name} spawned at {Pos(busPos)} (net id {(bus.net != null ? bus.net.ID : 0u)})");
        AuditComponents(run, bus.gameObject, "parent live");

        GameObject visual = null;
        try
        {
            visual = UnityEngine.Object.Instantiate(missile, bus.transform);
            visual.transform.localPosition = Vector3.zero;
            visual.transform.localRotation = Quaternion.identity;
            Line(run, "STEP", $"server-side silo missile copy created under '{bus.transform.name}' world={Pos(visual.transform.position)}");
        }
        catch (Exception ex)
        {
            ErrorLog(run, "R1f instantiate", ex);
            Line(run, "STEP", $"Instantiate threw {ex.GetType().Name}: {ex.Message}");
        }

        if (visual != null)
        {
            var copy = visual;
            run.Cleanup.Add(new CleanupItem
            {
                Label = "destroy server-side silo missile copy",
                Run = () =>
                {
                    UnityEngine.Object.Destroy(copy);
                    return "destroyed";
                },
            });
            AuditVisuals(run, visual, "server-side copy");
        }

        Line(run, "RESULT", "Operator: stand 30 m away and look 14 m in front of you at about 1 m height, then report whether you see the missile body. " +
                            "Expected: NO. If SCENE listed an in-world silo missile, also go to its position and report whether it is visible there.");
    }

    // ── R1e: child toggle replication on the visible warhead bus ─────────────────────────────────
    // args: [child name, default nuclear_warhead]. R1f showed the operator MIRV parts on the warhead bus.
    // This probe spawns that bus 14 m in front and lists every child first. Then, on the server:
    //   t+10 s  the chosen child is SetActive(false) and the bus gets SendNetworkUpdateImmediate
    //   t+25 s  the child is SetActive(true) and the bus is updated again
    // At each step the operator reports whether the view changed. A child that stays visible after
    // SetActive(false) means the server-side toggle does not replicate to clients.
    private void ProbeR1e(ProbeRun run, BasePlayer p)
    {
        run.TimeoutSeconds = 45f;
        var wanted = run.Args.Length > 0 ? run.Args[0] : "nuclear_warhead";
        var pos = InFront(p, 14f, 1f);

        var bus = GameManager.server.CreateEntity(BusPrefab, pos, Quaternion.identity);
        if (bus == null)
        {
            Line(run, "RESULT", $"CreateEntity returned null for {BusPrefab}. Probe not run.");
            return;
        }

        bus.Spawn();
        Track(run, bus, "warhead bus");
        Line(run, "STEP", $"warhead bus spawned at {Pos(pos)} (net id {(bus.net != null ? bus.net.ID : 0u)}); operator dist={Dist(pos, p):0.0} m");

        var transforms = bus.GetComponentsInChildren<Transform>(true);
        Line(run, "STEP", $"bus hierarchy: {transforms.Length} transform(s) including the root");

        Transform target = null;
        Transform fallback = null;
        foreach (var t in transforms)
        {
            if (t == null || t == bus.transform) continue;

            var hasRenderer = HasComponentNamed(t, "Renderer");
            var hasCollider = HasComponentNamed(t, "Collider");
            Line(run, "CHILD", $"'{t.name}' path={PathUnder(t, bus.transform)} activeSelf={t.gameObject.activeSelf} " +
                               $"activeInHierarchy={t.gameObject.activeInHierarchy} renderer={hasRenderer} collider={hasCollider} " +
                               $"local={Pos(t.localPosition)}");

            if (target == null && t.name.Equals(wanted, StringComparison.OrdinalIgnoreCase)) target = t;
            if (fallback == null && hasCollider) fallback = t;
        }

        if (target == null)
        {
            target = fallback;
            Line(run, "NOTE", $"no child named '{wanted}'; falling back to the first child with a collider: '{(target != null ? target.name : "none")}'");
        }

        if (target == null)
        {
            Line(run, "RESULT", "No child to toggle. Probe stopped; the bus is cleaned up at end.");
            return;
        }

        var original = target.gameObject.activeSelf;
        var targetName = target.name;
        var targetPath = PathUnder(target, bus.transform);
        Line(run, "STEP", $"target '{targetName}' path={targetPath} activeSelf before={original} renderer={HasComponentNamed(target, "Renderer")}");

        // Cleanup order is reverse, so this runs before the bus is killed.
        run.Cleanup.Add(new CleanupItem
        {
            Label = "restore child active state",
            Run = () =>
            {
                if (target == null) return "target already null";
                target.gameObject.SetActive(original);
                return $"'{targetName}' activeSelf={target.gameObject.activeSelf}";
            },
        });

        timer.Once(10f, () => R1eStep(run, p, bus, target, false, "hide"));
        timer.Once(25f, () => R1eStep(run, p, bus, target, true, "show"));
        timer.Once(40f, () => R1eStep(run, p, bus, target, true, "final check"));

        Line(run, "RESULT", $"Phase 0 (now): look at the bus 14 m in front, about 1 m high. Report what you see. " +
                            $"At 10 s the child '{targetName}' is hidden on the server; at 25 s it is shown again.");
    }

    private void R1eStep(ProbeRun run, BasePlayer p, BaseEntity bus, Transform target, bool on, string phase)
    {
        try
        {
            if (run.Ended) return;

            if (bus == null || bus.IsDestroyed || target == null)
            {
                Line(run, "NOTE", $"R1e {phase}: bus or child is gone; nothing to do");
                return;
            }

            if (phase != "final check")
            {
                target.gameObject.SetActive(on);
                bus.SendNetworkUpdateImmediate();
            }

            Line(run, "STEP", $"R1e {phase}: '{target.name}' activeSelf={target.gameObject.activeSelf} " +
                              $"activeInHierarchy={target.gameObject.activeInHierarchy} world={Pos(target.position)} " +
                              $"bus={Pos(bus.transform.position)} net={(bus.net != null ? bus.net.ID : 0u)}");

            if (phase == "hide")
            {
                Line(run, "RESULT", $"Phase hide: the child '{target.name}' is now OFF on the server and the bus was updated. Report: did the MIRV view disappear, partly change, or stay the same?");
            }
            else if (phase == "show")
            {
                Line(run, "RESULT", $"Phase show: the child '{target.name}' is ON again. Report: did the MIRV view come back?");
            }

            try { p?.ChatMessage($"R1e {phase} step logged. Report what you see in the observation box at the end."); }
            catch (Exception ex) { ErrorLog(run, "R1e chat", ex); }
        }
        catch (Exception ex)
        {
            ErrorLog(run, "R1e " + phase, ex);
        }
    }

    private static bool HasComponentNamed(Transform t, string fragment)
    {
        foreach (var c in t.GetComponents<Component>())
        {
            if (c != null && c.GetType().Name.IndexOf(fragment, StringComparison.OrdinalIgnoreCase) >= 0) return true;
        }

        return false;
    }

    private static string PathUnder(Transform t, Transform root)
    {
        var parts = new List<string>();
        var cursor = t;
        var guard = 0;
        while (cursor != null && cursor != root && guard++ < 64)
        {
            parts.Insert(0, cursor.name);
            cursor = cursor.parent;
        }

        return string.Join("/", parts);
    }

    // ── R8: bus prefab visibility ────────────────────────────────────────────────────────────────
    private void ProbeR8(ProbeRun run, BasePlayer p)
    {
        run.TimeoutSeconds = 120f;
        var e = GameManager.server.CreateEntity(BusPrefab, InFront(p, 8f, 2f), Quaternion.identity);
        if (e == null)
        {
            Line(run, "RESULT", "CreateEntity returned null");
            return;
        }

        e.Spawn();
        Track(run, e, "bus");
        Line(run, "RESULT", $"spawned {e.GetType().Name} 8 m in front. Report whether it is visible.");
    }

    // ── R9: FireBall plumes ──────────────────────────────────────────────────────────────────────
    // args: [spacing metres, default 2] [static|follow, default static].
    // Centre is 5 m in front of the operator. static = never parented. follow = parented after spawn;
    // positions are logged before and after SetParent so we can see whether the offset survives.
    private void ProbeR9(ProbeRun run, BasePlayer p)
    {
        run.TimeoutSeconds = 300f;
        // Accept the words in any order: "/probe R9 follow", "/probe R9 2 follow" or "/probe R9 2 static".
        // Run 6 read the mode from the second argument only, so "/probe R9 follow" silently ran static.
        var follow = run.Args.Any(a => a.Equals("follow", StringComparison.OrdinalIgnoreCase));
        var spacing = run.Args.Select(a => float.TryParse(a, out var v) ? v : (float?)null).FirstOrDefault(v => v.HasValue) ?? 2f;
        var mode = follow ? "follow" : "static";
        var centre = InFront(p, 5f, 1f);

        Line(run, "STEP", $"mode={(follow ? "follow (parent after spawn)" : "static (not parented)")} spacing={spacing:0.0} m centre={Pos(centre)}");
        Line(run, "STEP", $"operator at {Pos(p.transform.position)}");

        for (var i = 0; i < 4; i++)
        {
            var target = centre + p.transform.right * ((i - 1.5f) * spacing);
            var fire = GameManager.server.CreateEntity(PlumePrefab, target, Quaternion.identity) as FireBall;
            if (fire == null)
            {
                Line(run, "STEP", $"plume {i + 1}: CreateEntity returned null or not a FireBall");
                continue;
            }

            // Neuter. spreadSubEntity is left untouched: setting it to null caused a NullReferenceException
            // in FireBall.TryToSpread (run 2). generation = 1000 short-circuits the spread test.
            fire.damagePerSecond = 0f;
            fire.generation = 1000f;
            fire.canMerge = false;
            fire.ignoreNPC = true;
            fire.lifeTimeMin = 600f;
            fire.lifeTimeMax = 600f;

            fire.Spawn();
            Track(run, fire, $"plume {i + 1}");
            Line(run, "STEP", $"plume {i + 1}: spawned world={Pos(fire.transform.position)} dist={Dist(fire.transform.position, p):0.0} m");

            if (follow)
            {
                // Run 5: SetParent reset the plume's local position to 0, so both plumes sat on the operator.
                // Fix: after parenting, set the local offset from the operator to the planned world point, then push the update.
                var before = Pos(fire.transform.position);
                fire.SetParent(p, worldPositionStays: true);
                var afterParent = Pos(fire.transform.position);
                var local = p.transform.InverseTransformPoint(target);
                fire.transform.localPosition = local;
                fire.SendNetworkUpdateImmediate();
                Line(run, "STEP", $"plume {i + 1}: follow: before={before} afterSetParent={afterParent} plannedLocal={Pos(local)} final world={Pos(fire.transform.position)} final local={Pos(fire.transform.localPosition)} dist={Dist(fire.transform.position, p):0.0} m");
            }
        }

        Line(run, "RESULT", $"4 plumes, mode={mode}, spacing {spacing:0.0} m, centre 5 m in front. Report: how many you see, where they sit relative to you, follow or fixed, look.");
    }

    // ── R6c: component audit of the siren prefab and a live entity ───────────────────────────────
    // The siren has no AudioSource in its hierarchy (R6b) but it is audible, so the sound comes from another
    // component. This lists every component type and reflects simple members of anything sound-related.
    private void ProbeR6c(ProbeRun run, BasePlayer p)
    {
        run.TimeoutSeconds = 180f;
        var path = run.Args.Length > 0 ? run.Args[0] : SirenPrefab;

        var go = GameManager.server.FindPrefab(path);
        if (go == null)
        {
            Line(run, "RESULT", $"FindPrefab null for '{path}'");
            return;
        }

        AuditComponents(run, go, "prefab");

        var entity = GameManager.server.CreateEntity(path, InFront(p, 20f, 1f), Quaternion.identity);
        if (entity == null)
        {
            Line(run, "RESULT", "CreateEntity returned null; live audit skipped");
            return;
        }

        Track(run, entity, "siren audit entity");
        entity.Spawn();
        AuditComponents(run, entity.gameObject, "live");
        Line(run, "RESULT", "audit complete; entity placed 20 m in front and removed on end. Compare the prefab and live lists in the log.");
    }

    private static bool IsSoundRelated(string typeName)
    {
        var n = typeName.ToLowerInvariant();
        return n.Contains("sound") || n.Contains("audio") || n.Contains("play") || n.Contains("effect") ||
               n.Contains("loop") || n.Contains("siren") || n.Contains("clip") || n.Contains("music");
    }

    private static bool IsSimple(Type t) => t.IsPrimitive || t == typeof(string) || t.IsEnum;

    private void AuditComponents(ProbeRun run, GameObject root, string label)
    {
        var comps = root.GetComponentsInChildren<Component>(true);
        Line(run, "AUDIT", $"{label}: {comps.Length} component(s) in hierarchy of '{root.name}'");

        foreach (var c in comps)
        {
            if (c == null)
            {
                Line(run, "AUDIT", "  <null/missing component>");
                continue;
            }

            var type = c.GetType();
            Line(run, "AUDIT", $"  [{c.gameObject.name}] {type.FullName}");
            if (!IsSoundRelated(type.Name)) continue;

            const BindingFlags flags = BindingFlags.Public | BindingFlags.Instance;
            foreach (var f in type.GetFields(flags))
            {
                if (!IsSimple(f.FieldType)) continue;
                try { Line(run, "AUDIT", $"      field {f.Name} = {Convert.ToString(f.GetValue(c))}"); }
                catch (Exception ex) { Line(run, "AUDIT", $"      field {f.Name} threw {ex.GetType().Name}"); }
            }

            foreach (var pr in type.GetProperties(flags))
            {
                if (pr.GetIndexParameters().Length > 0 || !IsSimple(pr.PropertyType)) continue;
                try { Line(run, "AUDIT", $"      prop {pr.Name} = {Convert.ToString(pr.GetValue(c, null))}"); }
                catch (Exception ex) { Line(run, "AUDIT", $"      prop {pr.Name} threw {ex.GetType().Name}"); }
            }
        }
    }

    // ── R1b: can a visible missile body ride on an invisible networked entity? ─────────────────
    // A non-entity GameObject (the silo missile) cannot be networked on its own. This probe:
    //  1. audits the missile and the nuclear_warhead entity: child names, mesh names, materials;
    //  2. spawns a bare networked entity as the carrier (rocket_crane_lift_trigger: BaseEntity only);
    //  3. instantiates a SERVER-SIDE copy of the missile under the carrier.
    // Expected: clients do NOT see step 3, because only entities and their prefab children replicate.
    private void ProbeR1b(ProbeRun run, BasePlayer p)
    {
        run.TimeoutSeconds = 180f;

        var missile = GameManager.server.FindPrefab(MissilePrefab);
        if (missile == null)
        {
            Line(run, "RESULT", "FindPrefab null for the missile");
            return;
        }

        AuditVisuals(run, missile, "missile prefab");

        var warhead = GameManager.server.FindPrefab(BusPrefab);
        if (warhead != null) AuditVisuals(run, warhead, "nuclear_warhead entity prefab");

        var carrierPos = InFront(p, 14f, 1f);
        var carrier = GameManager.server.CreateEntity(CarrierPrefab, carrierPos, Quaternion.identity);
        if (carrier == null)
        {
            Line(run, "RESULT", $"carrier CreateEntity returned null for {CarrierPrefab}");
            return;
        }

        carrier.Spawn();
        Track(run, carrier, "carrier entity");
        Line(run, "STEP", $"carrier {carrier.GetType().Name} spawned at {Pos(carrierPos)} (net id {(carrier.net != null ? carrier.net.ID : 0u)})");
        AuditComponents(run, carrier.gameObject, "carrier live");

        GameObject visual = null;
        try
        {
            visual = UnityEngine.Object.Instantiate(missile, carrier.transform);
            visual.transform.localPosition = Vector3.zero;
            visual.transform.localRotation = Quaternion.identity;
            Line(run, "STEP", $"server-side missile copy created under the carrier '{carrier.transform.name}'");
        }
        catch (Exception ex)
        {
            Line(run, "STEP", $"Instantiate threw {ex.GetType().Name}: {ex.Message}");
        }

        if (visual != null)
        {
            run.Cleanup.Add(new CleanupItem
            {
                Label = "destroy server-side missile copy",
                Run = () =>
                {
                    UnityEngine.Object.Destroy(visual);
                    return "destroyed";
                },
            });
        }

        Line(run, "WARN", "carrier has a TriggerHurt child (triggerHurt). Keep players out of the 14 m spot during the test.");
        Line(run, "RESULT", "Operator: stand 30 m away, look 14 m in front of you at about 1 m height. " +
                            "Report whether you see the missile body. Expected: NO. Then /probe R1b end <observation>.");
    }

    // Lists transforms with their mesh and material names, so visuals can be compared between prefabs.
    private void AuditVisuals(ProbeRun run, GameObject root, string label)
    {
        var transforms = root.GetComponentsInChildren<Transform>(true);
        Line(run, "AUDIT", $"{label}: {transforms.Length} transform(s) under '{root.name}'");

        foreach (var t in transforms)
        {
            var comps = t.GetComponents<Component>().Where(c => c != null).Select(c => c.GetType().Name).ToArray();
            var meshText = string.Empty;

            foreach (var c in t.GetComponents<Component>())
            {
                if (c == null) continue;
                var typeName = c.GetType().Name;
                if (typeName == "MeshFilter") meshText += " mesh=" + Reflect(c, "sharedMesh");
                else if (typeName == "MeshRenderer" || typeName == "SkinnedMeshRenderer") meshText += " material=" + Reflect(c, "sharedMaterial");
            }

            Line(run, "AUDIT", $"  '{t.name}' active={t.gameObject.activeInHierarchy} [{string.Join(", ", comps)}]{meshText}");
        }
    }

    private static string Pos(Vector3 v) => $"({v.x:0.00}, {v.y:0.00}, {v.z:0.00})";

    private static float Dist(Vector3 v, BasePlayer p) => Vector3.Distance(v, p.transform.position);

    // ── value types ──────────────────────────────────────────────────────────────────────────────
    private sealed class ProbeRun
    {
        public string Id;
        public ulong OperatorId;
        public DateTime StartUtc;
        public bool Ended;
        public float TimeoutSeconds = 300f;
        public int Counter;
        public string LogPath;
        public string[] Args = new string[0];
        public readonly List<CleanupItem> Cleanup = new List<CleanupItem>();
        public readonly List<string> Results = new List<string>();
        public string Outcome = string.Empty;
        public string CleanupStatus = "not run";
        public bool Observed;
        public bool Completed;
        public int TickCount;
        public int ErrorCount;
        public readonly List<Tuple<string, BaseEntity>> Tracked = new List<Tuple<string, BaseEntity>>();
    }

    private sealed class SessionLog
    {
        public string Path;
        public DateTime StartUtc;
        public ulong OperatorId;
        public int ProbesRun;
    }

    private sealed class CleanupItem
    {
        public string Label;
        public Func<string> Run;
    }

    // ── R5 prefix: replicate SupplySignal.Explode minus the cargo plane ─────────────────────────
    // Verified source (SupplySignal.cs): Explode() creates EntityToCreate (cargo plane), calls
    // InitDropPosition, Spawn(), CallHook, Invoke(FinishUp, 210f), SetFlag(Flags.On, true),
    // SendNetworkUpdateImmediate(). Run 2 killed the signal instead, which also removed its smoke.
    [HarmonyPatch(typeof(SupplySignal), nameof(SupplySignal.Explode))]
    private static class SupplySignalExplodePatch
    {
        private static bool Prefix(SupplySignal __instance)
        {
            if (!_r5Armed) return true;

            try
            {
                __instance.SetFlag(BaseEntity.Flags.On, true);
                __instance.SendNetworkUpdateImmediate();
                __instance.Invoke(__instance.FinishUp, _r5SmokeSeconds);
                _instance?.OnR5Intercept(__instance);
            }
            catch (Exception ex)
            {
                // Fall back to vanilla on error, so a failed probe never leaves a signal inert.
                _instance?.ErrorLog(_instance._active, "R5 prefix (falling back to vanilla)", ex);
                return true;
            }

            return false;
        }
    }
}
