using Microsoft.EntityFrameworkCore.Design;

namespace VrcPhotoManager.Data;

/// <summary>Lets `dotnet ef migrations add` construct a context without running the app.</summary>
public class VrcdnDbContextFactory : IDesignTimeDbContextFactory<VrcdnDbContext>
{
    public VrcdnDbContext CreateDbContext(string[] args) => new("design_time.db");
}
