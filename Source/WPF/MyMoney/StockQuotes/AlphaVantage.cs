﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Serialization;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Walkabout.Utilities;

namespace Walkabout.StockQuotes
{

    /// <summary>
    /// Class that wraps the https://www.alphavantage.co/ API 
    /// </summary>
    class AlphaVantage : IStockQuoteService
    {
        static string FriendlyName = "AlphaVantage.com";
        const string address = "https://www.alphavantage.co/query?function=GLOBAL_QUOTE&symbol={0}&apikey={1}";
        char[] illegalUrlChars = new char[] { ' ', '\t', '\n', '\r', '/', '+', '=', '&', ':' };
        StockServiceSettings _settings;
        HashSet<string> _pending;
        HttpWebRequest _current;
        bool _cancelled;
        Thread _downloadThread;
        string _logPath;

        public AlphaVantage(StockServiceSettings settings, string logPath)
        {
            _settings = settings;
            StockQuoteThrottle.Instance.Settings = this._settings;
            settings.Name = FriendlyName;
            _logPath = logPath;
        }

        public static StockServiceSettings GetDefaultSettings()
        {
            return new StockServiceSettings()
            {
                Name = FriendlyName,
                ApiKey = "demo",
                ApiRequestsPerMinuteLimit = 5,
                ApiRequestsPerDayLimit = 500,
                ApiRequestsPerMonthLimit = 0
            };
        }

        public static bool IsMySettings(StockServiceSettings settings)
        {
            return settings.Name == FriendlyName;
        }

        public int PendingCount { get { return (_pending == null) ? 0 : _pending.Count; } }

        public void Cancel()
        {
            _cancelled = true;
            if (_current != null)
            {
                try
                {
                    _current.Abort();
                }
                catch { }
            }
        }

        public event EventHandler<StockQuote> QuoteAvailable;

        private void OnQuoteAvailable(StockQuote quote)
        {
            if (QuoteAvailable != null)
            {
                QuoteAvailable(this, quote);
            }
        }

        public event EventHandler<string> DownloadError;

        private void OnError(string message)
        {
            if (DownloadError != null)
            {
                DownloadError(this, message);
            }
        }

        public event EventHandler<bool> Complete;

        private void OnComplete(bool complete)
        {
            if (Complete != null)
            {
                Complete(this, complete);
            }
        }

        public event EventHandler<bool> Suspended;

        private void OnSuspended(bool suspended)
        {
            if (Suspended != null)
            {
                Suspended(this, suspended);
            }
        }

        public void BeginFetchQuotes(List<string> symbols)
        {
            int count = 0;
            if (_pending == null)
            {
                _pending = new HashSet<string>(symbols);
                count = symbols.Count;
            }
            else
            {
                lock (_pending)
                {
                    // merge the lists.                    
                    foreach (string s in symbols)
                    {
                        _pending.Add(s);
                    }
                    count = _pending.Count;
                }
            }
            _cancelled = false;
            if (_downloadThread == null)
            {
                _downloadThread = new Thread(new ThreadStart(DownloadQuotes));
                _downloadThread.Start();
            }
        }

        private void DownloadQuotes()
        {
            try
            {                
                while (!_cancelled)
                {
                    int remaining = 0;
                    string symbol = null;
                    lock (_pending)
                    {
                        if (_pending.Count > 0)
                        {
                            symbol = _pending.FirstOrDefault();
                            _pending.Remove(symbol);
                            remaining = _pending.Count;
                        }
                    }
                    if (symbol == null)
                    {
                        // done!
                        break;
                    }

                    // weed out any securities that have no symbol or have a 
                    if (string.IsNullOrEmpty(symbol))
                    {
                        // skip securities that have no symbol.
                    }
                    else if (symbol.IndexOfAny(illegalUrlChars) >= 0)
                    {
                        // since we are passing the symbol on an HTTP URI line, we can't pass Uri illegal characters...
                        OnError(string.Format(Walkabout.Properties.Resources.SkippingSecurityIllegalSymbol, symbol));
                    }
                    else
                    {
                        try
                        {
                            // this service doesn't want too many calls per second.
                            int ms = StockQuoteThrottle.Instance.GetSleep();
                            bool suspended = ms > 0;
                            if (suspended)
                            {
                                if (ms > 1000)
                                {
                                    int seconds = ms / 1000;
                                    OnError("AlphaVantage service needs to sleep for " + seconds + " seconds");
                                }
                                else
                                {
                                    OnError("AlphaVantage service needs to sleep for " + ms.ToString() + " ms");
                                }
                                OnSuspended(true);
                                while (!_cancelled && ms > 0)
                                {
                                    Thread.Sleep(1000);
                                    ms -= 1000;
                                }
                                OnSuspended(false);
                            }

                            string uri = string.Format(address, symbol, _settings.ApiKey);
                            HttpWebRequest req = (HttpWebRequest)WebRequest.Create(uri);
                            req.UserAgent = "USER_AGENT=Mozilla/4.0 (compatible; MSIE 6.0; Windows NT 5.1;)";
                            req.Method = "GET";
                            req.Timeout = 10000;
                            req.UseDefaultCredentials = false;
                            _current = req;

                            Debug.WriteLine("AlphaVantage fetching quote " + symbol);

                            WebResponse resp = req.GetResponse();
                            using (Stream stm = resp.GetResponseStream())
                            {
                                using (StreamReader sr = new StreamReader(stm, Encoding.UTF8))
                                {
                                    string json = sr.ReadToEnd();
                                    JObject o = JObject.Parse(json);
                                    StockQuote quote = ParseStockQuote(o);
                                    if (quote == null || quote.Symbol == null)
                                    {
                                        OnError(string.Format(Walkabout.Properties.Resources.ErrorFetchingSymbols, symbol));
                                    }
                                    else if (string.Compare(quote.Symbol, symbol, StringComparison.OrdinalIgnoreCase) != 0)
                                    {
                                        // todo: show appropriate error...
                                    }
                                    else
                                    {
                                        OnQuoteAvailable(quote);
                                    }
                                }
                            }

                            OnError(string.Format(Walkabout.Properties.Resources.FetchedStockQuotes, symbol));
                        }
                        catch (System.Net.WebException we)
                        {
                            if (we.Status != WebExceptionStatus.RequestCanceled)
                            {
                                OnError(string.Format(Walkabout.Properties.Resources.ErrorFetchingSymbols, symbol) + "\r\n" + we.Message);
                            }
                            else
                            {
                                // we cancelled, so bail. 
                                _cancelled = true;
                                break;
                            }

                            HttpWebResponse http = we.Response as HttpWebResponse;
                            if (http != null)
                            {
                                // certain http error codes are fatal.
                                switch (http.StatusCode)
                                {
                                    case HttpStatusCode.ServiceUnavailable:
                                    case HttpStatusCode.InternalServerError:
                                    case HttpStatusCode.Unauthorized:
                                        OnError(http.StatusDescription);
                                        _cancelled = true;
                                        break;
                                }
                            }
                        }
                        catch (Exception e)
                        {
                            // continue
                            OnError(string.Format(Walkabout.Properties.Resources.ErrorFetchingSymbols, symbol) + "\r\n" + e.Message);

                            var message = e.Message;
                            if (message.Contains("Please visit https://www.alphavantage.co/premium/"))
                            {
                                StockQuoteThrottle.Instance.CallsThisMinute += this._settings.ApiRequestsPerMinuteLimit;
                            }
                            OnComplete(PendingCount == 0);
                        }
                    }
                }
            }
            catch
            {
            }
            OnComplete(PendingCount == 0);
            StockQuoteThrottle.Instance.Save();
            _downloadThread = null;
            _current = null;
            Debug.WriteLine("AlphaVantage download thread terminating");
        }

        private static StockQuote ParseStockQuote(JObject o)
        {
            StockQuote result = null;
            Newtonsoft.Json.Linq.JToken value;

            if (o.TryGetValue("Note", StringComparison.Ordinal, out value))
            {
                string message = (string)value;
                throw new Exception(message);
            }

            if (o.TryGetValue("Global Quote", StringComparison.Ordinal, out value))
            {
                result = new StockQuote();
                if (value.Type == JTokenType.Object)
                {
                    JObject child = (JObject)value;
                    if (child.TryGetValue("01. symbol", StringComparison.Ordinal, out value))
                    {
                        result.Symbol = (string)value;
                    }
                    if (child.TryGetValue("02. open", StringComparison.Ordinal, out value))
                    {
                        result.Open = (decimal)value;
                    }
                    if (child.TryGetValue("03. high", StringComparison.Ordinal, out value))
                    {
                        result.High = (decimal)value;
                    }
                    if (child.TryGetValue("04. low", StringComparison.Ordinal, out value))
                    {
                        result.Low = (decimal)value;
                    }
                    if (child.TryGetValue("08. previous close", StringComparison.Ordinal, out value))
                    {
                        result.Close = (decimal)value;
                    }
                    if (child.TryGetValue("06. volume", StringComparison.Ordinal, out value))
                    {
                        result.Volume = (decimal)value;
                    }
                    if (child.TryGetValue("07. latest trading day", StringComparison.Ordinal, out value))
                    {
                        result.Date = (DateTime)value;
                    }
                }
            }
            return result;
        }

        public async Task<StockQuoteHistory> DownloadHistory(string symbol)
        {
            const string timeSeriesAddress = "https://www.alphavantage.co/query?function=TIME_SERIES_DAILY&symbol={0}&outputsize=full&apikey={1}";
            StockQuoteHistory history = null;
            string uri = string.Format(timeSeriesAddress, symbol, this._settings.ApiKey);
            await Task.Run(new Action(() =>
            {
                try
                {
                    // this service doesn't want too many calls per second.
                    int ms = StockQuoteThrottle.Instance.GetSleep();
                    if (ms > 0)
                    {
                        if (ms > 1000)
                        {
                            int seconds = ms / 1000;
                            OnError("AlphaVantage service needs to sleep for " + seconds + " seconds");
                        }
                        else
                        {
                            OnError("AlphaVantage service needs to sleep for " + ms.ToString() + " ms");
                        }

                        OnComplete(PendingCount == 0);
                    }
                    while (!_cancelled && ms > 0)
                    {
                        Thread.Sleep(1000);
                        ms -= 1000;
                    }

                    HttpWebRequest req = (HttpWebRequest)WebRequest.Create(uri);
                    req.UserAgent = "USER_AGENT=Mozilla/4.0 (compatible; MSIE 6.0; Windows NT 5.1;)";
                    req.Method = "GET";
                    req.Timeout = 10000;
                    req.UseDefaultCredentials = false;
                    _current = req;

                    Debug.WriteLine("AlphaVantage fetching history for " + symbol);

                    WebResponse resp = req.GetResponse();
                    using (Stream stm = resp.GetResponseStream())
                    {
                        using (StreamReader sr = new StreamReader(stm, Encoding.UTF8))
                        {
                            string json = sr.ReadToEnd();
                            JObject o = JObject.Parse(json);
                            history = ParseTimeSeries(o);
                            if (string.Compare(history.Symbol, symbol, StringComparison.OrdinalIgnoreCase) != 0)
                            {
                                OnError(string.Format("History for symbol {0} return different symbol {1}", symbol, history.Symbol));
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    string message = ex.Message;
                    OnError(message);
                    if (message.Contains("Please visit https://www.alphavantage.co/premium/"))
                    {
                        StockQuoteThrottle.Instance.CallsThisMinute += this._settings.ApiRequestsPerMinuteLimit;
                    }
                    OnComplete(PendingCount == 0);
                }

            }));
            return history;
        }

        private StockQuoteHistory ParseTimeSeries(JObject o)
        {
            StockQuoteHistory history = new StockQuoteHistory();
            history.History = new List<StockQuote>();

            Newtonsoft.Json.Linq.JToken value;

            if (o.TryGetValue("Note", StringComparison.Ordinal, out value))
            {
                string message = (string)value;
                throw new Exception(message);
            }

            if (o.TryGetValue("Meta Data", StringComparison.Ordinal, out value))
            {
                if (value.Type == JTokenType.Object)
                {
                    JObject child = (JObject)value;
                    if (child.TryGetValue("2. Symbol", StringComparison.Ordinal, out value))
                    {
                        history.Symbol = (string)value;
                    }
                }
            }
            else
            {
                throw new Exception("Time series data schema has changed");
            }

            if (o.TryGetValue("Time Series (Daily)", StringComparison.Ordinal, out value))
            {
                if (value.Type == JTokenType.Object)
                {
                    JObject series = (JObject)value;
                    foreach (var p in series.Properties().Reverse())
                    {
                        DateTime date;
                        if (DateTime.TryParse(p.Name, out date))
                        {
                            value = series.GetValue(p.Name);
                            if (value.Type == JTokenType.Object)
                            {
                                StockQuote quote = new StockQuote() { Date = date };
                                JObject child = (JObject)value;

                                if (child.TryGetValue("1. open", StringComparison.Ordinal, out value))
                                {
                                    quote.Open = (decimal)value;
                                }
                                if (child.TryGetValue("4. close", StringComparison.Ordinal, out value))
                                {
                                    quote.Close = (decimal)value;
                                }
                                if (child.TryGetValue("2. high", StringComparison.Ordinal, out value))
                                {
                                    quote.High = (decimal)value;
                                }
                                if (child.TryGetValue("3. low", StringComparison.Ordinal, out value))
                                {
                                    quote.Low = (decimal)value;
                                }
                                if (child.TryGetValue("5. volume", StringComparison.Ordinal, out value))
                                {
                                    quote.Volume = (decimal)value;
                                }
                                history.History.Add(quote);
                            }
                        }
                    }
                }
            }
            else
            {
                throw new Exception("Time series data schema has changed");
            }
            return history;
        }
    }
}
