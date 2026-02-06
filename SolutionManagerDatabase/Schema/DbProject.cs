using System;

namespace SolutionManagerDatabase.Schema;

// Represents a project within a solution, such as a .csproj file in a .sln
// This is not a Terrrafirma project, but rather a project as defined in the solution file, which may be a .NET project, a JavaScript project, etc.

public sealed class DbProject
{
    public long Id { get; set; }

    public long SolutionId { get; set; }
    public DbSolution Solution { get; set; } = null!;

    public string Name { get; set; } = null!;
    public string RelativeProjectPath { get; set; } = null!; // relative to repo root

    public string? ProjectType { get; set; }
    public string? TargetFrameworks { get; set; }

    public string? Platform { get; set; }
    public string? UiStack { get; set; }

    public bool IsWebApp { get; set; }
    public bool IsClassLibrary { get; set; }
    public bool IsTestProject { get; set; }

    public DateTime UpdatedOnUtc { get; set; } = DateTime.UtcNow;
}
