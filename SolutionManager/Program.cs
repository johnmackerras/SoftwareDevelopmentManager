using Microsoft.AspNetCore.Builder;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using SolutionManagerDatabase.Context;
using SolutionManagerDatabase.Services;
using Terrafirma.Core.Options;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddControllersWithViews();

builder.Services.AddDbContext<ApplicationDbContext>(options =>
  options.UseSqlServer(builder.Configuration.GetConnectionString("ApplicationDbContext")));

builder.Services.Configure<SolutionManagerOptions>(builder.Configuration.GetSection("SolutionManager"));

builder.Services.AddScoped<ISolutionScanService, SolutionScanService>();
builder.Services.AddScoped<IArtifactScanService, ArtifactScanService>();
builder.Services.AddScoped<IControllerActionScanService, ControllerActionScanService>();
builder.Services.AddScoped<IDbSetScanService, DbSetScanService>();
builder.Services.AddScoped<IClassMemberScanService, ClassMemberScanService>();
builder.Services.AddScoped<IGroupingResolverService, GroupingResolverService>();

builder.Services.AddScoped<SolutionManagerDatabase.Services.Queries.IClassQueryService,
    SolutionManagerDatabase.Services.Queries.ClassQueryService>();


var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    app.UseHsts();
}
else
{
    app.UseDeveloperExceptionPage();
}

using (var scope = app.Services.CreateScope())
{
    var services = scope.ServiceProvider;

    var context = services.GetRequiredService<ApplicationDbContext>();
    //context.Database.EnsureDeleted();
    context.Database.EnsureCreated();
    // DbInitializer.Initialize(context);
}

app.UseHttpsRedirection();
app.UseRouting();

app.UseAuthorization();

app.MapStaticAssets();

app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Home}/{action=Index}/{id?}")
    .WithStaticAssets();

app.Run();
