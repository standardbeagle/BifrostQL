namespace BifrostQL.Mcp
{
    /// <summary>
    /// The one loaded declarative tool document. The first <see cref="GetAsync"/> starts the async
    /// read; every later caller awaits the same task, so the document is read once whichever of the
    /// validator (host start), the stdio adapter or an HTTP session asks first. A failed load stays
    /// failed: the validator surfaces it at host start, which then refuses to run.
    /// </summary>
    public sealed class DeclarativeToolDocumentProvider(DeclarativeToolDocumentLoader loader)
    {
        private Task<DeclarativeToolDocument>? _load;

        public Task<DeclarativeToolDocument> GetAsync(CancellationToken cancellationToken)
        {
            var load = Volatile.Read(ref _load);
            if (load is null)
            {
                // Publish an unstarted completion first and let only the winner run the read:
                // lock-free, and a lost race never reads the document twice.
                var pending = new TaskCompletionSource<DeclarativeToolDocument>(TaskCreationOptions.RunContinuationsAsynchronously);
                load = Interlocked.CompareExchange(ref _load, pending.Task, null) ?? pending.Task;
                if (ReferenceEquals(load, pending.Task))
                    _ = CompleteAsync(pending);
            }
            // The shared read ignores any one caller's token; a caller that gives up only stops waiting.
            return load.WaitAsync(cancellationToken);
        }

        private async Task CompleteAsync(TaskCompletionSource<DeclarativeToolDocument> pending)
        {
            try
            {
                pending.SetResult(await loader.LoadAsync(CancellationToken.None));
            }
            catch (Exception ex)
            {
                pending.SetException(ex);
            }
        }
    }
}
