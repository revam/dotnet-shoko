﻿using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using NLog;
using Shoko.Commons.Extensions;
using Shoko.Models.Enums;
using Shoko.Models.Server;
using Shoko.Models.TvDB;
using Shoko.Server.Commands;
using Shoko.Server.Extensions;
using Shoko.Server.Models;
using Shoko.Server.Repositories;
using TvDbSharper;
using TvDbSharper.Dto;
using Language = TvDbSharper.Dto.Language;

namespace Shoko.Server.Providers.TvDB
{
    public static class TvDBApiHelper
    {
        private static readonly ITvDbClient client = new TvDbClient();
        private static readonly Logger logger = LogManager.GetCurrentClassLogger();

        public static string CurrentServerTime
        {
            get
            {
                DateTime epoch = new DateTime(1970, 1, 1, 0, 0, 0, 0).ToLocalTime();
                TimeSpan span = (new DateTime().ToLocalTime() - epoch);
                return span.TotalSeconds.ToString(CultureInfo.InvariantCulture);
            }
        }

        private static async Task CheckAuthorizationAsync()
        {
            try
            {
                client.AcceptedLanguage = ServerSettings.TvDB_Language;
                if (string.IsNullOrEmpty(client.Authentication.Token))
                {
                    TvDBRateLimiter.Instance.EnsureRate();
                    await client.Authentication.AuthenticateAsync(Constants.TvDB.apiKey);
                    if (string.IsNullOrEmpty(client.Authentication.Token))
                        throw new TvDbServerException("Authentication Failed", 200);
                }
            }
            catch (Exception e)
            {
                logger.Error(e, $"Error in TvDBAuth: {e}");
                throw;
            }
        }

        public static TvDB_Series GetSeriesInfoOnline(int seriesID, bool forceRefresh)
        {
            return Task.Run(async () => await GetSeriesInfoOnlineAsync(seriesID, forceRefresh)).Result;
        }

        public static async Task<TvDB_Series> GetSeriesInfoOnlineAsync(int seriesID, bool forceRefresh)
        {
            try
            {
                TvDB_Series tvSeries = Repo.TvDB_Series.GetByTvDBID(seriesID);
                if (tvSeries != null && !forceRefresh)
                    return tvSeries;
                await CheckAuthorizationAsync();

                TvDBRateLimiter.Instance.EnsureRate();
                var response = await client.Series.GetAsync(seriesID);
                Series series = response.Data;
                using (var tupd = Repo.TvDB_Series.BeginUpdate(tvSeries))
                {
                    tupd.Entity.PopulateFromSeriesInfo_RA(series);
                    tvSeries=tupd.Commit();
                }
                return tvSeries;
            }
            catch (TvDbServerException exception)
            {
                if (exception.StatusCode == (int)HttpStatusCode.Unauthorized)
                {
                    client.Authentication.Token = null;
                    await CheckAuthorizationAsync();
                    if (!string.IsNullOrEmpty(client.Authentication.Token))
                        return await GetSeriesInfoOnlineAsync(seriesID, forceRefresh);
                    // suppress 404 and move on
                } else if (exception.StatusCode == (int)HttpStatusCode.NotFound) return null;

                logger.Error(exception,
                    $"TvDB returned an error code: {exception.StatusCode}\n        {exception.Message}");
            }
            catch (Exception ex)
            {
                logger.Error(ex, $"Error in TvDBApiHelper.GetSeriesInfoOnline: {ex}");
            }

            return null;
        }

        public static List<TVDB_Series_Search_Response> SearchSeries(string criteria)
        {
            return Task.Run(async () => await SearchSeriesAsync(criteria)).Result;
        }

        public static async Task<List<TVDB_Series_Search_Response>> SearchSeriesAsync(string criteria)
        {
            List<TVDB_Series_Search_Response> results = new List<TVDB_Series_Search_Response>();

            try
            {
                await CheckAuthorizationAsync();

                // Search for a series
                logger.Trace($"Search TvDB Series: {criteria}");

                TvDBRateLimiter.Instance.EnsureRate();
                var response = await client.Search.SearchSeriesByNameAsync(criteria);
                SeriesSearchResult[] series = response?.Data;
                if (series == null) return results;

                foreach (SeriesSearchResult item in series)
                {
                    TVDB_Series_Search_Response searchResult = new TVDB_Series_Search_Response();
                    searchResult.Populate(item);
                    results.Add(searchResult);
                }
            }
            catch (TvDbServerException exception)
            {
                if (exception.StatusCode == (int)HttpStatusCode.Unauthorized)
                {
                    client.Authentication.Token = null;
                    await CheckAuthorizationAsync();
                    if (!string.IsNullOrEmpty(client.Authentication.Token))
                        return await SearchSeriesAsync(criteria);
                    // suppress 404 and move on
                } else if (exception.StatusCode == (int)HttpStatusCode.NotFound) return results;
                logger.Error(exception,
                    $"TvDB returned an error code: {exception.StatusCode}\n        {exception.Message}\n        when searching for {criteria}");
            }
            catch (Exception ex)
            {
                logger.Error(ex, $"Error in SearchSeries: {ex}");
            }

            return results;
        }

        public static void LinkAniDBTvDB(int animeID, EpisodeType aniEpType, int aniEpNumber, int tvDBID, int tvSeasonNumber, int tvEpNumber, bool excludeFromWebCache, bool additiveLink = false)
        {
            if (!additiveLink)
            {
                // remove all current links
                logger.Info($"Removing All TvDB Links for: {animeID}");
                RemoveAllAniDBTvDBLinks(animeID, -1, false);
            }

            // check if we have this information locally
            // if not download it now
            TvDB_Series tvSeries = Repo.TvDB_Series.GetByTvDBID(tvDBID);

            if (tvSeries != null)
            {
                // download and update series info, episode info and episode images
                // will also download fanart, posters and wide banners
                CommandRequest_TvDBUpdateSeries cmdSeriesEps =
                    new CommandRequest_TvDBUpdateSeries(tvDBID,
                        false);
                cmdSeriesEps.Save();
            }
            else
            {
                tvSeries = GetSeriesInfoOnline(tvDBID, true);
            }

            CrossRef_AniDB_TvDBV2 xref;
            using (var cupd = Repo.CrossRef_AniDB_TvDBV2.BeginAddOrUpdateWithLock(() => Repo.CrossRef_AniDB_TvDBV2.GetByTvDBID(tvDBID, tvSeasonNumber, tvEpNumber, animeID, (int) aniEpType, aniEpNumber)))
            {
                cupd.Entity.AnimeID = animeID;
                cupd.Entity.AniDBStartEpisodeType = (int)aniEpType;
                cupd.Entity.AniDBStartEpisodeNumber = aniEpNumber;

                cupd.Entity.TvDBID = tvDBID;
                cupd.Entity.TvDBSeasonNumber = tvSeasonNumber;
                cupd.Entity.TvDBStartEpisodeNumber = tvEpNumber;
                if (tvSeries != null)
                    cupd.Entity.TvDBTitle = tvSeries.SeriesName;

                if (excludeFromWebCache)
                    cupd.Entity.CrossRefSource = (int)CrossRefSource.WebCache;
                else
                    cupd.Entity.CrossRefSource = (int)CrossRefSource.User;
                xref=cupd.Commit();
            }

            logger.Info(
                $"Adding TvDB Link: AniDB(ID:{animeID}|Type:{aniEpType}|Number:{aniEpNumber}) -> TvDB(ID:{tvDBID}|Season:{tvSeasonNumber}|Number:{tvEpNumber})");
            if (!excludeFromWebCache)
            {
                var req = new CommandRequest_WebCacheSendXRefAniDBTvDB(xref.CrossRef_AniDB_TvDBV2ID);
                req.Save();
            }

            if (ServerSettings.Trakt_IsEnabled && !string.IsNullOrEmpty(ServerSettings.Trakt_AuthToken))
            {
                // check for Trakt associations
                Repo.CrossRef_AniDB_TraktV2.Delete(Repo.CrossRef_AniDB_TraktV2.GetByAnimeID(animeID));
                var cmd2 = new CommandRequest_TraktSearchAnime(animeID, false);
                cmd2.Save();
            }
        }

        public static void RemoveAllAniDBTvDBLinks(int animeID, int aniEpType = -1, bool updateStats = true)
        {
            // check for Trakt associations
            Repo.CrossRef_AniDB_TraktV2.Delete(Repo.CrossRef_AniDB_TraktV2.GetByAnimeID(animeID));

            List<CrossRef_AniDB_TvDBV2> xrefs = Repo.CrossRef_AniDB_TvDBV2.GetByAnimeID(animeID);
            if (xrefs == null || xrefs.Count == 0) return;

            foreach (CrossRef_AniDB_TvDBV2 xref in xrefs)
            {
                if (aniEpType != -1 && aniEpType == xref.AniDBStartEpisodeType) continue;

                Repo.CrossRef_AniDB_TvDBV2.Delete(xref.CrossRef_AniDB_TvDBV2ID);

                if (aniEpType == -1)
                {
                    foreach (EpisodeType eptype in Enum.GetValues(typeof(EpisodeType)))
                    {
                        CommandRequest_WebCacheDeleteXRefAniDBTvDB req = new CommandRequest_WebCacheDeleteXRefAniDBTvDB(
                            animeID,
                            (int)eptype, xref.AniDBStartEpisodeNumber,
                            xref.TvDBID, xref.TvDBSeasonNumber, xref.TvDBStartEpisodeNumber);
                        req.Save();
                    }
                }
                else
                {
                    CommandRequest_WebCacheDeleteXRefAniDBTvDB req = new CommandRequest_WebCacheDeleteXRefAniDBTvDB(
                        animeID,
                        aniEpType, xref.AniDBStartEpisodeNumber,
                        xref.TvDBID, xref.TvDBSeasonNumber, xref.TvDBStartEpisodeNumber);
                    req.Save();
                }
            }

            if (updateStats) SVR_AniDB_Anime.UpdateStatsByAnimeID(animeID);
        }

        public static List<TvDB_Language> GetLanguages()
        {
            return Task.Run(async () => await GetLanguagesAsync()).Result;
        }

        public static async Task<List<TvDB_Language>> GetLanguagesAsync()
        {
            List<TvDB_Language> languages = new List<TvDB_Language>();

            try
            {
                await CheckAuthorizationAsync();

                TvDBRateLimiter.Instance.EnsureRate();
                var response = await client.Languages.GetAllAsync();
                Language[] apiLanguages = response.Data;

                if (apiLanguages.Length <= 0)
                    return languages;

                foreach (Language item in apiLanguages)
                {
                    TvDB_Language lan = new TvDB_Language
                    {
                        Id = item.Id,
                        EnglishName = item.EnglishName,
                        Name = item.Name,
                        Abbreviation = item.Abbreviation
                    };
                    languages.Add(lan);
                }
            }
            catch (TvDbServerException exception)
            {
                if (exception.StatusCode == (int)HttpStatusCode.Unauthorized)
                {
                    client.Authentication.Token = null;
                    await CheckAuthorizationAsync();
                    if (!string.IsNullOrEmpty(client.Authentication.Token))
                        return await GetLanguagesAsync();
                    // suppress 404 and move on
                } else if (exception.StatusCode == (int)HttpStatusCode.NotFound) return languages;
                logger.Error(exception,
                    $"TvDB returned an error code: {exception.StatusCode}\n        {exception.Message}");
            }
            catch (Exception ex)
            {
                logger.Error(ex, $"Error in TVDBHelper.GetSeriesBannersOnline: {ex}");
            }

            return languages;
        }

        public static void DownloadAutomaticImages(int seriesID, bool forceDownload)
        {
            ImagesSummary summary = GetSeriesImagesCounts(seriesID);
            if (summary == null) return;
            if (summary.Fanart > 0) DownloadAutomaticImages(GetFanartOnline(seriesID), seriesID, forceDownload);
            if (summary.Poster > 0 || summary.Season > 0)
                DownloadAutomaticImages(GetPosterOnline(seriesID), seriesID, forceDownload);
            if (summary.Seasonwide > 0 || summary.Series > 0)
                DownloadAutomaticImages(GetBannerOnline(seriesID), seriesID, forceDownload);
        }

        static ImagesSummary GetSeriesImagesCounts(int seriesID)
        {
            return Task.Run(async () => await GetSeriesImagesCountsAsync(seriesID)).Result;
        }

        static async Task<ImagesSummary> GetSeriesImagesCountsAsync(int seriesID)
        {
            try
            {
                await CheckAuthorizationAsync();

                TvDBRateLimiter.Instance.EnsureRate();
                var response = await client.Series.GetImagesSummaryAsync(seriesID);
                return response.Data;
            }
            catch (TvDbServerException exception)
            {
                if (exception.StatusCode == (int)HttpStatusCode.Unauthorized)
                {
                    client.Authentication.Token = null;
                    await CheckAuthorizationAsync();
                    if (!string.IsNullOrEmpty(client.Authentication.Token))
                        return await GetSeriesImagesCountsAsync(seriesID);
                    // suppress 404 and move on
                } else if (exception.StatusCode == (int)HttpStatusCode.NotFound) return null;
                logger.Error(exception,
                    $"TvDB returned an error code: {exception.StatusCode}\n        {exception.Message}");
            }
            return null;
        }

        static async Task<Image[]> GetSeriesImagesAsync(int seriesID, KeyType type)
        {
            await CheckAuthorizationAsync();

            ImagesQuery query = new ImagesQuery
            {
                KeyType = type
            };
            TvDBRateLimiter.Instance.EnsureRate();
            try
            {
                var response = await client.Series.GetImagesAsync(seriesID, query);
                return response.Data;
            }
            catch (TvDbServerException exception)
            {
                if (exception.StatusCode == (int)HttpStatusCode.Unauthorized)
                {
                    client.Authentication.Token = null;
                    await CheckAuthorizationAsync();
                    if (!string.IsNullOrEmpty(client.Authentication.Token))
                        return await GetSeriesImagesAsync(seriesID, type);
                    // suppress 404 and move on
                }
                else if (exception.StatusCode == (int)HttpStatusCode.NotFound) return new Image[] { };
                logger.Error(exception,
                    $"TvDB returned an error code: {exception.StatusCode}\n        {exception.Message}");
            }
            catch
            {
                // ignore
            }
            return new Image[] { };
        }

        public static List<TvDB_ImageFanart> GetFanartOnline(int seriesID)
        {
            return Task.Run(async () => await GetFanartOnlineAsync(seriesID)).Result;
        }

        public static async Task<List<TvDB_ImageFanart>> GetFanartOnlineAsync(int seriesID)
        {
            List<int> validIDs = new List<int>();
            List<TvDB_ImageFanart> tvImages = new List<TvDB_ImageFanart>();
            try
            {
                Image[] images = await GetSeriesImagesAsync(seriesID, KeyType.Fanart);

                int count = 0;
                foreach (Image image in images)
                {
                    int id = image.Id ?? 0;
                    if (id == 0) continue;

                    if (count >= ServerSettings.TvDB_AutoFanartAmount) break;
                    TvDB_ImageFanart img;
                    using (var repo = Repo.TvDB_ImageFanart.BeginAddOrUpdateWithLock(() => Repo.TvDB_ImageFanart.GetByTvDBID(id)))
                    {
                        if (repo.Original == null)
                            repo.Entity.Enabled = 1;
                        repo.Entity.Populate_RA(seriesID, image);
                        repo.Entity.Language = client.AcceptedLanguage;
                        img = repo.Commit();

                    }
                    tvImages.Add(img);
                    validIDs.Add(id);
                    count++;
                }

                // delete any images from the database which are no longer valid
                foreach (TvDB_ImageFanart img in Repo.TvDB_ImageFanart.GetBySeriesID(seriesID))
                    if (!validIDs.Contains(img.Id))
                        Repo.TvDB_ImageFanart.Delete(img.TvDB_ImageFanartID);
            }
            catch (TvDbServerException exception)
            {
                if (exception.StatusCode == (int)HttpStatusCode.Unauthorized)
                {
                    client.Authentication.Token = null;
                    await CheckAuthorizationAsync();
                    if (!string.IsNullOrEmpty(client.Authentication.Token))
                        return await GetFanartOnlineAsync(seriesID);
                    // suppress 404 and move on
                }
                else if (exception.StatusCode == (int)HttpStatusCode.NotFound) return tvImages;
                logger.Error(exception,
                    $"TvDB returned an error code: {exception.StatusCode}\n        {exception.Message}");
            }
            catch (Exception ex)
            {
                logger.Error(ex, $"Error in TVDBApiHelper.GetSeriesBannersOnlineAsync: {ex}");
            }

            return tvImages;
        }

        public static List<TvDB_ImagePoster> GetPosterOnline(int seriesID)
        {
            return Task.Run(async () => await GetPosterOnlineAsync(seriesID)).Result;
        }

        public static async Task<List<TvDB_ImagePoster>> GetPosterOnlineAsync(int seriesID)
        {
            List<int> validIDs = new List<int>();
            List<TvDB_ImagePoster> tvImages = new List<TvDB_ImagePoster>();

            try
            {
                Image[] posters = await GetSeriesImagesAsync(seriesID, KeyType.Poster);
                Image[] season = await GetSeriesImagesAsync(seriesID, KeyType.Season);

                Image[] images = posters.Concat(season).ToArray();


                int count = 0;
                foreach (Image image in images)
                {
                    int id = image.Id ?? 0;
                    if (id == 0) continue;

                    if (count >= ServerSettings.TvDB_AutoPostersAmount) break;
                    TvDB_ImagePoster img;
                    using (var repo = Repo.TvDB_ImagePoster.BeginAddOrUpdateWithLock(() => Repo.TvDB_ImagePoster.GetByTvDBID(id)))
                    {
                        if (repo.Original == null)
                            repo.Entity.Enabled = 1;
                        repo.Entity.Populate_RA(seriesID, image);
                        repo.Entity.Language = client.AcceptedLanguage;
                        img = repo.Commit();
                    }
                    validIDs.Add(id);
                    tvImages.Add(img);
                    count++;
                }

                // delete any images from the database which are no longer valid
                foreach (TvDB_ImagePoster img in Repo.TvDB_ImagePoster.GetBySeriesID(seriesID))
                    if (!validIDs.Contains(img.Id))
                        Repo.TvDB_ImagePoster.Delete(img.TvDB_ImagePosterID);
            }
            catch (TvDbServerException exception)
            {
                if (exception.StatusCode == (int)HttpStatusCode.Unauthorized)
                {
                    client.Authentication.Token = null;
                    await CheckAuthorizationAsync();
                    if (!string.IsNullOrEmpty(client.Authentication.Token))
                        return await GetPosterOnlineAsync(seriesID);
                    // suppress 404 and move on
                }
                else if (exception.StatusCode == (int)HttpStatusCode.NotFound) return tvImages;
                logger.Error(exception,
                    $"TvDB returned an error code: {exception.StatusCode}\n        {exception.Message}");
            }
            catch (Exception ex)
            {
                logger.Error(ex, $"Error in TVDBApiHelper.GetPosterOnlineAsync: {ex}");
            }

            return tvImages;
        }

        public static List<TvDB_ImageWideBanner> GetBannerOnline(int seriesID)
        {
            return Task.Run(async () => await GetBannerOnlineAsync(seriesID)).Result;
        }

        public static async Task<List<TvDB_ImageWideBanner>> GetBannerOnlineAsync(int seriesID)
        {
            List<int> validIDs = new List<int>();
            List<TvDB_ImageWideBanner> tvImages = new List<TvDB_ImageWideBanner>();

            try
            {
                Image[] season = await GetSeriesImagesAsync(seriesID, KeyType.Seasonwide);
                Image[] series = await GetSeriesImagesAsync(seriesID, KeyType.Series);

                Image[] images = season.Concat(series).ToArray();

                int count = 0;
                foreach (Image image in images)
                {
                    int id = image.Id ?? 0;
                    if (id == 0) continue;

                    if (count >= ServerSettings.TvDB_AutoWideBannersAmount) break;
                    TvDB_ImageWideBanner img;
                    using (var repo = Repo.TvDB_ImageWideBanner.BeginAddOrUpdateWithLock(() => Repo.TvDB_ImageWideBanner.GetByTvDBID(id)))
                    {
                        if (repo.Original == null)
                            repo.Entity.Enabled = 1;
                        repo.Entity.Populate_RA(seriesID, image);
                        repo.Entity.Language = client.AcceptedLanguage;
                        img = repo.Commit();
                    }
                    validIDs.Add(id);
                    tvImages.Add(img);
                    count++;
                }

                // delete any images from the database which are no longer valid
                foreach (TvDB_ImageWideBanner img in Repo.TvDB_ImageWideBanner.GetBySeriesID(seriesID))
                    if (!validIDs.Contains(img.Id))
                        Repo.TvDB_ImageWideBanner.Delete(img.TvDB_ImageWideBannerID);
            }
            catch (TvDbServerException exception)
            {
                if (exception.StatusCode == (int)HttpStatusCode.Unauthorized)
                {
                    client.Authentication.Token = null;
                    await CheckAuthorizationAsync();
                    if (!string.IsNullOrEmpty(client.Authentication.Token))
                        return await GetBannerOnlineAsync(seriesID);
                    // suppress 404 and move on
                }
                else if (exception.StatusCode == (int)HttpStatusCode.NotFound) return tvImages;
                logger.Error(exception,
                    $"TvDB returned an error code: {exception.StatusCode}\n        {exception.Message}");
            }
            catch (Exception ex)
            {
                logger.Error(ex, $"Error in TVDBApiHelper.GetPosterOnlineAsync: {ex}");
            }

            return tvImages;
        }

        public static void DownloadAutomaticImages(List<TvDB_ImageFanart> images, int seriesID, bool forceDownload)
        {
            // find out how many images we already have locally
            int imageCount = Repo.TvDB_ImageFanart.GetBySeriesID(seriesID).Count(fanart =>
                !string.IsNullOrEmpty(fanart.GetFullImagePath()) && File.Exists(fanart.GetFullImagePath()));

            foreach (TvDB_ImageFanart img in images)
                if (ServerSettings.TvDB_AutoFanart && imageCount < ServerSettings.TvDB_AutoFanartAmount &&
                    !string.IsNullOrEmpty(img.GetFullImagePath()))
                {
                    bool fileExists = File.Exists(img.GetFullImagePath());
                    if (fileExists && !forceDownload) continue;
                    CommandRequest_DownloadImage cmd = new CommandRequest_DownloadImage(img.TvDB_ImageFanartID,
                        ImageEntityType.TvDB_FanArt, forceDownload);
                    cmd.Save();
                    imageCount++;
                }
                else
                {
                    //The TvDB_AutoFanartAmount point to download less images than its available
                    // we should clean those image that we didn't download because those dont exists in local repo
                    // first we check if file was downloaded
                    if (string.IsNullOrEmpty(img.GetFullImagePath()) || !File.Exists(img.GetFullImagePath()))
                        Repo.TvDB_ImageFanart.Delete(img.TvDB_ImageFanartID);
                }
        }

        public static void DownloadAutomaticImages(List<TvDB_ImagePoster> images, int seriesID, bool forceDownload)
        {
            // find out how many images we already have locally
            int imageCount = Repo.TvDB_ImagePoster.GetBySeriesID(seriesID).Count(fanart =>
                !string.IsNullOrEmpty(fanart.GetFullImagePath()) && File.Exists(fanart.GetFullImagePath()));

            foreach (TvDB_ImagePoster img in images)
                if (ServerSettings.TvDB_AutoFanart && imageCount < ServerSettings.TvDB_AutoFanartAmount &&
                    !string.IsNullOrEmpty(img.GetFullImagePath()))
                {
                    bool fileExists = File.Exists(img.GetFullImagePath());
                    if (fileExists && !forceDownload) continue;
                    CommandRequest_DownloadImage cmd = new CommandRequest_DownloadImage(img.TvDB_ImagePosterID,
                        ImageEntityType.TvDB_Cover, forceDownload);
                    cmd.Save();
                    imageCount++;
                }
                else
                {
                    //The TvDB_AutoFanartAmount point to download less images than its available
                    // we should clean those image that we didn't download because those dont exists in local repo
                    // first we check if file was downloaded
                    if (string.IsNullOrEmpty(img.GetFullImagePath()) || !File.Exists(img.GetFullImagePath()))
                        Repo.TvDB_ImageFanart.Delete(img.TvDB_ImagePosterID);
                }
        }

        public static void DownloadAutomaticImages(List<TvDB_ImageWideBanner> images, int seriesID, bool forceDownload)
        {
            // find out how many images we already have locally
            int imageCount = Repo.TvDB_ImageWideBanner.GetBySeriesID(seriesID).Count(banner =>
                !string.IsNullOrEmpty(banner.GetFullImagePath()) && File.Exists(banner.GetFullImagePath()));

            foreach (TvDB_ImageWideBanner img in images)
                if (ServerSettings.TvDB_AutoWideBanners && imageCount < ServerSettings.TvDB_AutoWideBannersAmount &&
                    !string.IsNullOrEmpty(img.GetFullImagePath()))
                {
                    bool fileExists = File.Exists(img.GetFullImagePath());
                    if (fileExists && !forceDownload) continue;
                    CommandRequest_DownloadImage cmd = new CommandRequest_DownloadImage(img.TvDB_ImageWideBannerID,
                        ImageEntityType.TvDB_Banner, forceDownload);
                    cmd.Save();
                    imageCount++;
                }
                else
                {
                    //The TvDB_AutoFanartAmount point to download less images than its available
                    // we should clean those image that we didn't download because those dont exists in local repo
                    // first we check if file was downloaded
                    if (string.IsNullOrEmpty(img.GetFullImagePath()) || !File.Exists(img.GetFullImagePath()))
                        Repo.TvDB_ImageWideBanner.Delete(img.TvDB_ImageWideBannerID);
                }
        }

        public static List<BasicEpisode> GetEpisodesOnline(int seriesID)
        {
            return Task.Run(async () => await GetEpisodesOnlineAsync(seriesID)).Result;
        }

        static async Task<List<BasicEpisode>> GetEpisodesOnlineAsync(int seriesID)
        {
            List<BasicEpisode> apiEpisodes = new List<BasicEpisode>();
            try
            {
                await CheckAuthorizationAsync();

                var tasks = new List<Task<TvDbResponse<BasicEpisode[]>>>();
                TvDBRateLimiter.Instance.EnsureRate();
                var firstResponse = await client.Series.GetEpisodesAsync(seriesID, 1);

                for (int i = 2; i <= firstResponse.Links.Last; i++)
                {
                    TvDBRateLimiter.Instance.EnsureRate();
                    tasks.Add(client.Series.GetEpisodesAsync(seriesID, i));
                }

                var results = await Task.WhenAll(tasks);

                apiEpisodes = firstResponse.Data.Concat(results.SelectMany(x => x.Data)).ToList();
            }
            catch (TvDbServerException exception)
            {
                if (exception.StatusCode == (int)HttpStatusCode.Unauthorized)
                {
                    client.Authentication.Token = null;
                    await CheckAuthorizationAsync();
                    if (!string.IsNullOrEmpty(client.Authentication.Token))
                        return await GetEpisodesOnlineAsync(seriesID);
                    // suppress 404 and move on
                }
                else if (exception.StatusCode == (int)HttpStatusCode.NotFound) return apiEpisodes;
                logger.Error(exception,
                    $"TvDB returned an error code: {exception.StatusCode}\n        {exception.Message}");
            }
            catch (Exception ex)
            {
                logger.Error(ex, $"Error in TvDBApiHelper.GetEpisodesOnlineAsync: {ex}");
            }

            return apiEpisodes;
        }

        public static void UpdateEpisode(int episodeID, bool downloadImages, bool forceRefresh)
        {
            Task.Run(async () => await QueueEpisodeImageDownloadAsync(episodeID, downloadImages, forceRefresh)).Wait();
        }

        static async Task<EpisodeRecord> GetEpisodeDetailsAsync(int episodeID)
        {
            try
            {
                await CheckAuthorizationAsync();

                TvDBRateLimiter.Instance.EnsureRate();
                var response = await client.Episodes.GetAsync(episodeID);
                return response.Data;
            }
            catch (TvDbServerException exception)
            {
                if (exception.StatusCode == (int)HttpStatusCode.Unauthorized)
                {
                    client.Authentication.Token = null;
                    await CheckAuthorizationAsync();
                    if (!string.IsNullOrEmpty(client.Authentication.Token))
                        return await GetEpisodeDetailsAsync(episodeID);
                    // suppress 404 and move on
                }
                else if (exception.StatusCode == (int)HttpStatusCode.NotFound) return null;
                logger.Error(exception,
                    $"TvDB returned an error code: {exception.StatusCode}\n        {exception.Message}");
            }
            catch (Exception ex)
            {
                logger.Error(ex, $"Error in TvDBApiHelper.GetEpisodeDetailsAsync: {ex}");
            }

            return null;
        }

        public static async Task QueueEpisodeImageDownloadAsync(int tvDBEpisodeID, bool downloadImages, bool forceRefresh)
        {
            try
            {
                TvDB_Episode ep = Repo.TvDB_Episode.GetByTvDBID(tvDBEpisodeID);
                if (ep == null || forceRefresh)
                {
                    EpisodeRecord episode = await GetEpisodeDetailsAsync(tvDBEpisodeID);
                    if (episode == null)
                        return;
                    using (var eup = Repo.TvDB_Episode.BeginUpdate(ep))
                    {
                        eup.Entity.Populate_RA(episode);
                        eup.Commit();
                    }
                }

                if (downloadImages)
                    if (!string.IsNullOrEmpty(ep.Filename))
                    {
                        bool fileExists = File.Exists(ep.GetFullImagePath());
                        if (!fileExists || forceRefresh)
                        {
                            CommandRequest_DownloadImage cmd =
                                new CommandRequest_DownloadImage(ep.TvDB_EpisodeID,
                                    ImageEntityType.TvDB_Episode, forceRefresh);
                            cmd.Save();
                        }
                    }
            }
            catch (TvDbServerException exception)
            {
                if (exception.StatusCode == (int)HttpStatusCode.Unauthorized)
                {
                    client.Authentication.Token = null;
                    await CheckAuthorizationAsync();
                    if (!string.IsNullOrEmpty(client.Authentication.Token))
                    {
                        await QueueEpisodeImageDownloadAsync(tvDBEpisodeID, downloadImages, forceRefresh);
                        return;
                    }
                    // suppress 404 and move on
                }
                else if (exception.StatusCode == (int)HttpStatusCode.NotFound) return;
                logger.Error(exception,
                    $"TvDB returned an error code: {exception.StatusCode}\n        {exception.Message}");
            }
            catch (Exception ex)
            {
                logger.Error(ex, $"Error in TVDBHelper.GetEpisodes: {ex}");
            }
        }

        public static void UpdateSeriesInfoAndImages(int seriesID, bool forceRefresh, bool downloadImages)
        {
            try
            {
                // update the series info
                TvDB_Series tvSeries = GetSeriesInfoOnline(seriesID, forceRefresh);
                if (tvSeries == null) return;

                if (downloadImages)
                    DownloadAutomaticImages(seriesID, forceRefresh);

                List<BasicEpisode> episodeItems = GetEpisodesOnline(seriesID);
                logger.Trace($"Found {episodeItems.Count} Episode nodes");

                List<int> existingEpIds = new List<int>();
                foreach (BasicEpisode item in episodeItems)
                {
                    if (!existingEpIds.Contains(item.Id))
                        existingEpIds.Add(item.Id);

                    string infoString = $"{tvSeries.SeriesName} - Episode {item.AbsoluteNumber?.ToString() ?? "X"}";
                    CommandRequest_TvDBUpdateEpisode epcmd =
                        new CommandRequest_TvDBUpdateEpisode(item.Id, infoString, downloadImages, forceRefresh);
                    epcmd.Save();
                }

                // get all the existing tvdb episodes, to see if any have been deleted
                List<TvDB_Episode> allEps = Repo.TvDB_Episode.GetBySeriesID(seriesID);
                foreach (TvDB_Episode oldEp in allEps)
                    if (!existingEpIds.Contains(oldEp.Id))
                        Repo.TvDB_Episode.Delete(oldEp.TvDB_EpisodeID);
                // Not updating stats as it will happen with the episodes
            }
            catch (Exception ex)
            {
                logger.Error(ex, $"Error in TVDBHelper.GetEpisodes: {ex}");
            }
        }

        public static void LinkAniDBTvDBEpisode(int aniDBID, int tvDBID, int animeID)
        {
            using (var upd = Repo.CrossRef_AniDB_TvDB_Episode.BeginAddOrUpdateWithLock(() => Repo.CrossRef_AniDB_TvDB_Episode.GetByAniDBEpisodeID(aniDBID)))
            {
                upd.Entity.AnimeID = animeID;
                upd.Entity.AniDBEpisodeID = aniDBID;
                upd.Entity.TvDBEpisodeID = tvDBID;
                upd.Commit();

            }
            SVR_AniDB_Anime.UpdateStatsByAnimeID(animeID);

            foreach (SVR_AnimeEpisode ep in Repo.AnimeEpisode.GetByAniDBEpisodeID(aniDBID))
                Repo.AnimeEpisode.BeginUpdate(ep).Commit();

            logger.Trace($"Changed tvdb episode association: {aniDBID}");
        }

        // Removes all TVDB information from a series, bringing it back to a blank state.
        public static void RemoveLinkAniDBTvDB(int animeID, EpisodeType aniEpType, int aniEpNumber, int tvDBID,
            int tvSeasonNumber, int tvEpNumber)
        {
            CrossRef_AniDB_TvDBV2 xref = Repo.CrossRef_AniDB_TvDBV2.GetByTvDBID(tvDBID, tvSeasonNumber,
                tvEpNumber, animeID,
                (int)aniEpType,
                aniEpNumber);
            if (xref == null) return;

            Repo.CrossRef_AniDB_TvDBV2.Delete(xref.CrossRef_AniDB_TvDBV2ID);

            SVR_AniDB_Anime.UpdateStatsByAnimeID(animeID);

            CommandRequest_WebCacheDeleteXRefAniDBTvDB req = new CommandRequest_WebCacheDeleteXRefAniDBTvDB(animeID,
                (int)aniEpType, aniEpNumber,
                tvDBID, tvSeasonNumber, tvEpNumber);
            req.Save();
        }

        public static void ScanForMatches()
        {
            IReadOnlyList<SVR_AnimeSeries> allSeries = Repo.AnimeSeries.GetAll();

            IReadOnlyList<CrossRef_AniDB_TvDBV2> allCrossRefs = Repo.CrossRef_AniDB_TvDBV2.GetAll();
            List<int> alreadyLinked = allCrossRefs.Select(xref => xref.AnimeID).ToList();

            foreach (SVR_AnimeSeries ser in allSeries)
            {
                if (alreadyLinked.Contains(ser.AniDB_ID)) continue;

                SVR_AniDB_Anime anime = ser.GetAnime();

                if (anime != null)
                {
                    if (!anime.GetSearchOnTvDB()) continue; // Don't log if it isn't supposed to be there
                    logger.Trace($"Found anime without tvDB association: {anime.MainTitle}");
                    if (anime.IsTvDBLinkDisabled())
                    {
                        logger.Trace($"Skipping scan tvDB link because it is disabled: {anime.MainTitle}");
                        continue;
                    }
                }

                CommandRequest_TvDBSearchAnime cmd = new CommandRequest_TvDBSearchAnime(ser.AniDB_ID, false);
                cmd.Save();
            }
        }

        public static void UpdateAllInfo(bool force)
        {
            IReadOnlyList<CrossRef_AniDB_TvDBV2> allCrossRefs = Repo.CrossRef_AniDB_TvDBV2.GetAll();
            foreach (CrossRef_AniDB_TvDBV2 xref in allCrossRefs)
            {
                CommandRequest_TvDBUpdateSeries cmd =
                    new CommandRequest_TvDBUpdateSeries(xref.TvDBID, force);
                cmd.Save();
            }
        }

        public static List<int> GetUpdatedSeriesList(string serverTime)
        {
            return Task.Run(async () => await GetUpdatedSeriesListAsync(serverTime)).Result;
        }

        public static async Task<List<int>> GetUpdatedSeriesListAsync(string serverTime)
        {
            List<int> seriesList = new List<int>();
            try
            {
                // Unix timestamp is seconds past epoch
                DateTime lastUpdateTime = new DateTime(1970, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc);
                lastUpdateTime = lastUpdateTime.AddSeconds(long.Parse(serverTime)).ToLocalTime();
                TvDBRateLimiter.Instance.EnsureRate();
                var response = await client.Updates.GetAsync(lastUpdateTime);

                Update[] updates = response?.Data;
                if (updates == null) return seriesList;

                seriesList.AddRange(updates.Where(item => item != null).Select(item => item.Id));

                return seriesList;
            }
            catch (TvDbServerException exception)
            {
                if (exception.StatusCode == (int)HttpStatusCode.Unauthorized)
                {
                    client.Authentication.Token = null;
                    await CheckAuthorizationAsync();
                    if (!string.IsNullOrEmpty(client.Authentication.Token))
                        return await GetUpdatedSeriesListAsync(serverTime);
                    // suppress 404 and move on
                }
                else if (exception.StatusCode == (int)HttpStatusCode.NotFound) return seriesList;
                logger.Error(exception,
                    $"TvDB returned an error code: {exception.StatusCode}\n        {exception.Message}");
            }
            catch (Exception ex)
            {
                logger.Error(ex, $"Error in GetUpdatedSeriesList: {ex}");
            }
            return seriesList;
        }

        // ReSharper disable once RedundantAssignment
        public static string IncrementalTvDBUpdate(ref List<int> tvDBIDs, ref bool tvDBOnline)
        {
            // check if we have record of doing an automated update for the TvDB previously
            // if we have then we have kept a record of the server time and can do a delta update
            // otherwise we need to do a full update and keep a record of the time

            List<int> allTvDBIDs = new List<int>();
            tvDBIDs = tvDBIDs ?? new List<int>();
            tvDBOnline = true;

            try
            {
                // record the tvdb server time when we started
                // we record the time now instead of after we finish, to include any possible misses
                string currentTvDBServerTime = CurrentServerTime;
                if (currentTvDBServerTime.Length == 0)
                {
                    tvDBOnline = false;
                    return currentTvDBServerTime;
                }

                foreach (SVR_AnimeSeries ser in Repo.AnimeSeries.GetAll())
                {
                    List<CrossRef_AniDB_TvDBV2> xrefs = ser.GetCrossRefTvDBV2();
                    if (xrefs == null) continue;

                    foreach (CrossRef_AniDB_TvDBV2 xref in xrefs)
                        if (!allTvDBIDs.Contains(xref.TvDBID)) allTvDBIDs.Add(xref.TvDBID);
                }

                // get the time we last did a TvDB update
                // if this is the first time it will be null
                // update the anidb info ever 24 hours

                ScheduledUpdate sched = Repo.ScheduledUpdate.GetByUpdateType((int)ScheduledUpdateType.TvDBInfo);

                string lastServerTime = string.Empty;
                if (sched != null)
                {
                    TimeSpan ts = DateTime.Now - sched.LastUpdate;
                    logger.Trace($"Last tvdb info update was {ts.TotalHours} hours ago");
                    if (!string.IsNullOrEmpty(sched.UpdateDetails))
                        lastServerTime = sched.UpdateDetails;

                    // the UpdateDetails field for this type will actually contain the last server time from
                    // TheTvDB that a full update was performed
                }


                // get a list of updates from TvDB since that time
                if (lastServerTime.Length > 0)
                {
                    List<int> seriesList = GetUpdatedSeriesList(lastServerTime);
                    logger.Trace($"{seriesList.Count} series have been updated since last download");
                    logger.Trace($"{allTvDBIDs.Count} TvDB series locally");

                    foreach (int id in seriesList)
                        if (allTvDBIDs.Contains(id)) tvDBIDs.Add(id);
                    logger.Trace($"{tvDBIDs.Count} TvDB local series have been updated since last download");
                }
                else
                {
                    // use the full list
                    tvDBIDs = allTvDBIDs;
                }

                return currentTvDBServerTime;
            }
            catch (Exception ex)
            {
                logger.Error(ex, $"IncrementalTvDBUpdate: {ex}");
                return string.Empty;
            }
        }
    }
}