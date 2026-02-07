using System;

namespace SolutionManagerDatabase.Schema;

public sealed class DbGroupingOverride
{
    public long Id { get; set; }

    // Exact-match selectors; null = match anything
    public string? RepositoryName { get; set; }
    public string? SolutionName { get; set; }
    public string? ProjectName { get; set; }
    public string? ClassName { get; set; }

    public string Module { get; set; } = null!;
    public string? Visibility { get; set; } = null!;     //Schema/Domain/Viewmodel
    public string? Feature { get; set; }


    // For uniqueness across all fields (human readable, stable)
    public string OverrideKey { get; set; } = null!;

    public DateTime UpdatedOnUtc { get; set; } = DateTime.UtcNow;

    public static string BuildKey(
        string? repo,
        string? solution,
        string? project,
        string? className,
        string module,
        string? visibility,
        string? feature)

    {
        return $"{repo ?? ""}|{solution ?? ""}|{project ?? ""}|{className ?? ""}|{module}|{visibility ?? ""}|{feature ?? ""}";
    }
}
