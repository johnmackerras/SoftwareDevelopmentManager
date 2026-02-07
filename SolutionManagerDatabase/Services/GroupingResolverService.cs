using Microsoft.EntityFrameworkCore;
using SolutionManagerDatabase.Context;
using SolutionManagerDatabase.Schema;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace SolutionManagerDatabase.Services;

public interface IGroupingResolverService
{
    Task<int> ResolveGroupingsAsync(CancellationToken ct = default);
}

public sealed class GroupingResolverService : IGroupingResolverService
{
    private readonly ApplicationDbContext _db;

    public GroupingResolverService(ApplicationDbContext db)
    {
        _db = db;
    }

    public async Task<int> ResolveGroupingsAsync(CancellationToken ct = default)
    {
        // Applies GroupingOverrides (exact match; null selector = wildcard) to artifacts.
        // Chooses ONE best match (most specific). If tie, highest Id wins.

        var overrides = await _db.GroupingOverrides.AsNoTracking().ToListAsync(ct);
        if (overrides.Count == 0) return 0;

        var artifacts = await _db.Artifacts
            .Include(a => a.Project).ThenInclude(p => p.Solution).ThenInclude(s => s.Repository)
            .ToListAsync(ct);

        var now = DateTime.UtcNow;
        int changed = 0;

        foreach (var art in artifacts)
        {
            ct.ThrowIfCancellationRequested();

            var repoName = art.Project.Solution.Repository.RepositoryName;
            var solName = art.Project.Solution.Name;
            var projName = art.Project.Name;
            var clsName = art.ClassName ?? art.LogicalName;

            var best = overrides
                .Where(o => Match(o.RepositoryName, repoName) &&
                            Match(o.SolutionName, solName) &&
                            Match(o.ProjectName, projName) &&
                            Match(o.ClassName, clsName))
                .Select(o => new { O = o, Score = SpecificityScore(o) })
                .OrderByDescending(x => x.Score)
                .ThenByDescending(x => x.O.Id) // tie-breaker: latest row wins
                .FirstOrDefault();

            // If no match, clear override-driven fields (optional). I recommend clearing so it stays truthful.
            if (best == null)
            {
                if (art.GroupingOverrideId != null || art.Module != null || art.Visibility != null || art.Feature != null)
                {
                    art.GroupingOverrideId = null;
                    art.Module = null;
                    art.Visibility = null;
                    art.Feature = null;
                    changed++;
                }
                continue;
            }

            var ov = best.O;

            if (art.GroupingOverrideId != ov.Id ||
                !string.Equals(art.Module, ov.Module, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(art.Visibility, ov.Visibility, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(art.Feature, ov.Feature, StringComparison.OrdinalIgnoreCase))
            {
                art.GroupingOverrideId = ov.Id;
                art.Module = ov.Module;
                art.Visibility = ov.Visibility;
                art.Feature = ov.Feature;
                art.UpdatedOnUtc = now;
                changed++;
            }
        }

        if (changed > 0)
            await _db.SaveChangesAsync(ct);

        return changed;
    }

    private static bool Match(string? selector, string value)
        => selector == null || selector.Length == 0
            ? true
            : string.Equals(selector, value, StringComparison.OrdinalIgnoreCase);

    private static int SpecificityScore(DbGroupingOverride o)
    {
        int s = 0;
        if (!string.IsNullOrWhiteSpace(o.RepositoryName)) s++;
        if (!string.IsNullOrWhiteSpace(o.SolutionName)) s++;
        if (!string.IsNullOrWhiteSpace(o.ProjectName)) s++;
        if (!string.IsNullOrWhiteSpace(o.ClassName)) s++;
        return s;
    }

}
