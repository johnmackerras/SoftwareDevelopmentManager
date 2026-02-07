using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SolutionManagerDatabase.Schema;

namespace SolutionManagerDatabase.Configure;

public sealed class DbClassMemberConfigure : IEntityTypeConfiguration<DbClassMember>
{
    public void Configure(EntityTypeBuilder<DbClassMember> b)
    {
        b.ToTable("ClassMembers");

        b.HasKey(x => x.Id);

        b.Property(x => x.MemberKind).HasMaxLength(16).IsRequired();
        b.Property(x => x.MemberName).HasMaxLength(256).IsRequired();
        b.Property(x => x.TypeRaw).HasMaxLength(512).IsRequired();

        b.Property(x => x.AttributesRaw).HasMaxLength(4000);
        b.Property(x => x.RelativeFilePath).HasMaxLength(1024).IsRequired();

        b.Property(x => x.IsRequired).IsRequired();
        b.Property(x => x.IsKey).IsRequired();

        b.Property(x => x.MaxLength);
        b.Property(x => x.MinLength);

        b.Property(x => x.SqlTypeName).HasMaxLength(128);
        b.Property(x => x.DataType).HasMaxLength(128);
        b.Property(x => x.DisplayName).HasMaxLength(256);

        b.Property(x => x.ForeignKey).HasMaxLength(256);
        b.Property(x => x.InverseProperty).HasMaxLength(512);

        b.Property(x => x.TypeDisplay).HasMaxLength(512);
        b.Property(x => x.IsCollection).IsRequired();
        b.Property(x => x.ElementTypeRaw).HasMaxLength(512);
        b.Property(x => x.DisplayFormatString).HasMaxLength(256);
        b.Property(x => x.DisplayFormatApplyInEditMode).IsRequired();

        b.HasIndex(x => new { x.DeclaringClassArtifactId, x.SpanStart }).IsUnique();

        // Avoid multiple cascade paths: keep cascade via DeclaringClassArtifact; Project FK is NoAction
        b.HasOne(x => x.Project)
            .WithMany()
            .HasForeignKey(x => x.ProjectId)
            .OnDelete(DeleteBehavior.NoAction);

        b.HasOne(x => x.DeclaringClassArtifact)
            .WithMany()
            .HasForeignKey(x => x.DeclaringClassArtifactId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
