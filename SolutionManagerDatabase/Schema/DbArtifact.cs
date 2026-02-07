using System;

namespace SolutionManagerDatabase.Schema;

public sealed class DbArtifact
{
    public long Id { get; set; }

    public long ProjectId { get; set; }
    public DbProject Project { get; set; } = null!;

    public string ArtifactType { get; set; } = null!;       // "Controller", "DbContext", "Service", "Class"
    public string? ArtifactSubType { get; set; }            // e.g. "Mvc", "Api", "DbContext", "IdentityDbContext"
    public string? BaseTypeName { get; set; }               // e.g. "ControllerBase", "IdentityDbContext<ApplicationUser>"

    public string? Namespace { get; set; }
    public string? ClassName { get; set; }                  // e.g. "ContactsController"
    public string? BaseClassName { get; set; }

    public string LogicalName { get; set; } = null!;        // e.g. "ContactsController"
    public string FileName { get; set; } = null!;           // e.g. "ContactsController.cs"

    public string? Module { get; set; }                     // e.g. "Contact"
    public string? Visibility { get; set; }                 // "Schema"/"Domain"/"Viewmodel"
    public string? Feature { get; set; }
    public string? LogicalClassKey { get; set; }


    public bool IsPartial { get; set; }
    public bool IsAbstract { get; set; }
    public bool IsStatic { get; set; }
    public string? InterfacesRaw { get; set; }              // "IDisposable, IFoo"


    public string RelativeFilePath { get; set; } = null!;   // repo-relative, e.g. "DevHelper/Controllers/ContactsController.cs"
        public long? FileSizeBytes { get; set; }
    public DateTime? FileLastWriteUtc { get; set; }
    public string? FileSha256 { get; set; } // hex





    public long? GroupingOverrideId { get; set; }                   //I don't see the need for this, better to just copy data into the artifact record for easier querying and to avoid joins
    public DbGroupingOverride? GroupingOverride { get; set; }       //I don't see the need for this






    public int SpanStart { get; set; }    // Roslyn node start (unique per declaration in a file)       - Character position where the class begins
    public int SpanLength { get; set; }   // optional but useful                                        - How many characters the class declaration occupies

    public DateTime UpdatedOnUtc { get; set; } = DateTime.UtcNow;
}
