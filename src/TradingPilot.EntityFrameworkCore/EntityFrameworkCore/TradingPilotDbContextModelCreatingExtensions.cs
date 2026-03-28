using Microsoft.EntityFrameworkCore;
using TradingPilot.Symbols;
using TradingPilot.Trading;
using Volo.Abp;
using Volo.Abp.EntityFrameworkCore.Modeling;

namespace TradingPilot.EntityFrameworkCore;

public static class TradingPilotDbContextModelCreatingExtensions
{
    public static void ConfigureTradingPilot(this ModelBuilder builder)
    {
        Check.NotNull(builder, nameof(builder));

        builder.Entity<Symbol>(b =>
        {
            b.ToTable(TradingPilotConsts.DbTablePrefix + "Symbols", TradingPilotConsts.DbSchema);
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).HasMaxLength(10);
            b.Property(x => x.Name).IsRequired().HasMaxLength(200);
            b.Property(x => x.WebullTickerId).IsRequired();
            b.Property(x => x.Exchange).HasMaxLength(20);
            b.Property(x => x.SecurityType).IsRequired().HasDefaultValue(SecurityType.Stock);
            b.Property(x => x.Sector).HasMaxLength(100);
            b.Property(x => x.Industry).HasMaxLength(100);
            b.Property(x => x.Status).IsRequired().HasDefaultValue(SymbolStatus.Active);
            b.Property(x => x.IsShortable).IsRequired().HasDefaultValue(true);
            b.Property(x => x.IsMarginable).IsRequired().HasDefaultValue(true);
            b.Property(x => x.IsWatched).IsRequired().HasDefaultValue(false);
            b.Property(x => x.IsActiveForTrading).IsRequired().HasDefaultValue(false);

            b.HasIndex(x => x.WebullTickerId).IsUnique().HasDatabaseName("IX_Symbols_WebullTickerId");
            b.HasIndex(x => x.IsWatched).HasFilter("\"IsWatched\" = true").HasDatabaseName("IX_Symbols_IsWatched");
        });

        builder.Entity<SymbolBar>(b =>
        {
            b.ToTable(TradingPilotConsts.DbTablePrefix + "SymbolBars", TradingPilotConsts.DbSchema);
            b.ConfigureByConvention();

            b.Property(x => x.SymbolId).IsRequired();
            b.Property(x => x.Timeframe).IsRequired();
            b.Property(x => x.Timestamp).IsRequired();
            b.Property(x => x.Open).IsRequired().HasPrecision(12, 4);
            b.Property(x => x.High).IsRequired().HasPrecision(12, 4);
            b.Property(x => x.Low).IsRequired().HasPrecision(12, 4);
            b.Property(x => x.Close).IsRequired().HasPrecision(12, 4);
            b.Property(x => x.Volume).IsRequired();
            b.Property(x => x.Vwap).HasPrecision(12, 4);
            b.Property(x => x.ChangeRatio).HasPrecision(8, 6);

            b.HasOne<Symbol>().WithMany().HasForeignKey(x => x.SymbolId).OnDelete(DeleteBehavior.Restrict);

            b.HasIndex(x => new { x.SymbolId, x.Timeframe, x.Timestamp }).IsUnique()
                .HasDatabaseName("IX_SymbolBars_SymbolId_Timeframe_Timestamp");
            b.HasIndex(x => new { x.Timestamp, x.Timeframe })
                .HasDatabaseName("IX_SymbolBars_Timestamp_Timeframe");
        });

        builder.Entity<SymbolBookSnapshot>(b =>
        {
            b.ToTable(TradingPilotConsts.DbTablePrefix + "SymbolBookSnapshots", TradingPilotConsts.DbSchema);
            b.ConfigureByConvention();

            b.Property(x => x.SymbolId).IsRequired();
            b.Property(x => x.Timestamp).IsRequired();
            b.Property(x => x.BidPrices).IsRequired().HasColumnType("jsonb");
            b.Property(x => x.BidSizes).IsRequired().HasColumnType("jsonb");
            b.Property(x => x.AskPrices).IsRequired().HasColumnType("jsonb");
            b.Property(x => x.AskSizes).IsRequired().HasColumnType("jsonb");
            b.Property(x => x.Spread).IsRequired().HasPrecision(10, 4);
            b.Property(x => x.MidPrice).IsRequired().HasPrecision(12, 4);
            b.Property(x => x.Imbalance).IsRequired().HasPrecision(6, 4);
            b.Property(x => x.Depth).IsRequired();

            b.HasOne<Symbol>().WithMany().HasForeignKey(x => x.SymbolId).OnDelete(DeleteBehavior.Restrict);

            b.HasIndex(x => new { x.SymbolId, x.Timestamp }).IsUnique()
                .HasDatabaseName("IX_SymbolBookSnapshots_SymbolId_Timestamp");
            b.HasIndex(x => x.Timestamp)
                .HasDatabaseName("IX_SymbolBookSnapshots_Timestamp");
        });

        builder.Entity<SymbolNews>(b =>
        {
            b.ToTable(TradingPilotConsts.DbTablePrefix + "SymbolNews", TradingPilotConsts.DbSchema);
            b.ConfigureByConvention();

            b.Property(x => x.SymbolId).IsRequired();
            b.Property(x => x.WebullNewsId).IsRequired();
            b.Property(x => x.Title).IsRequired().HasMaxLength(500);
            b.Property(x => x.Summary).HasMaxLength(2000);
            b.Property(x => x.SourceName).HasMaxLength(200);
            b.Property(x => x.Url).HasMaxLength(1000);
            b.Property(x => x.PublishedAt).IsRequired();
            b.Property(x => x.CollectedAt).IsRequired();

            // Day trading: news scoring fields
            b.Property(x => x.SentimentScore).HasPrecision(6, 4);
            b.Property(x => x.SentimentMethod).HasMaxLength(20);
            b.Property(x => x.CatalystType).HasMaxLength(50);

            b.HasOne<Symbol>().WithMany().HasForeignKey(x => x.SymbolId).OnDelete(DeleteBehavior.Restrict);

            b.HasIndex(x => new { x.SymbolId, x.WebullNewsId }).IsUnique()
                .HasDatabaseName("IX_SymbolNews_SymbolId_WebullNewsId");
            b.HasIndex(x => new { x.SymbolId, x.PublishedAt })
                .HasDatabaseName("IX_SymbolNews_SymbolId_PublishedAt");
        });

        builder.Entity<SymbolCapitalFlow>(b =>
        {
            b.ToTable(TradingPilotConsts.DbTablePrefix + "SymbolCapitalFlows", TradingPilotConsts.DbSchema);
            b.ConfigureByConvention();

            b.Property(x => x.SymbolId).IsRequired();
            b.Property(x => x.Date).IsRequired();
            b.Property(x => x.SuperLargeInflow).HasPrecision(18, 4);
            b.Property(x => x.SuperLargeOutflow).HasPrecision(18, 4);
            b.Property(x => x.LargeInflow).HasPrecision(18, 4);
            b.Property(x => x.LargeOutflow).HasPrecision(18, 4);
            b.Property(x => x.MediumInflow).HasPrecision(18, 4);
            b.Property(x => x.MediumOutflow).HasPrecision(18, 4);
            b.Property(x => x.SmallInflow).HasPrecision(18, 4);
            b.Property(x => x.SmallOutflow).HasPrecision(18, 4);
            b.Property(x => x.CollectedAt).IsRequired();

            b.HasOne<Symbol>().WithMany().HasForeignKey(x => x.SymbolId).OnDelete(DeleteBehavior.Restrict);

            b.HasIndex(x => new { x.SymbolId, x.Date }).IsUnique()
                .HasDatabaseName("IX_SymbolCapitalFlows_SymbolId_Date");
        });

        builder.Entity<SymbolFinancialSnapshot>(b =>
        {
            b.ToTable(TradingPilotConsts.DbTablePrefix + "SymbolFinancialSnapshots", TradingPilotConsts.DbSchema);
            b.ConfigureByConvention();

            b.Property(x => x.SymbolId).IsRequired();
            b.Property(x => x.Date).IsRequired();
            b.Property(x => x.Pe).HasPrecision(12, 4);
            b.Property(x => x.ForwardPe).HasPrecision(12, 4);
            b.Property(x => x.Eps).HasPrecision(12, 4);
            b.Property(x => x.EstEps).HasPrecision(12, 4);
            b.Property(x => x.MarketCap).HasPrecision(18, 2);
            b.Property(x => x.Volume).HasPrecision(18, 2);
            b.Property(x => x.AvgVolume).HasPrecision(18, 2);
            b.Property(x => x.High52w).HasPrecision(12, 4);
            b.Property(x => x.Low52w).HasPrecision(12, 4);
            b.Property(x => x.Beta).HasPrecision(8, 4);
            b.Property(x => x.DividendYield).HasPrecision(8, 4);
            b.Property(x => x.ShortFloat).HasPrecision(8, 4);
            b.Property(x => x.NextEarningsDate).HasMaxLength(50);
            b.Property(x => x.RawJson).HasColumnType("jsonb");
            b.Property(x => x.CollectedAt).IsRequired();

            b.HasOne<Symbol>().WithMany().HasForeignKey(x => x.SymbolId).OnDelete(DeleteBehavior.Restrict);

            b.HasIndex(x => new { x.SymbolId, x.Date }).IsUnique()
                .HasDatabaseName("IX_SymbolFinancialSnapshots_SymbolId_Date");
        });

        builder.Entity<TradingSignalRecord>(b =>
        {
            b.ToTable(TradingPilotConsts.DbTablePrefix + "TradingSignals", TradingPilotConsts.DbSchema);
            b.HasKey(x => x.Id);
            b.Property(x => x.Price).HasPrecision(12, 4);
            b.Property(x => x.Score).HasPrecision(8, 6);
            b.Property(x => x.ObiSmoothed).HasPrecision(8, 6);
            b.Property(x => x.Wobi).HasPrecision(8, 6);
            b.Property(x => x.PressureRoc).HasPrecision(8, 6);
            b.Property(x => x.SpreadSignal).HasPrecision(8, 6);
            b.Property(x => x.LargeOrderSignal).HasPrecision(8, 6);
            b.Property(x => x.Spread).HasPrecision(10, 4);
            b.Property(x => x.Imbalance).HasPrecision(8, 6);
            b.Property(x => x.Reason).HasMaxLength(500);
            b.Property(x => x.PriceAfter1Min).HasPrecision(12, 4);
            b.Property(x => x.PriceAfter5Min).HasPrecision(12, 4);
            b.Property(x => x.PriceAfter15Min).HasPrecision(12, 4);
            b.Property(x => x.PriceAfter30Min).HasPrecision(12, 4);
            b.Property(x => x.Type).HasConversion<byte>();
            b.Property(x => x.Strength).HasConversion<byte>();

            // Day trading: signal source and setup context
            b.Property(x => x.SetupScore).HasPrecision(8, 6);
            b.Property(x => x.TimingScore).HasPrecision(8, 6);
            b.Property(x => x.ContextScore).HasPrecision(8, 6);

            // Day trading: higher-timeframe indicators
            b.Property(x => x.Ema50).HasPrecision(12, 4);
            b.Property(x => x.Ema20_5m).HasPrecision(12, 4);
            b.Property(x => x.Ema50_5m).HasPrecision(12, 4);
            b.Property(x => x.Rsi14_5m).HasPrecision(8, 4);
            b.Property(x => x.Ema20_15m).HasPrecision(12, 4);
            b.Property(x => x.Ema50_15m).HasPrecision(12, 4);
            b.Property(x => x.Rsi14_15m).HasPrecision(8, 4);
            b.Property(x => x.TrendStrength).HasPrecision(8, 6);
            b.Property(x => x.VwapDeviation).HasPrecision(8, 6);
            b.Property(x => x.CapitalFlowScore).HasPrecision(8, 6);
            b.Property(x => x.RelativeVolume).HasPrecision(8, 4);

            // Day trading: news context
            b.Property(x => x.NewsSentiment).HasPrecision(6, 4);
            b.Property(x => x.SignalCatalystType).HasMaxLength(50);

            // Day trading: longer horizon verification
            b.Property(x => x.PriceAfter1Hr).HasPrecision(12, 4);
            b.Property(x => x.PriceAfter2Hr).HasPrecision(12, 4);
            b.Property(x => x.PriceAfter4Hr).HasPrecision(12, 4);

            b.HasOne<Symbol>().WithMany().HasForeignKey(x => x.SymbolId).OnDelete(DeleteBehavior.Restrict);

            b.HasIndex(x => new { x.SymbolId, x.Timestamp })
                .HasDatabaseName("IX_TradingSignals_SymbolId_Timestamp");
            b.HasIndex(x => x.Timestamp)
                .HasDatabaseName("IX_TradingSignals_Timestamp");
            b.HasIndex(x => new { x.Type, x.Strength, x.Timestamp })
                .HasDatabaseName("IX_TradingSignals_Type_Strength_Timestamp");
            b.HasIndex(x => x.VerifiedAt)
                .HasDatabaseName("IX_TradingSignals_VerifiedAt");
        });

        // TickSnapshots table removed — indicators now stored directly in TradingSignals
        // PaperTrades table removed — broker API is sole source of truth for orders/positions

        // DISPLAY ONLY — not used for trading decisions. Broker API remains sole source of truth.
        builder.Entity<CompletedTrade>(b =>
        {
            b.ToTable(TradingPilotConsts.DbTablePrefix + "CompletedTrades", TradingPilotConsts.DbSchema);
            b.HasKey(x => x.Id);
            b.Property(x => x.Ticker).IsRequired().HasMaxLength(20);
            b.Property(x => x.EntryPrice).HasPrecision(12, 4);
            b.Property(x => x.ExitPrice).HasPrecision(12, 4);
            b.Property(x => x.Pnl).HasPrecision(12, 4);
            b.Property(x => x.EntryScore).HasPrecision(8, 6);
            b.Property(x => x.EntrySource).HasMaxLength(20);
            b.Property(x => x.ExitReason).HasMaxLength(500);

            // Day trading fields
            b.Property(x => x.SetupScore).HasPrecision(8, 6);
            b.Property(x => x.TimingScore).HasPrecision(8, 6);
            b.Property(x => x.StopDistance).HasPrecision(12, 4);

            b.HasIndex(x => x.ExitTime).HasDatabaseName("IX_CompletedTrades_ExitTime");
            b.HasIndex(x => new { x.Ticker, x.ExitTime }).HasDatabaseName("IX_CompletedTrades_Ticker_ExitTime");
        });

        builder.Entity<BrokerSymbolMapping>(b =>
        {
            b.ToTable(TradingPilotConsts.DbTablePrefix + "BrokerSymbolMappings", TradingPilotConsts.DbSchema);
            b.HasKey(x => x.Id);
            b.Property(x => x.SymbolId).IsRequired().HasMaxLength(20);
            b.Property(x => x.BrokerName).IsRequired().HasMaxLength(50);
            b.Property(x => x.BrokerSymbolId).IsRequired().HasMaxLength(50);

            b.HasOne<Symbol>().WithMany().HasForeignKey(x => x.SymbolId).OnDelete(DeleteBehavior.Restrict);

            b.HasIndex(x => new { x.SymbolId, x.BrokerName })
                .IsUnique()
                .HasDatabaseName("IX_BrokerSymbolMappings_Symbol_Broker");
            b.HasIndex(x => new { x.BrokerName, x.BrokerSymbolId })
                .HasDatabaseName("IX_BrokerSymbolMappings_Broker_BrokerId");
        });

        builder.Entity<BarSetup>(b =>
        {
            b.ToTable(TradingPilotConsts.DbTablePrefix + "BarSetups", TradingPilotConsts.DbSchema);
            b.HasKey(x => x.Id);

            b.Property(x => x.SymbolId).IsRequired();
            b.Property(x => x.Timestamp).IsRequired();
            b.Property(x => x.Strength).HasPrecision(8, 6);
            b.Property(x => x.EntryZoneLow).HasPrecision(12, 4);
            b.Property(x => x.EntryZoneHigh).HasPrecision(12, 4);
            b.Property(x => x.StopLevel).HasPrecision(12, 4);
            b.Property(x => x.TargetLevel).HasPrecision(12, 4);
            b.Property(x => x.Price).HasPrecision(12, 4);

            // 1m indicators
            b.Property(x => x.Ema9).HasPrecision(12, 4);
            b.Property(x => x.Ema20).HasPrecision(12, 4);
            b.Property(x => x.Rsi14).HasPrecision(8, 4);
            b.Property(x => x.Vwap).HasPrecision(12, 4);
            b.Property(x => x.Atr14).HasPrecision(10, 4);
            b.Property(x => x.VolumeRatio).HasPrecision(8, 4);

            // 5m indicators
            b.Property(x => x.Ema20_5m).HasPrecision(12, 4);
            b.Property(x => x.Ema50_5m).HasPrecision(12, 4);
            b.Property(x => x.Rsi14_5m).HasPrecision(8, 4);
            b.Property(x => x.Atr14_5m).HasPrecision(10, 4);

            // 15m indicators
            b.Property(x => x.Ema20_15m).HasPrecision(12, 4);
            b.Property(x => x.Ema50_15m).HasPrecision(12, 4);
            b.Property(x => x.Rsi14_15m).HasPrecision(8, 4);

            // Context
            b.Property(x => x.CapitalFlowScore).HasPrecision(8, 6);
            b.Property(x => x.NewsSentiment).HasPrecision(6, 4);
            b.Property(x => x.CatalystType).HasMaxLength(50);

            // Outcomes
            b.Property(x => x.PriceAfter1Hr).HasPrecision(12, 4);
            b.Property(x => x.PriceAfter2Hr).HasPrecision(12, 4);
            b.Property(x => x.PriceAfter4Hr).HasPrecision(12, 4);
            b.Property(x => x.MaxFavorable).HasPrecision(12, 4);
            b.Property(x => x.MaxAdverse).HasPrecision(12, 4);

            b.HasOne<Symbol>().WithMany().HasForeignKey(x => x.SymbolId).OnDelete(DeleteBehavior.Restrict);

            b.HasIndex(x => new { x.SymbolId, x.Timestamp })
                .HasDatabaseName("IX_BarSetups_SymbolId_Timestamp");
            b.HasIndex(x => new { x.SetupType, x.Direction })
                .HasDatabaseName("IX_BarSetups_SetupType_Direction");
        });

        builder.Entity<DailyWatchlist>(b =>
        {
            b.ToTable(TradingPilotConsts.DbTablePrefix + "DailyWatchlists", TradingPilotConsts.DbSchema);
            b.HasKey(x => x.Id);

            b.Property(x => x.Date).IsRequired();
            b.Property(x => x.Selections).IsRequired().HasColumnType("jsonb");
            b.Property(x => x.CreatedAt).IsRequired();

            b.HasIndex(x => x.Date).IsUnique()
                .HasDatabaseName("IX_DailyWatchlists_Date");
        });
    }
}
