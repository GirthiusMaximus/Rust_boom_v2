# Rust Client/Server Internals for Modders

> **Scope.** How Rust's client and server actually talk, what a **server-side** mod can observe and
> change, and where the hard limits are (client mods, custom assets, pooling, spawn sharing).
>
> **Audience.** C# plugin authors targeting Carbon or a HarmonyMod. Read `CarbonMod.MD` for the
> framework API, `Senior_C.MD` for language/runtime rules, and `Exceptions.md` for exception
> containment and the separate error log.
>
> **Verified:** Rust `public`/`release` dedicated server, Unity 6 (June 2026 "Built Different"
> update), Harmony 2.3, Carbon `main` (2026-10-17). Vanilla Rust internals are decompiled/observed
> APIs, not an official SDK; **always re-check signatures against the build you deploy.**

---

## Table of contents

1. [The one rule: the server is authoritative](#1-the-one-rule-the-server-is-authoritative)
2. [Runtime reality](#2-runtime-reality)
3. [What a server mod can and cannot do](#3-what-a-server-mod-can-and-cannot-do)
4. [The server frame loop](#4-the-server-frame-loop)
5. [Entities and the network model](#5-entities-and-the-network-model)
6. [The wire protocol](#6-the-wire-protocol)
7. [Server → client communication](#7-server--client-communication)
8. [Client → server communication](#8-client--server-communication)
9. [Spawning, pooling and sharing](#9-spawning-pooling-and-sharing)
10. [Custom assets](#10-custom-assets)
11. [Client mods and why they are different](#11-client-mods-and-why-they-are-different)
12. [Debugging network behaviour](#12-debugging-network-behaviour)
13. [Performance and security checklist](#13-performance-and-security-checklist)
14. [Sources](#14-sources)

---

## 1. The one rule: the server is authoritative

Rust is a server-authoritative game. The client sends **intent** (input, chat, console commands,
RPCs); the server decides what happens and replicates the result. A server plugin therefore:

- sees all players, entities, inventories and world state,
- can modify server state and what is sent to clients,
- **cannot trust anything the client says** — every RPC/command is forgeable.

EAC (Easy Anti-Cheat) protects the official client. Server plugins run outside EAC's scope; they are
not detected as cheats, but they also cannot make a modified client legitimate. Keep EAC on for
normal servers.

---

## 2. Runtime reality

| Fact | Consequence |
|---|---|
| Unity **6** (since the June 2026 "Built Different" update), Mono scripting backend | .NET Framework 4.8-level BCL; no .NET 8/9/10 runtime APIs. |
| IL2CPP client | No runtime C# injection on the shipped client; client mods require a Doorstop-style loader and EAC off. |
| Mostly **single-threaded** server | Your hook time is frame time; one slow loop stalls every player. |
| Tick rate 30–60 (`server.tickrate`, read at startup only) | Per-tick budgets are ~16–33 ms for the whole server. |
| Monthly forced update (first Thursday) | Protocol, prefabs and method signatures change; patches break. |
| Windows Server 2019 / Windows 10 21H1 minimum | Older hosts no longer run current servers. |

> Unity 6 did not change the modding model: it is still server-authoritative, still Mono on the
> server, still protobuf on the wire. It did raise the client's minimum OS and introduced a
> high-polling-rate mouse issue (≥1000 Hz) that server admins see as "aim feels wrong"; it is a
> client-side setting, not something a plugin fixes.

---

## 3. What a server mod can and cannot do

| Capability | Server plugin | Notes |
|---|---|---|
| Read/modify players, entities, items, world | ✅ | Main use case. |
| Send chat/console messages | ✅ | `player.ChatMessage`, `SendConsoleCommand`. |
| Draw UI on clients | ✅ | CUI/LUI — JSON over an RPC; no client files needed. |
| Play existing effects/sounds | ✅ | `Effect.server.Run` with shipped prefab paths. |
| Spawn existing prefabs | ✅ | `GameManager.server.CreateEntity`. |
| Network a synthetic entity to selected clients | ✅ | `ClientEntity` (per-connection, no server logic). |
| Add new models/textures/sounds/prefabs | ❌ (for stock clients) | Assets must already exist on the client. |
| Run code on the client | ❌ | IL2CPP + EAC; Carbon-for-Client is separate and EAC-off. |
| Read encrypted Steam traffic | ❌ | You see post-decryption messages only inside the server process. |
| Change the wire format | ⚠️ | Possible via Harmony on serialization, but breaks vanilla clients. |

**Design rule:** if a feature needs new art or client code, it is not a server plugin — it is a
client mod, a workshop skin, or a redesign using shipped assets.

---

## 4. The server frame loop

Simplified per-frame order (names vary by build):

1. **Network receive** — Steam sockets deliver messages; `Network.Net.sv` cycles connections and
   reads packets.
2. **Message dispatch** — `Message.Type` handlers: `Entities`, `EntityDestroy`, `RPCMessage`,
   `EntityPosition`, `ConsoleMessage`, `ConsoleCommand`, `Effect`, …
3. **Entity ticks** — `BaseEntity.ServerThink`/`ServerCycle` for active entities on the main thread.
4. **Player ticks** — input processing, inventory, metabolism, subscriptions.
5. **Save** — periodic `ServerMgr` save; Carbon modifier data flushes around it.
6. **Network send** — queued updates/RPCs are flushed to connections.

Mod hooks run inside this loop. That has three consequences:

- **Never block.** A `Thread.Sleep`, synchronous HTTP call, or big LINQ query in a hook delays every
  player.
- **Never touch Unity objects off the main thread.** `Transform`, `GameObject`, `BasePlayer` and CUI
  are main-thread-only.
- **Queue, then flush.** For database/network work, enqueue and process on a timer/worker, then
  marshal the result back with `NextFrame`/`NextTick` (Carbon) or a main-thread dispatcher.

### 4.1 Tick budget

At 30 tick/s the entire server has ~33 ms per tick. A hook that takes 5 ms is 15% of the budget. Use
the Carbon profiler (`c.profile`) or stopwatch instrumentation around suspicious hooks; log only
when a threshold is exceeded (Carbon's `Debugging.HookLagSpikeThreshold` defaults to 1000 ms and
warns above 100 ms per hook).

---

## 5. Entities and the network model

### 5.1 Prefabs, net IDs and groups

- Every networked object is a `BaseNetworkable` with a **prefab ID** (`uint`, from `StringPool`) and a
  **networkable ID** (`NetworkableId`, a `ulong`).
- `StringPool` interns prefab paths/strings into numeric IDs used on the wire. `StringPool.Get(path)`
  returns the ID; the client resolves it against its own prefab manifest. **If the client does not
  have the prefab, it cannot spawn it.**
- Entities belong to a **network group** (`Proto.baseNetworkable.group`). Group 0 is the global
  group; non-zero groups scope visibility (used by instanced/session content).
- `BaseNetworkable.net` is the server-side network handle; `net.ID` is the `NetworkableId`.

### 5.2 Snapshots vs updates

When a player joins (or an entity becomes visible), the server sends a **snapshot**: the full
protobuf description of the entity (`ProtoBuf.Entity`, including `baseNetworkable`, `baseEntity`,
`baseCombat`, `basePlayer`, `parent`, `children`, `position`, `rotation`, flags, etc.).

After that, the server sends **delta updates**:

- `EntityPosition` for movement,
- `EntityDestroy` when removed,
- `RPCMessage` for entity RPCs,
- `Entities` for newly visible/changed entities,
- `ConsoleMessage`/`ConsoleCommand` for console traffic,
- `Effect` for one-shot visual/audio effects.

A plugin that changes an entity must call one of:

```csharp
entity.SendNetworkUpdate();                 // normal delta (queued)
entity.SendNetworkUpdateImmediate();        // force now (use sparingly)
entity.SendNetworkUpdate_Position();        // position/rotation only
```

If you only change server-side fields that are not in the protobuf, clients will not see anything —
and may desync. Change the proto-backed property (e.g. `Health`, `flags`, `skinID`) or send an RPC.

### 5.3 RPCs

RPCs are method calls addressed to an entity's network identity:

```
[RPCMessage][entity netId][rpc id][args...]
```

- The RPC id is a `uint` from `StringPool.Get("RpcName")` (the same string pool as prefabs).
- The server sends them with `BaseEntity.ClientRPC(RpcTarget target, string func, params object[] args)`
  or the `ClientRPCPlayer`/`ClientRPCEx` helpers.
- The client must have a matching `[RPC_Client]` method on the entity's behaviour; otherwise the RPC
  is dropped (and may log).
- Clients can send RPCs back; the server routes them by entity and `[RPC_Server]` method. **Validate
  everything**: the connection, the entity ownership, ranges, cooldowns and argument bounds.

For **UI**, the target is the `CommunityEntity` singleton, not a world entity:

```csharp
CommunityEntity.ServerInstance.ClientRPC(RpcTarget.Player("AddUI", player), json);
```

That is exactly what `CuiHelper.AddUi` does. You rarely need raw RPCs for UI.

### 5.4 Visibility

- `BasePlayer.net.connection` is the player's `Network.Connection`.
- `SendInfo` describes recipients: `new SendInfo(connection)`, `new SendInfo(connections)`, or a
  group/global send.
- `BaseNetworkable.SendAsSnapshot(connection)` sends the full state to one connection.
- `Network.Net.sv.connections` is the live connection list; do not cache it across frames.

Custom per-player entities (holograms, markers) should be spawned only to the players who need them:
network cost is per watcher, and clients keep ghost objects if you forget to kill them.

---

## 6. The wire protocol

### 6.1 Transport

Rust uses Steam's networking layer through Steamworks, wrapped by `Facepunch.Network`. The game
traffic is encrypted and authenticated by Steam; you cannot read it with a normal packet sniffer,
and you should not try.

Server-side, you operate **above** the transport: you receive `Network.Message` objects and send
through `Network.Net.sv`. Transport concerns (NAT punch, Steam relays, encryption) are handled for
you.

### 6.2 Protobuf messages

`Rust.Data.dll` contains the generated protobuf types (`ProtoBuf.*`). The wire format is protobuf;
`Message.Type` selects the payload:

| Type | Purpose |
|---|---|
| `Entities` | Entity snapshots/updates (a `ProtoBuf.Entity` per entity). |
| `EntityDestroy` | Remove entities by net ID. |
| `RPCMessage` | Entity RPC: net ID, rpc id, arguments. |
| `EntityPosition` | Position/rotation delta. |
| `ConsoleMessage` | Server → client console output. |
| `ConsoleCommand` | Console command execution (either direction). |
| `Effect` | One-shot effect event. |

(Exact enum names/order are build-specific; the RustRelay project documents numeric IDs and is a
good cross-check.)

### 6.3 RustRelay (observing the protocol)

Rust ships a **packet relay** (`RustRelay`) that can mirror server traffic to an external process
for tooling/analytics. It is not a mod API; it is a supported observability path.

Key facts from the wiki:

- Endpoints: `/ws/ingest`, `/api/{Packet,Snapshot,MapSnapshot,Manifest,StringPool,Auth/exchangeKey}`.
- Auth via `relay.cfg_*` convars and an exchanged key.
- Packets are framed with a 12-byte `RRTS` marker: `[int32 0x53545252][int64 time]`.
- Documented types include **5 Entities, 6 EntityDestroy, 9 RPCMessage, 10 EntityPosition,
  11 ConsoleMessage, 12 ConsoleCommand, 13 Effect**.
- Encryption: X25519 key exchange + AES-256-GCM, frame `[type][12-byte nonce][ciphertext][16-byte tag]`.
- A fake-player mode exists for testing consumers.

Use RustRelay when you need offline analysis (population heatmaps, damage logs, economy audits).
Do not build gameplay on it: it is a mirror with latency, not a control channel.

### 6.4 Adjacent protocols

| Protocol | Port | Use |
|---|---|---|
| Game (Steam) | 28015/UDP | Client/server gameplay. |
| RCON (WebSocket) | 28016/TCP | Admin commands, `ws://host:28016/<password>`. |
| Query | 28017/UDP (`1 + max(game, rcon)`) | Server browser/query. |
| Rust+ app | 28082/TCP (`game + 67`) | Companion app pairing. |

WebRCON is JSON over WebSocket:

```json
{"Identifier":1,"Message":"serverinfo","Name":"WebRcon"}
```

`+rcon.web 1` enables it (required on current builds); `rcon.web 0` is the deprecated legacy
protocol. There is no `wss://` — terminate TLS in a proxy if you need it.

---

## 7. Server → client communication

### 7.1 Chat and console

```csharp
player.ChatMessage("Hello");                              // Oxide/Carbon helper
player.SendConsoleCommand("chat.add", 2, player.userID, "Hello"); // vanilla path
player.SendConsoleCommand("chat.say", "/kit");            // make the client run a chat command
```

- `chat.add` writes a chat line locally on that client; it does not broadcast.
- `chat.say` executes a chat command **as the player**, which is useful from CUI buttons.
- Server-wide chat: `ConVar.Chat.Broadcast(message, username, color, userId)` or
  `server.Broadcast`.

### 7.2 CUI (the practical UI channel)

CUI is a JSON array sent to the client's `CommunityEntity` singleton. It requires no client assets
beyond the built-in UI prefabs.

```csharp
using Oxide.Game.Rust.Cui;
using UnityEngine;

var elements = new CuiElementContainer
{
    new CuiPanel
    {
        Image = { Color = "0.05 0.05 0.05 0.9" },
        RectTransform = { AnchorMin = "0.1 0.7", AnchorMax = "0.5 0.9" },
        CursorEnabled = true
    },
    new CuiLabel
    {
        Text = { Text = "Server panel", FontSize = 18, Align = TextAnchor.MiddleCenter },
        RectTransform = { AnchorMin = "0 0.6", AnchorMax = "1 1" }
    },
    new CuiButton
    {
        Button = { Command = "chat.say /myplugin close", Color = "0.2 0.6 0.2 0.9" },
        RectTransform = { AnchorMin = "0.3 0.1", AnchorMax = "0.7 0.5" },
        Text = { Text = "Close", FontSize = 14, Align = TextAnchor.MiddleCenter }
    }
};

CuiHelper.DestroyUi(player, "MyPluginPanel");
CuiHelper.AddUi(player, elements);
```

Rules:

- Buttons/inputs send **console** commands (`ConsoleNetwork.ClientRunOnServer`); use a console
  command or `chat.say /cmd` for chat commands.
- Server-side validate every action; CUI JSON is visible to the client.
- Destroy panels on unload/disconnect; CUI does not survive reconnect.
- Use Carbon protected commands for UI-only actions (`[ProtectedCommand]`).
- Images: built-in sprites, item icons, Steam avatars, raw PNG bytes or URLs. No asset bundles.

Full API: `CarbonMod.MD` §10.

### 7.3 Effects and sounds

```csharp
Effect.server.Run(
    "assets/bundled/prefabs/fx/impacts/additive/explosion.prefab",
    position,
    Vector3.up,
    null,          // connection: null = all in range
    false);
```

- Only **shipped** effect prefabs work; the client must already have them.
- `Effect.server.Run` is fire-and-forget; there is no handle to cancel it.
- Effects are cheap but not free: spamming them in a per-tick hook will hurt bandwidth.

### 7.4 Entity state

Prefer proto-backed state changes + `SendNetworkUpdate*`. For one-off data that is not part of the
entity proto, use an RPC to a client-side behaviour that exists on that prefab. You cannot add a new
client behaviour; you can only call methods that stock components already expose.

### 7.5 External channels

For data that must leave the game process (Discord, web dashboards, cross-server), use:

- **HTTP** from the plugin (allow-list hosts, timeouts, retries, secrets from Vault),
- **Carbon.Bridge** (WebSocket, password required, server↔server),
- **RCON/WebRCON** (admin/automation; not for gameplay traffic),
- **RustRelay** (read-only observability).

Do not block the main thread on any of these.

---

## 8. Client → server communication

### 8.1 Chat and console commands

Players can type chat (`/cmd`) and open the F1 console (`cmd args`). Both arrive as server-side
commands:

- chat → `[ChatCommand]` / `[Command]`,
- F1/RCon → `[ConsoleCommand]` / `[Command]`.

Treat every argument as hostile: length limits, numeric parsing (`TryParse`), allow-lists, range
checks, ownership checks. A `BasePlayer` can be null for console/RCon callers; handle it.

### 8.2 RPCs

Client RPC handlers on stock prefabs exist for movement, buttons, doors, lights, etc. If you patch
or replace one:

- check `packet.connection` is the expected player,
- check the entity still exists and is owned/usable by that player,
- clamp distances/intervals,
- never deserialize unbounded data,
- log anomalies without spamming the log.

`ClientEntity.OnRpc` is only called when the sending connection is in the entity's watchers, but you
must still validate arguments.

### 8.3 What the client can lie about

Everything: position (within server tolerance), aim, inventory actions, button presses, chat text,
RPC arguments, command names. Server-side checks are the only defence. Anti-cheat catches some
client tampering, but a plugin must not assume it.

---

## 9. Spawning, pooling and sharing

### 9.1 Creating entities

```csharp
var entity = GameManager.server.CreateEntity(
    "assets/prefabs/deployable/chair/chair.deployed.prefab",
    position,
    rotation);

if (entity != null)
{
    entity.Spawn();
}
```

- `CreateEntity` resolves the prefab from the server's manifest. If the path is unknown, it returns
  null (log it once, not every tick).
- `Spawn()` adds it to the world and networks it to watchers.
- Set proto-backed properties **before** `Spawn()` when possible; otherwise call
  `SendNetworkUpdate()` after changes.
- `Kill()`/`Kill(DestroyMode)` removes it and sends `EntityDestroy`.

### 9.2 Pooling (memory, not entities)

Rust does **not** pool spawned `GameObject` entities for reuse; spawn/kill is the model. What you
should pool are temporary managed objects:

```csharp
using Facepunch;

var list = Pool.Get<List<BasePlayer>>();
try
{
    // fill/use
}
finally
{
    Pool.Free(ref list);
}
```

- `Facepunch.Pool` reuses `List<>`, `Dictionary<>`, `StringBuilder`, etc. Return in `finally`.
- `StringPool` interns strings/prefab names to numeric IDs; use `StringPool.Get` instead of
  re-hashing paths in hot loops.
- Do not pool objects that escape to another thread or outlive the frame.
- Carbon's `HookCaller` already pools its argument buffers; don't add another layer in hooks.

### 9.3 Per-player entities vs shared entities

| Need | Use | Cost |
|---|---|---|
| Everyone sees the same object | normal `BaseEntity` + `Spawn()` | one entity, updates to all watchers |
| Only some players see it | `ClientEntity` (Carbon) or network group | one synthetic entity per connection/group |
| Visual-only marker/hologram | `ClientEntity` | no server logic, cheap proto |
| Persistent gameplay object | `BaseEntity` + save | participates in saves/queries |
| UI only | CUI | no entity at all |

`ClientEntity` is "braindead": the server does not tick it, it has no inventory/combat, and it must
be disposed. Use it for per-player props, not for economy storage.

### 9.4 Sharing spawns between plugins

Two plugins spawning the same marker at the same position cause duplicates and double network cost.
Use a small registry:

```csharp
public static class MarkerRegistry
{
    private static readonly Dictionary<ulong, ClientEntity> ByPlayer = new();

    public static ClientEntity GetOrCreate(BasePlayer player, Func<ClientEntity> factory)
    {
        if (ByPlayer.TryGetValue(player.userID, out var existing) && existing != null)
        {
            return existing;
        }

        var created = factory();
        ByPlayer[player.userID] = created;
        return created;
    }

    public static void Remove(ulong userId)
    {
        if (ByPlayer.Remove(userId, out var entity))
        {
            entity?.Dispose();
        }
    }
}
```

- Key by player/entity ID, not by prefab name.
- Clean up on disconnect/death/unload.
- If multiple plugins need the same world entity, expose a service/extension rather than spawning
  your own copy.
- For shared UI, use unique panel names and check `CuiHelper.ActivePanels` before drawing.

### 9.5 Prefab sharing

- Reuse the **same prefab path strings** (constants) so `StringPool` IDs are stable and cacheable.
- `GameManager.server.CreateEntity` does the prefab lookup for you; do not load bundles manually.
- Prefab paths are case-sensitive on Linux; test on the target OS.
- If a prefab is missing after a Rust update, fail gracefully (log once, disable the feature).

---

## 10. Custom assets

### 10.1 Why the stock client cannot use them

The shipped client is IL2CPP-compiled and EAC-protected. It has a fixed prefab manifest and a fixed
set of loaded assets. There is no supported "download this bundle and register this prefab" path for
an unmodded client. If the server sends a prefab ID the client does not know, the client cannot
instantiate it.

Consequences:

- You cannot ship a new model/texture/sound and have vanilla players see/hear it.
- You cannot stream a custom map or monument to clients.
- You cannot add a new client-side component/behaviour.

### 10.2 What you can do instead

| Goal | Practical approach |
|---|---|
| New visual prop | Reuse an existing prefab and recolor/reskin where supported (`skinID` on items). |
| Custom UI art | CUI: built-in sprites, item icons, raw PNG, URL images, Steam avatars. |
| Custom sound | Play a shipped effect/sound prefab; cannot add new audio. |
| Custom item | Use an existing item definition + skin/name/description; new item defs need client assets. |
| Custom map content | Use existing monuments/prefabs; map editing is a separate server-side pipeline. |
| Rich interaction | CUI buttons + server commands; no client scripting. |

### 10.3 Skins and workshop content

- Workshop skins are downloaded by the client through Steam; the server can reference a `skinID`
  that the client already owns/has cached. This is the only "new art" most servers can rely on.
- Do not assume every client has a given skin; fall back gracefully.
- Server-side "skin" swaps are cosmetic and client-validated only insofar as the client has the
  asset.

### 10.4 ServerPrefabs and prefab libraries

Community libraries (e.g. "ServerPrefabs") package **server-side spawn code** for prefabs that
already exist on clients. They do not add assets; they add convenience and validated paths. Treat
third-party prefab lists as build-specific: verify each path after a Rust update.

### 10.5 If you truly need client assets

You need a **client mod**:

- Doorstop/Unity Doorstop-style loader,
- EAC disabled (or a launcher that bypasses it — not permitted on official/competitive servers),
- an AssetBundle loader that registers prefabs before the client needs them,
- versioned bundles matching the client build.

That is outside the scope of a server plugin and cannot be assumed for public servers. Carbon for
Client exists but is a separate, evolving project with the same EAC/asset constraints; do not design
a server plugin around it.

---

## 11. Client mods and why they are different

| Aspect | Server plugin | Client mod |
|---|---|---|
| Runs on | Dedicated server process | Player's game client |
| Language | C# (Carbon/Harmony) | C#/IL2CPP patching via Doorstop, or native |
| EAC | Unaffected | Must be off/disabled |
| Assets | Existing client assets only | Can load bundles locally (if loader present) |
| Distribution | `.cs`/DLL to server | DLL/bundle to each player |
| Trust | Full server trust | Huge attack surface; never ship secrets |
| Use case | Gameplay, economy, admin | HUD/QoL/visuals |

A server plugin can **detect** that a client is running a mod only indirectly (e.g. custom RPC
handshake), and that is spoofable. Do not build enforcement on it.

---

## 12. Debugging network behaviour

- **Server log first.** Most "nothing happens" bugs are null prefabs, wrong RPC names or exceptions
  swallowed in a hook. Carbon logs hook exceptions with the plugin name.
- **Verify prefab IDs.** `StringPool.Get(path)` returns 0/unknown if the path is wrong; log the path
  and the result once at startup.
- **Check visibility.** An entity spawned with a network group is invisible outside that group; an
  entity killed on the server but cached on the client leaves a ghost until `EntityDestroy`.
- **RPC names are strings.** A typo silently does nothing. Log `StringPool.Get(func)` and compare
  with the client handler name.
- **Use RustRelay for ground truth.** Capture a window of packets and inspect `EntityPosition` /
  `RPCMessage` traffic; this distinguishes "server never sent it" from "client ignored it".
- **Do not sniff Steam traffic**; it is encrypted and out of scope.
- **Test with a real client on the target branch** (staging/aux differences are real).

### 12.1 Server exceptions vs plugin exceptions

Network symptoms often start as a managed exception. Distinguish the source before debugging
packets:

| Signal | Likely source | Where to look |
|---|---|---|
| `Failed to call hook '...' on plugin '...'` | Plugin hook threw; Carbon caught it | `carbon/logs/Carbon.Core.log`, plugin exception log |
| `Failed processing a timer of ...` | Timer callback threw; **timer was destroyed** | Plugin exception log; the repeating task is gone |
| `Failed executing chat/console command ...` | Command callback threw | Plugin exception log / `Carbon.Core.log` |
| `NullReferenceException` in Unity log, no plugin name | Unity callback or patch | `Application.logMessageReceivedThreaded` guard; `LogType.Exception` |
| Server process exits | Unhandled exception on a raw thread / native crash | `AppDomain.UnhandledException` crash file, OS core dump, `RustDedicated` output |
| Entity state wrong after an error | Partial mutation before throw | The boundary's rollback path; add try/catch + rollback |

- Carbon's containment prevents most crashes but **skips the hook's work**; a failed `Can*` hook may
  allow an action. See `Exceptions.md` §3.
- A throwing Harmony patch is **not** caught by Carbon's hook containment; without a finalizer it
  propagates into the game method's caller.
- A raw background thread exception can terminate the process; use `Safe`/guarded tasks.

### 12.2 Common failure modes

| Symptom | Likely cause |
|---|---|
| Entity invisible to one player | Not in watchers / wrong network group. |
| Ghost entities after despawn | `Kill` not called or `EntityDestroy` dropped. |
| UI does nothing | Button command is a chat command without `chat.say`, or panel destroyed. |
| RPC ignored | Wrong RPC id/name, entity not on client, or handler not present. |
| Server hitch on spawn | Spawning many entities in one frame; batch over ticks. |
| Desync after plugin change | Proto field changed without `SendNetworkUpdate`. |

---

## 13. Performance and security checklist

### 13.1 Performance

- [ ] No blocking I/O in hooks or entity ticks.
- [ ] Batch spawns/kills over frames; cap entities per tick.
- [ ] Spawn per-player entities only to watchers.
- [ ] Pool temporary lists/dicts; return in `finally`.
- [ ] Cache prefab IDs and `StringPool` lookups.
- [ ] Throttle CUI updates; update, don't rebuild.
- [ ] Avoid per-tick `BasePlayer.Find*` scans; keep indexes.
- [ ] Profile with `c.profile` before optimizing.
- [ ] No unguarded raw threads; every hook/timer/task/patch is wrapped (`Exceptions.md`).
- [ ] Exception log enabled and reviewed; no per-tick exception spam.

### 13.2 Security

- [ ] Validate every client RPC/command (connection, ownership, range, cooldown).
- [ ] Never trust client-supplied net IDs, item IDs or positions.
- [ ] Keep RCON bound to localhost/VPN; long password; no `wss` assumption.
- [ ] Do not log secrets or full packet dumps containing player data.
- [ ] Treat third-party DLLs (HarmonyMods/extensions) as full-trust code.
- [ ] Rate-limit UI/commands; CUI spam is a client-side DoS vector.

---

## 14. Sources

Checked 2026-10-17.

- **RustRelay** — <https://wiki.facepunch.com/rust/RustRelay> (endpoints, packet types, `RRTS`
  framing, X25519/AES-256-GCM, convar config, fake-player mode).
- **Protobuf** — <https://wiki.facepunch.com/rust/Protobuf> ("the protobuf is exactly what goes over
  the wire; changing it changes what is transmitted, not the entity").
- **Hooks** — <https://wiki.facepunch.com/rust/Hooks> (server hook return semantics).
- **Facepunch/Rust.Community** — `CommunityEntity.cs` (`SendDestroyUIs`), `CommunityEntity.UI.cs`
  (`[RPC_Client] AddUI/DestroyUI/DestroyUIs`, button → `ConsoleNetwork.ClientRunOnServer`, input →
  `cmd + " " + value`).
- **OxideMod/Oxide.Rust** — `src/RustCui.cs`, `src/RustPlugin.cs` (CUI helpers, `SendConsoleCommand`).
- **Carbon** — `Components/ClientEntity.cs` (proto entity, `SpawnFor`, `OnRpc`, watchers),
  `Oxide/CUI/CuiHelper.cs` (`AddUi` → `CommunityEntity.ServerInstance.ClientRPC`),
  `Carbon.Common/src/Carbon/CarbonPlugin.cs` (`CuiHandler`, `CreateCUI`).
- **Rust news, "Built Different"** (June 2026) and the Unity 6 upgrade coverage — engine jump,
  minimum Windows versions, high-polling-rate mouse issue, single-threaded server.
- **Rust update cadence** — first Thursday of each month (forced update/wipe); community trackers
  (Corrosion Hour, Rustafied).
- **WebRCON** — <https://github.com/Facepunch/webrcon> and server-setup guides (`+rcon.web 1`,
  `ws://host:28016/<password>`, JSON envelope, ports 28015/28016/28017/28082).
- **Custom prefabs/assets threads** (Codefling, uMod, Reddit) — consensus: server plugins cannot add
  client assets; prefab must exist client-side; Carbon-for-Client requires Doorstop + EAC off.
- **Facepunch/Rust.HarmonyMods** — HarmonyMod loader conventions, `HarmonyMods_Data`,
  `HarmonyConfig` (community convention).

> **Recency note:** the core protocol pages (RustRelay/Protobuf/Hooks) are current as of 2026; the
> custom-asset limitations reflect the IL2CPP/EAC client and have been stable for years. Vanilla
> method signatures are build-specific — re-verify after each forced update.
