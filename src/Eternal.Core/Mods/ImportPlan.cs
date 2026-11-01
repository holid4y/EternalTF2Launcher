using Eternal.Core.Engine;

namespace Eternal.Core.Mods;

/// <summary>
/// A download or folder looked at before anything reaches the library: what
/// parts it holds and which of them change the same files. Archives stay
/// unpacked in staging until the plan is disposed.
/// </summary>
public sealed class ImportPlan(string source, string path, string? staging, Inspection inspection) : IDisposable
{
    /// <summary>What the player picked, for messages.</summary>
    public string Source { get; } = source;

    /// <summary>What the engine imports: the file or folder itself, or where an archive was unpacked.</summary>
    public string Path { get; } = path;

    public bool Unpacked => staging is not null;
    public Inspection Inspection { get; } = inspection;
    public IReadOnlyList<ImportPart> Parts => Inspection.Parts;

    /// <summary>
    /// Parts that change the same files are alternatives, directly or through
    /// each other; of each such group the part with the most files is picked.
    /// Everything else is picked. Order follows <paramref name="parts"/>.
    /// </summary>
    public static IReadOnlyList<string> DefaultChoice(IReadOnlyList<ImportPart> parts)
    {
        var bySource = parts.GroupBy(p => p.Source).ToDictionary(g => g.Key, g => g.First());
        var group = new Dictionary<string, int>();
        var next = 0;
        foreach (var part in parts)
        {
            if (!group.TryAdd(part.Source, next))
                continue;
            var queue = new Queue<ImportPart>([part]);
            while (queue.TryDequeue(out var current))
            {
                foreach (var other in current.Conflicts)
                {
                    if (bySource.TryGetValue(other, out var found) && group.TryAdd(other, next))
                        queue.Enqueue(found);
                }
            }
            next++;
        }
        var picked = parts.GroupBy(p => group[p.Source])
            .Select(g => g.Aggregate((best, p) => p.Files > best.Files ? p : best).Source)
            .ToHashSet();
        return parts.Where(p => picked.Contains(p.Source)).Select(p => p.Source).Distinct().ToArray();
    }

    public void Dispose()
    {
        if (staging is not null)
            ArchiveIngest.TryDelete(staging);
    }
}
