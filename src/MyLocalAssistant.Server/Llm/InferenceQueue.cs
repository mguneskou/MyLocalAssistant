namespace MyLocalAssistant.Server.Llm;

/// <summary>
/// Single-slot FIFO that serialises chat generations on the one loaded model
/// (LLamaSharp StatelessExecutor is not safe for parallel inference on the same weights).
/// </summary>
public sealed class InferenceQueue
{
    private readonly SemaphoreSlim _slot = new(1, 1);

    public int Waiting => _waitingField;

    public async Task<IDisposable> AcquireAsync(CancellationToken ct)
    {
        Interlocked.Increment(ref _waitingField);
        try
        {
            await _slot.WaitAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            Interlocked.Decrement(ref _waitingField);
        }
        return new Lease(_slot);
    }

    private int _waitingField;

    private sealed class Lease(SemaphoreSlim slot) : IDisposable
    {
        private int _disposed;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
                slot.Release();
        }
    }
}
