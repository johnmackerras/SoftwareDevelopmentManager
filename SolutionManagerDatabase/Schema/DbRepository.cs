using System;
using System.Collections.Generic;

namespace SolutionManagerDatabase.Schema;

public sealed class DbRepository
{
    public long Id { get; set; }

    public string RepositoryName { get; set; } = null!;
    public string RepoRootRelativePath { get; set; } = null!; // e.g. "DevHelper"

    public string? RepositoryUrl { get; set; }
    public string? RepositoryProvider { get; set; } // e.g. "GitHub"
    public bool? IsPrivateRepo { get; set; } = true; // cannot be reliably inferred offline

    public DateTime? GitFirstCommitUtc { get; set; }
    public DateTime? GitLastCommitUtc { get; set; }
    public string? GitHeadSha { get; set; }

    public string? DefaultBranch { get; set; }
    public string? CurrentBranch { get; set; }

    public DateTime CreatedOnUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedOnUtc { get; set; } = DateTime.UtcNow;

    public List<DbSolution> Solutions { get; set; } = new();
}
