using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using IBApi;

namespace API
{
    public partial class MainForm : Form, EWrapper
    {
        private EClientSocket clientSocket;
        private EReaderSignal readerSignal;
        private ConcurrentDictionary<int, StockData> stockHash = new ConcurrentDictionary<int, StockData>();

        // Configuration Paths
        private string csvFolder = @"C:\Projects\IBKR\API\StockData";
        private string tickerListPath = @"C:\Projects\IBKR\API\sp500.txt";
        private string logPath = Path.Combine(@"C:\Projects\IBKR\API\", "session_log.txt");

        private System.Windows.Forms.Timer saveTimer;

        // Resilience & Heartbeat State
        private bool isManuallyStopped = false;
        private DateTime lastDataReceived = DateTime.Now;
        private int reconnectAttempts = 0;

        public MainForm()
        {
            InitializeComponent();

            // Setup Dark Terminal
            rtbLogs.BackColor = Color.Black;
            rtbLogs.ForeColor = Color.White;
            rtbLogs.Font = new Font("Consolas", 10, FontStyle.Regular);

            readerSignal = new EReaderMonitorSignal();
            clientSocket = new EClientSocket(this, readerSignal);

            saveTimer = new System.Windows.Forms.Timer();
            saveTimer.Interval = 1000;
            saveTimer.Tick += OnSaveTimerTick;
            this.FormClosing += (s, e) => {
                Log("SYSTEM", "Shutting down... Performing final flush.", Color.Yellow);
                FlushBuffersToDisk();
                // Small delay to ensure the Task completes
                Thread.Sleep(2000);
            };
        }

        private void btnStart_Click(object sender, EventArgs e)
        {
            isManuallyStopped = false;
            reconnectAttempts = 0;
            PerformConnection();
        }

        private void PerformConnection()
        {
            try
            {
                if (!clientSocket.IsConnected())
                {
                    Log("SYSTEM", "Initiating Live Connection...", Color.Cyan);
                    if (!Directory.Exists(csvFolder)) Directory.CreateDirectory(csvFolder);

                    // Client ID 1 for primary ingestion
                    clientSocket.eConnect("127.0.0.1", 7496, 1);

                    var reader = new EReader(clientSocket, readerSignal);
                    reader.Start();

                    new Thread(() => {
                        try
                        {
                            while (clientSocket.IsConnected())
                            {
                                readerSignal.waitForSignal();
                                reader.processMsgs();
                            }
                        }
                        catch (Exception ex)
                        {
                            Log("FATAL", $"Reader Thread Error: {ex.Message}", Color.Red);
                        }
                    })
                    { IsBackground = true }.Start();

                    Thread.Sleep(1500);

                    if (clientSocket.IsConnected())
                    {
                        // TYPE 1 = LIVE MARKET DATA
                        clientSocket.reqMarketDataType(1);

                        StartSubscriptions();
                        saveTimer.Start();

                        btnStart.Enabled = false;
                        btnStart.Text = "LIVE";
                        lastDataReceived = DateTime.Now;
                        Log("SYSTEM", "Engine Operational - LIVE DATA MODE", Color.Lime);
                    }
                }
            }
            catch (Exception ex)
            {
                Log("CRASH", $"Connection Error: {ex.Message}", Color.Red);
            }
        }

        private void btnStop_Click_1(object sender, EventArgs e)
        {
            isManuallyStopped = true;
            HaltSystem();
        }

        private void HaltSystem()
        {
            saveTimer.Stop();
            if (clientSocket.IsConnected()) clientSocket.eDisconnect();
            ResetAllPrices();
            stockHash.Clear();
            btnStart.Enabled = true;
            btnStart.Text = "Start Engine";
            Log("SYSTEM", "Engine Stopped by User.", Color.OrangeRed);
        }

        private void ResetAllPrices()
        {
            foreach (var stock in stockHash.Values) stock.LastPrice = 0;
        }

        private void StartSubscriptions()
        {
            if (File.Exists(tickerListPath))
            {
                string[] symbols = File.ReadAllLines(tickerListPath);
                int idCounter = 1;
                Log("SUBS", $"Subscribing to {symbols.Length} symbols...", Color.Gray);

                foreach (var sym in symbols)
                {
                    string s = sym.Trim();
                    if (string.IsNullOrEmpty(s)) continue;

                    Contract c = new Contract { Symbol = s, SecType = "STK", Exchange = "SMART", Currency = "USD" };
                    stockHash.TryAdd(idCounter, new StockData { Symbol = s });

                    clientSocket.reqMktData(idCounter++, c, "", false, false, null);

                    // Rate Limit Guard (prevents IBKR Error 100)
                    Thread.Sleep(50);
                }
            }
            else { Log("FATAL", "sp500.txt not found in project directory!", Color.Red); }
        }

        private int flushCounter = 0;

        private void OnSaveTimerTick(object sender, EventArgs e)
        {
            if (!clientSocket.IsConnected() && !isManuallyStopped) return;

            string tsUtc = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss");

            // STAGE 1: Aggregate the second and push to Buffer
            foreach (var stock in stockHash.Values)
            {
                if (stock.HasTicksInCurrentBar)
                {
                    string csvLine = $"{tsUtc},{stock.BarOpen},{stock.BarHigh},{stock.BarLow},{stock.BarClose},{stock.BarVolume}";
                    stock.BarBuffer.Enqueue(csvLine);
                    stock.ResetBar(stock.BarClose);
                }
            }

            // STAGE 2: Every 60 seconds, flush to Disk
            flushCounter++;
            if (flushCounter >= 60)
            {
                FlushBuffersToDisk();
                flushCounter = 0;
            }
        }

        private void FlushBuffersToDisk()
        {
            int totalBarsSaved = 0;

            //  background thread to keep the UI responsive
            Task.Run(() => {
                foreach (var stock in stockHash.Values)
                {
                    if (stock.BarBuffer.IsEmpty) continue;

                    List<string> linesToWrite = new List<string>();
                    while (stock.BarBuffer.TryDequeue(out string line))
                    {
                        linesToWrite.Add(line);
                    }

                    try
                    {
                        string path = Path.Combine(csvFolder, $"{stock.Symbol}.csv");
                        if (!File.Exists(path))
                            File.WriteAllText(path, "timestamp_utc,open,high,low,close,volume\n");

                        File.AppendAllLines(path, linesToWrite);
                        totalBarsSaved += linesToWrite.Count;
                    }
                    catch (IOException)
                    {
                        // If the file is locked (e.g., open in Excel), 
                        // re-enqueue the data so we don't lose it!
                        foreach (var line in linesToWrite) stock.BarBuffer.Enqueue(line);
                        Log("IO-ERROR", $"File locked for {stock.Symbol}. Data buffered for next attempt.", Color.Orange);
                    }
                }

                if (totalBarsSaved > 0)
                    Log("FLUSH", $"Batch saved {totalBarsSaved} bars to disk.", Color.Gold);
            });
        }

        private void ReconnectSystem()
        {
            saveTimer.Stop();
            if (clientSocket.IsConnected()) clientSocket.eDisconnect();

            reconnectAttempts++;
            Log("RECOVERY", $"Attempting Reconnect #{reconnectAttempts} in 5s...", Color.Yellow);

            Task.Delay(5000).ContinueWith(t => {
                if (!isManuallyStopped) this.Invoke(new Action(() => PerformConnection()));
            });
        }

        public void Log(string tag, string msg, Color color)
        {
            if (rtbLogs.InvokeRequired)
            {
                rtbLogs.Invoke(new Action(() => Log(tag, msg, color)));
                return;
            }

            rtbLogs.SelectionStart = rtbLogs.TextLength;
            rtbLogs.SelectionColor = Color.DarkGray;
            rtbLogs.AppendText($"[{DateTime.Now:HH:mm:ss}] ");

            rtbLogs.SelectionColor = color;
            rtbLogs.AppendText($"[{tag.PadRight(10)}] {msg}{Environment.NewLine}");
            rtbLogs.ScrollToCaret();

            // Background Thread File Logging
            Task.Run(() => {
                try { File.AppendAllText(logPath, $"[{tag}] {msg}{Environment.NewLine}"); } catch { }
            });
        }

        // --- EWrapper Implementation ---
        public void tickPrice(int tickerId, int field, double price, TickAttrib attribs)
        {
            // Capture Last (4) and Delayed Last (68)
            if ((field == 4 || field == 68) && price > 0)
            {
                if (stockHash.TryGetValue(tickerId, out StockData data))
                {
                    data.ProcessTick(price);
                    lastDataReceived = DateTime.Now;
                }
            }
        }
        public void tickSize(int tickerId, int field, int size)
        {
            // Field 8 = Total Volume for the day. 
            // We calculate incremental volume by watching the change in field 8 or using field 5 (Last Size).
            if (field == 5 && size > 0)
            {
                if (stockHash.TryGetValue(tickerId, out StockData data))
                {
                    data.ProcessTick(data.LastPrice, size);
                }
            }
        }

        public void error(int id, int errorCode, string errorMsg)
        {
            // Ignore background system messages
            if (errorCode == 2104 || errorCode == 2106 || errorCode == 2158) return;

            // Critical connection errors
            if (errorCode == 1100 || errorCode == 100 || errorCode == 504 || errorCode == 1101)
            {
                Log("IBKR-ERR", $"Code {errorCode}: {errorMsg}", Color.Red);
                if (!isManuallyStopped) ReconnectSystem();
            }
            else
            {
                Log("IBKR-WARN", $"Code {errorCode}: {errorMsg}", Color.Yellow);
            }
        }

        public void connectionClosed()
        {
            if (!isManuallyStopped) { ResetAllPrices(); ReconnectSystem(); }
        }

        // Mandatory Boilerplate (Empty)
        public void error(Exception e) => Log("FATAL", e.Message, Color.Red);
        public void error(string str) => Log("ERR", str, Color.Red);
        public void connectAck() { }
        public void nextValidId(int orderId) { }
        public void managedAccounts(string accountsList) { }
        //public void tickSize(int tickerId, int field, int size) { }
        public void tickString(int tickerId, int tickType, string value) { }
        public void tickGeneric(int tickerId, int tickType, double value) { }
        public void tickEFP(int tickerId, int tickType, double basisPoints, string formattedBasisPoints, double impliedFuture, int holdDays, string futureLastTradeDate, double dividendImpact, double dividendsToLastTradeDate) { }
        public void tickOptionComputation(int tickerId, int field, double impliedVol, double delta, double optPrice, double pvDividend, double gamma, double vega, double theta, double undPrice) { }
        public void deltaNeutralValidation(int reqId, DeltaNeutralContract deltaNeutralContract) { }
        public void updateAccountValue(string key, string val, string currency, string accountName) { }
        public void updatePortfolio(Contract contract, double position, double marketPrice, double marketValue, double averageCost, double unrealizedPNL, double realizedPNL, string accountName) { }
        public void updateAccountTime(string timestamp) { }
        public void accountDownloadEnd(string accountName) { }
        public void orderStatus(int orderId, string status, double filled, double remaining, double avgFillPrice, int permId, int parentId, double lastFillPrice, int clientId, string whyHeld, double mktCapPrice) { }
        public void openOrder(int orderId, Contract contract, Order order, OrderState orderState) { }
        public void openOrderEnd() { }
        public void contractDetails(int reqId, ContractDetails contractDetails) { }
        public void contractDetailsEnd(int reqId) { }
        public void execDetails(int reqId, Contract contract, Execution execution) { }
        public void execDetailsEnd(int reqId) { }
        public void commissionReport(CommissionReport commissionReport) { }
        public void fundamentalData(int reqId, string data) { }
        public void historicalData(int reqId, Bar bar) { }
        public void historicalDataEnd(int reqId, string start, string end) { }
        public void marketDataType(int reqId, int marketDataType) { }
        public void updateMktDepth(int tickerId, int position, int operation, int side, double price, int size) { }
        public void updateMktDepthL2(int tickerId, int position, string marketMaker, int operation, int side, double price, int size, bool isSmartDepth) { }
        public void updateNewsBulletin(int msgId, int msgType, string message, string originExch) { }
        public void receiveFA(int faDataType, string xml) { }
        public void verifyMessageAPI(string apiData) { }
        public void verifyCompleted(bool isSuccessful, string errorText) { }
        public void verifyAndAuthMessageAPI(string apiData, string xyzChallenge) { }
        public void verifyAndAuthCompleted(bool isSuccessful, string errorText) { }
        public void displayGroupList(int reqId, string groups) { }
        public void displayGroupUpdated(int reqId, string contractInfo) { }
        public void position(string account, Contract contract, double pos, double avgCost) { }
        public void positionEnd() { }
        public void realtimeBar(int reqId, long time, double open, double high, double low, double close, long volume, double wap, int count) { }
        public void scannerParameters(string xml) { }
        public void scannerData(int reqId, int rank, ContractDetails contractDetails, string distance, string benchmark, string projection, string legsStr) { }
        public void scannerDataEnd(int reqId) { }
        public void currentTime(long time) { }
        public void bondContractDetails(int reqId, ContractDetails contractDetails) { }
        public void softDollarTiers(int reqId, SoftDollarTier[] tiers) { }
        public void familyCodes(FamilyCode[] familyCodes) { }
        public void symbolSamples(int reqId, ContractDescription[] contractDescriptions) { }
        public void mktDepthExchanges(DepthMktDataDescription[] depthMktDataDescriptions) { }
        public void tickNews(int tickerId, long time, string providerCode, string articleId, string headline, string extraData) { }
        public void smartComponents(int reqId, Dictionary<int, KeyValuePair<string, char>> theMap) { }
        public void tickReqParams(int tickerId, double minTick, string bboExchange, int snapshotPermissions) { }
        public void newsProviders(NewsProvider[] newsProviders) { }
        public void newsArticle(int requestId, int articleType, string articleText) { }
        public void historicalNews(int requestId, string time, string providerCode, string articleId, string headline) { }
        public void historicalNewsEnd(int requestId, bool hasMore) { }
        public void headTimestamp(int reqId, string headTimestamp) { }
        public void histogramData(int reqId, HistogramEntry[] data) { }
        public void historicalDataUpdate(int reqId, Bar bar) { }
        public void rerouteMktDataReq(int reqId, int conId, string exchange) { }
        public void rerouteMktDepthReq(int reqId, int conId, string exchange) { }
        public void marketRule(int marketRuleId, PriceIncrement[] priceIncrements) { }
        public void pnl(int reqId, double dailyPnL, double unrealizedPnL, double realizedPnL) { }
        public void pnlSingle(int reqId, int pos, double dailyPnL, double unrealizedPnL, double realizedPnL, double value) { }
        public void historicalTicks(int reqId, HistoricalTick[] ticks, bool done) { }
        public void historicalTicksBidAsk(int reqId, HistoricalTickBidAsk[] ticks, bool done) { }
        public void historicalTicksLast(int reqId, HistoricalTickLast[] ticks, bool done) { }
        public void tickByTickAllLast(int reqId, int tickType, long time, double price, int size, TickAttribLast tickAttribLast, string exchange, string specialConditions) { }
        public void tickByTickBidAsk(int reqId, long time, double bidPrice, double askPrice, int bidSize, int askSize, TickAttribBidAsk tickAttribBidAsk) { }
        public void tickByTickMidPoint(int reqId, long time, double midPoint) { }
        public void orderBound(long orderId, int apiClientId, int apiOrderId) { }
        public void completedOrder(Contract contract, Order order, OrderState orderState) { }
        public void completedOrdersEnd() { }
        public void replaceFAEnd(int reqId, string text) { }
        public void wshMetaData(int reqId, string dataJson) { }
        public void wshEventData(int reqId, string dataJson) { }
        public void userInfo(int reqId, string userInfo) { }
        public void tickSnapshotEnd(int reqId) { }
        public void accountSummary(int reqId, string account, string tag, string value, string currency) { }
        public void accountSummaryEnd(int reqId) { }
        public void positionMulti(int reqId, string account, string modelCode, Contract contract, double pos, double avgCost) { }
        public void positionMultiEnd(int reqId) { }
        public void accountUpdateMulti(int reqId, string account, string modelCode, string key, string value, string currency) { }
        public void accountUpdateMultiEnd(int reqId) { }
        public void securityDefinitionOptionParameter(int reqId, string exchange, int underlyingConId, string tradingClass, string multiplier, HashSet<string> expirations, HashSet<double> strikes) { }
        public void securityDefinitionOptionParameterEnd(int reqId) { }
    }

    public class StockData
    {
        public string Symbol { get; set; }
        public double LastPrice { get; set; }

        // Current Live Bucket
        public double BarOpen { get; set; }
        public double BarHigh { get; set; }
        public double BarLow { get; set; }
        public double BarClose { get; set; }
        public long BarVolume { get; set; }
        public bool HasTicksInCurrentBar { get; set; }

        // Batch Buffer: Stores completed 1-second strings until the minute flush
        public ConcurrentQueue<string> BarBuffer = new ConcurrentQueue<string>();

        private readonly object _lock = new object();

        public StockData() { ResetBar(0); }

        public void ProcessTick(double price, long size = 0)
        {
            lock (_lock)
            {
                if (price <= 0) return;
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