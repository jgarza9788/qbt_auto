using Qbitflow.Core.Domain;

namespace Qbitflow.Sources.Concurrency;

public static class ParallelismMapping
{
    public static int WorkerCount(ParallelismLevel level) => level switch
    {
        ParallelismLevel.Low => 2,
        ParallelismLevel.Medium => 4,
        ParallelismLevel.High => 8,
        ParallelismLevel.VeryHigh => 16,
        _ => 4
    };
}
