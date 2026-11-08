# Agent team and task ownership

## Roles

Definitions live in `Zaagl-Backend/.claude/agents/`. A Claude Code session started in `Zaagl-Backend` can launch them by type (`zaagl-backend` and so on); each gets its own card on the dashboard (AGENT_DASHBOARD.md).

| Agent type | Role | Owns | Never does |
|---|---|---|---|
| main session | Lead architect and orchestrator | Plan, integration decisions, merges, STATUS.md, SESSION_HANDOFF.md, final acceptance of tasks | Implements whole features alone when a specialist owns them |
| `zaagl-architect` | Principal architect | ARC tasks: contracts C1–C12, ADRs, cross-module design reviews, architecture tests | Ships feature code |
| `zaagl-backend` | Senior backend engineer | BE tasks: modules, migrations, APIs, jobs, backend tests | Edits frontend or infra files |
| `zaagl-frontend` | Senior frontend engineer | FE tasks: pages, components, BFF, i18n, frontend tests | Edits backend code |
| `zaagl-devops` | Senior DevOps / SRE | OPS tasks: compose, Nginx, storage, CI/CD, secrets, backups, monitoring | Changes application logic |
| `zaagl-desktop-cli` | Desktop and CLI engineer | DSK and CLI tasks: Tauri app, AOT CLI, signing, distribution | Changes backend contracts (requests them instead) |
| `zaagl-qa` | Independent QA and security | QA tasks: test plans, test harnesses, security suites, gate evidence, defect reports | Implements product features or marks its own fixes verified |

## Task ownership

| Agent | Tasks | IDs |
|---|---|---|
| Architect | 18 | ARC-001 … ARC-018 |
| Backend | 31 | BE-001 … BE-031 |
| Frontend | 29 | FE-001 … FE-029 |
| DevOps | 32 | OPS-001 … OPS-032 |
| Desktop / CLI | 37 | DSK-001 … DSK-022, CLI-001 … CLI-015 |
| QA | 48 | QA-001 … QA-048 |
| **Total** | **195** | |

Each task has exactly one owner. A task that needs another agent's work names it as a dependency instead of sharing ownership.

## File ownership (to keep parallel agents from editing the same files)

| Path | Owner | Others may |
|---|---|---|
| `Zaagl-Backend/src/Modules/{Resources,Snippets,Files,Media}/**` | backend | read |
| `Zaagl-Backend/src/Modules/{Links,Access,Billing,Analytics,Identity}/**` (Phase 1 extensions) | backend, one task at a time per module | read |
| `Zaagl-Backend/src/Modules/*/Contracts/**` | the module owner, after an architect-approved contract change | read |
| `Zaagl-Backend/src/Zaagl.Api/Program.cs`, `Endpoints/**` | backend | read |
| `Zaagl-Backend/src/Zaagl.Worker/**` | backend | read |
| `Zaagl-Backend/tests/**` (product tests) | backend | QA reviews |
| `Zaagl-Backend/tests/Zaagl.Qa.*/**`, `tests/corpus/**`, `tools/traceability-check/**` | QA | read |
| `Zaagl-Backend/deploy/**`, `docker-compose*.yml`, `**/Dockerfile`, `.github/workflows/**` | devops | backend proposes changes by PR comment |
| `Zaagl-Frontend/src/**` | frontend | read |
| `Zaagl-Frontend/e2e/**` | QA (security and gate suites), frontend (feature E2E) | — |
| `zaagl-desktop/**`, `zaagl-cli/**` (new repos, PD-21) | desktop-cli | read |
| `docs/phase1-implementation/{IMPLEMENTATION_PLAN,STATUS,SESSION_HANDOFF,DECISIONS,AGENT_TASKS}.md` | lead architect | agents report; the lead writes |
| `docs/phase1-implementation/<topic>.md` | the agent named in its header | the lead adds consolidation notes |

For parallel implementation, each agent works on its own branch (`p1/<agent>/<task-id>`) or git worktree. The lead merges after review.

## Parallelism

| Stage | Runs in parallel | Must be sequential |
|---|---|---|
| 1 | Backend Phase 0 fixes (BE-001, BE-030, BE-031); frontend tooling and BFF (FE-001…FE-006, FE-027, FE-029); DevOps CI (OPS-003…OPS-006); QA test isolation and safety nets (QA-001…QA-011) | Contracts (ARC-001) before BE-006/BE-007; plan model (ARC-005 → BE-003) after PD-04 |
| 2 and 3 | Stage 2 (snippets) and Stage 3 (uploads, media) as two streams once Stage 1 passes; within each, backend and frontend in parallel against the contract with mocked APIs (MSW) | Integration, then QA gate |
| 4 | Burn, expiry and quotas (backend) with the download page (frontend) | Burn race suite after BE-018 |
| 5 | Desktop and CLI with backend auth support | Signing after PD-20 procurement |
| 6 | QA leads; engineers fix in parallel | Retest after each fix |

Do not run agents in parallel on tightly coupled work, such as one module's contract and its first implementation.

## Shared contracts (agree in Stage 1, before dependent work)

C1–C12 in ARCHITECTURE.md §5. Owners: Links (C1, C10, C12 with Files), Resources (C2, C10), architect + DevOps (C3), Access (C4), Billing (C5), Analytics (C6), Files (C7, C8, C12), architect review of the public API (C9), Identity (C11).

## Integration checkpoints

1. **Contract freeze** (end of ARC-001): stubs compile, consuming agents have reviewed them.
2. **Per stage:** backend and frontend merged on one branch, OpenAPI updated, generated clients compile, CI green, then QA starts the gate.
3. **Before Stage 6:** all stages merged; full Phase 0 regression run.

## Review requirements

- Every merge: the lead architect reviews; security-relevant changes (auth, uploads, delivery, burn, sanitising) also need QA review.
- QA results are recorded as PASS, FAIL, BLOCKED or NOT_TESTED with evidence (command, output, file and line).
- An agent never approves its own work.
