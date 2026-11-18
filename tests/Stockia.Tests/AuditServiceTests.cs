using Microsoft.EntityFrameworkCore;
using Stockia.Web.Data;
using Stockia.Web.Domain;
using Stockia.Web.Services;
using Xunit;

namespace Stockia.Tests;

/// <summary>SR-6 (d2): the audit trail records who, what and when, and is read per company.</summary>
public class AuditServiceTests
{
    private sealed class TestClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 11, 18, 10, 15, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static async Task<(AuditService Service, StockiaDbContext Db, TestClock Clock, int A, int B, int UserA)> Build()
    {
        var options = new DbContextOptionsBuilder<StockiaDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        var db = new StockiaDbContext(options);
        var a = new Company { Name = "Company A" };
        var b = new Company { Name = "Company B" };
        db.Companies.AddRange(a, b);
        await db.SaveChangesAsync();
        var user = new AppUser { CompanyId = a.Id, Username = "joy", PasswordHash = "x", DisplayName = "Joy" };
        db.AppUsers.Add(user);
        await db.SaveChangesAsync();
        var clock = new TestClock();
        return (new AuditService(db, clock), db, clock, a.Id, b.Id, user.Id);
    }

    [Fact]
    public async Task Records_who_did_what_and_when()
    {
        var (service, db, clock, a, _, user) = await Build();
        await service.RecordAsync(a, user, AuditActions.EntryPosted, "JE-001", 7);

        var log = await db.AuditLogs.SingleAsync();
        Assert.Equal(a, log.CompanyId);
        Assert.Equal(user, log.UserId);
        Assert.Equal(7, log.EntryId);
        Assert.Equal("entry posted", log.Action);
        Assert.Equal("JE-001", log.Details);
        Assert.Equal(clock.Now.UtcDateTime, log.LogTime);
    }

    [Fact]
    public async Task Lists_newest_first_with_the_name_of_the_user_and_only_for_the_company()
    {
        var (service, db, clock, a, b, user) = await Build();
        await service.RecordAsync(a, user, "first", null);
        clock.Now = clock.Now.AddMinutes(5);
        await service.RecordAsync(a, user, "second", null);
        await service.RecordAsync(b, user, "other company", null);

        var list = await service.ListAsync(a);
        Assert.Equal(new[] { "second", "first" }, list.Select(x => x.Action));
        Assert.Equal("Joy", list[0].User!.DisplayName);
    }

    [Fact]
    public async Task The_list_can_be_limited()
    {
        var (service, _, clock, a, _, user) = await Build();
        for (var i = 0; i < 5; i++)
        {
            clock.Now = clock.Now.AddMinutes(1);
            await service.RecordAsync(a, user, "action " + i, null);
        }

        Assert.Equal(2, (await service.ListAsync(a, 2)).Count);
    }

    [Fact]
    public async Task The_history_of_an_entry_is_oldest_first_and_only_its_own_lines()
    {
        var (service, _, clock, a, b, user) = await Build();
        await service.RecordAsync(a, user, "posted", null, 1);
        clock.Now = clock.Now.AddMinutes(5);
        await service.RecordAsync(a, user, "reversed", null, 1);
        await service.RecordAsync(a, user, "other entry", null, 2);
        await service.RecordAsync(b, user, "other company, same entry id", null, 1);

        var history = await service.ForEntryAsync(a, 1);
        Assert.Equal(new[] { "posted", "reversed" }, history.Select(x => x.Action));
    }

    [Fact]
    public async Task Details_longer_than_400_characters_are_cut()
    {
        var (service, db, _, a, _, user) = await Build();
        await service.RecordAsync(a, user, "action", new string('x', 500));

        Assert.Equal(400, (await db.AuditLogs.SingleAsync()).Details!.Length);
    }
}