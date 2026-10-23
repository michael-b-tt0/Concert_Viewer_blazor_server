namespace Concert_Viewer.Services;

// All application writes and scraper runs share this process-wide lease.
public sealed class OperationGate
{
    private readonly object sync = new();
    private string? operation;
    public string? Operation { get { lock (sync) return operation; } }
    public bool IsBusy => Operation is not null;

    public IDisposable Enter(string name)
    {
        lock (sync)
        {
            if (operation is not null)
                throw new InvalidOperationException($"Please finish {operation} first.");
            operation = name;
            return new Lease(this);
        }
    }

    private sealed class Lease(OperationGate gate) : IDisposable
    {
        private int disposed;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) == 0)
                lock (gate.sync) gate.operation = null;
        }
    }
}
