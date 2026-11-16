using EntregasApi.Models;
using EntregasApi.Services;
using Xunit;

namespace EntregasApi.Tests;

public class OrderReminderPolicyTests
{
    // Hora local del negocio simulada como UTC-6 fijo, para que las pruebas no dependan de tzdata.
    private static readonly TimeZoneInfo Zone =
        TimeZoneInfo.CreateCustomTimeZone("MX-test", TimeSpan.FromHours(-6), "MX", "MX");

    // 2026-11-16 15:30 hora local = 21:30 UTC.
    private static readonly DateTime Now = new(2026, 11, 16, 21, 30, 0, DateTimeKind.Utc);
    // "Después de las 4:00 pm" de ese mismo día = 22:00 UTC.
    private static readonly DateTime FourPm = new(2026, 11, 16, 22, 0, 0, DateTimeKind.Utc);

    // ── Validate ────────────────────────────────────────────────────────────

    [Fact]
    public void Validate_AcceptsFutureTime()
    {
        Assert.Null(OrderReminderPolicy.Validate(FourPm, 30, "en su trabajo", Now));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(OrderReminderPolicy.MaxLeadMinutes + 1)]
    public void Validate_RejectsLeadOutOfRange(int lead)
    {
        Assert.NotNull(OrderReminderPolicy.Validate(FourPm, lead, null, Now));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(OrderReminderPolicy.MaxLeadMinutes)]
    public void Validate_AcceptsLeadBoundaries(int lead)
    {
        Assert.Null(OrderReminderPolicy.Validate(FourPm, lead, null, Now));
    }

    [Fact]
    public void Validate_RejectsTimeAlreadyPast()
    {
        var error = OrderReminderPolicy.Validate(Now.AddMinutes(-30), 0, null, Now);
        Assert.NotNull(error);
    }

    [Fact]
    public void Validate_ReportsMissingTimeInsteadOfPast()
    {
        // Un body sin targetAt llega como DateTime.MinValue: el error debe decir que falta, no que "ya pasó".
        var error = OrderReminderPolicy.Validate(default, 30, null, Now);
        Assert.NotNull(error);
        Assert.Contains("Falta", error);
    }

    [Fact]
    public void Validate_ToleratesSmallClockSkew()
    {
        Assert.Null(OrderReminderPolicy.Validate(Now.AddMinutes(-1), 0, null, Now));
    }

    [Fact]
    public void Validate_RejectsNoteLongerThanLimit()
    {
        var tooLong = new string('x', OrderReminderPolicy.MaxNoteLength + 1);
        Assert.NotNull(OrderReminderPolicy.Validate(FourPm, 30, tooLong, Now));
        Assert.Null(OrderReminderPolicy.Validate(FourPm, 30, new string('x', OrderReminderPolicy.MaxNoteLength), Now));
    }

    // ── RemindAt / helpers ──────────────────────────────────────────────────

    [Theory]
    [InlineData(0, 22, 0)]
    [InlineData(15, 21, 45)]
    [InlineData(30, 21, 30)]
    [InlineData(60, 21, 0)]
    public void ComputeRemindAt_SubtractsLead(int lead, int hour, int minute)
    {
        var remindAt = OrderReminderPolicy.ComputeRemindAt(FourPm, lead);
        Assert.Equal(new DateTime(2026, 11, 16, hour, minute, 0, DateTimeKind.Utc), remindAt);
    }

    [Theory]
    [InlineData(OrderStatus.Pending, true)]
    [InlineData(OrderStatus.Confirmed, true)]
    [InlineData(OrderStatus.InRoute, true)]
    [InlineData(OrderStatus.Postponed, true)]
    [InlineData(OrderStatus.Delivered, false)]
    [InlineData(OrderStatus.Canceled, false)]
    public void IsOrderActionable_OnlyWhilePendingDelivery(OrderStatus status, bool expected)
    {
        Assert.Equal(expected, OrderReminderPolicy.IsOrderActionable(status));
    }

    [Fact]
    public void NormalizeUtc_HandlesEveryKind()
    {
        var utc = new DateTime(2026, 11, 16, 22, 0, 0, DateTimeKind.Utc);
        Assert.Equal(utc, OrderReminderPolicy.NormalizeUtc(utc));

        var unspecified = new DateTime(2026, 11, 16, 22, 0, 0, DateTimeKind.Unspecified);
        var fromUnspecified = OrderReminderPolicy.NormalizeUtc(unspecified);
        Assert.Equal(DateTimeKind.Utc, fromUnspecified.Kind);
        Assert.Equal(utc, fromUnspecified);

        Assert.Equal(DateTimeKind.Utc, OrderReminderPolicy.NormalizeUtc(utc.ToLocalTime()).Kind);
        Assert.Equal(utc, OrderReminderPolicy.NormalizeUtc(utc.ToLocalTime()));
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("   ", null)]
    [InlineData("  en su trabajo  ", "en su trabajo")]
    public void CleanNote_TrimsAndNullsEmpty(string? input, string? expected)
    {
        Assert.Equal(expected, OrderReminderPolicy.CleanNote(input));
    }

    // ── Texto del push ──────────────────────────────────────────────────────

    [Fact]
    public void BuildMessage_ShowsClientTimeRelativeAndNote()
    {
        var (title, body) = OrderReminderPolicy.BuildMessage(
            "Camila Rodríguez", FourPm, "Será en su trabajo, otra dirección", Now, Zone);

        Assert.Equal("🔔 Entrega de Camila Rodríguez", title);
        Assert.Equal(
            "Hora acordada: hoy 4:00 pm (en 30 min). 📝 Será en su trabajo, otra dirección",
            body);
    }

    [Fact]
    public void BuildMessage_OmitsNoteWhenEmpty()
    {
        var (_, body) = OrderReminderPolicy.BuildMessage("Camila", FourPm, "  ", Now, Zone);
        Assert.Equal("Hora acordada: hoy 4:00 pm (en 30 min).", body);
        Assert.DoesNotContain("📝", body);
    }

    [Fact]
    public void BuildMessage_FallsBackWhenNoClientName()
    {
        var (title, _) = OrderReminderPolicy.BuildMessage("  ", FourPm, null, Now, Zone);
        Assert.Equal("🔔 Entrega de una clienta", title);
    }

    [Fact]
    public void BuildMessage_LabelsTomorrow()
    {
        var (_, body) = OrderReminderPolicy.BuildMessage("Ana", FourPm.AddDays(1), null, Now, Zone);
        Assert.StartsWith("Hora acordada: mañana 4:00 pm", body);
    }

    [Fact]
    public void BuildMessage_SaysHowLateWhenFiredAfterTheTime()
    {
        // El servidor estuvo caído: el aviso sale 20 min después de la hora de la clienta.
        var late = FourPm.AddMinutes(20);
        var (_, body) = OrderReminderPolicy.BuildMessage("Ana", FourPm, null, late, Zone);
        Assert.Contains("(hace 20 min)", body);
    }

    [Fact]
    public void BuildMessage_UsesLocalDayNotUtcDay()
    {
        // 8:00 pm hora local del 16 = 02:00 UTC del 17: debe decir "hoy", no "mañana".
        var nowLocalEvening = new DateTime(2026, 11, 17, 0, 0, 0, DateTimeKind.Utc); // 6:00 pm del 16 local
        var target = new DateTime(2026, 11, 17, 2, 0, 0, DateTimeKind.Utc);          // 8:00 pm del 16 local
        var (_, body) = OrderReminderPolicy.BuildMessage("Ana", target, null, nowLocalEvening, Zone);
        Assert.StartsWith("Hora acordada: hoy 8:00 pm (en 2 h)", body);
    }

    // ── Formato de hora y tiempos relativos ─────────────────────────────────

    [Theory]
    [InlineData(0, 0, "12:00 am")]
    [InlineData(0, 5, "12:05 am")]
    [InlineData(9, 30, "9:30 am")]
    [InlineData(12, 0, "12:00 pm")]
    [InlineData(16, 0, "4:00 pm")]
    [InlineData(23, 59, "11:59 pm")]
    public void FormatClock_Uses12HourClock(int hour, int minute, string expected)
    {
        Assert.Equal(expected, OrderReminderPolicy.FormatClock(new DateTime(2026, 1, 1, hour, minute, 0)));
    }

    [Theory]
    [InlineData(0, "ahora")]
    [InlineData(20, "en 20 min")]
    [InlineData(60, "en 1 h")]
    [InlineData(90, "en 1 h 30 min")]
    [InlineData(-45, "hace 45 min")]
    [InlineData(-180, "hace 3 h")]
    public void Relative_DescribesTheGap(int minutes, string expected)
    {
        Assert.Equal(expected, OrderReminderPolicy.Relative(TimeSpan.FromMinutes(minutes)));
    }
}
