// TheatricalNukeProbe.cs — M0 probe harness, v0.6.0.
//
// v0.2 compiled and ran on the live test server (run 1 + run 2 console output), so the
// calls it used are proven. New in v0.3 and NOT yet compiled: BaseEntity.SetFlag,
// BaseEntity.Flags.On, SendNetworkUpdateImmediate, Invoke(Action,float) inside the R5
// prefix, UnityEngine.Object.FindObjectsOfType.
// Compile-checked in run 3: SetFlag, Flags.On, IsStatic() and GetIdentifier() are public.
// AudioSource is NOT referenced by type (AudioModule is not in the plugin reference set); it is read by reflection.
// If Carbon reports a compile error, those lines are the first suspects.
//
// PROTOCOL (v0.6, one session log per session):
//   /probe start                  open the session log. Required before any probe.
//   /probe <ID> [args]            start a probe. Everything it does is written to the session log.
//   /probe <ID> end               end it, run cleanup, then open an observation box. Type, press Submit.
//   /probe <ID> end <words>       end it with the observation typed in chat instead (no box).
//   /probe end                    close the session log (refused while a probe or box is open).
//   /probe abort | status | help
// IDs: R0 R1 R1b R2 R3 R4 R5 R6 R6b R6c R7 R8 R9 R10
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
using Carbon.Plugins;
using HarmonyLib;
using Oxide.Plugins;
using UnityEngine;

namespace Carbon.Plugins;

[Info("TheatricalNukeProbe", "Theatrical Nuke", "0.6.0")]
[Description("M0 probes R0-R10: one probe per command, start/end banners, per-run log file, cleanup.")]
public class TheatricalNukeProbe : CarbonPlugin
{
    private const string Version = "0.6.0";
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
    private const int MaxObservationWords = 1000;
    private const int MaxObservationChars = 6000;
    private const float TickSeconds = 5f;

    private static readonly string[] KnownIds = { "R0", "R1", "R2", "R3", "R4", "R5", "R6", "R6b", "R6c", "R7", "R8", "R9", "R10", "R1b" };
    private static readonly float[] MarkerRadii = { 0.5f, 1f, 2f, 3f, 5f };

    private static TheatricalNukeProbe _instance;
    private static bool _r5Armed;
    private static float _r5SmokeSeconds = 15f;

    private ProbeRun _active;
    private Harmony _harmony;

    // Session and observation state.
    private SessionLog _session;
    private ProbeRun _awaiting;
    private readonly Dictionary<ulong, string> _obsText = new Dictionary<ulong, string>();
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
            if (_awaiting != null) SubmitObservation(null, "(plugin unloaded before the observation box was submitted)", true);
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
                var awaiting = _awaiting == null ? string.Empty : $"; observation box open for {_awaiting.Id}";
                player.ChatMessage($"{session}; {running}{awaiting}.");
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

                EndSession(player, "operator /probe end");
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
                    // No words after end: open the observation box and wait for the operator to submit it.
                    EndProbe(_active, "ended by operator", null, player, true);
                }
                else
                {
                    EndProbe(_active, "ended by operator (observation typed in chat)", chatObservation, player, false);
                }

                return;
            }

            if (_awaiting != null)
            {
                player.ChatMessage($"The observation box for {_awaiting.Id} is still open. Submit it first.");
                return;
            }

            if (_session == null)
            {
                player.ChatMessage("No session is open. Start one with /probe start first.");
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
        player.ChatMessage("Session: /probe start (open the session log)   /probe end (close it)   /probe status");
        player.ChatMessage("Probe: /probe <ID> [args]   End: /probe <ID> end (opens an observation box)");
        player.ChatMessage("Probes: R0 R1 R1b R2 R3 R4 R5 R6 R6b R6c R7 R8 R9 R10");
        player.ChatMessage("R4 <path> [x y z]   R5 [smoke seconds]   R6 [metres]   R6b [path] [x y z]   R6c [path]");
        player.ChatMessage("R7 [radius ...] (default 0.5 1 2 3 5)   R9 [spacing] [static|follow]   R1b (carrier test)");
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
        _obsText.Clear();

        SessionLine("BANNER", "########## PROBE SESSION START ##########");
        SessionLine("INFO", $"plugin=TheatricalNukeProbe v{Version}");
        SessionLine("INFO", $"utc={started:o} local={LocalNow(started)}");
        SessionLine("INFO", $"operator={player.userID} '{player.displayName}' pos={Pos(player.transform.position)} yaw={player.transform.eulerAngles.y:0.0}");
        SessionLine("INFO", $"server hostname='{ConVar.Server.hostname}' level='{ConVar.Server.level}' worldsize={ConVar.Server.worldsize} m");
        SessionLine("INFO", $"players online={BasePlayer.activePlayerList.Count}");
        SessionLine("PROTOCOL", "one probe at a time; every probe writes into this file; /probe <ID> end opens an observation box; /probe end closes the session.");

        player.ChatMessage($"Probe session started. Log: {_session.Path}");
    }

    private void EndSession(BasePlayer player, string reason)
    {
        if (_session == null)
        {
            player?.ChatMessage("No session is open.");
            return;
        }

        if (_awaiting != null)
        {
            player?.ChatMessage($"The observation box for {_awaiting.Id} is still open. Submit it first.");
            return;
        }

        if (_active != null)
        {
            player?.ChatMessage($"{_active.Id} is still running. End it with /probe {_active.Id} end, or /probe abort.");
            return;
        }

        CloseSession(reason, player);
    }

    private void CloseSession(string reason, BasePlayer player)
    {
        var closing = _session;
        if (closing == null) return;

        var duration = (DateTime.UtcNow - closing.StartUtc).TotalSeconds;
        SessionLine("INFO", $"reason={reason}; utc={DateTime.UtcNow:o}; duration={duration:0.0}s; probes run={closing.ProbesRun}");

        if (_sessionSummary.Count == 0) SessionLine("SUMMARY", "no probes run");
        foreach (var line in _sessionSummary) SessionLine("SUMMARY", line);

        SessionLine("BANNER", "########## PROBE SESSION END ##########");

        _session = null;
        _sessionSummary.Clear();
        _obsText.Clear();
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

    private void EndProbe(ProbeRun run, string outcome, string observation, BasePlayer op, bool openBox)
    {
        if (run == null || run.Ended) return;
        run.Ended = true;
        run.Outcome = outcome;
        if (ReferenceEquals(_active, run)) _active = null;

        try
        {
            if (!string.IsNullOrEmpty(observation))
            {
                run.Observed = true;
                Line(run, "OBSERVE", observation);
            }

            SnapshotEnvironment(run, op, "end");
            run.CleanupStatus = RunCleanup(run);

            if (openBox && op != null)
            {
                Line(run, "END-PENDING", $"cleanup={run.CleanupStatus}; waiting for the observation box to be submitted");
                _awaiting = run;
                OpenObservationBox(op, run);
                op.ChatMessage($"Type your observation for {run.Id} in the box, then press Submit.");
                return;
            }

            FinishProbe(run, op);
        }
        catch (Exception ex)
        {
            ErrorLog(run, "EndProbe", ex);
            if (!ReferenceEquals(_awaiting, run)) FinishProbeSafely(run);
        }
    }

    private void FinishProbe(ProbeRun run, BasePlayer op)
    {
        var duration = (DateTime.UtcNow - run.StartUtc).TotalSeconds;
        var observed = run.Observed ? "yes" : "no";
        Banner(run, "END", $"outcome={run.Outcome}; cleanup={run.CleanupStatus}; duration={duration:0.0}s; observation={observed}; results={run.Results.Count}");
        _sessionSummary.Add($"{run.Id} outcome={run.Outcome} cleanup={run.CleanupStatus} duration={duration:0.0}s observation={observed}");
        op?.ChatMessage($"{run.Id} ended. Cleanup: {run.CleanupStatus}. Session log: {run.LogPath}");
    }

    private void FinishProbeSafely(ProbeRun run)
    {
        try
        {
            FinishProbe(run, null);
        }
        catch (Exception ex)
        {
            ErrorLog(run, "FinishProbe", ex);
        }
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

    // ── observation box (CUI) ────────────────────────────────────────────────────────────────────
    private void OpenObservationBox(BasePlayer op, ProbeRun run)
    {
        _obsText[op.userID] = string.Empty;
        CuiHelper.DestroyUi(op, ObsBoxName);
        CuiHelper.AddUi(op, ObservationBoxJson(run.Id));
        Line(run, "DEBUG", $"observation box opened for operator {op.userID}; limit {MaxObservationWords} words, {MaxObservationChars} characters");
    }

    private void CloseObservationBox(BasePlayer op)
    {
        CuiHelper.DestroyUi(op, ObsBoxName);
        _obsText.Remove(op.userID);
    }

    private static string ObservationBoxJson(string probeId)
    {
        return "[" +
               "{\"name\":\"" + ObsBoxName + "\",\"parent\":\"Overlay\",\"components\":[" +
               "{\"type\":\"UnityEngine.UI.Image\",\"color\":\"0 0 0 0.92\"}," +
               "{\"type\":\"NeedsCursor\"},{\"type\":\"NeedsKeyboard\"}," +
               "{\"type\":\"RectTransform\",\"anchormin\":\"0.22 0.16\",\"anchormax\":\"0.78 0.86\"}]}," +
               "{\"parent\":\"" + ObsBoxName + "\",\"components\":[" +
               "{\"type\":\"UnityEngine.UI.Text\",\"text\":\"Observation for " + probeId + ": type below, then press Submit\",\"fontSize\":16,\"align\":\"UpperCenter\",\"color\":\"1 1 1 1\"}," +
               "{\"type\":\"RectTransform\",\"anchormin\":\"0.03 0.88\",\"anchormax\":\"0.97 0.98\"}]}," +
               "{\"parent\":\"" + ObsBoxName + "\",\"components\":[" +
               "{\"type\":\"UnityEngine.UI.InputField\",\"text\":\"\",\"fontSize\":14,\"align\":\"UpperLeft\",\"command\":\"" + ObsCmdText + "\",\"characterLimit\":" + MaxObservationChars + ",\"lineType\":\"MultiLineNewline\"}," +
               "{\"type\":\"RectTransform\",\"anchormin\":\"0.03 0.22\",\"anchormax\":\"0.97 0.85\"}]}," +
               "{\"parent\":\"" + ObsBoxName + "\",\"components\":[" +
               "{\"type\":\"UnityEngine.UI.Text\",\"text\":\"Limit " + MaxObservationWords + " words (about " + MaxObservationChars + " characters). Click the box, type, then press Submit.\",\"fontSize\":13,\"align\":\"MiddleLeft\",\"color\":\"0.8 0.8 0.8 1\"}," +
               "{\"type\":\"RectTransform\",\"anchormin\":\"0.03 0.13\",\"anchormax\":\"0.97 0.20\"}]}," +
               "{\"parent\":\"" + ObsBoxName + "\",\"components\":[" +
               "{\"type\":\"UnityEngine.UI.Button\",\"color\":\"0.15 0.55 0.2 1\",\"command\":\"" + ObsCmdSubmit + "\"}," +
               "{\"type\":\"RectTransform\",\"anchormin\":\"0.35 0.03\",\"anchormax\":\"0.65 0.12\"}]}," +
               "{\"parent\":\"" + ObsBoxName + "\",\"components\":[" +
               "{\"type\":\"UnityEngine.UI.Text\",\"text\":\"Submit observation\",\"fontSize\":16,\"align\":\"MiddleCenter\",\"color\":\"1 1 1 1\"}," +
               "{\"type\":\"RectTransform\",\"anchormin\":\"0 0\",\"anchormax\":\"1 1\"}]}" +
               "]";
    }

    [ConsoleCommand(ObsCmdText)]
    private void ObservationTextCommand(ConsoleSystem.Arg arg)
    {
        try
        {
            var player = arg.Player();
            if (player == null || _awaiting == null || player.userID != _awaiting.OperatorId) return;

            var text = JoinArgs(arg);
            _obsText[player.userID] = text;
            Line(_awaiting, "DEBUG", $"input field sent text: words={CountWords(text)} characters={text.Length}");
        }
        catch (Exception ex)
        {
            ErrorLog(_awaiting, "observation text command", ex);
        }
    }

    [ConsoleCommand(ObsCmdSubmit)]
    private void ObservationSubmitCommand(ConsoleSystem.Arg arg)
    {
        try
        {
            var player = arg.Player();
            if (player == null) return;

            if (_awaiting == null)
            {
                player.ChatMessage("No observation box is open.");
                return;
            }

            var text = _obsText.TryGetValue(player.userID, out var stored) ? stored : string.Empty;
            Line(_awaiting, "DEBUG", $"submit pressed: stored text words={CountWords(text)} characters={text.Length}");
            SubmitObservation(player, text, false);
        }
        catch (Exception ex)
        {
            ErrorLog(_awaiting, "observation submit command", ex);
        }
    }

    private void SubmitObservation(BasePlayer op, string text, bool forced)
    {
        var run = _awaiting;
        if (run == null) return;

        if (!forced)
        {
            if (op == null || op.userID != run.OperatorId)
            {
                op?.ChatMessage("Only the operator who ended the probe can submit this box.");
                return;
            }

            if (string.IsNullOrWhiteSpace(text))
            {
                op.ChatMessage("No text has arrived yet. Click in the box, type your observation, then press Submit.");
                Line(run, "DEBUG", "submit rejected: no text received from the input field");
                return;
            }
        }

        var words = CountWords(text);
        if (words > MaxObservationWords)
        {
            Line(run, "DEBUG", $"submit rejected: {words} words exceeds the limit of {MaxObservationWords}");
            op?.ChatMessage($"Observation is {words} words; the limit is {MaxObservationWords}. Shorten it and press Submit.");
            return;
        }

        _awaiting = null;
        if (op != null) CloseObservationBox(op);

        Line(run, "DEBUG", $"observation accepted: words={words} characters={text.Length}{(forced ? " (forced: plugin unloading)" : string.Empty)}");
        run.Observed = !string.IsNullOrWhiteSpace(text);
        Line(run, "OBSERVE", string.IsNullOrWhiteSpace(text) ? "(none entered)" : text);
        FinishProbe(run, op);
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

    private static int CountWords(string text)
    {
        return string.IsNullOrWhiteSpace(text) ? 0 : text.Split((char[])null, StringSplitOptions.RemoveEmptyEntries).Length;
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

            var target = run?.LogPath ?? _session?.Path;
            if (target != null) Append(target, $"ERROR    {where}: {ex.GetType().Name}: {ex.Message}\n{ex}");
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
    // "NUKECAM#ID" gives a unique identifier. Set before Spawn(). IsValidIdentifier: alphanumeric, <= 32 chars.
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

        Line(run, "STEP", $"prefab identifier before spawn='{cctv.rcIdentifier}'; setting template 'NUKECAM#ID'");
        cctv.rcIdentifier = "NUKECAM#ID";
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
    private void ProbeR7(ProbeRun run, BasePlayer p)
    {
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
        var spacing = ParseFloat(run.Args, 0) ?? 2f;
        var mode = run.Args.Length > 1 ? run.Args[1].ToLowerInvariant() : "static";
        var follow = mode == "follow";
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
        public int TickCount;
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
