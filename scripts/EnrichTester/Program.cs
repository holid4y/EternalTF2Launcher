using System;
using System.Threading.Tasks;
using LauncherTF2.Services;
using LauncherTF2.Models;

class Program
{
    static async Task<int> Main()
    {
        try
        {
            var svc = new GameBananaEnrichmentService();

            var names = new[] { "skyboxpack", "femmepyroedit2026_v1_6_2" };

            foreach (var n in names)
            {
                Console.WriteLine($"--- Enriching: {n}");
                var queries = svc.GetSearchQueries(n);
                Console.WriteLine("Queries:");
                foreach (var q in queries) Console.WriteLine("  " + q);
                var mod = new ModModel { Name = n };
                await svc.EnrichModAsync(mod);
                Console.WriteLine($"IsEnriched: {mod.IsEnriched}");
                Console.WriteLine($"ThumbnailPath: {mod.ThumbnailPath}");
                Console.WriteLine($"Author: {mod.Author}");
                Console.WriteLine();
            }

            return 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine("Error during test: " + ex);
            return 2;
        }
    }
}
