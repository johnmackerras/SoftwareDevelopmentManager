using Microsoft.EntityFrameworkCore;
using SolutionManagerDatabase.Context;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace SolutionManagerDatabase.Services.Queries;


public interface IClassQueryService
{
    Task<IReadOnlyList<ClassVersionDto>> GetClassVersionsAsync(string logicalClassKey, CancellationToken ct = default);

    Task<ClassCompareMatrixDto?> GetClassCompareMatrixAsync(string logicalClassKey, CancellationToken ct = default);

    Task<ClassCompareMatrixDto?> GetClassCompareMatrixAsync(string classNameOrLogicalName, string? module, string? visibility, string? feature, CancellationToken ct = default);
    Task<IReadOnlyList<string>> GetDistinctClassNamesAsync(CancellationToken ct = default);
    Task<IReadOnlyList<string>> GetDistinctModulesAsync(CancellationToken ct = default);
    Task<IReadOnlyList<string>> GetDistinctVisibilitiesAsync(CancellationToken ct = default);
    Task<IReadOnlyList<string>> GetDistinctFeaturesAsync(CancellationToken ct = default);


}


public sealed class ClassQueryService : IClassQueryService
{
    private readonly ApplicationDbContext _db;

    public ClassQueryService(ApplicationDbContext db)
    {
        _db = db;
    }

    public async Task<IReadOnlyList<ClassVersionDto>> GetClassVersionsAsync(string logicalClassKey, CancellationToken ct = default)
    {
        logicalClassKey = (logicalClassKey ?? "").Trim();
        if (logicalClassKey.Length == 0)
            return Array.Empty<ClassVersionDto>();

        var artifacts = await _db.Artifacts
            .AsNoTracking()
            .Include(a => a.Project).ThenInclude(p => p.Solution).ThenInclude(s => s.Repository)
            .Where(a => a.LogicalClassKey == logicalClassKey)
            .OrderBy(a => a.Project.Solution.Repository.RepositoryName)
            .ThenBy(a => a.Project.Solution.Name)
            .ThenBy(a => a.Project.Name)
            .ThenBy(a => a.RelativeFilePath)
            .ThenBy(a => a.SpanStart)
            .ToListAsync(ct);

        if (artifacts.Count == 0)
            return Array.Empty<ClassVersionDto>();

        var artifactIds = artifacts.Select(a => a.Id).ToList();

        var members = await _db.ClassMembers
            .AsNoTracking()
            .Where(m => artifactIds.Contains(m.DeclaringClassArtifactId))
            .OrderBy(m => m.MemberKind)
            .ThenBy(m => m.SpanStart)
            .ToListAsync(ct);

        var membersByArtifact = members
            .GroupBy(m => m.DeclaringClassArtifactId)
            .ToDictionary(
                g => g.Key,
                g => (IReadOnlyList<ClassMemberDto>)g.Select(m => new ClassMemberDto(
                    m.MemberKind,
                    m.MemberName,
                    m.TypeDisplay ?? m.TypeRaw,
                    m.TypeRaw,
                    m.IsNullable,
                    m.IsCollection,
                    m.ElementTypeRaw,
                    m.AttributesRaw,
                    m.IsRequired,
                    m.IsKey,
                    m.MaxLength,
                    m.MinLength,
                    m.SqlTypeName,
                    m.DataType,
                    m.DisplayName,
                    m.DisplayFormatString,
                    m.DisplayFormatApplyInEditMode,
                    m.ForeignKey,
                    m.InverseProperty,
                    m.HasGetter,
                    m.HasSetter,
                    m.IsInitOnly,
                    m.IsStatic,
                    m.IsAbstract,
                    m.IsVirtual,
                    m.IsOverride,
                    m.SpanStart
                )).ToList(),
                comparer: EqualityComparer<long>.Default);

        var result = artifacts.Select(a =>
        {
            membersByArtifact.TryGetValue(a.Id, out var mems);
            mems ??= Array.Empty<ClassMemberDto>();

            return new ClassVersionDto(
                a.Project.Solution.Repository.RepositoryName,
                a.Project.Solution.Name,
                a.Project.Name,

                a.Id,
                a.LogicalClassKey ?? logicalClassKey,
                a.ClassName ?? a.LogicalName,
                a.Namespace,

                a.Module,
                a.Visibility,
                a.Feature,

                a.RelativeFilePath,
                a.FileName,

                a.BaseClassName,
                a.BaseTypeName,
                a.InterfacesRaw,
                a.IsAbstract,
                a.IsStatic,
                a.IsPartial,

                a.FileSha256,
                a.FileLastWriteUtc,
                a.FileSizeBytes,

                mems
            );
        }).ToList();

        return result;
    }

    public async Task<ClassCompareMatrixDto?> GetClassCompareMatrixAsync(string logicalClassKey, CancellationToken ct = default)
    {
        logicalClassKey = (logicalClassKey ?? "").Trim();
        if (logicalClassKey.Length == 0)
            return null;

        // 1) Pull all artifacts for the class key (these become columns)
        var artifacts = await _db.Artifacts
            .AsNoTracking()
            .Include(a => a.Project).ThenInclude(p => p.Solution).ThenInclude(s => s.Repository)
            .Where(a => a.LogicalClassKey != null && a.LogicalClassKey.Contains(logicalClassKey))
            .OrderBy(a => a.Project.Solution.Repository.RepositoryName)
            .ThenBy(a => a.Project.Solution.Name)
            .ThenBy(a => a.Project.Name)
            .ThenBy(a => a.RelativeFilePath)
            .ThenBy(a => a.SpanStart)
            .ToListAsync(ct);

        if (artifacts.Count == 0)
            return null;

        var columns = artifacts.Select((a, i) => new MatrixColumnDto(
            i,
            a.Project.Solution.Repository.RepositoryName,
            a.Project.Solution.Name,
            a.Project.Name,
            a.Id,
            a.RelativeFilePath,
            a.FileSha256
        )).ToList();

        var artifactIds = artifacts.Select(a => a.Id).ToList();

        // 2) Pull members for those artifacts
        var members = await _db.ClassMembers
            .AsNoTracking()
            .Where(m => artifactIds.Contains(m.DeclaringClassArtifactId))
            .OrderBy(m => m.SpanStart)
            .ToListAsync(ct);

        // Group members by artifact
        var byArtifact = members
            .GroupBy(m => m.DeclaringClassArtifactId)
            .ToDictionary(g => g.Key, g => g.ToList());

        // 3) Build the row order:
        // Baseline is the first column's member order (by SpanStart), then append extras found elsewhere.
        var baselineArtifactId = columns[0].ArtifactId;
        var baseline = byArtifact.TryGetValue(baselineArtifactId, out var baseList)
            ? baseList
            : new List<Schema.DbClassMember>();

        static string RowKey(Schema.DbClassMember m) => $"{m.MemberKind}||{m.MemberName}";

        var orderedKeys = new List<string>();
        var keyToName = new Dictionary<string, (string Kind, string Name)>(StringComparer.OrdinalIgnoreCase);

        foreach (var m in baseline)
        {
            var k = RowKey(m);
            if (keyToName.ContainsKey(k)) continue;
            orderedKeys.Add(k);
            keyToName[k] = (m.MemberKind, m.MemberName);
        }

        // Append keys not present in baseline, preserving the order they appear in each artifact (by SpanStart)
        foreach (var col in columns.Skip(1))
        {
            if (!byArtifact.TryGetValue(col.ArtifactId, out var list)) continue;

            foreach (var m in list)
            {
                var k = RowKey(m);
                if (keyToName.ContainsKey(k)) continue;
                orderedKeys.Add(k);
                keyToName[k] = (m.MemberKind, m.MemberName);
            }
        }

        // 4) Create cells per row per column
        var colIndexByArtifactId = columns.ToDictionary(c => c.ArtifactId, c => c.Index);

        // Build quick lookup: (artifactId, rowKey) -> member
        var memberLookup = new Dictionary<string, Schema.DbClassMember>(StringComparer.OrdinalIgnoreCase);
        foreach (var (artifactId, list) in byArtifact)
        {
            foreach (var m in list)
            {
                var k = $"{artifactId}||{RowKey(m)}";
                // If duplicates (same name/kind), keep first by SpanStart (list is SpanStart ordered)
                if (!memberLookup.ContainsKey(k))
                    memberLookup[k] = m;
            }
        }

        var rows = new List<MatrixRowDto>(orderedKeys.Count);

        foreach (var rowKey in orderedKeys)
        {
            var (kind, name) = keyToName[rowKey];

            var cells = new List<MatrixCellDto>(columns.Count);

            foreach (var col in columns)
            {
                var lk = $"{col.ArtifactId}||{rowKey}";
                if (memberLookup.TryGetValue(lk, out var m))
                {
                    cells.Add(new MatrixCellDto(
                        col.Index,
                        m.TypeDisplay ?? m.TypeRaw,
                        m.TypeRaw,
                        m.IsRequired,
                        m.MaxLength,
                        m.SqlTypeName
                    ));
                }
                else
                {
                    cells.Add(new MatrixCellDto(
                        col.Index,
                        null,   // render as ---
                        null,
                        null,
                        null,
                        null
                    ));
                }
            }

            rows.Add(new MatrixRowDto(kind, name, cells));
        }

        // 5) Header info: take from first artifact (they should all agree; if not, baseline wins)
        var a0 = artifacts[0];

        return new ClassCompareMatrixDto(
            logicalClassKey,
            a0.Module,
            a0.Visibility,
            a0.Feature,
            a0.Namespace,
            a0.ClassName ?? a0.LogicalName,
            columns,
            rows
        );
    }

    public async Task<ClassCompareMatrixDto?> GetClassCompareMatrixAsync(
    string classNameOrLogicalName,
    string? module,
    string? visibility,
    string? feature,
    CancellationToken ct = default)
    {
        classNameOrLogicalName = (classNameOrLogicalName ?? "").Trim();
        module = (module ?? "").Trim();
        visibility = (visibility ?? "").Trim();
        feature = (feature ?? "").Trim();

        if (classNameOrLogicalName.Length == 0)
            return null;

        var q = _db.Artifacts
            .AsNoTracking()
            .Include(a => a.Project).ThenInclude(p => p.Solution).ThenInclude(s => s.Repository)
            .Where(a =>
                (a.ClassName != null && a.ClassName == classNameOrLogicalName) ||
                (a.LogicalName != null && a.LogicalName == classNameOrLogicalName));

        if (module.Length > 0)
            q = q.Where(a => a.Module != null && a.Module == module);

        if (visibility.Length > 0)
            q = q.Where(a => a.Visibility != null && a.Visibility == visibility);

        if (feature.Length > 0)
            q = q.Where(a => a.Feature != null && a.Feature == feature);

        var artifacts = await q
            .OrderBy(a => a.Project.Solution.Repository.RepositoryName)
            .ThenBy(a => a.Project.Solution.Name)
            .ThenBy(a => a.Project.Name)
            .ThenBy(a => a.RelativeFilePath)
            .ThenBy(a => a.SpanStart)
            .ToListAsync(ct);

        if (artifacts.Count == 0)
            return null;

        // Reuse your existing matrix builder logic by temporarily calling the original method,
        // but we need to build matrix from these artifacts directly. Easiest is to inline the existing method’s body.
        // Below is the same matrix build, starting from the artifacts list.

        var columns = artifacts.Select((a, i) => new MatrixColumnDto(
            i,
            a.Project.Solution.Repository.RepositoryName,
            a.Project.Solution.Name,
            a.Project.Name,
            a.Id,
            a.RelativeFilePath,
            a.FileSha256
        )).ToList();

        var artifactIds = artifacts.Select(a => a.Id).ToList();

        var members = await _db.ClassMembers
            .AsNoTracking()
            .Where(m => artifactIds.Contains(m.DeclaringClassArtifactId))
            .OrderBy(m => m.SpanStart)
            .ToListAsync(ct);

        var byArtifact = members
            .GroupBy(m => m.DeclaringClassArtifactId)
            .ToDictionary(g => g.Key, g => g.ToList());

        static string RowKey(SolutionManagerDatabase.Schema.DbClassMember m) => $"{m.MemberKind}||{m.MemberName}";

        var baselineArtifactId = columns[0].ArtifactId;
        var baseline = byArtifact.TryGetValue(baselineArtifactId, out var baseList)
            ? baseList
            : new List<SolutionManagerDatabase.Schema.DbClassMember>();

        var orderedKeys = new List<string>();
        var keyToName = new Dictionary<string, (string Kind, string Name)>(StringComparer.OrdinalIgnoreCase);

        foreach (var m in baseline)
        {
            var k = RowKey(m);
            if (keyToName.ContainsKey(k)) continue;
            orderedKeys.Add(k);
            keyToName[k] = (m.MemberKind, m.MemberName);
        }

        foreach (var col in columns.Skip(1))
        {
            if (!byArtifact.TryGetValue(col.ArtifactId, out var list)) continue;

            foreach (var m in list)
            {
                var k = RowKey(m);
                if (keyToName.ContainsKey(k)) continue;
                orderedKeys.Add(k);
                keyToName[k] = (m.MemberKind, m.MemberName);
            }
        }

        var memberLookup = new Dictionary<string, SolutionManagerDatabase.Schema.DbClassMember>(StringComparer.OrdinalIgnoreCase);
        foreach (var (artifactId, list) in byArtifact)
        {
            foreach (var m in list)
            {
                var k = $"{artifactId}||{RowKey(m)}";
                if (!memberLookup.ContainsKey(k))
                    memberLookup[k] = m;
            }
        }

        var rows = new List<MatrixRowDto>(orderedKeys.Count);

        foreach (var rowKey in orderedKeys)
        {
            var (kind, name) = keyToName[rowKey];
            var cells = new List<MatrixCellDto>(columns.Count);

            foreach (var col in columns)
            {
                var lk = $"{col.ArtifactId}||{rowKey}";
                if (memberLookup.TryGetValue(lk, out var m))
                {
                    cells.Add(new MatrixCellDto(
                        col.Index,
                        m.TypeDisplay ?? m.TypeRaw,
                        m.TypeRaw,
                        m.IsRequired,
                        m.MaxLength,
                        m.SqlTypeName
                    ));
                }
                else
                {
                    cells.Add(new MatrixCellDto(col.Index, null, null, null, null, null));
                }
            }

            rows.Add(new MatrixRowDto(kind, name, cells));
        }

        var a0 = artifacts[0];

        return new ClassCompareMatrixDto(
            a0.LogicalClassKey ?? "",
            a0.Module,
            a0.Visibility,
            a0.Feature,
            a0.Namespace,
            a0.ClassName ?? a0.LogicalName,
            columns,
            rows
        );
    }

    public async Task<IReadOnlyList<string>> GetDistinctClassNamesAsync(CancellationToken ct = default)
    {
        return await _db.Artifacts
            .AsNoTracking()
            .Where(a => a.LogicalName != null && a.LogicalName != "")
            .Select(a => a.LogicalName!)
            .Distinct()
            .OrderBy(x => x)
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<string>> GetDistinctModulesAsync(CancellationToken ct = default)
    {
        return await _db.Artifacts.AsNoTracking()
            .Where(a => a.Module != null && a.Module != "")
            .Select(a => a.Module!)
            .Distinct()
            .OrderBy(x => x)
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<string>> GetDistinctVisibilitiesAsync(CancellationToken ct = default)
    {
        return await _db.Artifacts.AsNoTracking()
            .Where(a => a.Visibility != null && a.Visibility != "")
            .Select(a => a.Visibility!)
            .Distinct()
            .OrderBy(x => x)
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<string>> GetDistinctFeaturesAsync(CancellationToken ct = default)
    {
        return await _db.Artifacts.AsNoTracking()
            .Where(a => a.Feature != null && a.Feature != "")
            .Select(a => a.Feature!)
            .Distinct()
            .OrderBy(x => x)
            .ToListAsync(ct);
    }


}
