using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SolutionManagerDatabase.Schema;

namespace SolutionManagerDatabase.Configure;

public sealed class DbControllerActionConfigure : IEntityTypeConfiguration<DbControllerAction>
{
    public void Configure(EntityTypeBuilder<DbControllerAction> b)
    {
        b.ToTable("ControllerActions");

        b.HasKey(x => x.Id);

        b.Property(x => x.ControllerName).HasMaxLength(256).IsRequired();
        b.Property(x => x.ActionName).HasMaxLength(256).IsRequired();

        b.Property(x => x.HttpMethod).HasMaxLength(16).IsRequired();
        b.Property(x => x.RouteTemplate).HasMaxLength(1024);

        b.Property(x => x.ReturnType).HasMaxLength(256);
        b.Property(x => x.Parameters).HasMaxLength(2048);

        b.Property(x => x.RelativeFilePath).HasMaxLength(1024).IsRequired();

        b.HasIndex(x => new { x.ControllerArtifactId, x.SpanStart }).IsUnique();

        b.HasOne(x => x.Project)
            .WithMany()
            .HasForeignKey(x => x.ProjectId)
            .OnDelete(DeleteBehavior.NoAction);

        b.HasOne(x => x.ControllerArtifact)
            .WithMany()
            .HasForeignKey(x => x.ControllerArtifactId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
