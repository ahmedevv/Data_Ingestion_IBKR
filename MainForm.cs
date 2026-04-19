using System;
using System.Collections.Concurrent;
using System.Drawing;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using IBApi;

namespace API
{
    public partial class MainForm : Form
    {
        private EClientSocket clientSocket;
        private EReaderSignal readerSignal;
        private MarketDataWrapper ibWrapper;
        private DiskWriter diskWriter;

        private ConcurrentDictionary<int, StockData> stockHash = new ConcurrentDictionary<int, StockData>();

        // Configuration
        private string csvFolder = @"C:\Projects\IBKR\API\StockData";
        private string tickerListPath = @"C:\Projects\IBKR\API\sp500.txt";
        private string logPath = Path.Combine(@"C:\Projects\IBKR\API\", "session_log.txt");

        private System.Windows.Forms.Timer saveTimer;
        private bool isManuallyStopped = false;
        private int reconnectAttempts = 0;
        private int _currentClientId = 1;
        private int flushCounter = 0;

        private readonly string csvHeader = "timestamp_utc,bar_open,bar_high,bar_low,bar_close,bar_vol," +
            "bid_sz,bid_px,ask_px,ask_sz,last_px,last_sz,high,low,volume,close," +
            "bid_opt_comp,ask_opt_comp,last_opt_comp,model_opt_comp,open_tick,low_13w,high_13w,low_26w,high_26w,low_52w,high_52w," +
            "avg_vol,open_interest,opt_hist_vol,opt_impl_vol,opt_bid_exch,opt_ask_exch,opt_call_oi,opt_put_oi,opt_call_vol,opt_put_vol," +
            "index_future_premium,bid_exch,ask_exch,auction_vol,auction_px,auction_imbalance,mark_px,bid_efp,ask_efp,last_efp,open_efp," +
            "high_efp,low_efp,close_efp,last_timestamp,shortable,rt_volume,halted,bid_yield,ask_yield,last_yield,cust_opt_comp," +
            "trade_count,trade_rate,vol_rate,last_rth_trade,rt_hist_vol,ib_dividends,bond_factor,reg_imbalance,news,short_term_vol_3m," +
            "short_term_vol_5m,short_term_vol_10m,delayed_bid,delayed_ask,delayed_last,delayed_bid_sz,delayed_ask_sz,delayed_last_sz," +
            "delayed_high,delayed_low,delayed_vol,delayed_close,delayed_open,rt_trade_vol,creditman_mark,creditman_slow_mark," +
            "delayed_bid_opt,delayed_ask_opt,delayed_last_opt,delayed_model_opt,last_exch,last_reg_time,futures_oi,avg_opt_vol," +
            "delayed_last_timestamp,shortable_shares,etf_nav_last,etf_nav_frozen,etf_nav_high,etf_nav_low,estimated_ipo_mid,final_ipo_px\n";

        public MainForm()
        {
            InitializeComponent();

            rtbLogs.BackColor = Color.Black;
            rtbLogs.ForeColor = Color.White;
            rtbLogs.Font = new Font("Consolas", 10, FontStyle.Regular);

            Log("SYS-INIT", "Orchestrator MainForm initialized.", Color.Gray);

            diskWriter = new DiskWriter(csvFolder, csvHeader, Log);
            readerSignal = new EReaderMonitorSignal();
            ibWrapper = new MarketDataWrapper(stockHash, Log, TriggerReconnect);
            clientSocket = new EClientSocket(ibWrapper, readerSignal);

            saveTimer = new System.Windows.Forms.Timer();
            saveTimer.Interval = 1000;
            saveTimer.Tick += OnSaveTimerTick;

            this.FormClosing += (s, e) => {
                Log("SHUTDOWN", "Form closing event triggered. Executing final disk flush.", Color.Yellow);
                diskWriter.Flush(stockHash);
                Thread.Sleep(2000);
            };
        }

        private void btnStart_Click(object sender, EventArgs e)
        {
            Log("UI-BTN", "Start sequence initiated by user.", Color.White);
            isManuallyStopped = false;
            reconnectAttempts = 0;
            PerformConnection();
        }

        private void btnStop_Click_1(object sender, EventArgs e)
        {
            Log("UI-BTN", "Stop Engine button clicked. Halting systems.", Color.OrangeRed);
            isManuallyStopped = true;
            saveTimer.Stop();

            if (clientSocket.IsConnected()) clientSocket.eDisconnect();

            foreach (var stock in stockHash.Values) stock.LastPrice = 0;
            stockHash.Clear();

            btnStart.Enabled = true;
            btnStart.Text = "Start Engine";
            Log("SYS-MEM", "RAM Array cleared successfully.", Color.DarkGray);
        }

        private async void PerformConnection()
        {
            try
            {
                if (!clientSocket.IsConnected())
                {
                    Log("CONNECT", $"Initiating Live Connection (Client ID: {_currentClientId})...", Color.Cyan);
                    clientSocket.eConnect("127.0.0.1", 7496, _currentClientId);

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
                            Log("THREAD", "Background Message loop terminated.", Color.DarkGray);
                        }
                        catch (Exception ex) { Log("FATAL", $"Reader Thread Error: {ex.Message}", Color.Red); }
                    })
                    { IsBackground = true }.Start();

                    // THE FIX: asynchronous, non-blocking wait. The UI remains 100% responsive.
                    await Task.Delay(3000);

                    if (clientSocket.IsConnected())
                    {
                        Log("SYS-UP", "Handshake confirmed. Requesting Market Data configuration.", Color.Lime);
                        clientSocket.reqMarketDataType(1);

                        reconnectAttempts = 0;

                        // THE FIX: Push the 25-second subscription loop to a background thread.
                        // The UI instantly moves on and starts the timer.
                        _ = Task.Run(() => StartSubscriptions());

                        saveTimer.Start();
                        btnStart.Enabled = false;
                        btnStart.Text = "LIVE";
                        Log("ENGINE", "Operational. Buffer Timer & EST Time-Gate Active.", Color.LimeGreen);
                    }
                    else
                    {
                        Log("CONNECT", "Broker handshake failed. Target machine refused connection.", Color.Red);
                        if (!isManuallyStopped) TriggerReconnect();
                    }
                }
            }
            catch (Exception ex)
            {
                Log("CRASH", $"PerformConnection Error: {ex.Message}", Color.Red);
                if (!isManuallyStopped) TriggerReconnect();
            }
        }

        private void StartSubscriptions()
        {
            if (File.Exists(tickerListPath))
            {
                string[] symbols = File.ReadAllLines(tickerListPath);
                int idCounter = 1;

                Log("SUBS", $"Dispatching requests for {symbols.Length} symbols...", Color.Cyan);
                foreach (var sym in symbols)
                {
                    string s = sym.Trim();
                    if (string.IsNullOrEmpty(s)) continue;

                    Contract c = new Contract { Symbol = s, SecType = "STK", Exchange = "SMART", Currency = "USD", PrimaryExch = "NASDAQ" };
                    stockHash.TryAdd(idCounter, new StockData() { Symbol = s });

                    clientSocket.reqMktData(idCounter++, c, "106,165,236", false, false, null);
                    Thread.Sleep(50);
                }
                Log("SUBS-DONE", "All API requests dispatched successfully.", Color.Lime);
            }
            else { Log("FATAL", "sp500.txt not found in project directory!", Color.Red); }
        }

        private void OnSaveTimerTick(object sender, EventArgs e)
        {
            if (!clientSocket.IsConnected() && !isManuallyStopped) return;

            try
            {
                TimeZoneInfo estZone = TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");
                DateTime estTime = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, estZone);
                TimeSpan currentTime = estTime.TimeOfDay;

                TimeSpan marketOpen = new TimeSpan(9, 30, 0);
                TimeSpan marketClose = new TimeSpan(16, 0, 0);

                if (currentTime < marketOpen || currentTime > marketClose)
                {
                    // Throttle the Time-Gate log so it only prints once every 60 seconds (at exactly the top of the minute)
                    if (DateTime.Now.Second == 0)
                    {
                        Log("TIME-GATE", $"Market Closed ({estTime:HH:mm} EST). Forward-fill engine paused.", Color.Orange);
                    }
                    return;
                }
            }
            catch (Exception ex)
            {
                Log("SYS-ERR", $"Timezone conversion logic crashed: {ex.Message}", Color.Red);
                return;
            }

            string tsUtc = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss");

            foreach (var stock in stockHash.Values)
            {
                if (!stock.IsInitialized) continue;

                StringBuilder sb = new StringBuilder();
                sb.Append($"{tsUtc},{stock.BarOpen},{stock.BarHigh},{stock.BarLow},{stock.BarClose},{stock.BarVolume}");

                for (int i = 0; i <= 102; i++)
                {
                    string val = stock.GetRawValue(i).Replace(",", ";");
                    sb.Append($",{val}");
                }

                stock.BarBuffer.Enqueue(sb.ToString());
                stock.ResetBar(stock.BarClose);
            }

            flushCounter++;
            if (flushCounter >= 60)
            {
                diskWriter.Flush(stockHash);
                flushCounter = 0;
            }
        }

        private void TriggerReconnect()
        {
            // THE FIX: If IBKR's background network thread calls this, force it back onto the Main UI Thread.
            if (this.InvokeRequired)
            {
                this.Invoke(new Action(TriggerReconnect));
                return;
            }

            if (isManuallyStopped) return;

            Log("RECONNECT", "Triggering automated recovery sequence...", Color.Yellow);

            // This is now safe because we are guaranteed to be on the UI thread.
            saveTimer.Stop();

            if (clientSocket.IsConnected()) clientSocket.eDisconnect();

            reconnectAttempts++;

            _currentClientId++;
            if (_currentClientId > 900) _currentClientId = 1;

            Log("RECOVERY", $"Attempting Reconnect #{reconnectAttempts} in 10s... (New ID: {_currentClientId})", Color.Yellow);

            Task.Delay(10000).ContinueWith(t => {
                if (!isManuallyStopped)
                {
                    this.Invoke(new Action(() => PerformConnection()));
                }
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
            rtbLogs.AppendText($"[{tag.PadRight(12)}] {msg}{Environment.NewLine}");
            rtbLogs.ScrollToCaret();

            Task.Run(() => {
                try { File.AppendAllText(logPath, $"[{DateTime.Now:HH:mm:ss}] [{tag.PadRight(12)}] {msg}{Environment.NewLine}"); } catch { }
            });
        }
    }
}