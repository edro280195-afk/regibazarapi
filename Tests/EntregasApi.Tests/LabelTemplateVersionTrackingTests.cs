using EntregasApi.Data;
using EntregasApi.Models;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace EntregasApi.Tests;

public class LabelTemplateVersionTrackingTests
{
    [Fact]
    public void AddNextDraft_TracksTheNewVersionForInsert()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql("Host=localhost;Database=label_tracking;Username=test;Password=test")
            .Options;
        using var db = new AppDbContext(options);
        var template = new LabelTemplate();
        db.Attach(template);

        var nextDraft = new LabelTemplateVersion
        {
            LabelTemplateId = template.Id,
            VersionNumber = 2,
            DesignJson = "{}"
        };

        db.LabelTemplateVersions.Add(nextDraft);

        Assert.Equal(EntityState.Added, db.Entry(nextDraft).State);
    }
}
