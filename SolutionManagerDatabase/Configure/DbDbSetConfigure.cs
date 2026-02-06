using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SolutionManagerDatabase.Schema;

namespace SolutionManagerDatabase.Configure;

public sealed class DbDbSetConfigure : IEntityTypeConfiguration<DbDbSet>
{
    public void Configure(EntityTypeBuilder<DbDbSet> b)
    {
        b.ToTable("DbSets");

        b.HasKey(x => x.Id);

        b.Property(x => x.DbContextName).HasMaxLength(256).IsRequired();
        b.Property(x => x.DbSetName).HasMaxLength(256).IsRequired();
        b.Property(x => x.EntityType).HasMaxLength(512).IsRequired();

        b.Property(x => x.Namespace).HasMaxLength(512);
        b.Property(x => x.RelativeFilePath).HasMaxLength(1024).IsRequired();

        b.HasIndex(x => new { x.DbContextArtifactId, x.SpanStart }).IsUnique();

        // Avoid multiple cascade paths: keep cascade via DbContextArtifact; Project FK is NoAction
        b.HasOne(x => x.Project)
            .WithMany()
            .HasForeignKey(x => x.ProjectId)
            .OnDelete(DeleteBehavior.NoAction);

        b.HasOne(x => x.DbContextArtifact)
            .WithMany()
            .HasForeignKey(x => x.DbContextArtifactId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
