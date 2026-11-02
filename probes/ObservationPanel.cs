// ObservationPanel.cs — Observations tab on Zen Panel, v0.1.4.
//
// Zen Panel (CodeFling, Brad Copp) is a RUNTIME dependency only. This file never references Zen Panel's
// code or types; it looks the plugin up by name and calls its public API_ methods with Call(). Zen Panel code
// must not be copied into this repository, and Zen Panel may only run on a server the operator owns.
//
// Each Submit saves the observation to SQLite BEFORE the UI changes. The row is read back and compared with
// the saved text; the box closes only after that read-back matches. SQLite is reached through the ADO.NET
// provider Mono.Data.Sqlite by reflection (no compile reference), the same route ObservationDbCheck v0.1.4
// proved on this server. Reads use ExecuteScalar only, because DataReader.Read() returned no rows there.
//
// Commands (admin only): chat /obspanel opens the box; console obspanel.clear discards every open box.
// Tab button: obspanel.new (rendered into Zen Panel's tab).
//
// Debug log: carbon/logs/ObservationPanel/panel_<yyyyMMdd-HHmmss>.log, one per server start.
// Database:  carbon/data/ObservationPanel/observations.db

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using Carbon.Components;
using Carbon.Plugins;
using Oxide.Core.Plugins;
using Oxide.Game.Rust.Cui;
using UnityEngine;

namespace Carbon.Plugins;

[Info("ObservationPanel", "Theatrical Nuke", "0.1.4")]
[Description("Observations tab on Zen Panel. Saves each submitted observation to SQLite before the UI changes.")]
public class ObservationPanel : CarbonPlugin
{
    private const string Version = "0.1.4";
    private const string ZenPanelName = "ZenPanel";
    private const string TabId = "observations";
    private const string TabTitle = "Observations";
    private const string RenderHookName = "ObsRenderTab";
    private const string DbFile = "observations";
    private const string BoxName = "ObservationPanel.box";
    private const string CmdNew = "obspanel.new";
    private const string CmdText = "obspanel.text";
    private const string CmdAdd = "obspanel.add";
    private const string CmdSubmit = "obspanel.submit";
    private const string CmdClose = "obspanel.close";
    private const string CmdClear = "obspanel.clear";
    private const string ProbeLabel = "free";
    // v0.1.4: each part is capped at PartChars. The input field stops accepting text at that point, so nothing
    // is cut silently. Typed input has been cut at 79 and 109 characters, so parts stay well under those.
    private const int PartChars = 60;
    private const int ListCount = 8;
    private const int PreviewChars = 110;
    private const float CommitDelaySeconds = 0.6f;
    private const char Unit = '\u001f';
    private const char Record = '\u001e';

    private readonly Dictionary<ulong, List<string>> _parts = new();
    private readonly Dictionary<ulong, string> _current = new();
    private readonly HashSet<ulong> _boxOpen = new();
    private readonly HashSet<ulong> _submitPending = new();
    private string _logPath;

    // ── Lifecycle ────────────────────────────────────────────────────────────

    private void OnServerInitialized()
    {
        try
        {
            var logDir = Path.Combine(Directory.GetCurrentDirectory(), "carbon", "logs", "ObservationPanel");
            Directory.CreateDirectory(logDir);
            _logPath = Path.Combine(logDir, "panel_" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + ".log");
            Debug("START", $"ObservationPanel v{Version} started; log {_logPath}");
            Debug("START", $"runtime {Environment.Version}; unity {Application.unityVersion}");

            try
            {
                EnsureDatabase();
            }
            catch (Exception ex)
            {
                // Logged in full; the tab still registers so the failure is visible in the UI and the log.
                LogException("database setup", ex);
            }
            RegisterTab();
        }
        catch (Exception ex)
        {
            LogException("startup", ex);
        }
    }

    // Zen Panel can load after this plugin; register the tab then.
    private void OnPluginLoaded(Plugin plugin)
    {
        try
        {
            if (plugin != null && plugin.Name == ZenPanelName)
            {
                Debug("TAB", "ZenPanel loaded after this plugin; registering the tab");
                RegisterTab();
            }
        }
        catch (Exception ex)
        {
            LogException("plugin loaded", ex);
        }
    }

    private void Unload()
    {
        try
        {
            foreach (var p in BasePlayer.activePlayerList)
            {
                if (p != null) CuiHelper.DestroyUi(p, BoxName);
            }
            UnregisterTab();
            Debug("STOP", "plugin unloaded; boxes destroyed and tab removed");
        }
        catch (Exception ex)
        {
            LogException("unload", ex);
        }
    }

    private void OnPlayerDisconnected(BasePlayer player, string reason)
    {
        if (player == null) return;
        _parts.Remove(player.userID);
        _current.Remove(player.userID);
        _boxOpen.Remove(player.userID);
        _submitPending.Remove(player.userID);
    }

    // ── Zen Panel integration (runtime calls only) ───────────────────────────

    private bool RegisterTab()
    {
        try
        {
            var zen = plugins.Find(ZenPanelName);
            if (zen == null)
            {
                Debug("TAB", "ZenPanel is not loaded; tab not registered yet");
                return false;
            }

            var result = zen.Call("API_RegisterTab", this, TabId, TabTitle, RenderHookName, null, null);
            Debug("TAB", $"API_RegisterTab('{TabId}') returned {Describe(result)}");
            return result is bool ok && ok;
        }
        catch (Exception ex)
        {
            LogException("register tab", ex);
            return false;
        }
    }

    private void UnregisterTab()
    {
        var zen = plugins.Find(ZenPanelName);
        if (zen == null) return;
        var result = zen.Call("API_UnregisterTab", this, TabId);
        Debug("TAB", $"API_UnregisterTab('{TabId}') returned {Describe(result)}");
    }

    // Called by Zen Panel with its LUI, the tab's root element, the viewing player, its palette and the tab id.
    public void ObsRenderTab(LUI ui, string root, BasePlayer player, object palette, string tabId)
    {
        try
        {
            var newBtn = ui.CreateButton(root, new LuiPosition(0.74f, 0.91f, 0.98f, 0.98f), LuiOffset.None,
                CmdNew, "0.15 0.55 0.2 1", isProtected: false);
            ui.CreateText(newBtn, LuiPosition.Full, LuiOffset.None, 14, "1 1 1 1", "New observation", TextAnchor.MiddleCenter);

            var total = CountObservations();
            ui.CreateText(root, new LuiPosition(0.02f, 0.91f, 0.72f, 0.98f), LuiOffset.None, 16, "1 1 1 1",
                $"Observations saved to the database: {total}", TextAnchor.MiddleLeft);

            var lines = RecentLines(ListCount);
            if (lines.Count == 0)
            {
                ui.CreateText(root, new LuiPosition(0.02f, 0.70f, 0.98f, 0.85f), LuiOffset.None, 14, "0.7 0.7 0.7 1",
                    "No observations yet. Use New observation to add one.", TextAnchor.MiddleLeft);
                return;
            }

            for (var i = 0; i < lines.Count; i++)
            {
                var top = 0.86f - i * 0.09f;
                ui.CreateText(root, new LuiPosition(0.02f, top - 0.08f, 0.98f, top), LuiOffset.None, 13, "0.9 0.9 0.9 1",
                    lines[i], TextAnchor.MiddleLeft);
            }
        }
        catch (Exception ex)
        {
            LogException("render tab", ex);
            ui.CreateText(root, LuiPosition.Full, LuiOffset.None, 14, "1 0.5 0.5 1",
                "Observations tab error. See carbon/logs/ObservationPanel.", TextAnchor.MiddleCenter);
        }
    }

    // ── Commands ─────────────────────────────────────────────────────────────

    [ChatCommand("obspanel")]
    private void CmdOpenBox(BasePlayer player, string command, string[] args)
    {
        if (!IsAllowed(player))
        {
            if (player != null) player.ChatMessage("Admin only.");
            return;
        }
        OpenBox(player);
    }

    [ConsoleCommand(CmdNew)]
    private void ConsoleNew(ConsoleSystem.Arg arg)
    {
        try
        {
            var player = arg.Player();
            if (!IsAllowed(player)) return;
            OpenBox(player);
        }
        catch (Exception ex)
        {
            LogException("new", ex);
        }
    }

    [ConsoleCommand(CmdText)]
    private void ConsoleText(ConsoleSystem.Arg arg)
    {
        try
        {
            var player = arg.Player();
            if (!IsAllowed(player) || !_boxOpen.Contains(player.userID)) return;

            var text = JoinArgs(arg);
            _current[player.userID] = text;
            Debug("BOX", $"{player.UserIDString} input sent: characters={text.Length}");

            if (_submitPending.Contains(player.userID)) CommitSubmit(player);
        }
        catch (Exception ex)
        {
            LogException("text", ex);
        }
    }

    [ConsoleCommand(CmdAdd)]
    private void ConsoleAdd(ConsoleSystem.Arg arg)
    {
        try
        {
            var player = arg.Player();
            if (!IsAllowed(player) || !_boxOpen.Contains(player.userID)) return;
            Debug("BOX", $"{player.UserIDString} add part pressed");
            // The input sends its text when focus leaves, so wait briefly for it before reading.
            timer.Once(CommitDelaySeconds, () => CommitPart(player));
        }
        catch (Exception ex)
        {
            LogException("add part", ex);
        }
    }

    [ConsoleCommand(CmdSubmit)]
    private void ConsoleSubmit(ConsoleSystem.Arg arg)
    {
        try
        {
            var player = arg.Player();
            if (!IsAllowed(player) || !_boxOpen.Contains(player.userID)) return;

            Debug("BOX", $"{player.UserIDString} submit pressed");
            _submitPending.Add(player.userID);
            timer.Once(CommitDelaySeconds, () => CommitSubmit(player));
        }
        catch (Exception ex)
        {
            LogException("submit", ex);
        }
    }

    [ConsoleCommand(CmdClose)]
    private void ConsoleClose(ConsoleSystem.Arg arg)
    {
        try
        {
            var player = arg.Player();
            if (player == null) return;
            DiscardState(player.userID);
            CuiHelper.DestroyUi(player, BoxName);
            Debug("BOX", $"{player.UserIDString} closed without saving");
            player.ChatMessage("Closed without saving.");
        }
        catch (Exception ex)
        {
            LogException("close", ex);
        }
    }

    [ConsoleCommand(CmdClear)]
    private void ConsoleClear(ConsoleSystem.Arg arg)
    {
        try
        {
            var player = arg.Player();
            if (arg.Player() != null && !IsAllowed(player)) return;

            var boxes = _boxOpen.Count;
            foreach (var p in BasePlayer.activePlayerList)
            {
                if (p != null) CuiHelper.DestroyUi(p, BoxName);
            }
            _parts.Clear();
            _current.Clear();
            _boxOpen.Clear();
            _submitPending.Clear();
            Debug("CLEAR", $"discarded {boxes} open box(es) and all kept parts");
            arg.ReplyWith($"ObservationPanel: {boxes} open box(es) closed; kept parts discarded.");
        }
        catch (Exception ex)
        {
            LogException("clear", ex);
        }
    }

    // ── Box ──────────────────────────────────────────────────────────────────

    private void OpenBox(BasePlayer player)
    {
        _boxOpen.Add(player.userID);
        _submitPending.Remove(player.userID);
        SendBox(player);
        Debug("BOX", $"{player.UserIDString} box opened; parts kept: {PartCount(player.userID)}");
    }

    private void SendBox(BasePlayer player)
    {
        var uid = player.userID;
        var kept = KeptSummary(uid);
        var title = "New observation. Each part holds up to " + PartChars + " characters. Add part keeps it and starts the next; Submit saves all parts as typed. "
            + "to the database. X discards.";

        using var cui = new CUI(CuiHandler);
        var root = cui.v2.CreateParent(CUI.ClientPanels.Overlay, LuiPosition.Full, BoxName);
        var overlay = cui.v2.CreatePanel(root, LuiPosition.Full, LuiOffset.None, "0 0 0 0.92");
        overlay.AddCursor();

        var box = cui.v2.CreatePanel(overlay, new LuiPosition(0.22f, 0.16f, 0.78f, 0.86f), LuiOffset.None, "0.05 0.05 0.05 0.98");
        cui.v2.CreateText(box, new LuiPosition(0.03f, 0.89f, 0.88f, 0.98f), LuiOffset.None, 15, "1 1 1 1", title, TextAnchor.UpperLeft);

        var close = cui.v2.CreateButton(box, new LuiPosition(0.92f, 0.90f, 0.98f, 0.98f), LuiOffset.None, CmdClose, "0.6 0.1 0.1 1", isProtected: false);
        cui.v2.CreateText(close, LuiPosition.Full, LuiOffset.None, 16, "1 1 1 1", "X", TextAnchor.MiddleCenter);

        cui.v2.CreateText(box, new LuiPosition(0.03f, 0.76f, 0.97f, 0.86f), LuiOffset.None, 12, "0.7 0.7 0.7 1", kept, TextAnchor.UpperLeft);

        var input = cui.v2.CreateInput(box, new LuiPosition(0.03f, 0.22f, 0.97f, 0.74f), LuiOffset.None, "1 1 1 1", string.Empty, 14, CmdText,
            charLimit: PartChars, isProtected: false, alignment: TextAnchor.UpperLeft);
        input.SetInputAutoFocus(true);
        input.SetInputKeyboard(true);

        var add = cui.v2.CreateButton(box, new LuiPosition(0.03f, 0.03f, 0.32f, 0.12f), LuiOffset.None, CmdAdd, "0.2 0.35 0.65 1", isProtected: false);
        cui.v2.CreateText(add, LuiPosition.Full, LuiOffset.None, 14, "1 1 1 1", "Add part (keep typing)", TextAnchor.MiddleCenter);

        var submit = cui.v2.CreateButton(box, new LuiPosition(0.35f, 0.03f, 0.65f, 0.12f), LuiOffset.None, CmdSubmit, "0.15 0.55 0.2 1", isProtected: false);
        cui.v2.CreateText(submit, LuiPosition.Full, LuiOffset.None, 16, "1 1 1 1", "Submit observation", TextAnchor.MiddleCenter);

        cui.v2.SendUi(player);
    }

    private void CommitPart(BasePlayer player)
    {
        try
        {
            if (player == null || !_boxOpen.Contains(player.userID)) return;

            var uid = player.userID;
            var text = _current.TryGetValue(uid, out var t) ? t : string.Empty;
            if (!string.IsNullOrWhiteSpace(text))
            {
                if (!_parts.TryGetValue(uid, out var list)) _parts[uid] = list = new List<string>();
                list.Add(text);
            }
            _current[uid] = string.Empty;
            Debug("BOX", $"{player.UserIDString} part kept: characters={text.Length}; parts now {PartCount(uid)}");
            var full = text.Length >= PartChars;

            CuiHelper.DestroyUi(player, BoxName);
            SendBox(player);
            player.ChatMessage($"Part {PartCount(uid)} kept ({text.Length} characters)."
                + (full ? " That part is full; the field stops at " + PartChars + " characters. Press Add part to continue." : " Keep typing, then Add part again or Submit."));
        }
        catch (Exception ex)
        {
            LogException("commit part", ex);
        }
    }

    private void CommitSubmit(BasePlayer player)
    {
        try
        {
            if (player == null) return;
            var uid = player.userID;
            if (!_submitPending.Remove(uid)) return;
            if (!_boxOpen.Contains(uid)) return;

            var text = FullText(uid);
            // v0.1.3: show what arrived, so a cut in the input field is visible at once.
            var tail = text.Length > 40 ? text.Substring(text.Length - 40) : text;
            var endsMidWord = text.Length > 0 && char.IsLetter(text[text.Length - 1]);
            Debug("RECEIVED", $"{player.UserIDString} characters={text.Length} parts={PartCount(uid)} ends_mid_word={endsMidWord} tail=\"{tail.Replace("\n", " ")}\"");
            if (string.IsNullOrWhiteSpace(text))
            {
                Debug("SAVE", $"{player.UserIDString} submit with empty text; nothing saved, box stays open");
                player.ChatMessage("Nothing to save. Type your observation first.");
                return;
            }

            var partCount = PartCount(uid) + (string.IsNullOrWhiteSpace(_current.TryGetValue(uid, out var c) ? c : null) ? 0 : 1);
            var saved = SaveObservation(player, ProbeLabel, text, partCount, out var id, out var error);
            if (!saved)
            {
                // The box stays open so the text is not lost.
                player.ChatMessage($"Not saved: {error}. Your text is still in the box; try Submit again.");
                return;
            }

            // The row is saved and read back before the box changes.
            DiscardState(uid);
            CuiHelper.DestroyUi(player, BoxName);
            _boxOpen.Remove(uid);
            Debug("SAVE", $"{player.UserIDString} observation #{id} saved and read back; characters={text.Length}; parts={partCount}");
            player.ChatMessage($"Observation #{id} saved: {text.Length} characters received, ending \"{tail.Replace("\n", " ")}\"."
                + (endsMidWord ? " It ends mid-word, so the input may have been cut." : string.Empty));
        }
        catch (Exception ex)
        {
            LogException("submit", ex);
        }
    }

    private void DiscardState(ulong uid)
    {
        _parts.Remove(uid);
        _current.Remove(uid);
        _submitPending.Remove(uid);
        _boxOpen.Remove(uid);
    }

    private string FullText(ulong uid)
    {
        var pieces = new List<string>();
        if (_parts.TryGetValue(uid, out var list)) pieces.AddRange(list);
        if (_current.TryGetValue(uid, out var cur) && !string.IsNullOrWhiteSpace(cur)) pieces.Add(cur);
        // v0.1.4: parts are joined exactly as typed, with no added separator, so the saved text matches the input.
        return string.Concat(pieces);
    }

    private int PartCount(ulong uid) => _parts.TryGetValue(uid, out var list) ? list.Count : 0;

    private string KeptSummary(ulong uid)
    {
        var text = FullText(uid);
        if (string.IsNullOrEmpty(text)) return "Nothing kept yet.";
        var preview = text.Replace("\r", " ").Replace("\n", " ");
        if (preview.Length > 300) preview = preview.Substring(0, 300) + "...";
        return $"Kept: {PartCount(uid)} part(s), {text.Length} characters. {preview}";
    }

    // ── Database (Mono.Data.Sqlite by reflection; scalar reads only) ─────────

    private bool SaveObservation(BasePlayer player, string probeId, string text, int partCount, out long id, out string error)
    {
        return SaveRow(probeId, player.UserIDString, player.displayName ?? string.Empty, text, partCount, out id, out error);
    }

    // Public API for other plugins (the probes). Returns "saved:<id>" or "error:<message>". Called with Call().
    public string OP_SaveObservation(string probeId, string steamId, string playerName, string text, int parts)
    {
        if (string.IsNullOrWhiteSpace(text)) return "error:empty text";
        var ok = SaveRow(probeId ?? ProbeLabel, steamId ?? "server", playerName ?? "server", text, parts <= 0 ? 1 : parts,
            out var id, out var error);
        return ok ? "saved:" + id : "error:" + error;
    }

    private bool SaveRow(string probeId, string steam, string name, string text, int partCount, out long id, out string error)
    {
        id = 0;
        error = null;
        try
        {
            var created = DateTime.UtcNow.ToString("O");
            id = WithDb(conn =>
            {
                Exec(conn,
                    "INSERT INTO observations (created_utc, steam_id, player_name, probe_id, text, characters, parts) VALUES (@created, @steam, @name, @probe, @text, @chars, @parts)",
                    ("@created", created), ("@steam", steam), ("@name", name), ("@probe", probeId),
                    ("@text", text), ("@chars", text.Length), ("@parts", partCount));
                var newId = Convert.ToInt64(Scalar(conn, "SELECT last_insert_rowid()"));
                var back = Convert.ToString(Scalar(conn, "SELECT text FROM observations WHERE id = @id", ("@id", newId)));
                if (!string.Equals(back, text, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException($"read-back of #{newId} did not match the saved text (saved {text.Length}, read {(back ?? "null").Length} characters)");
                }
                return newId;
            });
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            LogException("save observation", ex);
            return false;
        }
    }

    private long CountObservations()
    {
        return WithDb(conn => Convert.ToInt64(Scalar(conn, "SELECT COUNT(*) FROM observations")));
    }

    private List<string> RecentLines(int count)
    {
        var raw = WithDb(conn => Convert.ToString(Scalar(conn,
            "SELECT group_concat(line, char(30)) FROM (SELECT id || char(31) || created_utc || char(31) || player_name || char(31) || probe_id || char(31) || replace(replace(text, char(10), ' '), char(13), ' ') AS line FROM observations ORDER BY id DESC LIMIT @n)",
            ("@n", count))));
        var lines = new List<string>();
        if (string.IsNullOrEmpty(raw)) return lines;
        foreach (var record in raw.Split(Record))
        {
            var f = record.Split(Unit);
            if (f.Length < 5) continue;
            var preview = f[4].Length > PreviewChars ? f[4].Substring(0, PreviewChars) + "..." : f[4];
            lines.Add($"#{f[0]}  {f[1]}  {f[2]} [{f[3]}]  {preview}");
        }
        return lines;
    }

    private T WithDb<T>(Func<object, T> work)
    {
        object conn = null;
        try
        {
            conn = OpenConnection();
            return work(conn);
        }
        finally
        {
            if (conn != null)
            {
                try
                {
                    DynInvoke(conn, "Close");
                }
                catch (Exception ex)
                {
                    LogException("close database", ex);
                }
            }
        }
    }

    private object OpenConnection()
    {
        var type = FindSqliteConnectionType();
        if (type == null) throw new InvalidOperationException("Mono.Data.Sqlite.SqliteConnection is not loaded on this server");

        var path = DatabasePath();
        var conn = Activator.CreateInstance(type, new object[] { "Data Source=" + path + ";Version=3;" });
        DynInvoke(conn, "Open");
        // SQLite creates the file on first open. Create the table on every open, so a missing or emptied file
        // is rebuilt before any read or write.
        Exec(conn, Schema);
        return conn;
    }

    private const string Schema = "CREATE TABLE IF NOT EXISTS observations (id INTEGER PRIMARY KEY AUTOINCREMENT, created_utc TEXT NOT NULL, steam_id TEXT NOT NULL, player_name TEXT NOT NULL, probe_id TEXT NOT NULL, text TEXT NOT NULL, characters INTEGER NOT NULL, parts INTEGER NOT NULL)";

    // Startup check: report whether the file and the table exist, creating both when missing.
    private void EnsureDatabase()
    {
        var path = DatabasePath();
        var existed = File.Exists(path);
        Debug("DB", $"database file {path}: {(existed ? "found" : "not found; SQLite creates it on first open")}");

        WithDb(conn =>
        {
            var tables = Convert.ToInt64(Scalar(conn, "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'observations'"));
            if (tables != 1) throw new InvalidOperationException("table 'observations' is missing after schema creation");
            var rows = Convert.ToInt64(Scalar(conn, "SELECT COUNT(*) FROM observations"));
            Debug("DB", $"table 'observations' ready; {rows} row(s); file now {new FileInfo(path).Length} bytes");
            return rows;
        });
    }

    private static string DatabasePath()
    {
        var dir = Path.Combine(Directory.GetCurrentDirectory(), "carbon", "data", "ObservationPanel");
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, DbFile + ".db");
    }

    private static Type FindSqliteConnectionType()
    {
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            var t = asm.GetType("Mono.Data.Sqlite.SqliteConnection", false);
            if (t != null) return t;
        }
        return null;
    }

    private void Exec(object conn, string sql, params (string Name, object Value)[] parameters)
    {
        var cmd = Prepare(conn, sql, parameters);
        try
        {
            DynInvoke(cmd, "ExecuteNonQuery");
        }
        finally
        {
            ReleaseCommand(cmd);
        }
    }

    private object Scalar(object conn, string sql, params (string Name, object Value)[] parameters)
    {
        var cmd = Prepare(conn, sql, parameters);
        try
        {
            var value = DynInvoke(cmd, "ExecuteScalar");
            return value is DBNull ? null : value;
        }
        finally
        {
            ReleaseCommand(cmd);
        }
    }

    private object Prepare(object conn, string sql, (string Name, object Value)[] parameters)
    {
        var cmd = DynInvoke(conn, "CreateCommand");
        DynSet(cmd, "CommandText", sql);
        foreach (var p in parameters)
        {
            var prm = DynInvoke(cmd, "CreateParameter");
            DynSet(prm, "ParameterName", p.Name);
            DynSet(prm, "Value", p.Value ?? DBNull.Value);
            DynInvoke(DynGet(cmd, "Parameters"), "Add", prm);
        }
        return cmd;
    }

    private static void ReleaseCommand(object o)
    {
        if (o is IDisposable d) d.Dispose();
    }

    // ── Reflection helpers (same rules as ObservationDbCheck v0.1.2) ─────────

    private static object DynInvoke(object target, string name, params object[] args)
    {
        if (target == null) throw new ArgumentNullException(name, "target is null");
        var m = target.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static)
            .FirstOrDefault(x => x.Name == name && x.GetParameters().Length == args.Length);
        if (m == null) throw new MissingMethodException(target.GetType().FullName, name);
        return m.Invoke(m.IsStatic ? null : target, args);
    }

    // GetProperty(name) throws AmbiguousMatchException when a class implements the same property explicitly
    // (SqliteCommand.Parameters). Prefer the property declared on the runtime type.
    private static PropertyInfo FindProp(Type t, string name)
    {
        var all = t.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static)
            .Where(p => p.Name == name).ToList();
        return all.FirstOrDefault(p => p.DeclaringType == t) ?? all.FirstOrDefault();
    }

    private static object DynGet(object target, string name)
    {
        var p = FindProp(target.GetType(), name);
        if (p == null) throw new MissingMemberException(target.GetType().FullName, name);
        var getter = p.GetGetMethod(true);
        return p.GetValue(getter != null && getter.IsStatic ? null : target, null);
    }

    private static void DynSet(object target, string name, object value)
    {
        var p = FindProp(target.GetType(), name);
        if (p == null || !p.CanWrite) throw new MissingMemberException(target.GetType().FullName, name);
        p.SetValue(target, value, null);
    }

    // ── Helpers and logging ──────────────────────────────────────────────────

    private static bool IsAllowed(BasePlayer player) => player != null && player.IsAdmin;

    private static string JoinArgs(ConsoleSystem.Arg arg)
    {
        var parts = new List<string>();
        for (var i = 0; i < 5000 && arg.HasArgs(i + 1); i++)
        {
            parts.Add(arg.GetString(i));
        }
        return string.Join(" ", parts);
    }

    private static string Describe(object o)
    {
        if (o == null) return "null";
        if (o is string s) return "\"" + s + "\"";
        return o.GetType().Name + ": " + o;
    }

    private void Debug(string where, string text)
    {
        var line = $"[{DateTime.UtcNow:O}] [{where}] {text}";
        Puts(line);
        if (_logPath == null) return;
        try
        {
            File.AppendAllText(_logPath, line + Environment.NewLine, Encoding.UTF8);
        }
        catch (Exception ex)
        {
            Puts($"[{DateTime.UtcNow:O}] [LOG] could not write {_logPath}: {ex.Message}");
        }
    }

    private void LogException(string where, Exception ex)
    {
        Debug("EXCEPTION", $"{where}: {ex}");
    }
}
