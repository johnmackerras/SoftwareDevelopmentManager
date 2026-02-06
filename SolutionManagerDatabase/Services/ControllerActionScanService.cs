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

namespace SolutionManagerDatabase.Services
{
    public interface IControllerActionScanService
    {
        Task<int> ScanProjectControllerActionsAsync(
            string gitRootPath,
            DbProject project,
            CancellationToken ct = default);
    }

    public sealed class ControllerActionScanService : IControllerActionScanService
    {
        private readonly ApplicationDbContext _db;

        public ControllerActionScanService(ApplicationDbContext db)
        {
            _db = db;
        }

        public async Task<int> ScanProjectControllerActionsAsync(string gitRootPath, DbProject project, CancellationToken ct = default)
        {
            // Scans controller artifacts for this project and records public action methods.
            // MVC vs API is driven by DbArtifact.ArtifactSubType ("Mvc"/"Api") captured earlier.

            var controllers = await _db.Artifacts
                .Where(a => a.ProjectId == project.Id && a.ArtifactType == "Controller")
                .ToListAsync(ct);

            if (controllers.Count == 0)
                return 0;

            var existing = await _db.ControllerActions
                .Where(x => x.ProjectId == project.Id)
                .ToListAsync(ct);

            // cache parse per file
            var treeCache = new Dictionary<string, CompilationUnitSyntax>(StringComparer.OrdinalIgnoreCase);

            var now = DateTime.UtcNow;
            var found = new List<DbControllerAction>(capacity: 256);

            foreach (var ctrl in controllers)
            {
                ct.ThrowIfCancellationRequested();

                var fullPath = Path.GetFullPath(Path.Combine(
                    gitRootPath,
                    ctrl.RelativeFilePath.Replace('/', Path.DirectorySeparatorChar)));

                if (!File.Exists(fullPath))
                    continue;

                if (!treeCache.TryGetValue(fullPath, out var root))
                {
                    string text;
                    try
                    {
                        text = await File.ReadAllTextAsync(fullPath, ct);
                    }
                    catch
                    {
                        continue;
                    }

                    var tree = CSharpSyntaxTree.ParseText(text);
                    root = tree.GetCompilationUnitRoot(ct);
                    treeCache[fullPath] = root;
                }

                // Find the exact class declaration by SpanStart (works even for partial/multiple declarations)
                var classNode = root.DescendantNodes()
                    .OfType<ClassDeclarationSyntax>()
                    .FirstOrDefault(c => c.SpanStart == ctrl.SpanStart);

                if (classNode == null)
                    continue;

                var controllerRoutePrefix = GetControllerRouteTemplateFromAllPartials(root, classNode.Identifier.ValueText);

                var isApi = string.Equals(ctrl.ArtifactSubType, "Api", StringComparison.OrdinalIgnoreCase);

                var methods = classNode.Members.OfType<MethodDeclarationSyntax>();

                foreach (var m in methods)
                {
                    if (!m.Modifiers.Any(x => x.IsKind(SyntaxKind.PublicKeyword)))
                        continue;

                    if (m.Modifiers.Any(x => x.IsKind(SyntaxKind.StaticKeyword)))
                        continue;

                    if (HasAttribute(m.AttributeLists, "NonAction"))
                        continue;

                    var actionName = m.Identifier.ValueText;
                    if (string.IsNullOrWhiteSpace(actionName))
                        continue;

                    var (httpMethod, methodRoute) = GetHttpMethodAndRoute(m.AttributeLists);

                    // MVC convention: if no [Http*], it can still be an action.
                    // API: also allow ANY if not specified (we’ll tighten later if needed).
                    httpMethod ??= "ANY";

                    var effectiveRoute = CombineRoutes(controllerRoutePrefix, methodRoute);

                    found.Add(new DbControllerAction
                    {
                        ProjectId = project.Id,
                        ControllerArtifactId = ctrl.Id,
                        ControllerName = ctrl.LogicalName,
                        ActionName = actionName,
                        HttpMethod = httpMethod,
                        RouteTemplate = effectiveRoute,
                        IsApi = isApi,
                        IsAsync = IsAsyncReturn(m.ReturnType),
                        ReturnType = m.ReturnType.ToString(),
                        Parameters = FormatParameters(m.ParameterList),
                        RelativeFilePath = ctrl.RelativeFilePath,
                        SpanStart = m.SpanStart,
                        SpanLength = m.Span.Length,
                        UpdatedOnUtc = now
                    });
                }
            }

            // Upsert by (ControllerArtifactId, SpanStart)
            static string Key(DbControllerAction a) => $"{a.ControllerArtifactId}||{a.SpanStart}";

            var existingByKey = existing.ToDictionary(Key, StringComparer.OrdinalIgnoreCase);
            var foundByKey = found.ToDictionary(Key, StringComparer.OrdinalIgnoreCase);

            foreach (var ex in existing)
            {
                if (!foundByKey.ContainsKey(Key(ex)))
                    _db.ControllerActions.Remove(ex);
            }

            foreach (var f in found)
            {
                if (existingByKey.TryGetValue(Key(f), out var ex))
                {
                    ex.ControllerName = f.ControllerName;
                    ex.ActionName = f.ActionName;
                    ex.HttpMethod = f.HttpMethod;
                    ex.RouteTemplate = f.RouteTemplate;
                    ex.IsApi = f.IsApi;
                    ex.IsAsync = f.IsAsync;
                    ex.ReturnType = f.ReturnType;
                    ex.Parameters = f.Parameters;
                    ex.RelativeFilePath = f.RelativeFilePath;
                    ex.SpanLength = f.SpanLength;
                    ex.UpdatedOnUtc = now;
                }
                else
                {
                    _db.ControllerActions.Add(f);
                }
            }

            return found.Count;
        }

        private static string? GetControllerRouteTemplateFromAllPartials(CompilationUnitSyntax root, string className)
        {
            // If a controller is partial, attributes may appear on a different partial declaration.
            // We scan all class declarations in this file with the same identifier and take the first [Route("...")] we find.
            foreach (var cls in root.DescendantNodes().OfType<ClassDeclarationSyntax>())
            {
                if (!string.Equals(cls.Identifier.ValueText, className, StringComparison.Ordinal))
                    continue;

                var route = GetRouteAttributeTemplate(cls.AttributeLists);
                if (!string.IsNullOrWhiteSpace(route))
                    return route;
            }

            return null;
        }


        private static bool HasAttribute(SyntaxList<AttributeListSyntax> lists, string simpleName)
            => lists.SelectMany(l => l.Attributes)
                .Select(a => a.Name.ToString().Split('.').Last())
                .Any(n => n.Equals(simpleName, StringComparison.OrdinalIgnoreCase) ||
                          n.Equals(simpleName + "Attribute", StringComparison.OrdinalIgnoreCase));

        private static string? GetRouteAttributeTemplate(SyntaxList<AttributeListSyntax> lists)
        {
            // [Route("api/[controller]")]
            foreach (var a in lists.SelectMany(l => l.Attributes))
            {
                var n = a.Name.ToString().Split('.').Last();
                if (!n.Equals("Route", StringComparison.OrdinalIgnoreCase) &&
                    !n.Equals("RouteAttribute", StringComparison.OrdinalIgnoreCase))
                    continue;

                var s = FirstStringArg(a);
                if (!string.IsNullOrWhiteSpace(s))
                    return s;
            }
            return null;
        }

        private static (string? HttpMethod, string? Route) GetHttpMethodAndRoute(SyntaxList<AttributeListSyntax> lists)
        {
            // Prefer [HttpGet("x")] etc; fallback to [Route("x")] on method
            string? method = null;
            string? route = null;

            foreach (var a in lists.SelectMany(l => l.Attributes))
            {
                var n = a.Name.ToString().Split('.').Last();

                if (n.Equals("HttpGet", StringComparison.OrdinalIgnoreCase) || n.Equals("HttpGetAttribute", StringComparison.OrdinalIgnoreCase))
                { method = "GET"; route ??= FirstStringArg(a); continue; }

                if (n.Equals("HttpPost", StringComparison.OrdinalIgnoreCase) || n.Equals("HttpPostAttribute", StringComparison.OrdinalIgnoreCase))
                { method = "POST"; route ??= FirstStringArg(a); continue; }

                if (n.Equals("HttpPut", StringComparison.OrdinalIgnoreCase) || n.Equals("HttpPutAttribute", StringComparison.OrdinalIgnoreCase))
                { method = "PUT"; route ??= FirstStringArg(a); continue; }

                if (n.Equals("HttpDelete", StringComparison.OrdinalIgnoreCase) || n.Equals("HttpDeleteAttribute", StringComparison.OrdinalIgnoreCase))
                { method = "DELETE"; route ??= FirstStringArg(a); continue; }

                if (n.Equals("HttpPatch", StringComparison.OrdinalIgnoreCase) || n.Equals("HttpPatchAttribute", StringComparison.OrdinalIgnoreCase))
                { method = "PATCH"; route ??= FirstStringArg(a); continue; }

                if (n.Equals("AcceptVerbs", StringComparison.OrdinalIgnoreCase) || n.Equals("AcceptVerbsAttribute", StringComparison.OrdinalIgnoreCase))
                {
                    // [AcceptVerbs("GET","POST")] -> store "GET,POST"
                    var verbs = AllStringArgs(a);
                    if (verbs.Count > 0) method = string.Join(',', verbs.Select(v => v.ToUpperInvariant()));
                    route ??= null;
                    continue;
                }

                if ((n.Equals("Route", StringComparison.OrdinalIgnoreCase) || n.Equals("RouteAttribute", StringComparison.OrdinalIgnoreCase)) && route == null)
                {
                    route = FirstStringArg(a);
                }
            }

            return (method, route);
        }

        private static string? FirstStringArg(AttributeSyntax a)
        {
            var args = a.ArgumentList?.Arguments;
            if (args == null || args.Value.Count == 0) return null;

            foreach (var arg in args.Value)
            {
                var expr = arg.Expression;
                if (expr is LiteralExpressionSyntax lit && lit.IsKind(SyntaxKind.StringLiteralExpression))
                    return lit.Token.ValueText;
            }
            return null;
        }

        private static List<string> AllStringArgs(AttributeSyntax a)
        {
            var result = new List<string>();
            var args = a.ArgumentList?.Arguments;
            if (args == null || args.Value.Count == 0) return result;

            foreach (var arg in args.Value)
            {
                var expr = arg.Expression;
                if (expr is LiteralExpressionSyntax lit && lit.IsKind(SyntaxKind.StringLiteralExpression))
                    result.Add(lit.Token.ValueText);
            }
            return result;
        }

        private static string? CombineRoutes(string? controllerRoute, string? methodRoute)
        {
            if (string.IsNullOrWhiteSpace(controllerRoute)) return methodRoute;
            if (string.IsNullOrWhiteSpace(methodRoute)) return controllerRoute;

            var a = controllerRoute.Trim().TrimEnd('/');
            var b = methodRoute.Trim().TrimStart('/');
            return $"{a}/{b}";
        }

        private static bool IsAsyncReturn(TypeSyntax returnType)
        {
            var rt = returnType.ToString();
            return rt.StartsWith("Task", StringComparison.Ordinal) || rt.Contains("Task<", StringComparison.Ordinal);
        }

        private static string? FormatParameters(ParameterListSyntax list)
        {
            if (list.Parameters.Count == 0) return null;

            var parts = list.Parameters
                .Select(p =>
                {
                    var t = p.Type?.ToString() ?? "var";
                    var n = p.Identifier.ValueText;
                    return $"{t} {n}".Trim();
                })
                .ToList();

            return string.Join(", ", parts);
        }
    }
}
