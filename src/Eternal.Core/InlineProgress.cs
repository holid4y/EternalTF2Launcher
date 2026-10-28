namespace Eternal.Core;

/// <summary>
/// Reports on the caller's thread. <see cref="Progress{T}"/> on a background
/// thread queues each report to the thread pool, where it can land after the
/// work it describes has already finished.
/// </summary>
internal sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
{
    public void Report(T value) => report(value);
}
