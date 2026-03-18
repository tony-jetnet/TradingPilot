using System.Collections.Generic;
using Shouldly;
using TradingPilot.Trading;
using Xunit;

namespace TradingPilot.Trading;

public class StrategyRuleEvaluatorTests
{
    [Fact]
    public void IsRuleTradeworthy_HighConfidence_LargeSample_PositivePnl_ReturnsTrue()
    {
        var rule = new StrategyRule
        {
            Confidence = 0.62m,
            ExpectedPnlPer100Shares = 5.0m,
            SampleSize = 50,
        };

        StrategyRuleEvaluator.IsRuleTradeworthy(rule).ShouldBeTrue();
    }

    [Fact]
    public void IsRuleTradeworthy_LowConfidence_ReturnsFalse()
    {
        var rule = new StrategyRule
        {
            Confidence = 0.50m, // Below 0.55 threshold
            ExpectedPnlPer100Shares = 10.0m,
            SampleSize = 100,
        };

        StrategyRuleEvaluator.IsRuleTradeworthy(rule).ShouldBeFalse();
    }

    [Fact]
    public void IsRuleTradeworthy_NegativePnl_ReturnsFalse()
    {
        var rule = new StrategyRule
        {
            Confidence = 0.70m,
            ExpectedPnlPer100Shares = -2.0m, // Negative
            SampleSize = 100,
        };

        StrategyRuleEvaluator.IsRuleTradeworthy(rule).ShouldBeFalse();
    }

    [Fact]
    public void IsRuleTradeworthy_SmallSample_ReturnsFalse()
    {
        var rule = new StrategyRule
        {
            Confidence = 0.70m,
            ExpectedPnlPer100Shares = 10.0m,
            SampleSize = 20, // Below 30 threshold
        };

        StrategyRuleEvaluator.IsRuleTradeworthy(rule).ShouldBeFalse();
    }

    [Fact]
    public void IsRuleTradeworthy_ExactBoundary_ReturnsTrue()
    {
        var rule = new StrategyRule
        {
            Confidence = 0.55m,
            ExpectedPnlPer100Shares = 0.01m, // Just barely positive
            SampleSize = 30,
        };

        StrategyRuleEvaluator.IsRuleTradeworthy(rule).ShouldBeTrue();
    }

    [Fact]
    public void IsRuleTradeworthy_ZeroPnl_ReturnsFalse()
    {
        var rule = new StrategyRule
        {
            Confidence = 0.60m,
            ExpectedPnlPer100Shares = 0m, // Zero, not positive
            SampleSize = 50,
        };

        StrategyRuleEvaluator.IsRuleTradeworthy(rule).ShouldBeFalse();
    }

    [Fact]
    public void FindMatchingRule_FiltersOutUntradeworthyRules()
    {
        var evaluator = new StrategyRuleEvaluator();
        evaluator.SetConfig(new StrategyConfig
        {
            GlobalRules = new GlobalRules { MinConfidence = 0.50m, MinSampleSize = 10 },
            Symbols = new Dictionary<string, SymbolStrategy>
            {
                ["NVDA"] = new SymbolStrategy
                {
                    TickerId = 913243251,
                    Rules = new List<StrategyRule>
                    {
                        new()
                        {
                            Id = "NVDA-001",
                            Direction = "BUY",
                            Confidence = 0.50m, // Below IsRuleTradeworthy threshold
                            ExpectedPnlPer100Shares = 5.0m,
                            SampleSize = 50,
                            Conditions = new RuleConditions(),
                        },
                        new()
                        {
                            Id = "NVDA-002",
                            Direction = "BUY",
                            Confidence = 0.60m, // Good confidence
                            ExpectedPnlPer100Shares = -1.0m, // Negative PnL
                            SampleSize = 50,
                            Conditions = new RuleConditions(),
                        },
                    }
                }
            }
        });

        var indicators = new IndicatorSnapshot();
        var result = evaluator.FindMatchingRule(913243251, "NVDA", 10, indicators);

        // Both rules should be filtered out by IsRuleTradeworthy
        result.ShouldBeNull();
    }

    [Fact]
    public void FindMatchingRule_SelectsHighestConfidenceTradeworthyRule()
    {
        var evaluator = new StrategyRuleEvaluator();
        evaluator.SetConfig(new StrategyConfig
        {
            GlobalRules = new GlobalRules { MinConfidence = 0.50m, MinSampleSize = 10 },
            Symbols = new Dictionary<string, SymbolStrategy>
            {
                ["TSLA"] = new SymbolStrategy
                {
                    TickerId = 913255598,
                    Rules = new List<StrategyRule>
                    {
                        new()
                        {
                            Id = "TSLA-001",
                            Direction = "BUY",
                            Confidence = 0.58m,
                            ExpectedPnlPer100Shares = 3.0m,
                            SampleSize = 40,
                            Conditions = new RuleConditions(),
                        },
                        new()
                        {
                            Id = "TSLA-002",
                            Direction = "BUY",
                            Confidence = 0.65m, // Higher confidence
                            ExpectedPnlPer100Shares = 7.0m,
                            SampleSize = 60,
                            Conditions = new RuleConditions(),
                        },
                    }
                }
            }
        });

        var indicators = new IndicatorSnapshot();
        var result = evaluator.FindMatchingRule(913255598, "TSLA", 10, indicators);

        result.ShouldNotBeNull();
        result.Value.Rule.Id.ShouldBe("TSLA-002");
    }
}
