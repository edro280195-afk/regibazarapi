using EntregasApi.Data;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace EntregasApi.Tests;

public class LabelPrintJobsQueryTests
{
    [Fact]
    public void PackagePrintGraph_WithANavigationCycle_IsRejectedForNoTrackingQueries()
    {
        using var db = CreateDbContext();
        var query = db.OrderPackages
            .AsNoTracking()
            .Include(current => current.Order)
            .ThenInclude(order => order.Packages)
            .AsSplitQuery();

        Assert.Throws<InvalidOperationException>(() => query.ToQueryString());
    }

    [Fact]
    public void PackagePrintGraph_CompilesWithoutANoTrackingNavigationCycle()
    {
        using var db = CreateDbContext();

        var query = db.OrderPackages
            .AsNoTracking()
            .Include(current => current.Order)
            .ThenInclude(order => order.Client)
            .Include(current => current.Order)
            .ThenInclude(order => order.Items)
            .AsSplitQuery();

        var sql = query.ToQueryString();

        Assert.Contains("OrderPackages", sql, StringComparison.Ordinal);
    }

    private static AppDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql("Host=localhost;Database=label_print_query;Username=test;Password=test")
            .Options;
        return new AppDbContext(options);
    }
}
