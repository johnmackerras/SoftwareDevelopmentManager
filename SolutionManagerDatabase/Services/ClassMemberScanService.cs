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


public interface IClassMemberScanService
{
    Task<int> ScanProjectClassMembersAsync(string gitRootPath, DbProject project, CancellationToken ct = default);
}


public sealed class ClassMemberScanService : IClassMemberScanService
{
    private readonly ApplicationDbContext _db;

    public ClassMemberScanService(ApplicationDbContext db)
    {
        _db = db;
    }

    public async Task<int> ScanProjectClassMembersAsync(string gitRootPath, DbProject project, CancellationToken ct = default)
    {
        // Scans PUBLIC fields + PUBLIC properties for all Class-like artifacts in this project.
        // Excludes bin/obj/.git and (by design) does not walk directories; it parses only files already referenced by artifacts.
        // Partial classes are stored as separate artifacts; we scan each artifact declaration by SpanStart.

        var classArtifacts = await _db.Artifacts
            .Where(a => a.ProjectId == project.Id &&
                        (a.ArtifactType == "Class" || a.ArtifactType == "DbContext" || a.ArtifactType == "Controller" || a.ArtifactType == "Service"))
            .ToListAsync(ct);

        if (classArtifacts.Count == 0)
            return 0;

        var existing = await _db.ClassMembers
            .Where(x => x.ProjectId == project.Id)
            .ToListAsync(ct);

        var treeCache = new Dictionary<string, CompilationUnitSyntax>(StringComparer.OrdinalIgnoreCase);
        var now = DateTime.UtcNow;

        var found = new List<DbClassMember>(capacity: 1024);

        foreach (var art in classArtifacts)
        {
            ct.ThrowIfCancellationRequested();

            var fullPath = Path.GetFullPath(Path.Combine(
                gitRootPath,
                art.RelativeFilePath.Replace('/', Path.DirectorySeparatorChar)));

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

            // Find exact class declaration for this artifact instance
            var cls = root.DescendantNodes().OfType<ClassDeclarationSyntax>()
                .FirstOrDefault(c => c.SpanStart == art.SpanStart);

            if (cls == null)
                continue;

            // Update class metadata on the artifact (abstract/static/interfaces)
            art.IsAbstract = cls.Modifiers.Any(m => m.IsKind(SyntaxKind.AbstractKeyword));
            art.IsStatic = cls.Modifiers.Any(m => m.IsKind(SyntaxKind.StaticKeyword));
            art.InterfacesRaw = GetInterfacesRaw(cls);

            // Fields (public, including const/static/readonly)
            foreach (var f in cls.Members.OfType<FieldDeclarationSyntax>())
            {
                if (!f.Modifiers.Any(m => m.IsKind(SyntaxKind.PublicKeyword)))
                    continue;

                var typeRaw = f.Declaration.Type.ToString();
                var isNullable = IsNullableTypeSyntax(f.Declaration.Type);

                var attrs = FormatAttributes(f.AttributeLists);

                var isStatic = f.Modifiers.Any(m => m.IsKind(SyntaxKind.StaticKeyword));
                var isAbstract = false;
                var isVirtual = false;
                var isOverride = false;

                // Note: one FieldDeclaration can declare multiple variables: "public int A, B;"
                foreach (var v in f.Declaration.Variables)
                {
                    var name = v.Identifier.ValueText;
                    if (string.IsNullOrWhiteSpace(name))
                        continue;

                    found.Add(new DbClassMember
                    {
                        ProjectId = project.Id,
                        DeclaringClassArtifactId = art.Id,
                        MemberKind = "Field",
                        MemberName = name,
                        TypeRaw = typeRaw,
                        IsNullable = isNullable,
                        HasGetter = false,
                        HasSetter = false,
                        IsInitOnly = false,
                        IsStatic = isStatic,
                        IsAbstract = isAbstract,
                        IsVirtual = isVirtual,
                        IsOverride = isOverride,
                        AttributesRaw = attrs,
                        RelativeFilePath = art.RelativeFilePath,
                        SpanStart = v.SpanStart,
                        SpanLength = v.Span.Length,
                        UpdatedOnUtc = now
                    });
                }
            }

            // Properties (public)
            foreach (var p in cls.Members.OfType<PropertyDeclarationSyntax>())
            {
                if (!p.Modifiers.Any(m => m.IsKind(SyntaxKind.PublicKeyword)))
                    continue;

                var typeRaw = p.Type.ToString();
                var isNullable = IsNullableTypeSyntax(p.Type);

                var attrs = FormatAttributes(p.AttributeLists);

                var name = p.Identifier.ValueText;
                if (string.IsNullOrWhiteSpace(name))
                    continue;

                var isStatic = p.Modifiers.Any(m => m.IsKind(SyntaxKind.StaticKeyword));
                var isAbstract = p.Modifiers.Any(m => m.IsKind(SyntaxKind.AbstractKeyword));
                var isVirtual = p.Modifiers.Any(m => m.IsKind(SyntaxKind.VirtualKeyword));
                var isOverride = p.Modifiers.Any(m => m.IsKind(SyntaxKind.OverrideKeyword));

                var (hasGetter, hasSetter, isInitOnly) = GetAccessorInfo(p);

                found.Add(new DbClassMember
                {
                    ProjectId = project.Id,
                    DeclaringClassArtifactId = art.Id,
                    MemberKind = "Property",
                    MemberName = name,
                    TypeRaw = typeRaw,
                    IsNullable = isNullable,
                    HasGetter = hasGetter,
                    HasSetter = hasSetter,
                    IsInitOnly = isInitOnly,
                    IsStatic = isStatic,
                    IsAbstract = isAbstract,
                    IsVirtual = isVirtual,
                    IsOverride = isOverride,
                    AttributesRaw = attrs,
                    RelativeFilePath = art.RelativeFilePath,
                    SpanStart = p.SpanStart,
                    SpanLength = p.Span.Length,
                    UpdatedOnUtc = now
                });
            }
        }

        // Persist artifact metadata updates
        await _db.SaveChangesAsync(ct);

        // Upsert members by (DeclaringClassArtifactId, SpanStart)
        static string Key(DbClassMember x) => $"{x.DeclaringClassArtifactId}||{x.SpanStart}";

        var existingByKey = existing.ToDictionary(Key, StringComparer.OrdinalIgnoreCase);
        var foundByKey = found.ToDictionary(Key, StringComparer.OrdinalIgnoreCase);

        foreach (var ex in existing)
        {
            if (!foundByKey.ContainsKey(Key(ex)))
                _db.ClassMembers.Remove(ex);
        }

        foreach (var f in found)
        {
            if (existingByKey.TryGetValue(Key(f), out var ex))
            {
                ex.MemberKind = f.MemberKind;
                ex.MemberName = f.MemberName;
                ex.TypeRaw = f.TypeRaw;
                ex.IsNullable = f.IsNullable;
                ex.HasGetter = f.HasGetter;
                ex.HasSetter = f.HasSetter;
                ex.IsInitOnly = f.IsInitOnly;
                ex.IsStatic = f.IsStatic;
                ex.IsAbstract = f.IsAbstract;
                ex.IsVirtual = f.IsVirtual;
                ex.IsOverride = f.IsOverride;
                ex.AttributesRaw = f.AttributesRaw;
                ex.RelativeFilePath = f.RelativeFilePath;
                ex.SpanLength = f.SpanLength;
                ex.UpdatedOnUtc = now;
            }
            else
            {
                _db.ClassMembers.Add(f);
            }
        }

        return found.Count;
    }

    private static string? GetInterfacesRaw(ClassDeclarationSyntax cls)
    {
        if (cls.BaseList == null) return null;

        // First type is typically base class; interfaces follow (but not guaranteed). We keep interfaces best-effort:
        // include everything after the first that doesn't look like a base class? too risky.
        // v1: store all base-list entries except the first if there are 2+.
        var types = cls.BaseList.Types.Select(t => t.Type.ToString()).ToList();
        if (types.Count <= 1) return null;

        var iface = types.Skip(1).ToList();
        return iface.Count == 0 ? null : string.Join(", ", iface);
    }

    private static bool IsNullableTypeSyntax(TypeSyntax t)
    {
        if (t is NullableTypeSyntax) return true;
        return t.ToString().TrimEnd().EndsWith("?", StringComparison.Ordinal);
    }

    private static string? FormatAttributes(SyntaxList<AttributeListSyntax> lists)
    {
        var attrs = lists
            .SelectMany(l => l.Attributes)
            .Select(a => a.ToString())
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .ToList();

        return attrs.Count == 0 ? null : string.Join(";", attrs);
    }

    private static (bool HasGetter, bool HasSetter, bool IsInitOnly) GetAccessorInfo(PropertyDeclarationSyntax p)
    {
        // Expression-bodied property: "public int X => 1;"
        if (p.ExpressionBody != null)
            return (true, false, false);

        // Auto/custom accessor list
        if (p.AccessorList == null)
            return (false, false, false);

        bool hasGet = false;
        bool hasSet = false;
        bool isInit = false;

        foreach (var a in p.AccessorList.Accessors)
        {
            if (a.IsKind(SyntaxKind.GetAccessorDeclaration)) hasGet = true;
            if (a.IsKind(SyntaxKind.SetAccessorDeclaration)) hasSet = true;
            if (a.IsKind(SyntaxKind.InitAccessorDeclaration)) { hasSet = true; isInit = true; }
        }

        return (hasGet, hasSet, isInit);
    }
}
