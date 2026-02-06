using Microsoft.EntityFrameworkCore;
using SolutionManagerDatabase.Schema;
using System.Collections.Generic;

namespace SolutionManagerDatabase.Context
{
    public class ApplicationDbContext : DbContext
    {
        public ApplicationDbContext(DbContextOptions<ApplicationDbContext> options) : base(options)
        {
        }

        public DbSet<DbRepository> Repositories { get; set; }
        public DbSet<DbSolution> Solutions { get; set; }
        public DbSet<DbProject> DevProjects { get; set; }
        public DbSet<DbArtifact> Artifacts { get; set; }
        public DbSet<DbControllerAction> ControllerActions { get; set; }



        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.ApplyConfigurationsFromAssembly(typeof(ApplicationDbContext).Assembly);
        }

    }
}
