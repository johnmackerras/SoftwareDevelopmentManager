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

    public bool IsRequired { get; set; }
    public bool IsKey { get; set; }

    public int? MaxLength { get; set; }        // [MaxLength] / [StringLength]
    public int? MinLength { get; set; }        // [MinLength] / [StringLength]

    public string? SqlTypeName { get; set; }   // [Column(TypeName="...")]
    public string? DataType { get; set; }      // [DataType(DataType.MultilineText)]
    public string? DisplayName { get; set; }   // [Display(Name="...")]

    public string? ForeignKey { get; set; }    // [ForeignKey("X")]
    public string? InverseProperty { get; set; } // [InverseProperty(nameof(...))]


    public string? TypeDisplay { get; set; }     // "string(50)", "string(max)", "decimal(18,2)", "List<Child>"
    public bool IsCollection { get; set; }
    public string? ElementTypeRaw { get; set; }
    public string? DisplayFormatString { get; set; }   // "{0:P4}"
    public bool DisplayFormatApplyInEditMode { get; set; }




    public string RelativeFilePath { get; set; } = null!; // gitroot-relative
    public int SpanStart { get; set; }
    public int SpanLength { get; set; }

    public DateTime UpdatedOnUtc { get; set; } = DateTime.UtcNow;
}
