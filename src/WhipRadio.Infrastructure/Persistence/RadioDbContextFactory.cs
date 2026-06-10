using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace WhipRadio.Infrastructure.Persistence;

/// <summary>Design-time factory so `dotnet ef migrations` works without a running host.</summary>
public class RadioDbContextFactory : IDesignTimeDbContextFactory<RadioDbContext>
{
    public RadioDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<RadioDbContext>()
            .UseSqlite("Data Source=design-time.db")
            .Options;
        return new RadioDbContext(options);
    }
}
