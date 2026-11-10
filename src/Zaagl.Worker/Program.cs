using Npgsql;
using Zaagl.Modules.Billing;
using Zaagl.Modules.Files;
using Zaagl.Modules.Links;
using Zaagl.Modules.Resources;
using Zaagl.Worker;

// ARC-012 / BE-013: the background worker for Phase 1 content jobs (task tables claimed with FOR UPDATE SKIP
// LOCKED). It exposes no endpoint, runs no migration (the API's MigrationHostedService owns them) and registers
// no Phase 0 hosted service: those stay in every API instance (cache listener, click buffer, creation gate) or
// in the API for Stage 1 (purge, safety re-check, email). Only module *services* plus the task runners live here.
var builder = Host.CreateApplicationBuilder(args);
var config = builder.Configuration;

// Own small pool (ARC-012 §5): ConnectionStrings:Jobs, else Postgres, capped at Jobs:MaxPoolSize (default 10),
// so a burst of jobs can never take connections the API's redirect pool needs.
var jobs = new[] { config.GetConnectionString("Jobs"), config.GetConnectionString("Postgres") }.FirstOrDefault(s => !string.IsNullOrWhiteSpace(s))
    ?? throw new InvalidOperationException("ConnectionStrings:Jobs (or Postgres) is not configured.");
var pool = new NpgsqlConnectionStringBuilder(jobs) { MaxPoolSize = config.GetValue("Jobs:MaxPoolSize", 10) };
config["ConnectionStrings:Postgres"] = pool.ConnectionString;

builder.Services.Configure<HostOptions>(o => o.ShutdownTimeout = TimeSpan.FromSeconds(60));
builder.Services.AddSingleton(TimeProvider.System);

// The scan task (ARC-009 section 7) needs the resource registry and the quota ledger (Resources, worker slice: no tickets, no
// owner routes) and the plan provider the ledger is built on (Billing without its jobs). Scan:Provider=Fake is refused
// outside Development (fail-closed scanning).
builder.Services
    .AddBillingModule(config, withJobs: false)
    .AddLinksTakedown(config)
    .AddResourcesModule(config, workerOnly: true)
    .AddFilesModule(config)
    .AddResourceJobs()
    .AddFilesTaskRunner(config, builder.Environment.IsDevelopment());

builder.Services.AddHostedService<LivenessFile>();

var host = builder.Build();
host.Run();
