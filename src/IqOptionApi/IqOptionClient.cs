using System;
using System.Collections.Generic;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Threading.Tasks;
using IqOptionApi.Http;
using IqOptionApi.Models;
using IqOptionApi.Ws;
using Serilog;

namespace IqOptionApi
{
    public class IqOptionClient : IIqOptionClient
    {
        #region [Ctor]

        public IqOptionClient(string username, string password, string host = "iqoption.com")
        {
            Username = username;
            Password = password;

            //set up client
            HttpClient = new IqOptionHttpClient(username, password);
        }

        #endregion

        public Task<bool> ConnectAsync()
        {
            connectedSubject.OnNext(false);
            IsConnected = false;

            var tcs = new TaskCompletionSource<bool>();
            try
            {
                HttpClient
                    .LoginAsync()
                    .ContinueWith(t =>
                    {
                        if (t.Result != null && t.Result.IsSuccessful)
                        {
                            _logger.Information($"{Username} logged in success!");

                            if (WsClient != null) WsClient.Dispose();
                            WsClient = new IqOptionWebSocketClient(t.Result.Data.Ssid);

                            SubscribeWebSocket();

                            IsConnected = true;
                            connectedSubject.OnNext(true);
                            tcs.TrySetResult(true);
                            return;
                        }

                        _logger.Information(
                            $"{Username} logged in failed due to {t.Result?.Errors?.GetErrorMessage()}");
                        tcs.TrySetResult(false);
                    });
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }

            return tcs.Task;
        }

        public async Task<Profile> GetProfileAsync()
        {
            var result = await HttpClient.GetProfileAsync();
            return result.Result;
        }

        public async Task<bool> ChangeBalanceAsync(long balanceId)
        {
            var result = await HttpClient.ChangeBalanceAsync(balanceId);

            if (result?.Message == null && !result.IsSuccessful)
            {
                _logger.Error($"Change balance ({balanceId}) error : {result.Message}");
                return false;
            }

            _logger.Information($"Change balance to {balanceId} successfully!");
            return true;
        }

        public Task<BuyResult> BuyAsync(ActivePair pair, int size, OrderDirection direction,
            DateTimeOffset expiration)
        {
            return WsClient?.BuyAsync(pair, size, direction, expiration);
        }


        public Task<CandleCollections> GetCandlesAsync(ActivePair pair, TimeFrame timeFrame, int count,
            DateTimeOffset to)
        {
            return WsClient?.GetCandlesAsync(pair, timeFrame, count, to);
        }

        public Task<IObservable<CurrentCandle>> SubscribeRealtimeDataAsync(ActivePair pair, TimeFrame tf)
        {
            WsClient?.SubscribeCandlesAsync(pair, tf).ConfigureAwait(false);

            var stream = WsClient?
                .RealTimeCandleInfoObservable
                .Where(x => x.ActivePair == pair && x.TimeFrame == tf);


            return Task.FromResult(stream);
        }

        public Task UnSubscribeRealtimeData(ActivePair pair, TimeFrame tf)
        {
            return WsClient?.UnsubscribeCandlesAsync(pair, tf);
        }

        public void Dispose()
        {
            connectedSubject?.Dispose();
            WsClient?.Dispose();
        }


        /// <summary>
        ///     listen to all obs, for make all properties updated
        /// </summary>
        private void SubscribeWebSocket()
        {
            //subscribe profile
            HttpClient.ProfileObservable()
                .Merge(WsClient.ProfileObservable())
                .DistinctUntilChanged()
                .Where(x => x != null)
                .Subscribe(x => Profile = x);

            //subscribe for instrument updated
            WsClient.InstrumentResultSetObservable
                .Subscribe(x => Instruments = x);
        }

        #region [Privates]

        private readonly ILogger _logger = IqOptionLoggerFactory.CreateLogger();
        private readonly Subject<Profile> _profileSubject = new Subject<Profile>();

        private readonly Subject<bool> connectedSubject = new Subject<bool>();
        private Profile _profile;

        #endregion

        #region [Publics]

        public string Username { get; }
        public string Password { get; }
        public IDictionary<InstrumentType, Instrument[]> Instruments { get; private set; }

        public Profile Profile
        {
            get => _profile;
            private set
            {
                _profileSubject.OnNext(value);
                _profile = value;
            }
        }

        public bool IsConnected { get; private set; }

        //clients
        public IqOptionHttpClient HttpClient { get; }
        public IqOptionWebSocketClient WsClient { get; private set; }

        //obs
        public IObservable<InfoData[]> InfoDatasObservable => WsClient?.InfoDataObservable;
        public IObservable<Profile> ProfileObservable => _profileSubject.AsObservable();
        public IObservable<bool> ConnectedObservable => connectedSubject.AsObservable();
        public IObservable<BuyResult> BuyResultObservable => WsClient?.BuyResultObservable;

        #endregion
    }
}