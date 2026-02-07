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


public interface IArtifactScanService
{
    Task<int> ScanProjectArtifactsAsync(string gitRootPath, string repoDir, DbProject project, CancellationToken ct = default);


}

public sealed class ArtifactScanService : IArtifactScanService
{
    private readonly ApplicationDbContext _db;

    public ArtifactScanService(ApplicationDbContext db)
    {
        _db = db;
    }

    public async Task<int> ScanProjectArtifactsAsync(string gitRootPath, string repoDir, DbProject project, CancellationToken ct = default)
    {
        // Scans *.cs files (excluding bin/obj/.git) under the project directory.
        // Captures top-level PUBLIC classes only and classifies:
        // - Controller: class name ends with "Controller" AND (base type Controller/ControllerBase OR [ApiController])
        // - DbContext: inherits DbContext / IdentityDbContext / *DbContext
        // - Service: name ends with Service OR implements I{ClassName} OR located under a /Services/ folder
        // - Class: any other public class
        //
        // Note: This is syntax-based Roslyn parsing (no compilation), intentionally robust for mixed repos.

        if (string.IsNullOrWhiteSpace(project.RelativeProjectPath))
            return 0;

        var csprojFull = Path.GetFullPath(Path.Combine(gitRootPath, project.RelativeProjectPath.Replace('/', Path.DirectorySeparatorChar)));
        var projectDir = Path.GetDirectoryName(csprojFull);
        if (projectDir == null || !Directory.Exists(projectDir))
            return 0;

        // Load existing artifacts for project (we will replace with fresh set)
        var existing = await _db.Artifacts
            .Where(a => a.ProjectId == project.Id)
            .ToListAsync(ct);

        var now = DateTime.UtcNow;
        var found = new List<DbArtifact>(capacity: 256);

        foreach (var file in EnumerateCsFiles(projectDir))
        {
            ct.ThrowIfCancellationRequested();

            string text;
            try
            {
                text = await File.ReadAllTextAsync(file, ct);
            }
            catch
            {
                continue;
            }

            var tree = CSharpSyntaxTree.ParseText(text);
            var root = tree.GetCompilationUnitRoot(ct);

            var fileNs = GetFileNamespace(root);

            // Only top-level class declarations
            var classes = root.DescendantNodes()
                .OfType<ClassDeclarationSyntax>()
                .Where(c => c.Parent is NamespaceDeclarationSyntax
                         || c.Parent is FileScopedNamespaceDeclarationSyntax
                         || c.Parent is CompilationUnitSyntax)
                .ToList();

            foreach (var cls in classes)
            {
                if (!cls.Modifiers.Any(m => m.IsKind(SyntaxKind.PublicKeyword)))
                    continue;

                var className = cls.Identifier.ValueText;
                if (string.IsNullOrWhiteSpace(className))
                    continue;

                var relFile = NormalizeRelPath(Path.GetRelativePath(gitRootPath, file));

                var (artifactType, artifactSubType, baseTypeName) = ClassifyArtifactType(className, relFile, cls);


                found.Add(new DbArtifact
                {
                    ProjectId = project.Id,
                    ArtifactType = artifactType,
                    ArtifactSubType = artifactSubType,
                    BaseTypeName = baseTypeName,
                    LogicalName = className,
                    FileName = Path.GetFileName(relFile),
                    RelativeFilePath = relFile,
                    Namespace = fileNs,
                    ClassName = className,
                    IsPartial = cls.Modifiers.Any(m => m.IsKind(SyntaxKind.PartialKeyword)),
                    SpanStart = cls.SpanStart,
                    SpanLength = cls.Span.Length,
                    UpdatedOnUtc = now
                });

            }
        }

        // Replace strategy: delete missing, upsert existing/new by (ProjectId, RelativeFilePath, LogicalName)
        var key = static (DbArtifact a) => $"{a.RelativeFilePath}||{a.LogicalName}||{a.SpanStart}";
        var existingByKey = existing.ToDictionary(key, StringComparer.OrdinalIgnoreCase);
        var foundByKey = found.ToDictionary(key, StringComparer.OrdinalIgnoreCase);



        // deletions
        foreach (var ex in existing)
        {
            if (!foundByKey.ContainsKey(key(ex)))
                _db.Artifacts.Remove(ex);
        }

        // upserts/inserts
        foreach (var f in found)
        {
            if (existingByKey.TryGetValue(key(f), out var ex))
            {
                ex.ArtifactType = f.ArtifactType;
                ex.ArtifactSubType = f.ArtifactSubType;
                ex.BaseTypeName = f.BaseTypeName;
                ex.Namespace = f.Namespace;
                ex.ClassName = f.ClassName;
                ex.UpdatedOnUtc = now;
            }
            else
            {
                _db.Artifacts.Add(f);
            }
        }

        return found.Count;
    }

    private static IEnumerable<string> EnumerateCsFiles(string rootDir)
    {
        var stack = new Stack<string>();
        stack.Push(rootDir);

        while (stack.Count > 0)
        {
            var dir = stack.Pop();

            var name = Path.GetFileName(dir);
            if (name.Equals("bin", StringComparison.OrdinalIgnoreCase)) continue;
            if (name.Equals("obj", StringComparison.OrdinalIgnoreCase)) continue;
            if (name.Equals(".git", StringComparison.OrdinalIgnoreCase)) continue;

            IEnumerable<string> subDirs;
            try { subDirs = Directory.EnumerateDirectories(dir); }
            catch { continue; }

            foreach (var sd in subDirs)
                stack.Push(sd);

            IEnumerable<string> files;
            try { files = Directory.EnumerateFiles(dir, "*.cs", SearchOption.TopDirectoryOnly); }
            catch { continue; }

            foreach (var f in files)
                yield return f;
        }
    }

    private static string NormalizeRelPath(string path) => path.Replace('\\', '/');

    private static string? GetFileNamespace(CompilationUnitSyntax root)
    {
        var fns = root.Members.OfType<FileScopedNamespaceDeclarationSyntax>().FirstOrDefault();
        if (fns != null) return fns.Name.ToString();

        var ns = root.Members.OfType<NamespaceDeclarationSyntax>().FirstOrDefault();
        if (ns != null) return ns.Name.ToString();

        return null;
    }

    private static (string ArtifactType, string? SubType, string? BaseTypeName) ClassifyArtifactType(string className, string relFile, ClassDeclarationSyntax cls)
    {
        var baseNames = GetBaseTypeNames(cls);
        var baseTypeRaw = baseNames.FirstOrDefault(); // first base type is usually the superclass
        var attrNames = GetAttributeNames(cls);

        // Controller
        var isApiControllerAttr = attrNames.Any(a =>
            a.Equals("ApiController", StringComparison.OrdinalIgnoreCase) ||
            a.Equals("ApiControllerAttribute", StringComparison.OrdinalIgnoreCase));

        var inheritsControllerBase =
            baseNames.Any(b => b.Equals("ControllerBase", StringComparison.OrdinalIgnoreCase) ||
                               b.EndsWith(".ControllerBase", StringComparison.OrdinalIgnoreCase));

        var inheritsController =
            baseNames.Any(b => b.Equals("Controller", StringComparison.OrdinalIgnoreCase) ||
                               b.EndsWith(".Controller", StringComparison.OrdinalIgnoreCase));

        if (className.EndsWith("Controller", StringComparison.OrdinalIgnoreCase) &&
            (inheritsController || inheritsControllerBase || isApiControllerAttr))
        {
            // API vs MVC heuristic:
            // - [ApiController] => API
            // - ControllerBase => API
            // - Controller => MVC (usually)
            var sub =
                isApiControllerAttr || inheritsControllerBase ? "Api" :
                inheritsController ? "Mvc" :
                null;

            return ("Controller", sub, baseTypeRaw);
        }

        // DbContext / IdentityDbContext
        var isIdentityDbContext =
            baseNames.Any(b => b.StartsWith("IdentityDbContext", StringComparison.OrdinalIgnoreCase) ||
                               b.EndsWith(".IdentityDbContext", StringComparison.OrdinalIgnoreCase) ||
                               b.Contains("IdentityDbContext<", StringComparison.OrdinalIgnoreCase));

        var isDbContext =
            baseNames.Any(b => b.Equals("DbContext", StringComparison.OrdinalIgnoreCase) ||
                               b.EndsWith(".DbContext", StringComparison.OrdinalIgnoreCase) ||
                               b.EndsWith("DbContext", StringComparison.OrdinalIgnoreCase));

        if (isIdentityDbContext)
            return ("DbContext", "IdentityDbContext", baseTypeRaw);

        if (isDbContext)
            return ("DbContext", "DbContext", baseTypeRaw);

        // Service (v1 heuristics)
        var inServicesFolder = relFile.Contains("/Services/", StringComparison.OrdinalIgnoreCase);

        var implementsIClassName = cls.BaseList?.Types
            .Select(t => t.Type.ToString())
            .Any(t => t.Equals("I" + className, StringComparison.OrdinalIgnoreCase) ||
                      t.EndsWith(".I" + className, StringComparison.OrdinalIgnoreCase)) == true;

        if (className.EndsWith("Service", StringComparison.OrdinalIgnoreCase) || inServicesFolder || implementsIClassName)
            return ("Service", null, baseTypeRaw);

        return ("Class", null, baseTypeRaw);
    }


    private static List<string> GetBaseTypeNames(ClassDeclarationSyntax cls)
    {
        var list = new List<string>();
        if (cls.BaseList == null) return list;

        foreach (var bt in cls.BaseList.Types)
            list.Add(bt.Type.ToString());

        return list;
    }

    private static List<string> GetAttributeNames(ClassDeclarationSyntax cls)
    {
        var list = new List<string>();

        foreach (var al in cls.AttributeLists)
            foreach (var a in al.Attributes)
            {
                var raw = a.Name.ToString();
                if (string.IsNullOrWhiteSpace(raw)) continue;

                // strip namespace prefix
                var simple = raw.Split('.').Last();
                list.Add(simple);
            }

        return list;
    }
}
