// ObservationDbCheck.cs — SQLite feasibility check for the observation box (v0.1.0).
//
// Question it answers: can a Carbon plugin on this server open an SQLite database, create a table,
// insert a row, and read the same row back, through the SQLite extension that Oxide provides?
//
// Design:
//   * This file has NO compile-time reference to any SQLite assembly. Every type and method is found and
//     called by reflection, so it compiles on a server where the SQLite extension is missing, and it cannot
//     break TheatricalNukeProbe. If the extension is missing, the log says so and the verdict is FAIL.
//   * Extremely verbose: every assembly, type, member signature, call, argument, return value, callback,
//     timing and exception (with inner exceptions and stack) is written to one log file per run.
//   * It never touches the probe's session logs or any probe state.
//
// Usage (admin):
//   /dbcheck            chat command; runs the check
//   dbcheck.run         console command; runs the check
// Log:
//   carbon/logs/ObservationDbCheck/dbcheck_<yyyyMMdd-HHmmss>.log   every step of one run
//   The verdict line is the last line of the log: VERDICT PASS or VERDICT FAIL.
//
// Test server only. The check writes a table named dbcheck_probe into its own database file.

using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Text;
using Carbon.Plugins;
using UnityEngine;

namespace Carbon.Plugins;

[Info("ObservationDbCheck", "Theatrical Nuke", "0.1.0")]
[Description("Checks whether this server can write to an SQLite database from a plugin. Logs every step.")]
public class ObservationDbCheck : CarbonPlugin
{
    private const string Version = "0.1.0";
    private const string LogDirectory = "carbon/logs/ObservationDbCheck";
    private const string DbName = "ObservationDbCheck_check";
    private const string Table = "dbcheck_probe";
    private const float TimeoutSeconds = 15f;

    private string _logPath = string.Empty;
    private int _seq;
    private string _stage = "idle";
    private bool _finished;
    private bool _running;
    private object _library;
    private object _connection;
    private string _token = string.Empty;
    private BasePlayer _requester;
    private DateTime _startedUtc;

    // ── Commands ─────────────────────────────────────────────────────────────

    [ChatCommand("dbcheck")]
    private void CmdDbCheck(BasePlayer player, string command, string[] args)
    {
        if (player == null || !player.IsAdmin)
        {
            if (player != null) player.ChatMessage("Admin only.");
            return;
        }
        if (_running)
        {
            player.ChatMessage("A check is already running. Wait for its verdict.");
            return;
        }
        _requester = player;
        RunCheck();
        player.ChatMessage($"SQLite check running. Log: {_logPath}");
    }

    [ConsoleCommand("dbcheck.run")]
    private void CmdDbCheckConsole(ConsoleSystem.Arg arg)
    {
        if (_running)
        {
            Puts("ObservationDbCheck: a check is already running.");
            return;
        }
        _requester = null;
        RunCheck();
        Puts($"ObservationDbCheck: running. Log: {_logPath}");
    }

    // ── Run ──────────────────────────────────────────────────────────────────

    private void RunCheck()
    {
        _startedUtc = DateTime.UtcNow;
        _logPath = Path.Combine(LogDirectory, $"dbcheck_{_startedUtc:yyyyMMdd-HHmmss}.log");
        _seq = 0;
        _finished = false;
        _running = true;
        _library = null;
        _connection = null;
        _token = Guid.NewGuid().ToString("N");
        _stage = "start";

        WriteLog("START", $"ObservationDbCheck v{Version} run started (UTC {_startedUtc:O})");
        WriteLog("START", $"log file: {Path.GetFullPath(_logPath)}");
        WriteLog("START", $"working directory: {Directory.GetCurrentDirectory()}");
        WriteLog("START", $"runtime: {Environment.Version} on {Environment.OSVersion}; unity {Application.unityVersion}");
        WriteLog("START", $"token for this run: {_token}");

        timer.Once(TimeoutSeconds, () => FinishRun($"FAIL: no result within {TimeoutSeconds:0} s; last stage was '{_stage}'"));

        try
        {
            ScanAssemblies();
            FindTypes();
            _stage = "library";
            _library = GetLibraryInstance();
            if (_library == null) { FinishRun("FAIL: could not get the Oxide SQLite library instance (see the lines above)"); return; }
            DumpMembers(_library, "library");
            _stage = "open";
            OpenDatabase();
        }
        catch (Exception ex)
        {
            LogException("run", ex);
            FinishRun("FAIL: exception before the database was opened (see the stack above)");
        }
    }

    // ── Steps ────────────────────────────────────────────────────────────────

    private void ScanAssemblies()
    {
        WriteLog("ASSEMBLY", "scanning every loaded assembly for SQLite, Oxide, Carbon and System.Data names");
        var assemblies = AppDomain.CurrentDomain.GetAssemblies();
        WriteLog("ASSEMBLY", $"loaded assemblies: {assemblies.Length}");
        foreach (var asm in assemblies)
        {
            var name = asm.GetName();
            var lower = name.Name.ToLowerInvariant();
            var interesting = lower.Contains("sqlite") || lower.Contains("oxide") || lower.Contains("carbon")
                || lower.Contains("microsoft.data") || lower.Contains("system.data") || lower.Contains("mono.data");
            if (!interesting) continue;
            string location;
            try { location = asm.IsDynamic ? "(dynamic)" : asm.Location; }
            catch (Exception ex) { location = $"(location error: {ex.Message})"; }
            WriteLog("ASSEMBLY", $"match: {name.Name} version={name.Version} location={location}");
        }
    }

    private void FindTypes()
    {
        WriteLog("TYPE", "looking up the types this check needs by full name");
        var names = new[]
        {
            "Oxide.Core.Interface",
            "Oxide.Ext.SQLite.Libraries.SQLite",
            "Oxide.Ext.SQLite.Connection",
            "Oxide.Core.Database.Sql",
            "Oxide.Core.Database.Connection",
            "Oxide.Core.Plugins.Plugin",
            "System.Data.SQLite.SQLiteConnection",
            "Microsoft.Data.Sqlite.SqliteConnection",
            "Mono.Data.Sqlite.SqliteConnection",
        };
        foreach (var n in names)
        {
            var t = FindType(n);
            if (t == null)
            {
                WriteLog("TYPE", $"not found: {n}");
            }
            else
            {
                WriteLog("TYPE", $"found: {n} in {t.Assembly.GetName().Name} {t.Assembly.GetName().Version}");
            }
        }
    }

    private object GetLibraryInstance()
    {
        var ifaceType = FindType("Oxide.Core.Interface");
        var libType = FindType("Oxide.Ext.SQLite.Libraries.SQLite");
        if (ifaceType == null) { WriteLog("LIBRARY", "Oxide.Core.Interface not found; cannot reach GetLibrary"); return null; }
        if (libType == null) { WriteLog("LIBRARY", "Oxide.Ext.SQLite.Libraries.SQLite not found; the SQLite extension is probably not installed on this server"); return null; }

        try
        {
            var oxideProp = ifaceType.GetProperty("Oxide", BindingFlags.Public | BindingFlags.Static);
            WriteLog("LIBRARY", $"Interface.Oxide property present: {oxideProp != null}");
            if (oxideProp == null) return null;
            var oxide = oxideProp.GetValue(null, null);
            WriteLog("LIBRARY", $"Interface.Oxide value type: {(oxide == null ? "null" : oxide.GetType().FullName)}");
            if (oxide == null) return null;

            var getLib = oxide.GetType().GetMethods()
                .FirstOrDefault(m => m.Name == "GetLibrary" && m.IsGenericMethodDefinition && m.GetParameters().Length == 0);
            WriteLog("LIBRARY", $"generic GetLibrary<T>() present: {getLib != null}");
            if (getLib == null) return null;

            var lib = getLib.MakeGenericMethod(libType).Invoke(oxide, null);
            WriteLog("LIBRARY", $"GetLibrary<SQLite> returned: {(lib == null ? "null" : lib.GetType().FullName)}");
            return lib;
        }
        catch (Exception ex)
        {
            LogException("GetLibrary", ex);
            return null;
        }
    }

    private void OpenDatabase()
    {
        WriteLog("OPEN", $"opening database name '{DbName}' with this plugin as the owner");
        var ok = CallLib("OpenDb", new object[] { DbName, this, true }, result =>
        {
            _connection = result;
            WriteLog("OPEN", $"OpenDb returned: {Describe(result)}");
        });
        if (!ok || _connection == null)
        {
            FinishRun("FAIL: OpenDb did not return a connection (see the OPEN lines above)");
            return;
        }
        DumpMembers(_connection, "connection");
        _stage = "create";
        var create = $"CREATE TABLE IF NOT EXISTS {Table} (id INTEGER PRIMARY KEY AUTOINCREMENT, token TEXT NOT NULL, created_utc TEXT NOT NULL, value INTEGER NOT NULL)";
        ExecuteSql("create", create, new object[0], result => InsertStep());
    }

    private void InsertStep()
    {
        _stage = "insert";
        var sql = $"INSERT INTO {Table} (token, created_utc, value) VALUES (@0, @1, @2)";
        ExecuteSql("insert", sql, new object[] { _token, DateTime.UtcNow.ToString("O"), 42 }, result => SelectStep());
    }

    private void SelectStep()
    {
        _stage = "select";
        var sql = $"SELECT id, token, created_utc, value FROM {Table} WHERE token = @0";
        var sqlObj = BuildSql(sql, new object[] { _token });
        if (sqlObj == null) { FinishRun("FAIL: could not build the SELECT statement (see the SQL lines above)"); return; }
        var ok = CallLib("Query", new object[] { sqlObj, _connection }, result => VerifyResult(result));
        if (!ok) FinishRun("FAIL: no Query overload could be called (see the CALL lines above)");
    }

    private void VerifyResult(object result)
    {
        _stage = "verify";
        var rows = ToRows(result);
        WriteLog("VERIFY", $"rows returned: {rows.Count}");
        foreach (var row in rows)
        {
            var parts = row.Select(kv => $"{kv.Key}={kv.Value}");
            WriteLog("VERIFY", "row: " + string.Join(", ", parts));
        }
        var match = rows.Any(row => row.ContainsKey("token") && Convert.ToString(row["token"]) == _token);
        if (match)
        {
            FinishRun($"PASS: row written with token {_token} and read back (log: {_logPath})");
        }
        else
        {
            FinishRun("FAIL: the token written by INSERT was not read back by SELECT (see the VERIFY lines above)");
        }
    }

    private void FinishRun(string verdict)
    {
        if (_finished) return;
        _finished = true;
        _running = false;
        WriteLog("VERDICT", verdict);
        WriteLog("VERDICT", $"elapsed {(DateTime.UtcNow - _startedUtc).TotalSeconds:0.00} s");
        if (_connection != null)
        {
            CallLib("CloseDb", new object[] { _connection, this }, r => WriteLog("CLOSE", $"CloseDb returned: {Describe(r)}"));
        }
        ListDataFiles();
        var line = verdict.StartsWith("PASS", StringComparison.Ordinal) ? "SQLite check PASSED" : "SQLite check FAILED";
        Puts($"ObservationDbCheck: {line}. {verdict}. Log: {Path.GetFullPath(_logPath)}");
        if (_requester != null && !_requester.IsDestroyed)
        {
            _requester.ChatMessage($"{line}. Log: {_logPath}");
        }
    }

    // ── Reflection helpers ───────────────────────────────────────────────────

    // Calls a library method by name, trying overloads with the most parameters first.
    // Each argument is taken from the pool by type. A delegate parameter gets a callback that passes the
    // value to onResult. A method with no delegate parameter passes its return value to onResult.
    private bool CallLib(string methodName, object[] pool, Action<object> onResult)
    {
        if (_library == null) { WriteLog("CALL", $"{methodName}: no library instance"); return false; }
        var overloads = _library.GetType().GetMethods()
            .Where(m => m.Name == methodName)
            .OrderByDescending(m => m.GetParameters().Length)
            .ToList();
        if (overloads.Count == 0)
        {
            WriteLog("CALL", $"{methodName}: no method with that name on {_library.GetType().FullName}");
            return false;
        }
        WriteLog("CALL", $"{methodName}: {overloads.Count} overload(s): " + string.Join(" | ", overloads.Select(Signature)));

        foreach (var m in overloads)
        {
            var ps = m.GetParameters();
            var args = new object[ps.Length];
            var filled = true;
            var hasDelegate = false;
            for (var i = 0; i < ps.Length; i++)
            {
                var pt = ps[i].ParameterType;
                if (typeof(Delegate).IsAssignableFrom(pt) && pt != typeof(Delegate))
                {
                    if (hasDelegate) { filled = false; break; }
                    hasDelegate = true;
                    args[i] = MakeCallback(pt, onResult, methodName);
                    continue;
                }
                var source = pool.FirstOrDefault(o => o != null && pt.IsInstanceOfType(o));
                if (source != null) { args[i] = source; continue; }
                if (pt.IsValueType && pool.Any(o => o != null && o.GetType() == pt)) { args[i] = pool.First(o => o != null && o.GetType() == pt); continue; }
                if (ps[i].HasDefaultValue) { args[i] = ps[i].DefaultValue; continue; }
                filled = false;
                break;
            }
            if (!filled)
            {
                WriteLog("CALL", $"{methodName}: skipped {Signature(m)}; no argument for at least one parameter");
                continue;
            }

            WriteLog("CALL", $"{methodName}: invoking {Signature(m)} with [{string.Join(", ", args.Select(Describe))}]");
            var started = DateTime.UtcNow;
            try
            {
                var ret = m.Invoke(_library, args);
                WriteLog("CALL", $"{methodName}: returned in {(DateTime.UtcNow - started).TotalMilliseconds:0} ms: {Describe(ret)}");
                if (!hasDelegate && m.ReturnType != typeof(void)) onResult(ret);
                if (!hasDelegate && m.ReturnType == typeof(void)) onResult(null);
                return true;
            }
            catch (TargetInvocationException tie)
            {
                LogException(methodName, tie.InnerException ?? tie);
                return false;
            }
            catch (Exception ex)
            {
                LogException(methodName, ex);
                return false;
            }
        }
        WriteLog("CALL", $"{methodName}: no overload could be invoked with the available arguments");
        return false;
    }

    private void ExecuteSql(string label, string text, object[] args, Action<object> next)
    {
        var sqlObj = BuildSql(text, args);
        if (sqlObj == null) { FinishRun($"FAIL: could not build the {label} statement"); return; }
        var ok = CallLib("ExecuteNonQuery", new object[] { sqlObj, _connection }, result =>
        {
            WriteLog(label.ToUpperInvariant(), $"{label} result: {Describe(result)}");
            next(result);
        });
        if (!ok)
        {
            WriteLog(label.ToUpperInvariant(), $"{label}: ExecuteNonQuery could not be called; trying Insert for the same statement");
            var ok2 = CallLib("Insert", new object[] { sqlObj, _connection }, result =>
            {
                WriteLog(label.ToUpperInvariant(), $"{label} (Insert) result: {Describe(result)}");
                next(result);
            });
            if (!ok2) FinishRun($"FAIL: neither ExecuteNonQuery nor Insert could run the {label} statement");
        }
    }

    private object BuildSql(string text, object[] args)
    {
        WriteLog("SQL", $"building statement: {text} | args: [{string.Join(", ", args.Select(Describe))}]");
        var sqlType = FindType("Oxide.Core.Database.Sql");
        if (sqlType == null) { WriteLog("SQL", "Oxide.Core.Database.Sql not found"); return null; }
        try
        {
            var builderProp = sqlType.GetProperty("Builder", BindingFlags.Public | BindingFlags.Static);
            if (builderProp != null)
            {
                var builder = builderProp.GetValue(null, null);
                WriteLog("SQL", $"Sql.Builder: {Describe(builder)}");
                if (builder != null)
                {
                    var append = builder.GetType().GetMethods().FirstOrDefault(m =>
                        m.Name == "Append" && m.GetParameters().Length == 2 && m.GetParameters()[1].ParameterType == typeof(object[]));
                    if (append != null)
                    {
                        var s = append.Invoke(builder, new object[] { text, args });
                        WriteLog("SQL", $"built with Sql.Builder.Append: {Describe(s)}");
                        return s;
                    }
                    WriteLog("SQL", "Sql.Builder has no Append(string, object[])");
                }
            }
            else
            {
                WriteLog("SQL", "Sql has no static Builder property");
            }
            var ctor = sqlType.GetConstructor(new[] { typeof(string), typeof(object[]) });
            WriteLog("SQL", $"constructor (string, object[]) present: {ctor != null}");
            if (ctor != null)
            {
                var s = ctor.Invoke(new object[] { text, args });
                WriteLog("SQL", $"built with new Sql(text, args): {Describe(s)}");
                return s;
            }
        }
        catch (Exception ex)
        {
            LogException("BuildSql", ex);
        }
        return null;
    }

    private Delegate MakeCallback(Type delegateType, Action<object> handler, string label)
    {
        WriteLog("CALLBACK", $"{label}: building callback of type {delegateType.FullName}");
        var invoke = delegateType.GetMethod("Invoke");
        var ps = invoke.GetParameters().Select(p => Expression.Parameter(p.ParameterType, p.Name)).ToArray();
        var handlerInvoke = typeof(Action<object>).GetMethod("Invoke");
        var handlerConst = Expression.Constant(handler);
        Expression argument = ps.Length > 0
            ? (Expression)Expression.Convert(ps[0], typeof(object))
            : Expression.Constant(null, typeof(object));
        Expression body = Expression.Call(handlerConst, handlerInvoke, argument);
        if (invoke.ReturnType != typeof(void))
        {
            body = Expression.Block(body, Expression.Default(invoke.ReturnType));
        }
        var lambda = Expression.Lambda(delegateType, body, ps).Compile();
        WriteLog("CALLBACK", $"{label}: callback built; parameters: {string.Join(", ", invoke.GetParameters().Select(p => p.ParameterType.Name))}");
        return lambda;
    }

    private void DumpMembers(object obj, string label)
    {
        var t = obj.GetType();
        WriteLog("MEMBERS", $"{label} type {t.FullName}; base {(t.BaseType == null ? "none" : t.BaseType.FullName)}");
        foreach (var m in t.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static).OrderBy(x => x.Name))
        {
            if (m.DeclaringType == typeof(object)) continue;
            WriteLog("MEMBERS", $"{label} method: {Signature(m)}");
        }
        foreach (var p in t.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static))
        {
            string value;
            try { value = Describe(p.GetValue(p.GetGetMethod(true).IsStatic ? null : obj, null)); }
            catch (Exception ex) { value = $"(getter threw {ex.GetType().Name}: {ex.Message})"; }
            WriteLog("MEMBERS", $"{label} property: {p.PropertyType.Name} {p.Name} = {value}");
        }
        foreach (var f in t.GetFields(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static))
        {
            string value;
            try { value = Describe(f.GetValue(obj)); }
            catch (Exception ex) { value = $"(read threw {ex.GetType().Name}: {ex.Message})"; }
            WriteLog("MEMBERS", $"{label} field: {f.FieldType.Name} {f.Name} = {value}");
        }
    }

    private void ListDataFiles()
    {
        WriteLog("FILES", "searching carbon/, oxide/ and the working directory for files named like dbcheck or ObservationDbCheck");
        var roots = new[] { "carbon", "oxide", "." };
        foreach (var root in roots)
        {
            try
            {
                if (!Directory.Exists(root)) { WriteLog("FILES", $"{root}: directory not found"); continue; }
                var option = root == "." ? SearchOption.TopDirectoryOnly : SearchOption.AllDirectories;
                var files = Directory.GetFiles(root, "*", option)
                    .Where(f => Path.GetFileName(f).IndexOf("dbcheck", StringComparison.OrdinalIgnoreCase) >= 0
                        || Path.GetFileName(f).IndexOf("ObservationDbCheck", StringComparison.OrdinalIgnoreCase) >= 0)
                    .ToList();
                WriteLog("FILES", $"{root}: {files.Count} matching file(s)");
                foreach (var f in files)
                {
                    var info = new FileInfo(f);
                    WriteLog("FILES", $"  {Path.GetFullPath(f)} size={info.Length} bytes modified={info.LastWriteTimeUtc:O}");
                }
            }
            catch (Exception ex)
            {
                LogException($"ListDataFiles {root}", ex);
            }
        }
    }

    private static List<Dictionary<string, object>> ToRows(object result)
    {
        var rows = new List<Dictionary<string, object>>();
        if (result == null) return rows;
        var enumerable = result as IEnumerable;
        if (enumerable == null || result is string) return rows;
        foreach (var item in enumerable)
        {
            var dict = item as IDictionary;
            if (dict == null) continue;
            var row = new Dictionary<string, object>();
            foreach (DictionaryEntry kv in dict)
            {
                row[Convert.ToString(kv.Key)] = kv.Value;
            }
            rows.Add(row);
        }
        return rows;
    }

    private static Type FindType(string fullName)
    {
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            try
            {
                var t = asm.GetType(fullName, false);
                if (t != null) return t;
            }
            catch (Exception)
            {
                // Some dynamic assemblies throw on GetType; skip them.
            }
        }
        return null;
    }

    private static string Signature(MethodInfo m)
    {
        var ps = m.GetParameters().Select(p => $"{TypeName(p.ParameterType)} {p.Name}{(p.HasDefaultValue ? " = default" : string.Empty)}");
        return $"{TypeName(m.ReturnType)} {m.Name}({string.Join(", ", ps)})";
    }

    private static string TypeName(Type t)
    {
        if (t == null) return "null";
        if (!t.IsGenericType) return t.FullName ?? t.Name;
        var args = string.Join(", ", t.GetGenericArguments().Select(TypeName));
        var name = t.GetGenericTypeDefinition().FullName ?? t.Name;
        var tick = name.IndexOf('`');
        if (tick > 0) name = name.Substring(0, tick);
        return $"{name}<{args}>";
    }

    private static string Describe(object o)
    {
        if (o == null) return "null";
        if (o is string s) return "\"" + s + "\"";
        if (o is IDictionary d)
        {
            var parts = new List<string>();
            foreach (DictionaryEntry kv in d) parts.Add($"{kv.Key}={Describe(kv.Value)}");
            return "{" + string.Join(", ", parts) + "}";
        }
        if (o is IEnumerable e && !(o is string))
        {
            var parts = new List<string>();
            foreach (var item in e) parts.Add(Describe(item));
            return "[" + string.Join(", ", parts) + "] (" + parts.Count + ")";
        }
        return $"{o.GetType().FullName}: {o}";
    }

    // ── Logging ──────────────────────────────────────────────────────────────

    private void WriteLog(string tag, string text)
    {
        _seq++;
        var line = $"[{DateTime.UtcNow:O}] [{_seq:00000}] [{tag}] {text}";
        try
        {
            Directory.CreateDirectory(LogDirectory);
            File.AppendAllText(_logPath, line + Environment.NewLine, Encoding.UTF8);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"ObservationDbCheck: could not write log: {ex.Message}");
        }
    }

    private void LogException(string where, Exception ex)
    {
        WriteLog("EXCEPTION", $"{where}: {ex.GetType().FullName}: {ex.Message}");
        var inner = ex.InnerException;
        var depth = 1;
        while (inner != null)
        {
            WriteLog("EXCEPTION", $"  inner {depth}: {inner.GetType().FullName}: {inner.Message}");
            inner = inner.InnerException;
            depth++;
        }
        WriteLog("EXCEPTION", $"  stack: {ex.StackTrace}");
    }
}
