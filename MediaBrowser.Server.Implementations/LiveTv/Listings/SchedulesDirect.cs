﻿using MediaBrowser.Common;
using MediaBrowser.Common.Net;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.LiveTv;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Serialization;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace MediaBrowser.Server.Implementations.LiveTv.Listings
{
    public class SchedulesDirect : IListingsProvider
    {
        private readonly ILogger _logger;
        private readonly IJsonSerializer _jsonSerializer;
        private readonly IHttpClient _httpClient;
        private readonly SemaphoreSlim _tokenSemaphore = new SemaphoreSlim(1, 1);
        private readonly IApplicationHost _appHost;

        private const string ApiUrl = "https://json.schedulesdirect.org/20141201";

        private readonly ConcurrentDictionary<string, ScheduleDirect.Station> _channelPair =
            new ConcurrentDictionary<string, ScheduleDirect.Station>();

        public SchedulesDirect(ILogger logger, IJsonSerializer jsonSerializer, IHttpClient httpClient, IApplicationHost appHost)
        {
            _logger = logger;
            _jsonSerializer = jsonSerializer;
            _httpClient = httpClient;
            _appHost = appHost;
        }

        private string UserAgent
        {
            get { return "Emby/" + _appHost.ApplicationVersion; }
        }

        private List<string> GetScheduleRequestDates(DateTime startDateUtc, DateTime endDateUtc)
        {
            List<string> dates = new List<string>();

            var start = new List<DateTime> { startDateUtc, startDateUtc.ToLocalTime() }.Min();
            var end = new List<DateTime> { endDateUtc, endDateUtc.ToLocalTime() }.Max();

            while (start.DayOfYear <= end.Day)
            {
                dates.Add(start.ToString("yyyy-MM-dd"));
                start = start.AddDays(1);
            }

            return dates;
        }

        public async Task<IEnumerable<ProgramInfo>> GetProgramsAsync(ListingsProviderInfo info, string channelNumber, DateTime startDateUtc, DateTime endDateUtc, CancellationToken cancellationToken)
        {
            List<ProgramInfo> programsInfo = new List<ProgramInfo>();

            var token = await GetToken(info, cancellationToken);

            if (string.IsNullOrWhiteSpace(token))
            {
                return programsInfo;
            }

            if (string.IsNullOrWhiteSpace(info.ListingsId))
            {
                return programsInfo;
            }

            var httpOptions = new HttpRequestOptions()
            {
                Url = ApiUrl + "/schedules",
                UserAgent = UserAgent,
                CancellationToken = cancellationToken
            };

            httpOptions.RequestHeaders["token"] = token;

            var dates = GetScheduleRequestDates(startDateUtc, endDateUtc);

            ScheduleDirect.Station station = null;

            if (!_channelPair.TryGetValue(channelNumber, out station))
            {
                return programsInfo;
            }
            string stationID = station.stationID;

            _logger.Info("Channel Station ID is: " + stationID);
            List<ScheduleDirect.RequestScheduleForChannel> requestList =
                new List<ScheduleDirect.RequestScheduleForChannel>()
                    {
                        new ScheduleDirect.RequestScheduleForChannel()
                        {
                            stationID = stationID,
                            date = dates
                        }
                    };

            var requestString = _jsonSerializer.SerializeToString(requestList);
            _logger.Debug("Request string for schedules is: " + requestString);
            httpOptions.RequestContent = requestString;
            using (var response = await _httpClient.Post(httpOptions))
            {
                StreamReader reader = new StreamReader(response.Content);
                string responseString = reader.ReadToEnd();
                var dailySchedules = _jsonSerializer.DeserializeFromString<List<ScheduleDirect.Day>>(responseString);
                _logger.Debug("Found " + dailySchedules.Count() + " programs on " + channelNumber + " ScheduleDirect");

                httpOptions = new HttpRequestOptions()
                {
                    Url = ApiUrl + "/programs",
                    UserAgent = UserAgent,
                    CancellationToken = cancellationToken
                };

                httpOptions.RequestHeaders["token"] = token;

                List<string> programsID = new List<string>();
                programsID = dailySchedules.SelectMany(d => d.programs.Select(s => s.programID)).Distinct().ToList();
                var requestBody = "[\"" + string.Join("\", \"", programsID) + "\"]";
                httpOptions.RequestContent = requestBody;

                using (var innerResponse = await _httpClient.Post(httpOptions))
                {
                    StreamReader innerReader = new StreamReader(innerResponse.Content);
                    responseString = innerReader.ReadToEnd();

                    var programDetails =
                        _jsonSerializer.DeserializeFromString<List<ScheduleDirect.ProgramDetails>>(
                            responseString);
                    var programDict = programDetails.ToDictionary(p => p.programID, y => y);

                    var images = await GetImageForPrograms(programDetails.Where(p => p.hasImageArtwork).Select(p => p.programID).ToList(), cancellationToken);

                    var schedules = dailySchedules.SelectMany(d => d.programs);
                    foreach (ScheduleDirect.Program schedule in schedules)
                    {
                        //_logger.Debug("Proccesing Schedule for statio ID " + stationID +
                        //              " which corresponds to channel " + channelNumber + " and program id " +
                        //              schedule.programID + " which says it has images? " +
                        //              programDict[schedule.programID].hasImageArtwork);

                        var imageIndex = images.FindIndex(i => i.programID == schedule.programID.Substring(0, 10));
                        if (imageIndex > -1)
                        {
                            programDict[schedule.programID].images = GetProgramLogo(ApiUrl, images[imageIndex]);
                        }

                        programsInfo.Add(GetProgram(channelNumber, schedule, programDict[schedule.programID]));
                    }
                    _logger.Info("Finished with EPGData");
                }
            }

            return programsInfo;
        }

        public async Task AddMetadata(ListingsProviderInfo info, List<ChannelInfo> channels,
            CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(info.ListingsId))
            {
                throw new Exception("ListingsId required");
            }

            var token = await GetToken(info, cancellationToken);

            if (string.IsNullOrWhiteSpace(token))
            {
                throw new Exception("token required");
            }

            _channelPair.Clear();

            var httpOptions = new HttpRequestOptions()
            {
                Url = ApiUrl + "/lineups/" + info.ListingsId,
                UserAgent = UserAgent,
                CancellationToken = cancellationToken
            };

            httpOptions.RequestHeaders["token"] = token;

            using (var response = await _httpClient.Get(httpOptions))
            {
                var root = _jsonSerializer.DeserializeFromStream<ScheduleDirect.Channel>(response);
                _logger.Info("Found " + root.map.Count() + " channels on the lineup on ScheduleDirect");
                _logger.Info("Mapping Stations to Channel");
                foreach (ScheduleDirect.Map map in root.map)
                {
                    var channel = (map.channel ?? (map.atscMajor + "." + map.atscMinor)).TrimStart('0');
                    _logger.Debug("Found channel: " + channel + " in Schedules Direct");
                    var schChannel = root.stations.FirstOrDefault(item => item.stationID == map.stationID);

                    if (!_channelPair.ContainsKey(channel) && channel != "0.0" && schChannel != null)
                    {
                        _channelPair.TryAdd(channel, schChannel);
                    }
                }
                _logger.Info("Added " + _channelPair.Count() + " channels to the dictionary");

                foreach (ChannelInfo channel in channels)
                {
                    //  Helper.logger.Info("Modifyin channel " + channel.Number);
                    if (_channelPair.ContainsKey(channel.Number))
                    {
                        string channelName;
                        if (_channelPair[channel.Number].logo != null)
                        {
                            channel.ImageUrl = _channelPair[channel.Number].logo.URL;
                            channel.HasImage = true;
                        }
                        if (_channelPair[channel.Number].affiliate != null)
                        {
                            channelName = _channelPair[channel.Number].affiliate;
                        }
                        else
                        {
                            channelName = _channelPair[channel.Number].name;
                        }
                        channel.Name = channelName;
                    }
                    else
                    {
                        _logger.Info("Schedules Direct doesnt have data for channel: " + channel.Number + " " +
                                     channel.Name);
                    }
                }
            }
        }

        private ProgramInfo GetProgram(string channel, ScheduleDirect.Program programInfo,
            ScheduleDirect.ProgramDetails details)
        {
            //_logger.Debug("Show type is: " + (details.showType ?? "No ShowType"));
            DateTime startAt = DateTime.ParseExact(programInfo.airDateTime, "yyyy'-'MM'-'dd'T'HH':'mm':'ss'Z'",
                CultureInfo.InvariantCulture);
            DateTime endAt = startAt.AddSeconds(programInfo.duration);
            ProgramAudio audioType = ProgramAudio.Stereo;

            bool repeat = (programInfo.@new == null);
            string newID = programInfo.programID + "T" + startAt.Ticks + "C" + channel;

            if (programInfo.audioProperties != null)
            {
                if (programInfo.audioProperties.Exists(item => string.Equals(item, "dd 5.1", StringComparison.OrdinalIgnoreCase)))
                {
                    audioType = ProgramAudio.DolbyDigital;
                }
                else if (programInfo.audioProperties.Exists(item => string.Equals(item, "dd", StringComparison.OrdinalIgnoreCase)))
                {
                    audioType = ProgramAudio.DolbyDigital;
                }
                else if (programInfo.audioProperties.Exists(item => string.Equals(item, "stereo", StringComparison.OrdinalIgnoreCase)))
                {
                    audioType = ProgramAudio.Stereo;
                }
                else
                {
                    audioType = ProgramAudio.Mono;
                }
            }

            string episodeTitle = null;
            if (details.episodeTitle150 != null)
            {
                episodeTitle = details.episodeTitle150;
            }

            string imageUrl = null;

            if (details.hasImageArtwork)
            {
                imageUrl = details.images;
            }

            var showType = details.showType ?? string.Empty;

            var info = new ProgramInfo
            {
                ChannelId = channel,
                Id = newID,
                StartDate = startAt,
                EndDate = endAt,
                Name = details.titles[0].title120 ?? "Unkown",
                OfficialRating = null,
                CommunityRating = null,
                EpisodeTitle = episodeTitle,
                Audio = audioType,
                IsRepeat = repeat,
                IsSeries = showType.IndexOf("series", StringComparison.OrdinalIgnoreCase) != -1,
                ImageUrl = imageUrl,
                HasImage = details.hasImageArtwork,
                IsKids = string.Equals(details.audience, "children", StringComparison.OrdinalIgnoreCase),
                IsSports = showType.IndexOf("sports", StringComparison.OrdinalIgnoreCase) != -1,
                IsMovie = showType.IndexOf("movie", StringComparison.OrdinalIgnoreCase) != -1 || showType.IndexOf("film", StringComparison.OrdinalIgnoreCase) != -1,
                ShowId = programInfo.programID
            };

            if (programInfo.videoProperties != null)
            {
                info.IsHD = programInfo.videoProperties.Contains("hdtv", StringComparer.OrdinalIgnoreCase);
            }

            if (details.contentRating != null && details.contentRating.Count > 0)
            {
                info.OfficialRating = details.contentRating[0].code.Replace("TV", "TV-").Replace("--", "-");
            }

            if (details.descriptions != null)
            {
                if (details.descriptions.description1000 != null)
                {
                    info.Overview = details.descriptions.description1000[0].description;
                }
                else if (details.descriptions.description100 != null)
                {
                    info.ShortOverview = details.descriptions.description100[0].description;
                }
            }

            if (info.IsSeries)
            {
                info.SeriesId = programInfo.programID.Substring(0, 10);

                if (details.metadata != null)
                {
                    var gracenote = details.metadata.Find(x => x.Gracenote != null).Gracenote;
                    info.SeasonNumber = gracenote.season;
                    info.EpisodeNumber = gracenote.episode;
                }
            }

            if (!string.IsNullOrWhiteSpace(details.originalAirDate))
            {
                info.OriginalAirDate = DateTime.Parse(details.originalAirDate);
            }

            if (details.genres != null)
            {
                info.Genres = details.genres.Where(g => !string.IsNullOrWhiteSpace(g)).ToList();
                info.IsNews = details.genres.Contains("news", StringComparer.OrdinalIgnoreCase);
            }

            return info;
        }

        private string GetProgramLogo(string apiUrl, ScheduleDirect.ShowImages images)
        {
            string url = "";
            if (images.data != null)
            {
                var smallImages = images.data.Where(i => i.size == "Sm").ToList();
                if (smallImages.Any())
                {
                    images.data = smallImages;
                }
                var logoIndex = images.data.FindIndex(i => i.category == "Logo");
                if (logoIndex == -1)
                {
                    logoIndex = 0;
                }
                if (images.data[logoIndex].uri.Contains("http"))
                {
                    url = images.data[logoIndex].uri;
                }
                else
                {
                    url = apiUrl + "/image/" + images.data[logoIndex].uri;
                }
                //_logger.Debug("URL for image is : " + url);
            }
            return url;
        }

        private async Task<List<ScheduleDirect.ShowImages>> GetImageForPrograms(List<string> programIds,
           CancellationToken cancellationToken)
        {
            var imageIdString = "[";

            programIds.ForEach(i =>
            {
                if (!imageIdString.Contains(i.Substring(0, 10)))
                {
                    imageIdString += "\"" + i.Substring(0, 10) + "\",";
                }
                ;
            });
            imageIdString = imageIdString.TrimEnd(',') + "]";
            _logger.Debug("Json for show images = " + imageIdString);
            var httpOptions = new HttpRequestOptions()
            {
                Url = ApiUrl + "/metadata/programs",
                UserAgent = UserAgent,
                CancellationToken = cancellationToken,
                RequestContent = imageIdString
            };
            List<ScheduleDirect.ShowImages> images;
            using (var innerResponse2 = await _httpClient.Post(httpOptions))
            {
                images = _jsonSerializer.DeserializeFromStream<List<ScheduleDirect.ShowImages>>(
                    innerResponse2.Content);
            }

            return images;
        }

        public async Task<List<NameIdPair>> GetHeadends(ListingsProviderInfo info, string country, string location, CancellationToken cancellationToken)
        {
            var token = await GetToken(info, cancellationToken);

            var lineups = new List<NameIdPair>();

            if (string.IsNullOrWhiteSpace(token))
            {
                return lineups;
            }

            _logger.Info("Headends on account ");

            var options = new HttpRequestOptions()
            {
                Url = ApiUrl + "/headends?country=" + country + "&postalcode=" + location,
                UserAgent = UserAgent,
                CancellationToken = cancellationToken
            };

            options.RequestHeaders["token"] = token;

            try
            {
                using (Stream responce = await _httpClient.Get(options).ConfigureAwait(false))
                {
                    var root = _jsonSerializer.DeserializeFromStream<List<ScheduleDirect.Headends>>(responce);
                    _logger.Info("Lineups on account ");
                    if (root != null)
                    {
                        foreach (ScheduleDirect.Headends headend in root)
                        {
                            _logger.Info("Headend: " + headend.headend);
                            foreach (ScheduleDirect.Lineup lineup in headend.lineups)
                            {
                                _logger.Info("Headend: " + lineup.uri);

                                lineups.Add(new NameIdPair
                                {
                                    Name = string.IsNullOrWhiteSpace(lineup.name) ? lineup.lineup : lineup.name,
                                    Id = lineup.uri.Substring(18)
                                });
                            }
                        }
                    }
                    else
                    {
                        _logger.Info("No lineups on account");
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.Error("Error getting headends", ex);
            }

            return lineups;
        }

        private readonly ConcurrentDictionary<string, NameValuePair> _tokens = new ConcurrentDictionary<string, NameValuePair>();

        private async Task<string> GetToken(ListingsProviderInfo info, CancellationToken cancellationToken)
        {
            var username = info.Username;

            // Reset the token if there's no username
            if (string.IsNullOrWhiteSpace(username))
            {
                return null;
            }

            var password = info.Password;
            if (string.IsNullOrWhiteSpace(password))
            {
                return null;
            }

            NameValuePair savedToken = null;
            if (!_tokens.TryGetValue(username, out savedToken))
            {
                savedToken = new NameValuePair();
                _tokens.TryAdd(username, savedToken);
            }

            if (!string.IsNullOrWhiteSpace(savedToken.Name) && !string.IsNullOrWhiteSpace(savedToken.Value))
            {
                long ticks;
                if (long.TryParse(savedToken.Value, NumberStyles.Any, CultureInfo.InvariantCulture, out ticks))
                {
                    // If it's under 24 hours old we can still use it
                    if ((DateTime.UtcNow.Ticks - ticks) < TimeSpan.FromHours(24).Ticks)
                    {
                        return savedToken.Name;
                    }
                }
            }

            await _tokenSemaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var result = await GetTokenInternal(username, password, cancellationToken).ConfigureAwait(false);
                savedToken.Name = result;
                savedToken.Value = DateTime.UtcNow.Ticks.ToString(CultureInfo.InvariantCulture);
                return result;
            }
            finally
            {
                _tokenSemaphore.Release();
            }
        }

        private async Task<string> GetTokenInternal(string username, string password,
            CancellationToken cancellationToken)
        {
            var httpOptions = new HttpRequestOptions()
            {
                Url = ApiUrl + "/token",
                UserAgent = UserAgent,
                RequestContent = "{\"username\":\"" + username + "\",\"password\":\"" + password + "\"}",
                CancellationToken = cancellationToken
            };
            //_logger.Info("Obtaining token from Schedules Direct from addres: " + httpOptions.Url + " with body " +
            // httpOptions.RequestContent);

            using (var responce = await _httpClient.Post(httpOptions))
            {
                var root = _jsonSerializer.DeserializeFromStream<ScheduleDirect.Token>(responce.Content);
                if (root.message == "OK")
                {
                    _logger.Info("Authenticated with Schedules Direct token: " + root.token);
                    return root.token;
                }

                throw new ApplicationException("Could not authenticate with Schedules Direct Error: " + root.message);
            }
        }

        private async Task AddLineupToAccount(ListingsProviderInfo info, CancellationToken cancellationToken)
        {
            var token = await GetToken(info, cancellationToken);

            if (string.IsNullOrWhiteSpace(token))
            {
                throw new ArgumentException("Authentication required.");
            }

            if (string.IsNullOrWhiteSpace(info.ListingsId))
            {
                throw new ArgumentException("Listings Id required");
            }

            _logger.Info("Adding new LineUp ");

            var httpOptions = new HttpRequestOptions()
            {
                Url = ApiUrl + "/lineups/" + info.ListingsId,
                UserAgent = UserAgent,
                CancellationToken = cancellationToken
            };

            httpOptions.RequestHeaders["token"] = token;

            using (var response = await _httpClient.SendAsync(httpOptions, "PUT"))
            {
            }
        }

        public string Name
        {
            get { return "Schedules Direct"; }
        }

        public string Type
        {
            get { return "SchedulesDirect"; }
        }

        private async Task<bool> HasLineup(ListingsProviderInfo info, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(info.ListingsId))
            {
                throw new ArgumentException("Listings Id required");
            }

            var token = await GetToken(info, cancellationToken);

            if (string.IsNullOrWhiteSpace(token))
            {
                throw new Exception("token required");
            }

            _logger.Info("Headends on account ");

            var options = new HttpRequestOptions()
            {
                Url = ApiUrl + "/lineups",
                UserAgent = UserAgent,
                CancellationToken = cancellationToken
            };

            options.RequestHeaders["token"] = token;

            using (var response = await _httpClient.Get(options).ConfigureAwait(false))
            {
                var root = _jsonSerializer.DeserializeFromStream<ScheduleDirect.Lineups>(response);

                return root.lineups.Any(i => string.Equals(info.ListingsId, i.lineup, StringComparison.OrdinalIgnoreCase));
            }
        }

        public async Task Validate(ListingsProviderInfo info, bool validateLogin, bool validateListings)
        {
            if (validateLogin)
            {
                if (string.IsNullOrWhiteSpace(info.Username))
                {
                    throw new ArgumentException("Username is required");
                }
                if (string.IsNullOrWhiteSpace(info.Password))
                {
                    throw new ArgumentException("Password is required");
                }
            }
            if (validateListings)
            {
                if (string.IsNullOrWhiteSpace(info.ListingsId))
                {
                    throw new ArgumentException("Listings Id required");
                }

                var hasLineup = await HasLineup(info, CancellationToken.None).ConfigureAwait(false);

                if (!hasLineup)
                {
                    await AddLineupToAccount(info, CancellationToken.None).ConfigureAwait(false);
                }
            }
        }

        public Task<List<NameIdPair>> GetLineups(ListingsProviderInfo info, string country, string location)
        {
            return GetHeadends(info, country, location, CancellationToken.None);
        }

        public class ScheduleDirect
        {
            public class Token
            {
                public int code { get; set; }
                public string message { get; set; }
                public string serverID { get; set; }
                public string token { get; set; }
            }
            public class Lineup
            {
                public string lineup { get; set; }
                public string name { get; set; }
                public string transport { get; set; }
                public string location { get; set; }
                public string uri { get; set; }
            }

            public class Lineups
            {
                public int code { get; set; }
                public string serverID { get; set; }
                public string datetime { get; set; }
                public List<Lineup> lineups { get; set; }
            }


            public class Headends
            {
                public string headend { get; set; }
                public string transport { get; set; }
                public string location { get; set; }
                public List<Lineup> lineups { get; set; }
            }



            public class Map
            {
                public string stationID { get; set; }
                public string channel { get; set; }
                public int uhfVhf { get; set; }
                public int atscMajor { get; set; }
                public int atscMinor { get; set; }
            }

            public class Broadcaster
            {
                public string city { get; set; }
                public string state { get; set; }
                public string postalcode { get; set; }
                public string country { get; set; }
            }

            public class Logo
            {
                public string URL { get; set; }
                public int height { get; set; }
                public int width { get; set; }
                public string md5 { get; set; }
            }

            public class Station
            {
                public string stationID { get; set; }
                public string name { get; set; }
                public string callsign { get; set; }
                public List<string> broadcastLanguage { get; set; }
                public List<string> descriptionLanguage { get; set; }
                public Broadcaster broadcaster { get; set; }
                public string affiliate { get; set; }
                public Logo logo { get; set; }
                public bool? isCommercialFree { get; set; }
            }

            public class Metadata
            {
                public string lineup { get; set; }
                public string modified { get; set; }
                public string transport { get; set; }
            }

            public class Channel
            {
                public List<Map> map { get; set; }
                public List<Station> stations { get; set; }
                public Metadata metadata { get; set; }
            }

            public class RequestScheduleForChannel
            {
                public string stationID { get; set; }
                public List<string> date { get; set; }
            }




            public class Rating
            {
                public string body { get; set; }
                public string code { get; set; }
            }

            public class Multipart
            {
                public int partNumber { get; set; }
                public int totalParts { get; set; }
            }

            public class Program
            {
                public string programID { get; set; }
                public string airDateTime { get; set; }
                public int duration { get; set; }
                public string md5 { get; set; }
                public List<string> audioProperties { get; set; }
                public List<string> videoProperties { get; set; }
                public List<Rating> ratings { get; set; }
                public bool? @new { get; set; }
                public Multipart multipart { get; set; }
            }



            public class MetadataSchedule
            {
                public string modified { get; set; }
                public string md5 { get; set; }
                public string startDate { get; set; }
                public string endDate { get; set; }
                public int days { get; set; }
            }

            public class Day
            {
                public string stationID { get; set; }
                public List<Program> programs { get; set; }
                public MetadataSchedule metadata { get; set; }
            }

            //
            public class Title
            {
                public string title120 { get; set; }
            }

            public class EventDetails
            {
                public string subType { get; set; }
            }

            public class Description100
            {
                public string descriptionLanguage { get; set; }
                public string description { get; set; }
            }

            public class Description1000
            {
                public string descriptionLanguage { get; set; }
                public string description { get; set; }
            }

            public class DescriptionsProgram
            {
                public List<Description100> description100 { get; set; }
                public List<Description1000> description1000 { get; set; }
            }

            public class Gracenote
            {
                public int season { get; set; }
                public int episode { get; set; }
            }

            public class MetadataPrograms
            {
                public Gracenote Gracenote { get; set; }
            }

            public class ContentRating
            {
                public string body { get; set; }
                public string code { get; set; }
            }

            public class Cast
            {
                public string billingOrder { get; set; }
                public string role { get; set; }
                public string nameId { get; set; }
                public string personId { get; set; }
                public string name { get; set; }
                public string characterName { get; set; }
            }

            public class Crew
            {
                public string billingOrder { get; set; }
                public string role { get; set; }
                public string nameId { get; set; }
                public string personId { get; set; }
                public string name { get; set; }
            }

            public class QualityRating
            {
                public string ratingsBody { get; set; }
                public string rating { get; set; }
                public string minRating { get; set; }
                public string maxRating { get; set; }
                public string increment { get; set; }
            }

            public class Movie
            {
                public string year { get; set; }
                public int duration { get; set; }
                public List<QualityRating> qualityRating { get; set; }
            }

            public class Recommendation
            {
                public string programID { get; set; }
                public string title120 { get; set; }
            }

            public class ProgramDetails
            {
                public string audience { get; set; }
                public string programID { get; set; }
                public List<Title> titles { get; set; }
                public EventDetails eventDetails { get; set; }
                public DescriptionsProgram descriptions { get; set; }
                public string originalAirDate { get; set; }
                public List<string> genres { get; set; }
                public string episodeTitle150 { get; set; }
                public List<MetadataPrograms> metadata { get; set; }
                public List<ContentRating> contentRating { get; set; }
                public List<Cast> cast { get; set; }
                public List<Crew> crew { get; set; }
                public string showType { get; set; }
                public bool hasImageArtwork { get; set; }
                public string images { get; set; }
                public string imageID { get; set; }
                public string md5 { get; set; }
                public List<string> contentAdvisory { get; set; }
                public Movie movie { get; set; }
                public List<Recommendation> recommendations { get; set; }
            }

            public class Caption
            {
                public string content { get; set; }
                public string lang { get; set; }
            }

            public class ImageData
            {
                public string width { get; set; }
                public string height { get; set; }
                public string uri { get; set; }
                public string size { get; set; }
                public string aspect { get; set; }
                public string category { get; set; }
                public string text { get; set; }
                public string primary { get; set; }
                public string tier { get; set; }
                public Caption caption { get; set; }
            }

            public class ShowImages
            {
                public string programID { get; set; }
                public List<ImageData> data { get; set; }
            }

        }
    }
}