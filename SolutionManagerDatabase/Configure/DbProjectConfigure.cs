using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SolutionManagerDatabase.Schema;

namespace SolutionManagerDatabase.Configure;

public sealed class DbProjectConfigure : IEntityTypeConfiguration<DbProject>
{
    public void Configure(EntityTypeBuilder<DbProject> b)
    {
        b.ToTable("Project");

        b.HasKey(x => x.Id);

        b.Property(x => x.Name).HasMaxLength(256).IsRequired();
        b.Property(x => x.RelativeProjectPath).HasMaxLength(1024).IsRequired();

        b.Property(x => x.ProjectType).HasMaxLength(64);
        b.Property(x => x.TargetFrameworks).HasMaxLength(256);

        b.Property(x => x.Platform).HasMaxLength(64);
        b.Property(x => x.UiStack).HasMaxLength(64);

        b.HasIndex(x => new { x.SolutionId, x.RelativeProjectPath }).IsUnique();
    }
}
