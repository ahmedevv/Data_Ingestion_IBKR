using System.Collections.Concurrent;

namespace API
{
    public class StockData
    {
        public string Symbol { get; set; }
        public double LastPrice { get; set; }

        public double BarOpen { get; set; }
        public double BarHigh { get; set; }
        public double BarLow { get; set; }
        public double BarClose { get; set; }
        public long BarVolume { get; set; }
        public bool HasTicksInCurrentBar { get; set; }

        public bool IsInitialized { get; set; } = false;

        private string[] _allFields = new string[103];
        public ConcurrentQueue<string> BarBuffer = new ConcurrentQueue<string>();
        private readonly object _lock = new object();

        public StockData()
        {
            for (int i = 0; i < 103; i++) _allFields[i] = "0";
            ResetBar(0);
        }

        public void ProcessTick(double price, long size = 0)
        {
            lock (_lock)
            {
                if (price <= 0) return;

                IsInitialized = true; // Opens the forward-fill gate natively

                if (!HasTicksInCurrentBar)
                {
                    BarOpen = price; BarHigh = price; BarLow = price;
                    HasTicksInCurrentBar = true;
                }

                if (price > BarHigh) BarHigh = price;
                if (price < BarLow) BarLow = price;

                BarClose = price;
                BarVolume += size;
                LastPrice = price;
            }
        }

        public void UpdateRawValue(int field, string value)
        {
            if (field >= 0 && field < 103)
            {
                _allFields[field] = value;
                IsInitialized = true;
            }
        }

        public string GetRawValue(int field)
        {
            return (field >= 0 && field < 103) ? _allFields[field] : "0";
        }

        public void ResetBar(double startingPrice)
        {
            lock (_lock)
            {
                BarOpen = BarHigh = BarLow = BarClose = startingPrice;
                BarVolume = 0;
                HasTicksInCurrentBar = false;
            }
        }
    }
}