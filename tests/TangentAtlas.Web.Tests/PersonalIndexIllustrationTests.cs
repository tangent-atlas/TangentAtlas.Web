using TangentAtlas.Web.Illustration;

namespace TangentAtlas.Web.Tests;

public class PersonalIndexIllustrationTests
{
    private static IndexIllustration Illustrate(int companies, RiskSetting risk, double target = 0.15, decimal cash = 100_000m) =>
        PersonalIndexIllustration.Illustrate(new DialSettings(companies, risk, 500_000m, cash, target));

    [Fact]
    public void TheCurveIsShapedLikeTheMarket()
    {
        var all = PersonalIndexIllustration.IndexWeights(500);

        Assert.Equal(1.0, all.Sum(), 9);
        Assert.InRange(all[0], 0.07, 0.085);
        Assert.InRange(all.Take(10).Sum(), 0.37, 0.43);
        Assert.True(all.Zip(all.Skip(1)).All(pair => pair.First > pair.Second));   // weighted by size, largest first
    }

    [Fact]
    public void FewerCompaniesMeansMoreRisk()
    {
        var risks = PersonalIndexIllustration.CompanyStops
            .Select(n => Illustrate(n, RiskSetting.FullyInvested).Volatility)
            .ToArray();

        Assert.True(risks.Zip(risks.Skip(1)).All(pair => pair.First > pair.Second));
        Assert.InRange(risks[^1], 0.15, 0.18);   // all 500: about an index fund's long-run volatility
    }

    [Fact]
    public void HoldingRiskSteadyLandsOnTheSettingWithCash()
    {
        var ten = Illustrate(10, RiskSetting.HoldSteady, target: 0.15);

        Assert.Equal(0.15, ten.Volatility, 9);
        Assert.InRange(ten.CashWeight, 0.15, 0.25);
        Assert.False(ten.TargetAboveIndex);
        Assert.Equal(1.0, ten.LargestWeights.Sum() + ten.OtherCompaniesWeight + ten.CashWeight, 9);
    }

    [Fact]
    public void ASettingAboveTheIndexsOwnRiskNeverBorrows()
    {
        var all = Illustrate(500, RiskSetting.HoldSteady, target: 0.25);

        Assert.True(all.TargetAboveIndex);
        Assert.Equal(0.0, all.CashWeight);
        Assert.Equal(all.FullyInvestedVolatility, all.Volatility, 9);
    }

    [Fact]
    public void FixedCashLowersRiskInProportion()
    {
        var fixedCash = Illustrate(25, RiskSetting.FixedCash, cash: 100_000m);

        Assert.Equal(0.2, fixedCash.CashWeight, 9);
        Assert.Equal(0.8 * fixedCash.FullyInvestedVolatility, fixedCash.Volatility, 9);
    }

    [Fact]
    public void ABadYearIsTwoToThreeTimesTheVolatilityInDollars()
    {
        var steady = Illustrate(10, RiskSetting.HoldSteady, target: 0.10);

        Assert.Equal(100_000m, steady.BadYearLow);
        Assert.Equal(150_000m, steady.BadYearHigh);
    }

    [Theory]
    [InlineData(100_000, 500_000, 0.08)]   // the loss he could live with, divided by about 2.5
    [InlineData(0, 500_000, 0.05)]         // never below the dial's lowest setting
    [InlineData(500_000, 500_000, 0.25)]   // nor above its highest
    public void TheLossYouCouldLiveWithSetsTheDial(int loss, int account, double expected)
    {
        Assert.Equal(expected, PersonalIndexIllustration.TargetForLoss(loss, account), 9);
    }
}
