namespace Zaagl.SharedKernel.Testing;

/// <summary>
/// Named points in production code that a test can pause, crash or throw at, to drive race and
/// crash-ordering scenarios deterministically (spec §8.3). <see cref="NoOpFailpoints"/> is the only
/// implementation registered outside the <c>Testing</c> environment; a startup check in each host's
/// <c>Program.cs</c> refuses to run if any other implementation is registered elsewhere (spec MUST-NOT
/// #33: "Ship a build that registers a non-no-op failpoint implementation... outside Testing").
/// </summary>
public interface IFailpoints
{
    Task HitAsync(string name, CancellationToken ct = default);
}

public sealed class NoOpFailpoints : IFailpoints
{
    public Task HitAsync(string name, CancellationToken ct = default) => Task.CompletedTask;
}
