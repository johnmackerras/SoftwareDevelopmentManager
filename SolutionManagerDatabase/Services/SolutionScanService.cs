using LibGit2Sharp;
using Microsoft.Build.Construction;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SolutionManagerDatabase.Context;
using SolutionManagerDatabase.Schema;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using Terrafirma.Core.Options;

namespace SolutionManagerDatabase.Services;


public interface ISolutionScanService
{
    Task<int> ScanAllRepositoriesAsync(CancellationToken ct = default);
}

public sealed class SolutionScanService : ISolutionScanService
{
    private readonly ApplicationDbContext _db;
    private readonly SolutionManagerOptions _opt;
    private readonly IArtifactScanService _artifactScan;

    public SolutionScanService(
        ApplicationDbContext db,
        IOptions<SolutionManagerOptions> opt,
        IArtifactScanService artifactScan)
    {
        _db = db;
        _opt = opt.Value;
        _artifactScan = artifactScan;
    }

    public async Task<int> ScanAllRepositoriesAsync(CancellationToken ct = default)
    {
        var gitRoot = _opt.GitRootPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (!Directory.Exists(gitRoot)) throw new DirectoryNotFoundException($"GitRootPath not found: {gitRoot}");

        var repoDirs = Directory.EnumerateDirectories(gitRoot).ToList();
        var now = DateTime.UtcNow;

        int touched = 0;

        foreach (var repoDir in repoDirs)
        {
            ct.ThrowIfCancellationRequested();

            var folderName = Path.GetFileName(repoDir);
            var relativeRepoRoot = folderName; // repo root is direct child of GitRootPath

            // Upsert repository row
            var repoRow = await _db.Repositories
                .Include(r => r.Solutions)
                    .ThenInclude(s => s.Projects)
                .SingleOrDefaultAsync(r => r.RepoRootRelativePath == relativeRepoRoot, ct);

            if (repoRow == null)
            {
                repoRow = new DbRepository
                {
                    RepoRootRelativePath = relativeRepoRoot,
                    RepositoryName = folderName,
                    CreatedOnUtc = now,
                    UpdatedOnUtc = now
                };
                _db.Repositories.Add(repoRow);
            }
            else
            {
                repoRow.UpdatedOnUtc = now;
            }

            await PopulateGitInfoAsync(repoDir, folderName, repoRow, ct);

            // Solutions in this repo (prefer .sln/.slnx)
            var solutions = FindSolutions(repoDir).ToList();

            // Remove missing solutions (optional; keep simple for now: we only upsert found ones)
            foreach (var slnPath in solutions)
            {
                ct.ThrowIfCancellationRequested();

                var slnRelativeToRepo = NormalizeRelPath(Path.GetRelativePath(repoDir, slnPath));
                var slnFile = Path.GetFileName(slnPath);
                var slnName = Path.GetFileNameWithoutExtension(slnPath);

                var solRow = repoRow.Solutions.SingleOrDefault(s => s.SolutionFilePath == slnRelativeToRepo);
                if (solRow == null)
                {
                    solRow = new DbSolution
                    {
                        Repository = repoRow,
                        Name = slnName,
                        SolutionFilePath = slnRelativeToRepo,
                        SolutionFile = slnFile,
                        UpdatedOnUtc = now
                    };
                    repoRow.Solutions.Add(solRow);
                }
                else
                {
                    solRow.Name = slnName;
                    solRow.SolutionFile = slnFile;
                    solRow.UpdatedOnUtc = now;
                }

                // Description from README.md at repo root
                solRow.Description ??= TryReadReadmeDescription(repoDir);

                // CreatedOn from first commit if available
                solRow.CreatedOnUtc ??= repoRow.GitFirstCommitUtc;

                // Projects from solution
                UpsertProjectsFromSolution(gitRoot, repoDir, solRow, slnPath, now);

                // Artifacts (Controllers, DbContexts, Services, Classes) for each project in this solution
                foreach (var proj in solRow.Projects)
                {
                    await _db.SaveChangesAsync(ct); // ensure proj.Id exists before artifact insert
                    await _artifactScan.ScanProjectArtifactsAsync(_opt.GitRootPath, repoDir, proj, ct);
                }


                // Runtime derived from projects
                DeriveSolutionRuntime(solRow);

                touched++;
            }
        }

        await _db.SaveChangesAsync(ct);
        return touched;
    }

    private async Task PopulateGitInfoAsync(string repoDir, string folderName, DbRepository repoRow, CancellationToken ct)
    {
        var gitDir = Path.Combine(repoDir, ".git");
        if (!Directory.Exists(gitDir))
        {
            // Not a git repo; keep folder name as repo name
            repoRow.RepositoryName = folderName;
            repoRow.RepositoryUrl = CombineUrl(_opt.DefaultRemoteRepoRootUrl, repoRow.RepositoryName);
            repoRow.RepositoryProvider = InferProvider(repoRow.RepositoryUrl);
            repoRow.CurrentBranch = null;
            repoRow.DefaultBranch = null;
            repoRow.GitHeadSha = null;
            repoRow.GitFirstCommitUtc = null;
            repoRow.GitLastCommitUtc = null;
            repoRow.IsPrivateRepo = null;
            return;
        }

        using var repo = new Repository(repoDir);

        repoRow.RepositoryName = !string.IsNullOrWhiteSpace(repo.Info.WorkingDirectory)
            ? folderName
            : folderName;

        var originUrl = repo.Network.Remotes["origin"]?.Url;
        repoRow.RepositoryUrl = !string.IsNullOrWhiteSpace(originUrl)
            ? originUrl
            : CombineUrl(_opt.DefaultRemoteRepoRootUrl, repoRow.RepositoryName);

        repoRow.RepositoryProvider = InferProvider(repoRow.RepositoryUrl);
        repoRow.IsPrivateRepo = null; // cannot reliably infer without talking to provider

        var head = repo.Head;
        repoRow.CurrentBranch = repo.Info.IsHeadDetached ? "(detached)" : head.FriendlyName;

        repoRow.GitHeadSha = head.Tip?.Sha;

        var lastCommit = head.Tip;
        repoRow.GitLastCommitUtc = lastCommit?.Author.When.UtcDateTime;

        // first commit = oldest reachable commit
        var firstCommit = repo.Commits.LastOrDefault();
        repoRow.GitFirstCommitUtc = firstCommit?.Author.When.UtcDateTime;

        repoRow.DefaultBranch = TryGetDefaultBranchName(repo) ?? repoRow.CurrentBranch;

        await Task.CompletedTask; // keeps method async-friendly
        ct.ThrowIfCancellationRequested();
    }

    private static string? TryGetDefaultBranchName(Repository repo)
    {
        // Attempt origin/HEAD -> refs/remotes/origin/{branch}
        var originHead = repo.Refs["refs/remotes/origin/HEAD"] as SymbolicReference;
        var target = originHead?.TargetIdentifier; // e.g. "refs/remotes/origin/main"
        if (string.IsNullOrWhiteSpace(target)) return null;

        var parts = target.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length > 0 ? parts[^1] : null;
    }

    private static IEnumerable<string> FindSolutions(string repoDir)
    {
        // Prefer solutions in repo root
        var top = Directory.EnumerateFiles(repoDir, "*.sln", SearchOption.TopDirectoryOnly)
            .Concat(Directory.EnumerateFiles(repoDir, "*.slnx", SearchOption.TopDirectoryOnly))
            .ToList();

        if (top.Count > 0) return top;

        // Fallback: anywhere under repo
        return Directory.EnumerateFiles(repoDir, "*.sln", SearchOption.AllDirectories)
            .Concat(Directory.EnumerateFiles(repoDir, "*.slnx", SearchOption.AllDirectories));
    }

    private static void UpsertProjectsFromSolution(string gitRoot, string repoDir, DbSolution solRow, string slnPath, DateTime nowUtc)
    {
        SolutionFile sln;
        try
        {
            sln = SolutionFile.Parse(slnPath);
        }
        catch
        {
            // If solution parsing fails, skip projects rather than crashing scan
            return;
        }

        var projPaths = sln.ProjectsInOrder
            .Where(p => p.ProjectType == SolutionProjectType.KnownToBeMSBuildFormat)
            .Select(p => p.RelativePath)
            .Where(p => p.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var relFromSlnDir in projPaths)
        {
            var slnDir = Path.GetDirectoryName(slnPath)!;
            var csprojFull = Path.GetFullPath(Path.Combine(slnDir, relFromSlnDir));
            if (!File.Exists(csprojFull)) continue;

            var relToGitRoot = NormalizeRelPath(Path.GetRelativePath(gitRoot, csprojFull));
            var name = Path.GetFileNameWithoutExtension(csprojFull);

            var projRow = solRow.Projects.SingleOrDefault(p => p.RelativeProjectPath == relToGitRoot);
            if (projRow == null)
            {
                projRow = new DbProject
                {
                    Solution = solRow,
                    Name = name,
                    RelativeProjectPath = relToGitRoot,
                    UpdatedOnUtc = nowUtc
                };
                solRow.Projects.Add(projRow);
            }
            else
            {
                projRow.Name = name;
                projRow.UpdatedOnUtc = nowUtc;
            }

            PopulateProjectFromCsproj(csprojFull, projRow);
        }
    }

    private static void PopulateProjectFromCsproj(string csprojFullPath, DbProject projRow)
    {
        XDocument doc;
        try
        {
            doc = XDocument.Load(csprojFullPath, LoadOptions.PreserveWhitespace);
        }
        catch
        {
            return;
        }

        var root = doc.Root;
        if (root == null) return;

        var sdk = (string?)root.Attribute("Sdk") ?? "";
        var propGroups = root.Descendants().Where(e => e.Name.LocalName == "PropertyGroup").ToList();

        static string? FirstPropValue(List<XElement> groups, string name)
            => groups.SelectMany(g => g.Elements().Where(e => e.Name.LocalName == name))
                     .Select(e => e.Value)
                     .FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));

        static string? NetFrameworkTfmFromTargetFrameworkVersion(string? tfv)
        {
            if (string.IsNullOrWhiteSpace(tfv)) return null;

            var s = tfv.Trim();
            if (s.StartsWith("v", StringComparison.OrdinalIgnoreCase))
                s = s.Substring(1);

            // e.g. "4.7.2" -> "net472", "4.8" -> "net48"
            var parts = s.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length == 0) return null;

            if (!int.TryParse(parts[0], out var major)) return null;
            if (major != 4) return null;

            var minor = (parts.Length >= 2 && int.TryParse(parts[1], out var mi)) ? mi : 0;
            var patch = (parts.Length >= 3 && int.TryParse(parts[2], out var pa)) ? pa : -1;

            return patch >= 0
                ? $"net{major}{minor}{patch}"   // 4.7.2 -> net472
                : $"net{major}{minor}";        // 4.8 -> net48, 4.7 -> net47
        }


        string? tfm = FirstPropValue(propGroups, "TargetFramework");
        string? tfms = FirstPropValue(propGroups, "TargetFrameworks");
        string? tfv = FirstPropValue(propGroups, "TargetFrameworkVersion");

        var tfvNet = NetFrameworkTfmFromTargetFrameworkVersion(tfv);

        // Prefer modern TFMs; fall back to .NET Framework TargetFrameworkVersion normalized to net4xx
        projRow.TargetFrameworks =
            !string.IsNullOrWhiteSpace(tfms) ? tfms!.Trim()
            : !string.IsNullOrWhiteSpace(tfm) ? tfm!.Trim()
            : !string.IsNullOrWhiteSpace(tfvNet) ? tfvNet
            : !string.IsNullOrWhiteSpace(tfv) ? tfv!.Trim() // last-resort if it's something odd
            : null;

        var tfIdentifier = FirstPropValue(propGroups, "TargetFrameworkIdentifier")?.Trim();

        // If still no TFM info, fall back to legacy Xamarin identifiers
        if (string.IsNullOrWhiteSpace(projRow.TargetFrameworks) && !string.IsNullOrWhiteSpace(tfIdentifier))
        {
            // e.g. "Xamarin.iOS", "Xamarin.Android"
            projRow.TargetFrameworks = tfIdentifier;
        }

        var tfmList = (projRow.TargetFrameworks ?? "")
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var isIos =
            tfmList.Any(t => t.EndsWith("-ios", StringComparison.OrdinalIgnoreCase)) ||
            string.Equals(tfIdentifier, "Xamarin.iOS", StringComparison.OrdinalIgnoreCase);

        var isAndroid =
            tfmList.Any(t => t.EndsWith("-android", StringComparison.OrdinalIgnoreCase)) ||
            string.Equals(tfIdentifier, "Xamarin.Android", StringComparison.OrdinalIgnoreCase);

        var outputType = FirstPropValue(propGroups, "OutputType")?.Trim();

        var isTestProp = FirstPropValue(propGroups, "IsTestProject")?.Trim();
        var isTestByProp = string.Equals(isTestProp, "true", StringComparison.OrdinalIgnoreCase);

        var pkgRefs = root.Descendants().Where(x => x.Name.LocalName == "PackageReference")
            .Select(x => ((string?)x.Attribute("Include")) ?? "")
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .ToList();

        var asmRefs = root.Descendants().Where(x => x.Name.LocalName == "Reference")
            .Select(x => ((string?)x.Attribute("Include")) ?? "")
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .ToList();

        var imports = root.Descendants().Where(x => x.Name.LocalName == "Import")
            .Select(x => ((string?)x.Attribute("Project")) ?? "")
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .ToList();


        var useMaui = string.Equals(FirstPropValue(propGroups, "UseMaui")?.Trim(), "true", StringComparison.OrdinalIgnoreCase);

        var isMauiBySdk = sdk.Contains("Microsoft.NET.Sdk.Maui", StringComparison.OrdinalIgnoreCase);
        var isMaui = useMaui || isMauiBySdk || pkgRefs.Any(p => p.StartsWith("Microsoft.Maui", StringComparison.OrdinalIgnoreCase));

        var isXamarin = pkgRefs.Any(p =>
            p.StartsWith("Xamarin.", StringComparison.OrdinalIgnoreCase) ||
            p.StartsWith("XamarinForms", StringComparison.OrdinalIgnoreCase) ||
            p.StartsWith("Xamarin.Forms", StringComparison.OrdinalIgnoreCase));

        var hasIPhonePrefix = !string.IsNullOrWhiteSpace(FirstPropValue(propGroups, "IPhoneResourcePrefix"));

        var isIosLegacy =
            asmRefs.Any(r => r.Equals("Xamarin.iOS", StringComparison.OrdinalIgnoreCase)) ||
            imports.Any(p => p.Replace('\\', '/').Contains("/Xamarin/iOS/", StringComparison.OrdinalIgnoreCase)) ||
            imports.Any(p => p.EndsWith("Xamarin.iOS.CSharp.targets", StringComparison.OrdinalIgnoreCase)) ||
            hasIPhonePrefix;

        var isAndroidLegacy =
            imports.Any(p => p.Replace('\\', '/').Contains("/Xamarin/Android/", StringComparison.OrdinalIgnoreCase)) ||
            imports.Any(p => p.EndsWith("Xamarin.Android.CSharp.targets", StringComparison.OrdinalIgnoreCase));


        var isBlazor = sdk.Contains("Microsoft.NET.Sdk.BlazorWebAssembly", StringComparison.OrdinalIgnoreCase) ||
                       pkgRefs.Any(p => p.StartsWith("Microsoft.AspNetCore.Components", StringComparison.OrdinalIgnoreCase));

        var isMvc = projRow.IsWebApp &&
                    pkgRefs.Any(p => p.StartsWith("Microsoft.AspNetCore.Mvc", StringComparison.OrdinalIgnoreCase));

        var isRazorPages = projRow.IsWebApp &&
                           pkgRefs.Any(p => p.StartsWith("Microsoft.AspNetCore.Mvc.RazorPages", StringComparison.OrdinalIgnoreCase));

        // “HTML” here meaning non-.NET web (only if the “project” isn’t really a csproj web app)
        // Keep conservative: only set if SDK is empty and looks like a tooling project.
        var isHtmlTooling = !projRow.IsWebApp && !string.IsNullOrWhiteSpace(projRow.TargetFrameworks) == false;

        var isTestByPkg = pkgRefs.Any(p =>
            p.StartsWith("xunit", StringComparison.OrdinalIgnoreCase) ||
            p.StartsWith("NUnit", StringComparison.OrdinalIgnoreCase) ||
            p.StartsWith("MSTest.", StringComparison.OrdinalIgnoreCase) ||
            p.Equals("Microsoft.NET.Test.Sdk", StringComparison.OrdinalIgnoreCase));

        projRow.IsTestProject = isTestByProp || isTestByPkg;

        projRow.IsWebApp =
            sdk.Contains("Microsoft.NET.Sdk.Web", StringComparison.OrdinalIgnoreCase) ||
            pkgRefs.Any(p => p.StartsWith("Microsoft.AspNetCore.", StringComparison.OrdinalIgnoreCase));

        var isExe = string.Equals(outputType, "Exe", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(outputType, "WinExe", StringComparison.OrdinalIgnoreCase);

        projRow.IsClassLibrary = !projRow.IsWebApp && !projRow.IsTestProject && !isExe;

        // Heuristics for UI stack/platform (keep conservative)var useWpf = string.Equals(FirstPropValue(propGroups, "UseWPF")?.Trim(), "true", StringComparison.OrdinalIgnoreCase);
        var useWpf = string.Equals(FirstPropValue(propGroups, "UseWPF")?.Trim(), "true", StringComparison.OrdinalIgnoreCase);
        var useWinForms = string.Equals(FirstPropValue(propGroups, "UseWindowsForms")?.Trim(), "true", StringComparison.OrdinalIgnoreCase);

        if (string.IsNullOrWhiteSpace(projRow.TargetFrameworks))
        {
            if (isIosLegacy) projRow.TargetFrameworks = "Xamarin.iOS";
            else if (isAndroidLegacy) projRow.TargetFrameworks = "Xamarin.Android";
        }

        // Platform: broad runtime target
        projRow.Platform =
            isIosLegacy ? "iOS" :
            isAndroidLegacy ? "Android" :
            isMaui ? "Multi-platform" :
            isXamarin ? "Mobile" :
            projRow.IsWebApp ? "Web" :
            useWpf || useWinForms ? "Windows" :
            null;


        // UiStack: what UI framework
        projRow.UiStack =
            isMaui ? "MAUI" :
            isXamarin ? "Xamarin" :
            isBlazor ? "Blazor" :
            isMvc ? "MVC" :
            isRazorPages ? "RazorPages" :
            useWpf ? "WPF" :
            useWinForms ? "WinForms" :
            projRow.IsWebApp ? "Web" :
            isHtmlTooling ? "HTML" :
            null;

        projRow.ProjectType =
            projRow.IsTestProject ? "Test" :
            projRow.IsWebApp ? "Web" :
            projRow.IsClassLibrary ? "Library" :
            "App";
    }

    private static void DeriveSolutionRuntime(DbSolution solRow)
    {
        var tfms = solRow.Projects
            .Select(p => p.TargetFrameworks)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .SelectMany(x => x!.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .ToList();

        bool anyNetFx = tfms.Any(t =>
            t.StartsWith("net4", StringComparison.OrdinalIgnoreCase) ||   // net472/net48
            t.StartsWith("v4", StringComparison.OrdinalIgnoreCase));      // legacy if any slipped through

        bool anyModernNet = tfms.Any(t =>
            t.StartsWith("net", StringComparison.OrdinalIgnoreCase) &&
            !t.StartsWith("net4", StringComparison.OrdinalIgnoreCase) &&
            !t.StartsWith("netstandard", StringComparison.OrdinalIgnoreCase) &&
            !t.StartsWith("netframework", StringComparison.OrdinalIgnoreCase));

        solRow.RuntimePlatform =
            anyModernNet ? ".NET" :
            anyNetFx ? ".NET Framework" :
            ".NET";

        // Version
        if (anyModernNet)
        {
            var versions = tfms
                .Select(TryParseNetMajorMinor)
                .Where(v => v.HasValue)
                .Select(v => v!.Value)
                .ToList();

            solRow.RuntimeVersion = versions.Count == 0 ? null : versions.Max().ToString("0.0");
            return;
        }

        if (anyNetFx)
        {
            // net472 -> 4.7.2, net48 -> 4.8
            var fxVersions = tfms
                .Select(TryParseNetFrameworkVersion)
                .Where(v => v != null)
                .Select(v => v!)
                .ToList();

            solRow.RuntimeVersion = fxVersions.Count == 0 ? null : fxVersions.Max(StringComparer.Ordinal);
        }
    }

    private static string? TryParseNetFrameworkVersion(string tfm)
    {
        // net472 -> "4.7.2", net48 -> "4.8"
        if (tfm.StartsWith("net4", StringComparison.OrdinalIgnoreCase))
        {
            var s = tfm.Substring(3); // "472" or "48"
            if (s.Length == 3) return $"{s[0]}.{s[1]}.{s[2]}";
            if (s.Length == 2) return $"{s[0]}.{s[1]}";
        }

        // v4.7.2 -> "4.7.2"
        if (tfm.StartsWith("v", StringComparison.OrdinalIgnoreCase))
            return tfm.Substring(1);

        return null;
    }


    private static decimal? TryParseNetMajorMinor(string tfm)
    {
        // net10.0 -> 10.0, net8.0 -> 8.0 (ignore netstandard/netframework)
        if (!tfm.StartsWith("net", StringComparison.OrdinalIgnoreCase)) return null;

        var s = tfm.Substring(3);

        // handle TFMs like "7.0-ios", "8.0-android", "10.0-windows10.0.19041.0"
        var dash = s.IndexOf('-');
        if (dash >= 0)
            s = s.Substring(0, dash);


        if (s.StartsWith("standard", StringComparison.OrdinalIgnoreCase)) return null;
        if (s.StartsWith("framework", StringComparison.OrdinalIgnoreCase)) return null;

        // accept "10.0", "8.0", also "80" style (net80 => 8.0)
        if (s.Length >= 2 && char.IsDigit(s[0]) && char.IsDigit(s[1]) && !s.Contains('.'))
        {
            // net80 => 8.0, net462 => 4.6 (treat as framework; ignore)
            if (s.Length >= 3) return null;
            var major = int.Parse(s[0].ToString());
            var minor = int.Parse(s[1].ToString());
            return major + (minor / 10m);
        }

        if (decimal.TryParse(s, out var v)) return v;
        return null;
    }

    private static string? TryReadReadmeDescription(string repoDir)
    {
        var readme = Path.Combine(repoDir, "README.md");
        if (!File.Exists(readme)) return null;

        try
        {
            // Grab first non-empty, non-heading line (simple + robust)
            foreach (var line in File.ReadLines(readme))
            {
                var t = line.Trim();
                if (string.IsNullOrWhiteSpace(t)) continue;
                if (t.StartsWith("#")) continue;
                return t.Length > 4000 ? t[..4000] : t;
            }
        }
        catch { /* ignore */ }

        return null;
    }

    private static string NormalizeRelPath(string path)
        => path.Replace('\\', '/');

    private static string CombineUrl(string root, string repoName)
    {
        if (string.IsNullOrWhiteSpace(root)) return repoName;
        if (!root.EndsWith("/")) root += "/";
        return root + repoName;
    }

    private static string? InferProvider(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        return url.Contains("github.com", StringComparison.OrdinalIgnoreCase) ? "GitHub" : "Unknown";
    }
}
