using System;

namespace SolutionManagerDatabase.Schema;

public sealed class DbDbSet
{
    public long Id { get; set; }

    public long ProjectId { get; set; }
    public DbProject Project { get; set; } = null!;

    public long DbContextArtifactId { get; set; }
    public DbArtifact DbContextArtifact { get; set; } = null!;

    public string DbContextName { get; set; } = null!;   // e.g. "MainDbContext"
    public string DbSetName { get; set; } = null!;       // e.g. "Contacts"
    public string EntityType { get; set; } = null!;      // e.g. "Contact" or "My.Namespace.Contact"

    public string? Namespace { get; set; }               // file/class namespace best-effort
    public string RelativeFilePath { get; set; } = null!; // gitroot-relative

    public int SpanStart { get; set; }                   // property location
    public int SpanLength { get; set; }

    public DateTime UpdatedOnUtc { get; set; } = DateTime.UtcNow;
}
