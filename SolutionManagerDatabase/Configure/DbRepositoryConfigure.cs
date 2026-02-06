using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SolutionManagerDatabase.Schema;

namespace SolutionManagerDatabase.Configure;

public sealed class DbRepositoryConfigure : IEntityTypeConfiguration<DbRepository>
{
    public void Configure(EntityTypeBuilder<DbRepository> b)
    {
        b.ToTable("Repository");

        b.HasKey(x => x.Id);

        b.Property(x => x.RepositoryName).HasMaxLength(256).IsRequired();
        b.Property(x => x.RepoRootRelativePath).HasMaxLength(512).IsRequired();

        b.Property(x => x.RepositoryUrl).HasMaxLength(1024);
        b.Property(x => x.RepositoryProvider).HasMaxLength(64);
        b.Property(x => x.GitHeadSha).HasMaxLength(64);
        b.Property(x => x.DefaultBranch).HasMaxLength(128);
        b.Property(x => x.CurrentBranch).HasMaxLength(128);

        b.HasIndex(x => x.RepoRootRelativePath).IsUnique();

        b.HasMany(x => x.Solutions)
            .WithOne(x => x.Repository)
            .HasForeignKey(x => x.RepositoryId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
