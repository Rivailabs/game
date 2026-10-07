using System;
using System.Collections.Generic;
using System.Linq;

namespace AstraKingdoms.Meta.Reporting
{
    /// <summary>One settled store transaction line (sale, refund or chargeback) for one period/region/population.</summary>
    public sealed class IapLine
    {
        public string OrderId { get; }
        public bool IsRefundOrChargeback { get; }
        /// <summary>Customer charge (positive for sales, the reversed charge as a positive number for refunds).</summary>
        public decimal Gross { get; }
        public decimal IndirectTax { get; }
        public decimal StoreFee { get; }
        public bool IsTestPurchase { get; }

        public IapLine(string orderId, bool isRefundOrChargeback, decimal gross, decimal indirectTax, decimal storeFee, bool isTestPurchase = false)
        {
            OrderId = orderId;
            IsRefundOrChargeback = isRefundOrChargeback;
            Gross = gross;
            IndirectTax = indirectTax;
            StoreFee = storeFee;
            IsTestPurchase = isTestPurchase;
        }
    }

    /// <summary>Ad-network funnel and settled revenue for one period. Each stage is recorded separately.</summary>
    public sealed class AdPeriod
    {
        public long Opportunities { get; set; }
        public long OptIns { get; set; }
        public long Fills { get; set; }
        public long PaidImpressions { get; set; }
        public long RewardCompletions { get; set; }
        /// <summary>Realised publisher eCPM (after network share), in the reporting currency.</summary>
        public decimal RealizedPublisherEcpm { get; set; }
        /// <summary>Invalid-traffic and other network adjustments (positive number deducted).</summary>
        public decimal InvalidTrafficAdjustment { get; set; }
    }

    public sealed class CostPeriod
    {
        public decimal MatchAndProfileServices { get; set; }
        public decimal Database { get; set; }
        public decimal Bandwidth { get; set; }
        public decimal Storage { get; set; }
        public decimal OtherUsage { get; set; }
        public decimal VariableSupportAndModeration { get; set; }
        public decimal FixedOperations { get; set; }
        public decimal DevelopmentAndContent { get; set; }
        public decimal AttributableAcquisition { get; set; }

        public decimal Variable => MatchAndProfileServices + Database + Bandwidth + Storage + OtherUsage + VariableSupportAndModeration;
    }

    public sealed class RevenueReport
    {
        public decimal NetIap { get; set; }
        public decimal NetAds { get; set; }
        public decimal GameNet => NetIap + NetAds;
        public decimal VariableCost { get; set; }
        public decimal Contribution => GameNet - VariableCost;
        public decimal OperatingResult { get; set; }
        public decimal AcquisitionContribution { get; set; }
        public decimal? NetRevenuePerMau { get; set; }
        public decimal? BackendCostPerMau { get; set; }
        public decimal? CostPerCompletedMatch { get; set; }
        public decimal? CostPerPlayerHour { get; set; }
        public double AverageConcurrency { get; set; }
        public IReadOnlyList<string> Warnings { get; set; } = Array.Empty<string>();
    }

    /// <summary>
    /// The plan's revenue and contribution model ("Revenue and contribution model"). Inputs must share
    /// one period, region and player population; the caller is responsible for that selection.
    /// No eCPM or earnings figure is assumed anywhere: ad revenue is only computed from observed values.
    /// </summary>
    public static class RevenueModel
    {
        /// <summary>
        /// Net IAP: charges minus indirect taxes, store deductions and refunds/chargebacks. A refund
        /// line is applied once per order id even if the export repeats it; test purchases are excluded.
        /// </summary>
        public static decimal NetIap(IEnumerable<IapLine> lines, List<string> warnings = null)
        {
            decimal net = 0;
            var refunded = new HashSet<string>(StringComparer.Ordinal);
            var sold = new HashSet<string>(StringComparer.Ordinal);
            foreach (IapLine l in lines.Where(x => !x.IsTestPurchase))
            {
                decimal lineNet = l.Gross - l.IndirectTax - l.StoreFee;
                if (l.IsRefundOrChargeback)
                {
                    if (!refunded.Add(l.OrderId))
                    {
                        warnings?.Add("duplicate refund line ignored for order " + l.OrderId);
                        continue;
                    }
                    net -= lineNet;
                }
                else
                {
                    if (!sold.Add(l.OrderId))
                    {
                        warnings?.Add("duplicate sale line ignored for order " + l.OrderId);
                        continue;
                    }
                    net += lineNet;
                }
            }
            return net;
        }

        /// <summary>Net ads = paid impressions × realised publisher eCPM ÷ 1,000 − invalid-traffic adjustments.</summary>
        public static decimal NetAds(AdPeriod ads) =>
            ads == null ? 0 : ads.PaidImpressions * ads.RealizedPublisherEcpm / 1000m - ads.InvalidTrafficAdjustment;

        public static RevenueReport Compute(IEnumerable<IapLine> iap, AdPeriod ads, CostPeriod costs, long monthlyActiveUsers,
            long completedMatches, decimal playerHours, double hoursInPeriod)
        {
            var warnings = new List<string>();
            var r = new RevenueReport
            {
                NetIap = NetIap(iap ?? Array.Empty<IapLine>(), warnings),
                NetAds = NetAds(ads),
                VariableCost = costs?.Variable ?? 0,
            };
            r.OperatingResult = r.Contribution - (costs?.FixedOperations ?? 0) - (costs?.DevelopmentAndContent ?? 0);
            r.AcquisitionContribution = r.Contribution - (costs?.AttributableAcquisition ?? 0);
            if (monthlyActiveUsers > 0)
            {
                r.NetRevenuePerMau = r.GameNet / monthlyActiveUsers;
                r.BackendCostPerMau = r.VariableCost / monthlyActiveUsers;
            }
            if (completedMatches > 0) r.CostPerCompletedMatch = r.VariableCost / completedMatches;
            if (playerHours > 0) r.CostPerPlayerHour = r.VariableCost / playerHours;
            r.AverageConcurrency = hoursInPeriod > 0 ? (double)playerHours / hoursInPeriod : 0;
            if (ads != null && (ads.OptIns > ads.Opportunities || ads.Fills > ads.OptIns || ads.PaidImpressions > ads.Fills))
                warnings.Add("ad funnel stages are not monotonic; check instrumentation");
            warnings.Add("average concurrency is not peak concurrency; capacity planning must measure peaks");
            r.Warnings = warnings;
            return r;
        }
    }
}
