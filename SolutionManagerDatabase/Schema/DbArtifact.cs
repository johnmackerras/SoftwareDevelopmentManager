using System;

namespace SolutionManagerDatabase.Schema;

public sealed class DbArtifact
{
    public long Id { get; set; }

    public long ProjectId { get; set; }
    public DbProject Project { get; set; } = null!;

    public string ArtifactType { get; set; } = null!;      // "Controller", "DbContext", "Service", "Class"
    public string? ArtifactSubType { get; set; }  // e.g. "Mvc", "Api", "DbContext", "IdentityDbContext"
    public string? BaseTypeName { get; set; }     // e.g. "ControllerBase", "IdentityDbContext<ApplicationUser>"

    public string LogicalName { get; set; } = null!;       // e.g. "ContactsController"
    public string RelativeFilePath { get; set; } = null!;  // repo-relative, e.g. "DevHelper/Controllers/ContactsController.cs"

    public string? Namespace { get; set; }
    public string? ClassName { get; set; }                 // e.g. "ContactsController"

    public bool IsPartial { get; set; }
    public int SpanStart { get; set; }    // Roslyn node start (unique per declaration in a file)       - Character position where the class begins
    public int SpanLength { get; set; }   // optional but useful                                        - How many characters the class declaration occupies

    public DateTime UpdatedOnUtc { get; set; } = DateTime.UtcNow;
}
