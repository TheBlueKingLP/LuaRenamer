﻿using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Microsoft.Extensions.Logging;
using NLua;
using NLua.Exceptions;
using Shoko.Plugin.Abstractions;
using Shoko.Plugin.Abstractions.Attributes;
using Shoko.Plugin.Abstractions.DataModels;

namespace LuaRenamer
{
    [Renamer(LuaRenamer.RenamerId)]
    // ReSharper disable once ClassNeverInstantiated.Global
    public class LuaRenamer : IRenamer
    {
        private readonly ILogger<LuaRenamer>? _logger;
        private const string RenamerId = nameof(LuaRenamer);
        private static readonly Type? Repofact = Utils.GetTypeFromAssemblies("Shoko.Server.Repositories.RepoFactory");
        private static readonly dynamic? VideoLocalRepo = Repofact?.GetProperty("VideoLocal")?.GetValue(null);
        private static readonly dynamic? ImportFolderRepo = Repofact?.GetProperty("ImportFolder")?.GetValue(null);
        private static readonly NLuaSingleton Lua = new();
        private static readonly MethodInfo LogMethod = typeof(LuaRenamer).GetMethod(nameof(Log), BindingFlags.Instance | BindingFlags.NonPublic)!;
        private static readonly MethodInfo LogWarnMethod = typeof(LuaRenamer).GetMethod(nameof(LogWarn), BindingFlags.Instance | BindingFlags.NonPublic)!;
        private static readonly MethodInfo LogErrorMethod = typeof(LuaRenamer).GetMethod(nameof(LogError), BindingFlags.Instance | BindingFlags.NonPublic)!;
        private static readonly MethodInfo EpNumsMethod =
            typeof(LuaRenamer).GetMethod(nameof(GetEpisodesString), BindingFlags.NonPublic | BindingFlags.Instance)!;

        private static string _scriptCache = string.Empty;
        private static readonly Dictionary<string, (DateTime setTIme, string filename, IImportFolder destination, string subfolder)> ResultCache = new();

        public MoveEventArgs Args = null!;

        public LuaRenamer(ILogger<LuaRenamer> logger)
        {
            _logger = logger;
        }

        private void Log(string message) => _logger.LogInformation(message);
        private void LogWarn(string message) => _logger.LogWarning(message);
        private void LogError(string message) => _logger.LogError(message);

        private (string filename, IImportFolder destination, string subfolder)? CheckCache()
        {
            var crc = Args.FileInfo.Hashes.CRC;
            if (Args.Script.Script != _scriptCache)
            {
                _scriptCache = Args.Script.Script;
                ResultCache.Clear();
                return null;
            }
            if (!ResultCache.TryGetValue(crc, out var res))
                return null;
            if (DateTime.UtcNow < res.setTIme + TimeSpan.FromSeconds(2))
                return (res.filename, res.destination, res.subfolder);
            ResultCache.Remove(crc);
            return null;
        }

        public string? GetFilename(RenameEventArgs args)
        {
            Args = new MoveEventArgs
            {
                Cancel = args.Cancel,
                AvailableFolders = new List<IImportFolder>(),
                FileInfo = args.FileInfo,
                AnimeInfo = args.AnimeInfo,
                GroupInfo = args.GroupInfo,
                EpisodeInfo = args.EpisodeInfo,
                Script = args.Script
            };
            CheckBadArgs();
            var result = GetInfo();
            return result?.filename;
        }

        public (IImportFolder? destination, string? subfolder) GetDestination(MoveEventArgs args)
        {
            Args = args;
            CheckBadArgs();
            var result = GetInfo();
            return (result?.destination, result?.subfolder);
        }

        public (string filename, IImportFolder destination, string subfolder)? GetInfo()
        {
            if (CheckCache() is { } cacheHit)
                return cacheHit;
            Args.AvailableFolders = ((IEnumerable?)ImportFolderRepo?.GetAll())?.Cast<IImportFolder>().ToList() ?? Args.AvailableFolders;
            var (retVal, luaEnv) = RunSandboxed(Args.Script.Script);
            if (retVal.Length == 2 && retVal[0] == null && retVal[1] is string errStr)
                throw new ArgumentException(errStr);
            var env = Lua.Inst.GetTableDict(luaEnv);
            var replaceIllegalChars = (bool)env[LuaEnv.replace_illegal_chars];
            var removeIllegalChars = (bool)env[LuaEnv.remove_illegal_chars];
            var useExistingAnimeLocation = (bool)env[LuaEnv.use_existing_anime_location];
            env.TryGetValue(LuaEnv.filename, out var luaFilename);
            env.TryGetValue(LuaEnv.destination, out var luaDestination);
            env.TryGetValue(LuaEnv.subfolder, out var luaSubfolder);
            var filename = luaFilename is string f
                ? (removeIllegalChars ? f : f.ReplacePathSegmentChars(replaceIllegalChars)).CleanPathSegment(true) + Path.GetExtension(Args.FileInfo.Filename)
                : Args.FileInfo.Filename;
            var (destination, subfolder) = (useExistingAnimeLocation ? GetExistingAnimeLocation() : null) ??
                                           (GetNewDestination(luaDestination), GetNewSubfolder(luaSubfolder, replaceIllegalChars, removeIllegalChars));
            if (filename is null || string.IsNullOrWhiteSpace(subfolder)) return null;
            ResultCache.Add(Args.FileInfo.Hashes.CRC, (DateTime.UtcNow, filename, destination, subfolder));
            return (filename, destination, subfolder);
        }

        private string GetNewSubfolder(object? subfolder, bool replaceIllegalChars, bool removeIllegalChars)
        {
            List<string> newSubFolderSplit;
            switch (subfolder)
            {
                case null:
                    newSubFolderSplit = new List<string> { Args.AnimeInfo.First().PreferredTitle };
                    break;
                case LuaTable subfolderTable:
                {
                    var subfolderDict = new SortedDictionary<long, string>();
                    foreach (KeyValuePair<object, object> kvp in subfolderTable)
                    {
                        if (kvp.Key is not long key)
                            continue;
                        if (kvp.Value is not string val)
                            throw new LuaScriptException("subfolder array must only contain strings", string.Empty);
                        subfolderDict[key] = val;
                    }
                    newSubFolderSplit = subfolderDict.Values.ToList();
                    break;
                }
                default:
                    throw new LuaScriptException("subfolder must be an array of path segments or nil", string.Empty);
            }
            newSubFolderSplit = newSubFolderSplit
                .Select(f => (removeIllegalChars ? f : f.ReplacePathSegmentChars(replaceIllegalChars)).CleanPathSegment(false)).ToList();
            var newSubfolder = Path.Combine(newSubFolderSplit.ToArray()).NormPath();
            return newSubfolder;
        }

        private IImportFolder GetNewDestination(object? destination)
        {
            IImportFolder? destfolder;
            switch (destination)
            {
                case null:
                    destfolder = Args.AvailableFolders
                        // Order by common prefix (stronger version of same drive)
                        .OrderByDescending(f => string.Concat(Args.FileInfo.FilePath.NormPath()
                            .TakeWhile((ch, i) => i < f.Location.NormPath().Length
                                                  && char.ToUpperInvariant(f.Location.NormPath()[i]) == char.ToUpperInvariant(ch))).Length)
                        .FirstOrDefault(f => f.DropFolderType.HasFlag(DropFolderType.Destination));
                    if (destfolder is null)
                        throw new ArgumentException("could not find an available destination import folder");
                    break;
                case string name:
                    destfolder = Args.AvailableFolders.FirstOrDefault(f => string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase));
                    if (destfolder is null)
                        throw new ArgumentException($"could not find destination folder by name: {name}");
                    break;
                case LuaTable destTable:
                    if ((string)destTable[LuaEnv.importfolder._classid] == "55138454-4A0D-45EB-8CCE-1CCF00220165")
                        destfolder = Args.AvailableFolders[Convert.ToInt32(destTable[LuaEnv.importfolder._index])];
                    else
                        throw new ArgumentException($"destination table was not the correct class, assign a table from {LuaEnv.importfolders}");
                    break;
                default:
                    throw new LuaScriptException("destination must be an import folder name, an import folder or nil", string.Empty);
            }
            if (!destfolder.DropFolderType.HasFlag(DropFolderType.Destination))
                throw new ArgumentException($"selected import folder \"{destfolder.Location}\" is not a destination folder, check import folder type");
            return destfolder;
        }

        private (IImportFolder destination, string subfolder)? GetExistingAnimeLocation()
        {
            if (VideoLocalRepo is null || ImportFolderRepo is null) return null;
            IImportFolder? oldFld = null;
            var lastFileLocation = ((IEnumerable<dynamic>)VideoLocalRepo.GetByAniDBAnimeID(Args.AnimeInfo.First().AnimeID))
                .Where(vl => !string.Equals(vl.CRC32, Args.FileInfo.Hashes.CRC, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(vl => vl.DateTimeUpdated)
                .Select(vl => vl.GetBestVideoLocalPlace())
                .FirstOrDefault(vlp => (oldFld = (IImportFolder)ImportFolderRepo.GetByID(vlp.ImportFolderID)) is not null &&
                                       (oldFld.DropFolderType.HasFlag(DropFolderType.Destination) ||
                                        oldFld.DropFolderType.HasFlag(DropFolderType.Excluded)));
            if (oldFld is null || lastFileLocation is null) return null;
            var subFld = Path.GetDirectoryName((string)lastFileLocation.FilePath);
            if (subFld is null) return null;
            return (oldFld, subFld);
        }

        private void CheckBadArgs()
        {
            if (string.IsNullOrWhiteSpace(Args.Script?.Script))
                throw new ArgumentException("Script is empty or null");
            if (Args.Script.Type != RenamerId)
                throw new ArgumentException($"Script doesn't match {RenamerId}");
            if (Args.AnimeInfo.Count == 0 || Args.EpisodeInfo.Count == 0)
                throw new ArgumentException("No anime and/or episode info");
        }

        private (object[] retVal, LuaTable env) RunSandboxed(string code)
        {
            var env = CreateLuaEnv();
            var luaEnv = Lua.Inst.CreateEnv(Lua.BaseEnvStrings);
            Lua.Inst["env"] = luaEnv;
            var objCache = new Dictionary<object, LuaTable>();
            foreach (var (k, v) in env) Lua.Inst.AddObject(luaEnv, v, k, objCache);
            return (Lua.LuaRunSandboxed.Call(code, luaEnv), luaEnv);
        }

        private Dictionary<string, object?> CreateLuaEnv()
        {
            List<Dictionary<string, object?>> ConvertTitles(IEnumerable<AnimeTitle> titles)
            {
                return titles.Select(t => new Dictionary<string, object?>
                {
                    { LuaEnv.title.name, t.Title },
                    { LuaEnv.title.language, t.Language.ToString() },
                    { LuaEnv.title.languagecode, t.LanguageCode },
                    { LuaEnv.title.type, t.Type.ToString() }
                }).ToList();
            }

            var animes = Args.AnimeInfo.Select(a => new Dictionary<string, object?>
            {
                { LuaEnv.anime.airdate, a.AirDate?.ToTable() },
                { LuaEnv.anime.enddate, a.EndDate?.ToTable() },
                { LuaEnv.anime.rating, a.Rating },
                { LuaEnv.anime.restricted, a.Restricted },
                { LuaEnv.anime.type, a.Type.ToString() },
                { LuaEnv.anime.preferredname, a.PreferredTitle },
                { LuaEnv.anime.id, a.AnimeID },
                { LuaEnv.anime.titles, ConvertTitles(a.Titles) },
                { LuaEnv.anime.getname, Lua.TitleFunc },
                {
                    LuaEnv.anime.episodecounts, new Dictionary<string, int>
                    {
                        { EpisodeType.Episode.ToString(), a.EpisodeCounts.Episodes },
                        { EpisodeType.Special.ToString(), a.EpisodeCounts.Specials },
                        { EpisodeType.Credits.ToString(), a.EpisodeCounts.Credits },
                        { EpisodeType.Trailer.ToString(), a.EpisodeCounts.Trailers },
                        { EpisodeType.Other.ToString(), a.EpisodeCounts.Others },
                        { EpisodeType.Parody.ToString(), a.EpisodeCounts.Parodies }
                    }
                }
            }).ToList();
            var anidb = Args.FileInfo.AniDBFileInfo is null
                ? null
                : new Dictionary<string, object?>
                {
                    { LuaEnv.file.anidb.censored, Args.FileInfo.AniDBFileInfo.Censored },
                    { LuaEnv.file.anidb.source, Args.FileInfo.AniDBFileInfo.Source },
                    { LuaEnv.file.anidb.version, Args.FileInfo.AniDBFileInfo.Version },
                    { LuaEnv.file.anidb.releasedate, Args.FileInfo.AniDBFileInfo.ReleaseDate?.ToTable() },
                    {
                        LuaEnv.file.anidb.releasegroup.N, Args.FileInfo.AniDBFileInfo.ReleaseGroup is null
                                                          || Args.FileInfo.AniDBFileInfo.ReleaseGroup.Name == "raw/unknown"
                            ? null
                            : new Dictionary<string, object>
                            {
                                { LuaEnv.file.anidb.releasegroup.name, Args.FileInfo.AniDBFileInfo.ReleaseGroup.Name },
                                { LuaEnv.file.anidb.releasegroup.shortname, Args.FileInfo.AniDBFileInfo.ReleaseGroup.ShortName }
                            }
                    },
                    { LuaEnv.file.anidb.id, Args.FileInfo.AniDBFileInfo.AniDBFileID },
                    {
                        LuaEnv.file.anidb.media.N, new Dictionary<string, object>
                        {
                            { LuaEnv.file.anidb.media.videocodec, Args.FileInfo.AniDBFileInfo.MediaInfo.VideoCodec },
                            {
                                LuaEnv.file.anidb.media.sublanguages,
                                Args.FileInfo.AniDBFileInfo.MediaInfo.SubLanguages.Select(l => l.ToString()).ToList()
                            },
                            {
                                LuaEnv.file.anidb.media.dublanguages,
                                Args.FileInfo.AniDBFileInfo.MediaInfo.AudioLanguages.Select(l => l.ToString()).ToList()
                            }
                        }
                    }
                };
            var mediainfo = Args.FileInfo.MediaInfo is null
                ? null
                : new Dictionary<string, object>
                {
                    { LuaEnv.file.media.chaptered, Args.FileInfo.MediaInfo.Chaptered },
                    {
                        LuaEnv.file.media.video.N, new Dictionary<string, object>
                        {
                            { LuaEnv.file.media.video.height, Args.FileInfo.MediaInfo.Video.Height },
                            { LuaEnv.file.media.video.width, Args.FileInfo.MediaInfo.Video.Width },
                            { LuaEnv.file.media.video.codec, Args.FileInfo.MediaInfo.Video.SimplifiedCodec },
                            { LuaEnv.file.media.video.res, Args.FileInfo.MediaInfo.Video.StandardizedResolution },
                            { LuaEnv.file.media.video.bitrate, Args.FileInfo.MediaInfo.Video.BitRate },
                            { LuaEnv.file.media.video.bitdepth, Args.FileInfo.MediaInfo.Video.BitDepth },
                            { LuaEnv.file.media.video.framerate, Args.FileInfo.MediaInfo.Video.FrameRate }
                        }
                    },
                    { LuaEnv.file.media.duration, Args.FileInfo.MediaInfo.General.Duration },
                    { LuaEnv.file.media.bitrate, Args.FileInfo.MediaInfo.General.OverallBitRate },
                    {
                        LuaEnv.file.media.sublanguages, Args.FileInfo.MediaInfo.Subs.Select(s =>
                            (Utils.ParseEnum<TitleLanguage>(s.LanguageName, false) is var l && l is TitleLanguage.Unknown
                                ? Utils.ParseEnum<TitleLanguage>(s.Title, false)
                                : l).ToString()).ToList()
                    },
                    {
                        LuaEnv.file.media.audio.N, Args.FileInfo.MediaInfo.Audio.Select(a => new Dictionary<string, object>
                        {
                            { LuaEnv.file.media.audio.compressionmode, a.Compression_Mode },
                            {
                                LuaEnv.file.media.audio.channels,
                                ((string?)((dynamic)a).ChannelLayout)?.Contains("LFE") ?? false ? a.Channels - 1 + 0.1 : a.Channels
                            },
                            { LuaEnv.file.media.audio.samplingrate, a.SamplingRate },
                            { LuaEnv.file.media.audio.codec, ((dynamic)a).Format },
                            {
                                LuaEnv.file.media.audio.language, (Utils.ParseEnum<TitleLanguage>(a.LanguageName, false) is var l && l is TitleLanguage.Unknown
                                    ? Utils.ParseEnum<TitleLanguage>(a.Title, false)
                                    : l).ToString()
                            },
                            { LuaEnv.file.media.audio.title, a.Title }
                        }).ToList()
                    }
                };
            var importfolders = Args.AvailableFolders.Select((f, i) => new Dictionary<string, object>
            {
                { LuaEnv.importfolder.name, f.Name },
                { LuaEnv.importfolder.location, f.Location },
                { LuaEnv.importfolder.type, f.DropFolderType.ToString() },
                { LuaEnv.importfolder._classid, "55138454-4A0D-45EB-8CCE-1CCF00220165" },
                { LuaEnv.importfolder._index, i }
            }).ToList();
            var file = new Dictionary<string, object?>
            {
                { LuaEnv.file.name, Args.FileInfo.Filename },
                { LuaEnv.file.path, Args.FileInfo.FilePath },
                { LuaEnv.file.size, Args.FileInfo.FileSize },
                {
                    LuaEnv.file.hashes.N, new Dictionary<string, object>
                    {
                        { LuaEnv.file.hashes.crc, Args.FileInfo.Hashes.CRC },
                        { LuaEnv.file.hashes.md5, Args.FileInfo.Hashes.MD5 },
                        { LuaEnv.file.hashes.ed2k, Args.FileInfo.Hashes.ED2K },
                        { LuaEnv.file.hashes.sha1, Args.FileInfo.Hashes.SHA1 },
                    }
                },
                { LuaEnv.file.anidb.N, anidb },
                { LuaEnv.file.media.N, mediainfo },
                {
                    LuaEnv.file.importfolder,
                    importfolders.First(i => Args.FileInfo.FilePath.NormPath().StartsWith(((string)i[LuaEnv.importfolder.location]).NormPath()))
                }
            };
            var episodes = Args.EpisodeInfo.Select(e => new Dictionary<string, object?>
            {
                { LuaEnv.episode.duration, e.Duration },
                { LuaEnv.episode.number, e.Number },
                { LuaEnv.episode.type, e.Type.ToString() },
                { LuaEnv.episode.airdate, e.AirDate?.ToTable() },
                { LuaEnv.episode.animeid, e.AnimeID },
                { LuaEnv.episode.id, e.EpisodeID },
                { LuaEnv.episode.titles, ConvertTitles(e.Titles) },
                { LuaEnv.episode.getname, Lua.TitleFunc },
                { LuaEnv.episode.prefix, Utils.EpPrefix[e.Type] }
            }).ToList();
            var groups = Args.GroupInfo.Select(g => new Dictionary<string, object?>
            {
                { LuaEnv.group.name, g.Name },
                // Just give Ids, subject to change if there is ever a reason to use more.
                { LuaEnv.group.mainseriesid, g.MainSeries?.AnimeID },
                { LuaEnv.group.seriesids, g.Series.Select(s => s.AnimeID).ToList() }
            }).ToList();
            return new Dictionary<string, object?>
            {
                { LuaEnv.filename, null },
                { LuaEnv.destination, null },
                { LuaEnv.subfolder, null },
                { LuaEnv.replace_illegal_chars, false },
                { LuaEnv.remove_illegal_chars, false },
                { LuaEnv.use_existing_anime_location, false },
                { LuaEnv.animes, animes },
                { LuaEnv.anime.N, animes.First() },
                { LuaEnv.file.N, file },
                { LuaEnv.episodes, episodes },
                {
                    LuaEnv.episode.N, episodes.Where(e => (int)e[LuaEnv.episode.animeid]! == (int)animes.First()[LuaEnv.anime.id]!)
                        .OrderBy(e => (string)e[LuaEnv.episode.type]! == EpisodeType.Other.ToString()
                            ? int.MinValue
                            : (int)Enum.Parse<EpisodeType>((string)e[LuaEnv.episode.type]!))
                        .ThenBy(e => (int)e[LuaEnv.episode.number]!)
                        .First()
                },
                { LuaEnv.importfolders, importfolders },
                { LuaEnv.groups, groups },
                { LuaEnv.group.N, groups.FirstOrDefault() },
                { LuaEnv.episode_numbers, Lua.Inst.RegisterFunction(LuaEnv.episode_numbers, this, EpNumsMethod) },
                { LuaEnv.log, Lua.Inst.RegisterFunction(LuaEnv.log, this, LogMethod) },
                { LuaEnv.logwarn, Lua.Inst.RegisterFunction(LuaEnv.log, this, LogWarnMethod) },
                { LuaEnv.logerror, Lua.Inst.RegisterFunction(LuaEnv.log, this, LogErrorMethod) }
            };
        }

        // ReSharper disable once UnusedMember.Local
        private string GetEpisodesString(int pad) => Args.EpisodeInfo.Where(e => e.AnimeID == Args.AnimeInfo.First().AnimeID)
            .OrderBy(e => e.Number)
            .GroupBy(e => e.Type)
            .OrderBy(g => g.Key)
            .Aggregate("", (s, g) =>
                s + " " + g.Aggregate(
                    (InRun: false, Seq: -1, Str: ""),
                    (tup, ep) => ep.Number == tup.Seq + 1
                        ? (true, ep.Number, tup.Str)
                        : tup.InRun
                            ? (false, ep.Number,
                                $"{tup.Str}-{tup.Seq.ToString().PadLeft(pad, '0')} {Utils.EpPrefix[g.Key]}{ep.Number.ToString().PadLeft(pad, '0')}")
                            : (false, ep.Number, $"{tup.Str} {Utils.EpPrefix[g.Key]}{ep.Number.ToString().PadLeft(pad, '0')}"),
                    tup => tup.InRun ? $"{tup.Str}-{tup.Seq.ToString().PadLeft(pad, '0')}" : tup.Str
                ).Trim()
            ).Trim();
    }
}
