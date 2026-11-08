# Agent dashboard

The local pixel-art dashboard that shows each Claude Code subagent working on Zaagl as its own card.

| | |
|---|---|
| Location | `C:\Users\pc\Downloads\agent-pixel-dashboard\` (not part of either Zaagl repo) |
| URL | http://localhost:4317 (bound to `127.0.0.1` only; not reachable from the network) |
| Watched project | `C:\Users\pc\Zaagl-Backend` |
| Requirements | Node.js 16+ on `PATH` (installed: Node 24.12) |
| State | In memory only; nothing is written to disk. A restart resets every card to idle. |

## Start, stop, restart

Start (from any shell; keep it running):

```
node "C:\Users\pc\Downloads\agent-pixel-dashboard\server.js" "C:\Users\pc\Zaagl-Backend"
```

Stop (PowerShell):

```
Stop-Process -Id (Get-NetTCPConnection -LocalPort 4317 -State Listen).OwningProcess
```

Restart = stop + start. Configuration lives in the project (`.claude/agents/*.md`, `.claude/agent-dashboard.config.json`, `.claude/settings.json`), so nothing is lost on restart. After editing the config without restarting: `curl -X POST http://127.0.0.1:4317/reload-config`.

Debug logging of incoming hook event fields: set `AGENT_DASHBOARD_DEBUG=1` before starting.

## How it is wired

`node install.js "C:\Users\pc\Zaagl-Backend"` (run 2026-11-09) did two things and touched nothing else:

1. Copied `hooks/notify.js` to `Zaagl-Backend\.claude\hooks\agent-dashboard-notify.js`. It forwards the hook's JSON to `127.0.0.1:4317/event` with a 200 ms timeout, prints nothing and always exits 0, so it can never block or change a tool call.
2. Added one entry each to `SubagentStart`, `PreToolUse`, `PostToolUse`, `PostToolUseFailure` and `SubagentStop` in `Zaagl-Backend\.claude\settings.json`. Existing permissions were kept (41 entries before and after). A backup of the previous file is `settings.json.bak`.

Project agent definitions (`Zaagl-Backend\.claude\agents\`), each with its own card, colour and en/ar label in `.claude\agent-dashboard.config.json`:

| Agent type | Role |
|---|---|
| `zaagl-architect` | Principal Architect / lead orchestrator |
| `zaagl-frontend` | Senior Frontend Engineer |
| `zaagl-backend` | Senior Backend Engineer |
| `zaagl-devops` | Senior DevOps / SRE |
| `zaagl-desktop-cli` | Desktop & CLI Engineer |
| `zaagl-qa` | Independent QA & Security |

## Event mapping

| Hook event | Card effect |
|---|---|
| `SubagentStart` | working, "Starting…" |
| `PreToolUse` / `PostToolUse` | working, task line = short summary of the tool call (file, command or pattern) |
| `PostToolUseFailure` | error with the message; back to idle after 30 s |
| `SubagentStop` | done with the agent's last message; back to idle after 20 s |
| Main session's `PostToolUse` of the `Agent` tool | names the new agent's card (see below) |

Only events that carry `agent_type` (subagents) move a card; the main session's own tool calls are ignored.

### Card naming (changes made to `server.js` on 2026-11-09)

The original server keyed cards by `agent_type`, so parallel agents of the same type (all `general-purpose`) shared one card. Changes:

1. **Loopback only**: `server.listen(PORT, '127.0.0.1')`; it previously listened on every interface.
2. **One card per running agent**: events are keyed by `agent_id` (present on every subagent event; verified with debug logging). Agents of a type that has its own definition file keep that single card; generic types get `general-purpose · <first 6 chars of id>`.
3. **Automatic names**: when the main session's `Agent` call completes, the server reads the launched agent's id from the result, and names the card after the role file the prompt tells it to adopt (`.claude/agents/<name>.md`), else after `subagent_type`, else after the description.
4. **Role-file detection**: a subagent that reads `.claude/agents/<name>.md` is shown on that role's card.
5. **`POST /alias`** `{"agentId": "...", "name": "zaagl-backend"}` names an agent the server did not see launch (used once for the six planning agents started before change 3).

## Validation evidence (2026-11-09)

| Check | Result | Evidence |
|---|---|---|
| Server reachable | PASS | `GET /` 200; `netstat` shows `127.0.0.1:4317 LISTENING` only |
| Agents loaded from definitions | PASS | `GET /state` lists the six `zaagl-*` cards with en and ar roles |
| English / Arabic UI | PASS (structure) | Page has the AR/EN toggle and `dir=rtl` switch; labels have both languages. Visual check by the user in the browser. |
| Real subagent events received | PASS | Six real planning subagents produced `PreToolUse`/`PostToolUse` events; debug log showed `agent_id`, `agent_type`, `tool_name`, `tool_input` |
| Concurrent agents as distinct cards | PASS | `/state` showed five `general-purpose · <id>` cards working at the same time, then named cards after `/alias` |
| Working / done states | PASS | Cards showed working with live task lines; a finished agent showed DONE with its last message |
| Error state | NOT_TESTED | No tool failure occurred during planning |
| Hooks active without restart | PASS | Hooks installed mid-session were picked up by this session |
| New agent types usable | BLOCKED until next session | `zaagl-*` types are loaded only at session start; planning agents ran as `general-purpose` with the role file as their first read |

## Known behaviour

- A card updates only when its agent makes a tool call. An agent that is thinking or writing a long file shows its last state (or idle after a server restart) until its next call.
- Every Claude Code session in `Zaagl-Backend` reports to the dashboard, so subagents of another open session also get cards.
- A restart clears all state; running agents reappear on their next tool call (named automatically only if their launch happened after the restart, otherwise use `/alias`).

## Troubleshooting

| Symptom | Check |
|---|---|
| Page shows "offline" | Is the server running? `curl http://127.0.0.1:4317/state` |
| No cards move | Hooks present in `Zaagl-Backend\.claude\settings.json`? Is the session started in `Zaagl-Backend`? |
| All agents on one card | Old `server.js` without the per-agent changes, or agents launched before a restart (use `/alias`) |
| Port in use | Another instance is running; stop it or set `AGENT_DASHBOARD_PORT` |

## Uninstall

Remove the hook entries whose command contains `agent-dashboard-notify.js` from `Zaagl-Backend\.claude\settings.json` and delete `.claude\hooks\agent-dashboard-notify.js`.
