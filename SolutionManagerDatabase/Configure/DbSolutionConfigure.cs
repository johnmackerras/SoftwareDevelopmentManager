using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SolutionManagerDatabase.Schema;

namespace SolutionManagerDatabase.Configure;

public sealed class DbSolutionConfigure : IEntityTypeConfiguration<DbSolution>
{
    public void Configure(EntityTypeBuilder<DbSolution> b)
    {
        b.ToTable("Solution");

        b.HasKey(x => x.Id);

        b.Property(x => x.Name).HasMaxLength(256).IsRequired();
        b.Property(x => x.Description).HasMaxLength(4000);

        b.Property(x => x.SolutionFilePath).HasMaxLength(1024).IsRequired();
        b.Property(x => x.SolutionFile).HasMaxLength(256).IsRequired();

        b.Property(x => x.ProjectType).HasMaxLength(64);
        b.Property(x => x.RuntimePlatform).HasMaxLength(64);
        b.Property(x => x.RuntimeVersion).HasMaxLength(32);

        b.HasIndex(x => new { x.RepositoryId, x.SolutionFilePath }).IsUnique();

        b.HasMany(x => x.Projects)
            .WithOne(x => x.Solution)
            .HasForeignKey(x => x.SolutionId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
