using IBApi;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Drawing;

namespace API
{
    public class MarketDataWrapper : EWrapper
    {
        private readonly ConcurrentDictionary<int, StockData> _stockHash;
        private readonly Action<string, string, Color> _logger;
        private readonly Action _onDisconnectTrigger;

        public MarketDataWrapper(ConcurrentDictionary<int, StockData> stockHash, Action<string, string, Color> logger, Action onDisconnectTrigger)
        {
            _stockHash = stockHash;
            _logger = logger;
            _onDisconnectTrigger = onDisconnectTrigger;
        }

        public void tickPrice(int tickerId, int field, double price, TickAttrib attribs)
        {
            if (_stockHash.TryGetValue(tickerId, out StockData data))
            {
                data.UpdateRawValue(field, price.ToString());
                if ((field == 4 || field == 68) && price > 0) data.ProcessTick(price);
            }
        }

        public void tickSize(int tickerId, int field, decimal size)
        {
            if (_stockHash.TryGetValue(tickerId, out StockData data))
            {
                data.UpdateRawValue(field, size.ToString());
                if (field == 5 && size > 0) data.ProcessTick(data.LastPrice, (long)size);
            }
        }

        public void tickGeneric(int tickerId, int tickType, double value)
        {
            if (_stockHash.TryGetValue(tickerId, out StockData data)) data.UpdateRawValue(tickType, value.ToString());
        }

        public void tickString(int tickerId, int tickType, string value)
        {
            if (_stockHash.TryGetValue(tickerId, out StockData data)) data.UpdateRawValue(tickType, value);
        }

        public void tickOptionComputation(int tickerId, int field, int tickAttrib, double impliedVolatility, double delta, double optPrice, double pvDividend, double gamma, double vega, double theta, double undPrice)
        {
            if (_stockHash.TryGetValue(tickerId, out StockData data))
            {
                data.UpdateRawValue(field, $"IV:{impliedVolatility:P2}|Delta:{delta:F3}");
                if (optPrice > 0) data.ProcessTick(optPrice);
            }
        }

        public void error(int id, int errorCode, string errorMsg)
        {
            // Ignore minor background warnings
            if (errorCode == 2104 || errorCode == 2106 || errorCode == 2158) return;

            // Catch critical connection drops and pacing violations
            if (errorCode == 1100 || errorCode == 100 || errorCode == 504 || errorCode == 1101)
            {
                _logger("IBKR-CRIT", $"Code {errorCode}: {errorMsg}", Color.Red);
                _onDisconnectTrigger?.Invoke();
            }
            else
            {
                _logger("IBKR-WARN", $"Code {errorCode}: {errorMsg}", Color.Yellow);
            }
        }

        public void connectionClosed()
        {
            _logger("API-DROP", "connectionClosed() actively received from IBKR. Socket severed.", Color.Orange);
            _onDisconnectTrigger?.Invoke();
        }

        public void error(Exception e) => _logger("FATAL-EX", e.Message, Color.Red);
        public void error(string str) => _logger("ERR-STR", str, Color.Red);
        public void connectAck() { _logger("API-ACK", "Broker Handshake complete. TWS authorized.", Color.Lime); }

        // --- BOILERPLATE: SILENT ---
        public void nextValidId(int orderId) { }
        public void managedAccounts(string accountsList) { }
        public void error(int id, long errorTime, int errorCode, string errorMsg, string advancedOrderRejectJson) { }
        public void tickEFP(int tickerId, int tickType, double basisPoints, string formattedBasisPoints, double impliedFuture, int holdDays, string futureLastTradeDate, double dividendImpact, double dividendsToLastTradeDate) { }
        public void deltaNeutralValidation(int reqId, DeltaNeutralContract deltaNeutralContract) { }
        public void updateAccountValue(string key, string val, string currency, string accountName) { }
        public void updatePortfolio(Contract contract, double position, double marketPrice, double marketValue, double averageCost, double unrealizedPNL, double realizedPNL, string accountName) { }
        public void updatePortfolio(Contract contract, decimal position, double marketPrice, double marketValue, double averageCost, double unrealizedPNL, double realizedPNL, string accountName) { }
        public void updateAccountTime(string timestamp) { }
        public void accountDownloadEnd(string accountName) { }
        public void orderStatus(int orderId, string status, double filled, double remaining, double avgFillPrice, int permId, int parentId, double lastFillPrice, int clientId, string whyHeld, double mktCapPrice) { }
        public void orderStatus(int orderId, string status, decimal filled, decimal remaining, double avgFillPrice, long permId, int parentId, double lastFillPrice, int clientId, string whyHeld, double mktCapPrice) { }
        public void openOrder(int orderId, Contract contract, Order order, OrderState orderState) { }
        public void openOrderEnd() { }
        public void contractDetails(int reqId, ContractDetails contractDetails) { }
        public void contractDetailsEnd(int reqId) { }
        public void execDetails(int reqId, Contract contract, Execution execution) { }
        public void execDetailsEnd(int reqId) { }
        public void fundamentalData(int reqId, string data) { }
        public void historicalData(int reqId, Bar bar) { }
        public void historicalDataEnd(int reqId, string start, string end) { }
        public void marketDataType(int reqId, int marketDataType) { }
        public void updateMktDepth(int tickerId, int position, int operation, int side, double price, int size) { }
        public void updateMktDepth(int tickerId, int position, int operation, int side, double price, decimal size) { }
        public void updateMktDepthL2(int tickerId, int position, string marketMaker, int operation, int side, double price, int size, bool isSmartDepth) { }
        public void updateMktDepthL2(int tickerId, int position, string marketMaker, int operation, int side, double price, decimal size, bool isSmartDepth) { }
        public void updateNewsBulletin(int msgId, int msgType, string message, string originExch) { }
        public void receiveFA(int faDataType, string xml) { }
        public void verifyMessageAPI(string apiData) { }
        public void verifyCompleted(bool isSuccessful, string errorText) { }
        public void verifyAndAuthMessageAPI(string apiData, string xyzChallenge) { }
        public void verifyAndAuthCompleted(bool isSuccessful, string errorText) { }
        public void displayGroupList(int reqId, string groups) { }
        public void displayGroupUpdated(int reqId, string contractInfo) { }
        public void position(string account, Contract contract, double pos, double avgCost) { }
        public void position(string account, Contract contract, decimal pos, double avgCost) { }
        public void positionEnd() { }
        public void realtimeBar(int reqId, long time, double open, double high, double low, double close, long volume, double wap, int count) { }
        public void realtimeBar(int reqId, long date, double open, double high, double low, double close, decimal volume, decimal WAP, int count) { }
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
        public void pnlSingle(int reqId, decimal pos, double dailyPnL, double unrealizedPnL, double realizedPnL, double value) { }
        public void historicalTicks(int reqId, HistoricalTick[] ticks, bool done) { }
        public void historicalTicksBidAsk(int reqId, HistoricalTickBidAsk[] ticks, bool done) { }
        public void historicalTicksLast(int reqId, HistoricalTickLast[] ticks, bool done) { }
        public void tickByTickAllLast(int reqId, int tickType, long time, double price, int size, TickAttribLast tickAttribLast, string exchange, string specialConditions) { }
        public void tickByTickAllLast(int reqId, int tickType, long time, double price, decimal size, TickAttribLast tickAttribLast, string exchange, string specialConditions) { }
        public void tickByTickBidAsk(int reqId, long time, double bidPrice, double askPrice, int bidSize, int askSize, TickAttribBidAsk tickAttribBidAsk) { }
        public void tickByTickBidAsk(int reqId, long time, double bidPrice, double askPrice, decimal bidSize, decimal askSize, TickAttribBidAsk tickAttribBidAsk) { }
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
        public void positionMulti(int requestId, string account, string modelCode, Contract contract, decimal pos, double avgCost) { }
        public void positionMultiEnd(int reqId) { }
        public void accountUpdateMulti(int reqId, string account, string modelCode, string key, string value, string currency) { }
        public void accountUpdateMultiEnd(int reqId) { }
        public void securityDefinitionOptionParameter(int reqId, string exchange, int underlyingConId, string tradingClass, string multiplier, HashSet<string> expirations, HashSet<double> strikes) { }
        public void securityDefinitionOptionParameterEnd(int reqId) { }
        public void commissionAndFeesReport(CommissionAndFeesReport commissionAndFeesReport) { }
        public void historicalSchedule(int reqId, string startDateTime, string endDateTime, string timeZone, HistoricalSession[] sessions) { }
        public void currentTimeInMillis(long timeInMillis) { }
        public void orderStatusProtoBuf(IBApi.protobuf.OrderStatus orderStatusProto) { }
        public void openOrderProtoBuf(IBApi.protobuf.OpenOrder openOrderProto) { }
        public void openOrdersEndProtoBuf(IBApi.protobuf.OpenOrdersEnd openOrdersEndProto) { }
        public void errorProtoBuf(IBApi.protobuf.ErrorMessage errorMessageProto) { }
        public void execDetailsProtoBuf(IBApi.protobuf.ExecutionDetails executionDetailsProto) { }
        public void execDetailsEndProtoBuf(IBApi.protobuf.ExecutionDetailsEnd executionDetailsEndProto) { }
    }
}