using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.EntityFrameworkCore;
using SolutionManagerDatabase.Context;
using SolutionManagerDatabase.Schema;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace SolutionManagerDatabase.Services;

public interface IDbSetScanService
{
    Task<int> ScanProjectDbSetsAsync(string gitRootPath, DbProject project, CancellationToken ct = default);
}

public sealed class DbSetScanService : IDbSetScanService
{
    private readonly ApplicationDbContext _db;

    public DbSetScanService(ApplicationDbContext db)
    {
        _db = db;
    }

    public async Task<int> ScanProjectDbSetsAsync(string gitRootPath, DbProject project, CancellationToken ct = default)
    {
        var contexts = await _db.Artifacts
            .Where(a => a.ProjectId == project.Id && a.ArtifactType == "DbContext")
            .ToListAsync(ct);

        if (contexts.Count == 0)
            return 0;

        var existing = await _db.DbSets
            .Where(x => x.ProjectId == project.Id)
            .ToListAsync(ct);

        var treeCache = new Dictionary<string, CompilationUnitSyntax>(StringComparer.OrdinalIgnoreCase);
        var now = DateTime.UtcNow;

        var found = new List<DbDbSet>(capacity: 256);

        foreach (var ctx in contexts)
        {
            ct.ThrowIfCancellationRequested();

            var fullPath = Path.GetFullPath(Path.Combine(
                gitRootPath,
                ctx.RelativeFilePath.Replace('/', Path.DirectorySeparatorChar)));

            if (!File.Exists(fullPath))
                continue;

            if (!treeCache.TryGetValue(fullPath, out var root))
            {
                string text;
                try { text = await File.ReadAllTextAsync(fullPath, ct); }
                catch { continue; }

                var tree = CSharpSyntaxTree.ParseText(text);
                root = tree.GetCompilationUnitRoot(ct);
                treeCache[fullPath] = root;
            }

            // Find exact partial declaration by SpanStart (you already store SpanStart on DbArtifact)
            var classNode = root.DescendantNodes()
                .OfType<ClassDeclarationSyntax>()
                .FirstOrDefault(c => c.SpanStart == ctx.SpanStart);

            if (classNode == null)
                continue;

            var ns = ctx.Namespace ?? GetFileNamespace(root);

            foreach (var prop in classNode.Members.OfType<PropertyDeclarationSyntax>())
            {
                if (!prop.Modifiers.Any(m => m.IsKind(SyntaxKind.PublicKeyword)))
                    continue;

                // DbSet<T> (supports "DbSet<T>" and "Microsoft.EntityFrameworkCore.DbSet<T>")
                if (!TryGetDbSetEntityType(prop.Type, out var entityType))
                    continue;

                var dbSetName = prop.Identifier.ValueText;
                if (string.IsNullOrWhiteSpace(dbSetName))
                    continue;

                found.Add(new DbDbSet
                {
                    ProjectId = project.Id,
                    DbContextArtifactId = ctx.Id,
                    DbContextName = ctx.LogicalName,
                    DbSetName = dbSetName,
                    EntityType = entityType,
                    Namespace = ns,
                    RelativeFilePath = ctx.RelativeFilePath,
                    SpanStart = prop.SpanStart,
                    SpanLength = prop.Span.Length,
                    UpdatedOnUtc = now
                });
            }
        }

        // Upsert by (DbContextArtifactId, SpanStart)
        static string Key(DbDbSet x) => $"{x.DbContextArtifactId}||{x.SpanStart}";

        var existingByKey = existing.ToDictionary(Key, StringComparer.OrdinalIgnoreCase);
        var foundByKey = found.ToDictionary(Key, StringComparer.OrdinalIgnoreCase);

        foreach (var ex in existing)
        {
            if (!foundByKey.ContainsKey(Key(ex)))
                _db.DbSets.Remove(ex);
        }

        foreach (var f in found)
        {
            if (existingByKey.TryGetValue(Key(f), out var ex))
            {
                ex.DbContextName = f.DbContextName;
                ex.DbSetName = f.DbSetName;
                ex.EntityType = f.EntityType;
                ex.Namespace = f.Namespace;
                ex.RelativeFilePath = f.RelativeFilePath;
                ex.SpanLength = f.SpanLength;
                ex.UpdatedOnUtc = now;
            }
            else
            {
                _db.DbSets.Add(f);
            }
        }

        return found.Count;
    }

    private static bool TryGetDbSetEntityType(TypeSyntax typeSyntax, out string entityType)
    {
        entityType = "";

        // DbSet<T> or Microsoft.EntityFrameworkCore.DbSet<T>
        if (typeSyntax is GenericNameSyntax g)
        {
            if (!IsDbSetName(g.Identifier.ValueText))
                return false;

            if (g.TypeArgumentList.Arguments.Count != 1)
                return false;

            entityType = g.TypeArgumentList.Arguments[0].ToString();
            return !string.IsNullOrWhiteSpace(entityType);
        }

        if (typeSyntax is QualifiedNameSyntax q && q.Right is GenericNameSyntax rg)
        {
            if (!IsDbSetName(rg.Identifier.ValueText))
                return false;

            if (rg.TypeArgumentList.Arguments.Count != 1)
                return false;

            entityType = rg.TypeArgumentList.Arguments[0].ToString();
            return !string.IsNullOrWhiteSpace(entityType);
        }

        return false;
    }

    private static bool IsDbSetName(string name)
        => name.Equals("DbSet", StringComparison.OrdinalIgnoreCase);

    private static string? GetFileNamespace(CompilationUnitSyntax root)
    {
        var fns = root.Members.OfType<FileScopedNamespaceDeclarationSyntax>().FirstOrDefault();
        if (fns != null) return fns.Name.ToString();

        var ns = root.Members.OfType<NamespaceDeclarationSyntax>().FirstOrDefault();
        if (ns != null) return ns.Name.ToString();

        return null;
    }
}
