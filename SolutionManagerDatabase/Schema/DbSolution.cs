using System;
using System.Collections.Generic;

namespace SolutionManagerDatabase.Schema;

public sealed class DbSolution
{
    public long Id { get; set; }

    public long RepositoryId { get; set; }
    public DbRepository Repository { get; set; } = null!;

    public string Name { get; set; } = null!;
    public string? Description { get; set; }

    public DateTime? CreatedOnUtc { get; set; }

    public string SolutionFilePath { get; set; } = null!; // relative to repo root
    public string SolutionFile { get; set; } = null!;     // filename only

    public string? ProjectType { get; set; }       // freeform: "MVC", "MAUI", "JS"
    public string? RuntimePlatform { get; set; }   // ".NET", "Node.js"
    public string? RuntimeVersion { get; set; }    // "10.0"

    public DateTime UpdatedOnUtc { get; set; } = DateTime.UtcNow;

    public List<DbProject> Projects { get; set; } = new();
}
