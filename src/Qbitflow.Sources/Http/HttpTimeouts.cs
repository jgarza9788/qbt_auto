namespace Qbitflow.Sources.Http;

internal static class HttpTimeouts
{
    public static CancellationTokenSource Create(CancellationToken parent, int timeoutSeconds)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(parent);
        cts.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, timeoutSeconds)));
        return cts;
    }
}
