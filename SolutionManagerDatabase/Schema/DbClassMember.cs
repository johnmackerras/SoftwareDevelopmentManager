using System;

namespace SolutionManagerDatabase.Schema;

public sealed class DbClassMember
{
    public long Id { get; set; }

    public long ProjectId { get; set; }
    public DbProject Project { get; set; } = null!;

    public long DeclaringClassArtifactId { get; set; }
    public DbArtifact DeclaringClassArtifact { get; set; } = null!;

    public string MemberKind { get; set; } = null!;     // "Field" | "Property"
    public string MemberName { get; set; } = null!;     // e.g. "Name"
    public string TypeRaw { get; set; } = null!;        // e.g. "string", "List<Child>", "DbContact?"
    public string? AttributesRaw { get; set; }          // "[Required];[StringLength(60)]"

    public bool IsNullable { get; set; }                // best-effort
    public bool HasGetter { get; set; }                 // properties only
    public bool HasSetter { get; set; }                 // properties only
    public bool IsInitOnly { get; set; }                // properties only

    public bool IsStatic { get; set; }
    public bool IsAbstract { get; set; }                // properties can be abstract; fields won't be
    public bool IsVirtual { get; set; }
    public bool IsOverride { get; set; }

    public string RelativeFilePath { get; set; } = null!; // gitroot-relative
    public int SpanStart { get; set; }
    public int SpanLength { get; set; }

    public DateTime UpdatedOnUtc { get; set; } = DateTime.UtcNow;
}
