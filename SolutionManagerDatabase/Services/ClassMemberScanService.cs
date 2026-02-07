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
            art.BaseClassName = GetBaseClassName(cls);
            art.LogicalClassKey = BuildLogicalClassKey(art);

            // Fields (public, including const/static/readonly)
            foreach (var f in cls.Members.OfType<FieldDeclarationSyntax>())
            {
                if (!f.Modifiers.Any(m => m.IsKind(SyntaxKind.PublicKeyword)))
                    continue;

                var typeRaw = f.Declaration.Type.ToString();
                var isNullable = IsNullableTypeSyntax(f.Declaration.Type);

                var attrs = FormatAttributes(f.AttributeLists);

                var ann = ParseEfAnnotations(f.AttributeLists);

                var (isCollection, elementType) = TryGetCollectionElementType(typeRaw);
                var typeDisplay = BuildTypeDisplay(typeRaw, ann);


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
                        IsRequired = ann.IsRequired,
                        IsKey = ann.IsKey,
                        MaxLength = ann.MaxLength,
                        MinLength = ann.MinLength,
                        SqlTypeName = ann.SqlTypeName,
                        DataType = ann.DataType,
                        DisplayName = ann.DisplayName,
                        ForeignKey = ann.ForeignKey,
                        InverseProperty = ann.InverseProperty,
                        TypeDisplay = typeDisplay,
                        IsCollection = isCollection,
                        ElementTypeRaw = elementType,
                        DisplayFormatString = ann.DisplayFormatString,
                        DisplayFormatApplyInEditMode = ann.DisplayFormatApplyInEditMode,
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

                var ann = ParseEfAnnotations(p.AttributeLists);

                var (isCollection, elementType) = TryGetCollectionElementType(typeRaw);
                var typeDisplay = BuildTypeDisplay(typeRaw, ann);

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
                    TypeDisplay = typeDisplay,
                    IsCollection = isCollection,
                    ElementTypeRaw = elementType,
                    DisplayFormatString = ann.DisplayFormatString,
                    DisplayFormatApplyInEditMode = ann.DisplayFormatApplyInEditMode,
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
                ex.IsRequired = f.IsRequired;
                ex.IsKey = f.IsKey;
                ex.MaxLength = f.MaxLength;
                ex.MinLength = f.MinLength;
                ex.SqlTypeName = f.SqlTypeName;
                ex.DataType = f.DataType;
                ex.DisplayName = f.DisplayName;
                ex.ForeignKey = f.ForeignKey;
                ex.InverseProperty = f.InverseProperty;
                ex.TypeDisplay = f.TypeDisplay;
                ex.IsCollection = f.IsCollection;
                ex.ElementTypeRaw = f.ElementTypeRaw;
                ex.DisplayFormatString = f.DisplayFormatString;
                ex.DisplayFormatApplyInEditMode = f.DisplayFormatApplyInEditMode;
                ex.UpdatedOnUtc = now;
            }
            else
            {
                _db.ClassMembers.Add(f);
            }
        }

        return found.Count;
    }

    private static (bool IsCollection, string? ElementType) TryGetCollectionElementType(string typeRaw)
    {
        // Common patterns: List<T>, ICollection<T>, IEnumerable<T>, IReadOnlyList<T>, HashSet<T>
        var t = typeRaw.Trim();

        int lt = t.IndexOf('<');
        int gt = t.LastIndexOf('>');
        if (lt <= 0 || gt <= lt) return (false, null);

        var outer = t.Substring(0, lt).Trim();
        var inner = t.Substring(lt + 1, gt - lt - 1).Trim();

        outer = outer.Split('.').Last(); // strip namespace

        if (outer is "List" or "IList" or "ICollection" or "IEnumerable" or "IReadOnlyList" or "IReadOnlyCollection" or "HashSet")
            return (true, inner);

        return (false, null);
    }

    private static string? GetBaseClassName(ClassDeclarationSyntax cls)
    {
        if (cls.BaseList == null || cls.BaseList.Types.Count == 0)
            return null;

        // First entry is usually base class
        var first = cls.BaseList.Types[0].Type.ToString().Trim();

        // Strip generic args: IdentityDbContext<AppUser> -> IdentityDbContext
        var lt = first.IndexOf('<');
        if (lt > 0) first = first.Substring(0, lt);

        // Strip namespace: Microsoft.AspNetCore.Mvc.Controller -> Controller
        first = first.Split('.').Last();

        return string.IsNullOrWhiteSpace(first) ? null : first;
    }

    private static string? BuildLogicalClassKey(DbArtifact art)
    {
        if (string.IsNullOrWhiteSpace(art.ClassName))
            return null;

        var ns = art.Namespace ?? "";
        var vis = art.Visibility ?? ""; // Schema/Domain/Viewmodel, from overrides
        var module = art.Module ?? "";

        // v1 key: Visibility|Module|Namespace.ClassName
        return $"{vis}|{module}|{ns}.{art.ClassName}".TrimEnd('.');
    }


    private static string BuildTypeDisplay(string typeRaw, EfAnn ann)
    {
        var t = typeRaw.Trim();

        // Respect explicit SQL type first
        if (!string.IsNullOrWhiteSpace(ann.SqlTypeName))
            return $"{StripNullable(t)}({ann.SqlTypeName})".Replace("((", "(").Replace("))", ")"); // safe-ish

        // If DataType looks like a SQL-ish decimal/number declaration, prefer it for display
        if (!string.IsNullOrWhiteSpace(ann.DataType))
        {
            var dt = ann.DataType.Trim();
            if (dt.StartsWith("decimal(", StringComparison.OrdinalIgnoreCase) ||
                dt.StartsWith("numeric(", StringComparison.OrdinalIgnoreCase))
            {
                return dt.ToLowerInvariant(); // "decimal(7,4)"
            }
        }

        // Strings: use MaxLength if present
        if (IsStringType(t))
        {
            if (ann.MaxLength.HasValue)
                return $"string({ann.MaxLength.Value})";

            // if no length, treat as max (for reporting convenience)
            return "string(max)";
        }

        return StripNullable(t);

        static bool IsStringType(string s)
        {
            s = s.Trim();
            return s.Equals("string", StringComparison.OrdinalIgnoreCase) ||
                   s.Equals("System.String", StringComparison.OrdinalIgnoreCase);
        }

        static string StripNullable(string s)
        {
            s = s.Trim();
            return s.EndsWith("?", StringComparison.Ordinal) ? s.Substring(0, s.Length - 1) : s;
        }
    }


    private sealed record EfAnn(
    bool IsRequired,
    bool IsKey,
    int? MaxLength,
    int? MinLength,
    string? SqlTypeName,
    string? DataType,
    string? DisplayName,
    string? ForeignKey,
    string? InverseProperty,
    string? DisplayFormatString,
    bool DisplayFormatApplyInEditMode
);

    private static EfAnn ParseEfAnnotations(SyntaxList<AttributeListSyntax> lists)
    {
        bool required = false;
        bool key = false;
        int? maxLen = null;
        int? minLen = null;
        string? sqlType = null;
        string? dataType = null;
        string? displayName = null;
        string? foreignKey = null;
        string? inverseProp = null;
        string? displayFormatString = null;
        bool displayFormatApply = false;

        foreach (var a in lists.SelectMany(l => l.Attributes))
        {
            var name = a.Name.ToString().Split('.').Last();

            if (Eq(name, "Required")) required = true;
            if (Eq(name, "Key")) key = true;

            if (Eq(name, "MaxLength"))
                maxLen ??= FirstIntArg(a);

            if (Eq(name, "MinLength"))
                minLen ??= FirstIntArg(a);

            if (Eq(name, "StringLength"))
            {
                maxLen ??= FirstIntArg(a);
                // [StringLength(60, MinimumLength = 2)]
                minLen ??= NamedIntArg(a, "MinimumLength");
            }

            if (Eq(name, "Column"))
            {
                // [Column(TypeName="nvarchar(50)")]
                sqlType ??= NamedStringArg(a, "TypeName");
            }

            if (Eq(name, "DataType"))
            {
                // [DataType(DataType.MultilineText)]
                dataType ??= FirstArgText(a);
            }

            if (Eq(name, "Display"))
            {
                // [Display(Name="Family name")]
                displayName ??= NamedStringArg(a, "Name");
            }

            if (Eq(name, "ForeignKey"))
            {
                // [ForeignKey("ContactId")]  or nameof(Contact)
                foreignKey ??= FirstArgText(a);
            }

            if (Eq(name, "InverseProperty"))
            {
                // [InverseProperty(nameof(DbContactAssociation.Contact))]
                inverseProp ??= FirstArgText(a);
            }

            if (Eq(name, "DisplayFormat"))
            {
                displayFormatString ??= NamedStringArg(a, "DataFormatString");
                displayFormatApply = displayFormatApply || (NamedBoolArg(a, "ApplyFormatInEditMode") == true);
            }

        }

        return new EfAnn(required, key, maxLen, minLen, sqlType, dataType, displayName, foreignKey, inverseProp, displayFormatString, displayFormatApply);


        static bool Eq(string actual, string expected)
            => actual.Equals(expected, StringComparison.OrdinalIgnoreCase) ||
               actual.Equals(expected + "Attribute", StringComparison.OrdinalIgnoreCase);
    }

    private static bool? NamedBoolArg(AttributeSyntax a, string name)
    {
        var args = a.ArgumentList?.Arguments;
        if (args == null) return null;

        foreach (var arg in args.Value)
        {
            if (arg.NameEquals?.Name.Identifier.ValueText.Equals(name, StringComparison.OrdinalIgnoreCase) == true)
            {
                if (arg.Expression is LiteralExpressionSyntax lit &&
                    (lit.IsKind(SyntaxKind.TrueLiteralExpression) || lit.IsKind(SyntaxKind.FalseLiteralExpression)))
                    return lit.IsKind(SyntaxKind.TrueLiteralExpression);
            }
        }
        return null;
    }


    private static int? FirstIntArg(AttributeSyntax a)
    {
        var args = a.ArgumentList?.Arguments;
        if (args == null || args.Value.Count == 0) return null;

        foreach (var arg in args.Value)
        {
            if (arg.Expression is LiteralExpressionSyntax lit &&
                lit.IsKind(SyntaxKind.NumericLiteralExpression) &&
                lit.Token.Value is int i)
                return i;

            if (arg.Expression is LiteralExpressionSyntax lit2 &&
                lit2.IsKind(SyntaxKind.NumericLiteralExpression) &&
                lit2.Token.Value is long l)
                return checked((int)l);
        }
        return null;
    }

    private static int? NamedIntArg(AttributeSyntax a, string name)
    {
        var args = a.ArgumentList?.Arguments;
        if (args == null) return null;

        foreach (var arg in args.Value)
        {
            if (arg.NameEquals?.Name.Identifier.ValueText.Equals(name, StringComparison.OrdinalIgnoreCase) == true)
            {
                if (arg.Expression is LiteralExpressionSyntax lit &&
                    lit.IsKind(SyntaxKind.NumericLiteralExpression) &&
                    lit.Token.Value is int i)
                    return i;

                if (arg.Expression is LiteralExpressionSyntax lit2 &&
                    lit2.IsKind(SyntaxKind.NumericLiteralExpression) &&
                    lit2.Token.Value is long l)
                    return checked((int)l);
            }
        }
        return null;
    }

    private static string? NamedStringArg(AttributeSyntax a, string name)
    {
        var args = a.ArgumentList?.Arguments;
        if (args == null) return null;

        foreach (var arg in args.Value)
        {
            if (arg.NameEquals?.Name.Identifier.ValueText.Equals(name, StringComparison.OrdinalIgnoreCase) == true)
                return ExtractArgText(arg.Expression);
        }
        return null;
    }

    private static string? FirstArgText(AttributeSyntax a)
    {
        var args = a.ArgumentList?.Arguments;
        if (args == null || args.Value.Count == 0) return null;

        return ExtractArgText(args.Value[0].Expression);
    }

    private static string? ExtractArgText(ExpressionSyntax expr)
    {
        if (expr is LiteralExpressionSyntax lit && lit.IsKind(SyntaxKind.StringLiteralExpression))
            return lit.Token.ValueText;

        // nameof(X) or nameof(A.B)
        if (expr is InvocationExpressionSyntax inv && inv.Expression.ToString() == "nameof")
            return inv.ArgumentList.Arguments.FirstOrDefault()?.Expression.ToString();

        // Enum member or identifier or member access: DataType.MultilineText, DbX.Y
        return expr.ToString();
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
