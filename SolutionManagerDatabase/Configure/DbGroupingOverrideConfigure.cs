using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SolutionManagerDatabase.Schema;

namespace SolutionManagerDatabase.Configure;

public sealed class DbGroupingOverrideConfigure : IEntityTypeConfiguration<DbGroupingOverride>
{
    public void Configure(EntityTypeBuilder<DbGroupingOverride> b)
    {
        b.ToTable("GroupingOverrides");

        b.HasKey(x => x.Id);

        b.Property(x => x.RepositoryName).HasMaxLength(256);
        b.Property(x => x.SolutionName).HasMaxLength(256);
        b.Property(x => x.ProjectName).HasMaxLength(256);
        b.Property(x => x.ClassName).HasMaxLength(256);

        b.Property(x => x.Module).HasMaxLength(50).IsRequired();
        b.Property(x => x.Visibility).HasMaxLength(50);
        b.Property(x => x.Feature).HasMaxLength(50);

        b.Property(x => x.OverrideKey).HasMaxLength(700).IsRequired();
        b.HasIndex(x => x.OverrideKey).IsUnique();
    }
}
