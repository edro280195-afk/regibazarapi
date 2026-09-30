using EntregasApi.Models;
using EntregasApi.Services;
using Xunit;

namespace EntregasApi.Tests;

public class OrderPackagePolicyTests
{
    private static readonly DateTime Now = new(2026, 9, 30, 15, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void EnsureTotal_CreatesEveryBagWithQr_WhenOrderHasNone()
    {
        var order = CreateOrder(12);

        var result = OrderPackagePolicy.EnsureTotal(order, 3, Now);

        Assert.Equal(new[] { 1, 2, 3 }, result.Created.Select(p => p.PackageNumber));
        Assert.All(result.Created, package =>
        {
            Assert.Equal(12, package.OrderId);
            Assert.Equal(PackageTrackingStatus.Packed, package.Status);
            Assert.Equal(Now, package.CreatedAt);
            Assert.StartsWith("RB-ORD12-PKG", package.QrCodeValue);
        });
        Assert.Equal(3, result.Created.Select(p => p.QrCodeValue).Distinct().Count());
        Assert.Equal(3, order.TotalPackages);
        Assert.True(order.PackagesConfirmed);
        Assert.True(order.IsFullyPacked);
        Assert.False(order.IsFullyLoaded);
        Assert.Equal(3, result.RequestedTotal);
        Assert.Equal(3, result.ActualTotal);
        Assert.False(result.KeptMoreThanRequested);
    }

    [Fact]
    public void EnsureTotal_OnlyCreatesTheMissingBags()
    {
        var order = CreateOrder(12, 1, 2);

        var result = OrderPackagePolicy.EnsureTotal(order, 3, Now);

        var created = Assert.Single(result.Created);
        Assert.Equal(3, created.PackageNumber);
        Assert.Equal(3, order.TotalPackages);
    }

    [Fact]
    public void EnsureTotal_NeverDeletesBags_WhenAskedForLess()
    {
        var order = CreateOrder(12, 1, 2, 3);

        var result = OrderPackagePolicy.EnsureTotal(order, 1, Now);

        Assert.Empty(result.Created);
        Assert.Equal(3, order.Packages.Count);
        Assert.Equal(3, order.TotalPackages);
        Assert.Equal(1, result.RequestedTotal);
        Assert.Equal(3, result.ActualTotal);
        Assert.True(result.KeptMoreThanRequested);
    }

    [Fact]
    public void EnsureTotal_ContinuesAfterTheLastNumber_EvenWhenABagWasDeleted()
    {
        var order = CreateOrder(12, 1, 3);

        var result = OrderPackagePolicy.EnsureTotal(order, 3, Now);

        var created = Assert.Single(result.Created);
        Assert.Equal(4, created.PackageNumber);
        Assert.Equal(3, order.TotalPackages);
    }

    [Fact]
    public void EnsureTotal_WithZeroAndNoBags_ConfirmsTheOrderGoesWithoutBags()
    {
        var order = CreateOrder(12);

        var result = OrderPackagePolicy.EnsureTotal(order, 0, Now);

        Assert.Empty(result.Created);
        Assert.Equal(0, order.TotalPackages);
        Assert.True(order.PackagesConfirmed);
        Assert.False(order.IsFullyPacked);
    }

    [Fact]
    public void EnsureTotal_ClearsThePendingReminder()
    {
        var order = CreateOrder(12);
        order.PackagesReminderSentAt = Now.AddHours(-2);

        OrderPackagePolicy.EnsureTotal(order, 2, Now);

        Assert.Null(order.PackagesReminderSentAt);
    }

    [Fact]
    public void EnsureTotal_OnlyMarksTheOrderAsNotFullyLoaded_WhenNewBagsAppear()
    {
        var order = CreateOrder(12, 1, 2);
        order.IsFullyLoaded = true;

        OrderPackagePolicy.EnsureTotal(order, 2, Now);
        Assert.True(order.IsFullyLoaded);

        OrderPackagePolicy.EnsureTotal(order, 3, Now);
        Assert.False(order.IsFullyLoaded);
    }

    [Fact]
    public void EnsureTotal_RejectsOrdersThatAreNotSavedYet()
    {
        var order = CreateOrder(0);

        Assert.Throws<InvalidOperationException>(() => OrderPackagePolicy.EnsureTotal(order, 2, Now));
    }

    [Fact]
    public void AddPackages_StacksOnTopOfTheExistingBags()
    {
        var order = CreateOrder(12, 1);

        var result = OrderPackagePolicy.AddPackages(order, 2, Now);

        Assert.Equal(new[] { 2, 3 }, result.Created.Select(p => p.PackageNumber));
        Assert.Equal(3, order.TotalPackages);
        Assert.True(order.PackagesConfirmed);
    }

    [Fact]
    public void AddPackages_RequiresAPositiveCount()
    {
        var order = CreateOrder(12);

        Assert.Throws<ArgumentOutOfRangeException>(() => OrderPackagePolicy.AddPackages(order, 0, Now));
    }

    [Fact]
    public void ApplyDeclaration_WithNumber_CreatesTheBags()
    {
        var order = CreateOrder(12);

        var result = OrderPackagePolicy.ApplyDeclaration(order, 2, confirmed: true, Now);

        Assert.Equal(2, result.Created.Count);
        Assert.Equal(2, order.TotalPackages);
        Assert.True(order.PackagesConfirmed);
    }

    [Fact]
    public void ApplyDeclaration_Pending_KeepsTheOrderPendingWhenItHasNoBags()
    {
        var order = CreateOrder(12);

        var result = OrderPackagePolicy.ApplyDeclaration(order, null, confirmed: false, Now);

        Assert.Empty(result.Created);
        Assert.Null(order.TotalPackages);
        Assert.False(order.PackagesConfirmed);
    }

    [Fact]
    public void ApplyDeclaration_Pending_IsIgnoredWhenTheOrderAlreadyHasBags()
    {
        var order = CreateOrder(12, 1, 2);

        var result = OrderPackagePolicy.ApplyDeclaration(order, null, confirmed: false, Now);

        Assert.Empty(result.Created);
        Assert.Equal(2, order.TotalPackages);
        Assert.True(order.PackagesConfirmed);
    }

    [Fact]
    public void ApplyDeclaration_WithoutBags_DoesNotDropTheBagsThatExist()
    {
        var order = CreateOrder(12, 1, 2);

        var result = OrderPackagePolicy.ApplyDeclaration(order, 0, confirmed: true, Now);

        Assert.Equal(2, order.Packages.Count);
        Assert.Equal(2, order.TotalPackages);
        Assert.True(result.KeptMoreThanRequested);
    }

    [Fact]
    public void ApplyDeclaration_ConfirmingWithoutANumber_ConfirmsWhatExists()
    {
        var order = CreateOrder(12, 1);

        OrderPackagePolicy.ApplyDeclaration(order, null, confirmed: true, Now);

        Assert.Equal(1, order.TotalPackages);
        Assert.True(order.PackagesConfirmed);
    }

    [Fact]
    public void BagsCapturedAtCreation_SurviveGeneratingMoreFromTheOrder()
    {
        // El bug original: capturar 3 al crear solo guardaba el número; al entrar al pedido
        // había 0 bolsas y generar 1 dejaba el contador en 1, perdiendo las 3 capturadas.
        var order = CreateOrder(12);

        var atCreation = OrderPackagePolicy.ApplyDeclaration(order, 3, confirmed: true, Now);
        foreach (var package in atCreation.Created) order.Packages.Add(package); // lo que hace EF al guardar
        Assert.Equal(3, order.Packages.Count);
        Assert.Equal(3, order.TotalPackages);

        var fromTheOrder = OrderPackagePolicy.AddPackages(order, 1, Now);
        foreach (var package in fromTheOrder.Created) order.Packages.Add(package);

        Assert.Equal(4, order.Packages.Count);
        Assert.Equal(4, order.TotalPackages);
        Assert.Equal(new[] { 1, 2, 3, 4 }, order.Packages.Select(p => p.PackageNumber).OrderBy(n => n));
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData(0, false)]
    [InlineData(1, false)]
    [InlineData(OrderPackagePolicy.MaxPackagesPerOrder, false)]
    [InlineData(-1, true)]
    [InlineData(OrderPackagePolicy.MaxPackagesPerOrder + 1, true)]
    public void ValidateTotal_RejectsNegativeAndAbsurdNumbers(int? total, bool shouldBeRejected)
    {
        Assert.Equal(shouldBeRejected, OrderPackagePolicy.ValidateTotal(total) is not null);
    }

    [Theory]
    [InlineData(0, 3, false)]
    [InlineData(48, 2, false)]
    [InlineData(48, 3, true)]
    public void ValidateAddition_CountsTheBagsTheOrderAlreadyHas(int existing, int count, bool shouldBeRejected)
    {
        Assert.Equal(shouldBeRejected, OrderPackagePolicy.ValidateAddition(existing, count) is not null);
    }

    private static Order CreateOrder(int id, params int[] existingPackageNumbers)
    {
        var order = new Order { Id = id };
        foreach (var number in existingPackageNumbers)
        {
            order.Packages.Add(new OrderPackage
            {
                OrderId = id,
                PackageNumber = number,
                QrCodeValue = $"RB-ORD{id}-PKG{number:D8}",
            });
        }

        return order;
    }
}
