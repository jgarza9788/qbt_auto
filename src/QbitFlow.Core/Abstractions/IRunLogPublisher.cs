using QbitFlow.Core.Domain;

namespace QbitFlow.Core.Abstractions;

/// <summary>Live fan-out of run log lines to any UI stream. The DB write is separate.</summary>
public interface IRunLogPublisher
{
    void Publish(Guid runId, RunLogEntry entry);
    void Complete(Guid runId);
}

public sealed class NullRunLogPublisher : IRunLogPublisher
{
    public void Publish(Guid runId, RunLogEntry entry) { }
    public void Complete(Guid runId) { }
}
