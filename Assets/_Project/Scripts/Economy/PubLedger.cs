using System;
using System.Text;
using BarOwner.Bar;
using Unity.Netcode;
using UnityEngine;

namespace BarOwner.Economy
{
    /// <summary>
    /// The pub's books (server-authoritative): drink prices you set, reputation (0–5 stars) that
    /// grows or shrinks with how each day went, and today's takings/costs for the end-of-day report.
    /// The till balance itself lives in <see cref="BarTill"/>.
    /// </summary>
    public class PubLedger : NetworkBehaviour
    {
        public static PubLedger Instance { get; private set; }

        /// <summary>End-of-day report text on every client.</summary>
        public static event Action<string> DayReport;

        public const int MinPricePence = 100;
        public const int MaxPricePence = 1500;

        [SerializeField] private float startingReputation = 1f;

        private readonly NetworkVariable<int> _priceLager = new(450);
        private readonly NetworkVariable<int> _priceBitter = new(420);
        private readonly NetworkVariable<int> _priceCider = new(480);
        private readonly NetworkVariable<float> _reputation = new(1f);

        // Today's numbers (server only; summarised in the report).
        private int _sales, _tips, _deposits, _kegCosts, _openingBalance;
        private int _served, _walkouts, _pricedOut, _shortPints;

        public float Reputation => _reputation.Value;

        public override void OnNetworkSpawn()
        {
            Instance = this;
            if (IsServer) _reputation.Value = startingReputation;
        }

        public override void OnNetworkDespawn()
        {
            if (Instance == this) Instance = null;
        }

        // ---------------------------------------------------------------- prices

        public int PricePence(DrinkType drink) => drink switch
        {
            DrinkType.Lager => _priceLager.Value,
            DrinkType.Bitter => _priceBitter.Value,
            DrinkType.Cider => _priceCider.Value,
            _ => 0,
        };

        /// <summary>Any client: nudge a price up/down (e.g. ±10p from the price board).</summary>
        [Rpc(SendTo.Server)]
        public void RequestPriceChangeRpc(DrinkType drink, int deltaPence) =>
            ServerSetPrice(drink, PricePence(drink) + deltaPence);

        public void ServerSetPrice(DrinkType drink, int pence)
        {
            if (!IsServer) return;
            pence = Mathf.Clamp(pence, MinPricePence, MaxPricePence);
            switch (drink)
            {
                case DrinkType.Lager: _priceLager.Value = pence; break;
                case DrinkType.Bitter: _priceBitter.Value = pence; break;
                case DrinkType.Cider: _priceCider.Value = pence; break;
            }
        }

        /// <summary>
        /// How expensive this price feels vs. the standard price (1 = normal, 1.3 = 30% dearer).
        /// Customers compare it against their own tolerance; better reputation buys you more tolerance.
        /// </summary>
        public float PriceRatio(DrinkType drink)
        {
            int standard = Drinks.PricePence(drink);
            return standard > 0 ? (float)PricePence(drink) / standard : 1f;
        }

        public void ServerSetReputation(float value)
        {
            if (IsServer) _reputation.Value = Mathf.Clamp(value, 0f, 5f);
        }

        // ---------------------------------------------------------------- daily books (server)

        public void ServerBeginDay()
        {
            if (!IsServer) return;
            _sales = _tips = _deposits = _kegCosts = 0;
            _served = _walkouts = _pricedOut = _shortPints = 0;
            _openingBalance = BarTill.Instance != null ? BarTill.Instance.Pence : 0;
        }

        public void ServerRecordSale(int pricePaid, int tip, bool shortPint)
        {
            _sales += pricePaid;
            _tips += tip;
            _served++;
            if (shortPint) _shortPints++;
        }

        public void ServerRecordWalkout() => _walkouts++;
        public void ServerRecordPricedOut() => _pricedOut++;
        public void ServerRecordKegPurchase(int pence) => _kegCosts += pence;
        public void ServerRecordDeposit(int pence) => _deposits += pence;

        /// <summary>SERVER: close the books, move reputation, and show everyone the report.</summary>
        public void ServerEndDay(int day)
        {
            if (!IsServer) return;

            int customers = _served + _walkouts + _pricedOut;
            float satisfaction = customers == 0 ? 0f
                : (_served - 2f * _walkouts - 0.5f * _pricedOut - 0.5f * _shortPints) / customers;
            float before = _reputation.Value;
            float change = Mathf.Clamp(satisfaction * 0.5f, -0.5f, 0.5f);
            _reputation.Value = Mathf.Clamp(before + change, 0f, 5f);

            int income = _sales + _tips + _deposits;
            int closing = BarTill.Instance != null ? BarTill.Instance.Pence : 0;

            var sb = new StringBuilder();
            sb.AppendLine($"<size=40><b>DAY {day} REPORT</b></size>");
            sb.AppendLine();
            sb.AppendLine($"Pints sold   {_served}{(_shortPints > 0 ? $"  ({_shortPints} short)" : "")}");
            sb.AppendLine($"Walked out   {_walkouts}");
            sb.AppendLine($"Too pricey   {_pricedOut}");
            sb.AppendLine();
            sb.AppendLine($"Sales        {Money.Format(_sales)}");
            sb.AppendLine($"Tips         {Money.Format(_tips)}");
            if (_deposits > 0) sb.AppendLine($"Keg deposits {Money.Format(_deposits)}");
            sb.AppendLine($"Kegs bought  -{Money.Format(_kegCosts)}");
            sb.AppendLine($"<b>Profit       {Money.Format(income - _kegCosts)}</b>");
            sb.AppendLine();
            sb.AppendLine($"Till         {Money.Format(_openingBalance)} -> {Money.Format(closing)}");
            sb.AppendLine($"Reputation   {before:0.0} -> {_reputation.Value:0.0} / 5  ({change:+0.0;-0.0;+0.0})");
            sb.AppendLine();
            sb.Append("<size=20>Game saved. Press Enter to close.</size>");

            DayReportRpc(sb.ToString());
        }

        [Rpc(SendTo.Everyone)]
        private void DayReportRpc(string report) => DayReport?.Invoke(report);
    }
}
