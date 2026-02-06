using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SolutionManagerDatabase.Schema;

namespace SolutionManagerDatabase.Configure;

public sealed class DbArtifactConfigure : IEntityTypeConfiguration<DbArtifact>
{
    public void Configure(EntityTypeBuilder<DbArtifact> b)
    {
        b.ToTable("Artifact");

        b.HasKey(x => x.Id);

        b.Property(x => x.ArtifactType).HasMaxLength(64).IsRequired();
        b.Property(x => x.ArtifactSubType).HasMaxLength(64);
        b.Property(x => x.BaseTypeName).HasMaxLength(512);

        b.Property(x => x.LogicalName).HasMaxLength(256).IsRequired();
        b.Property(x => x.RelativeFilePath).HasMaxLength(1024).IsRequired();

        b.Property(x => x.Namespace).HasMaxLength(512);
        b.Property(x => x.ClassName).HasMaxLength(256);

        b.HasIndex(x => new { x.ProjectId, x.RelativeFilePath, x.LogicalName, x.SpanStart }).IsUnique();

        b.HasOne(x => x.Project)
            .WithMany(p => p.Artifacts)
            .HasForeignKey(x => x.ProjectId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
