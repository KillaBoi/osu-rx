﻿using osu_rx.Helpers;
using OsuParsers.Beatmaps;
using OsuParsers.Database;
using OsuParsers.Decoders;
using OsuParsers.Enums;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;

namespace osu_rx.osu
{
    public class OsuManager
    {
        [DllImport("user32.dll")]
        private static extern bool GetCursorPos(out Point point);

        private IntPtr audioTimeAddress;
        private IntPtr isAudioPlayingAddress;
        private IntPtr gameStateAddress;
        private IntPtr modsAddress;
        private IntPtr replayModeAddress;
        private IntPtr cursorPositionXAddress;
        private IntPtr cursorPositionYAddress;

        private object interProcessOsu;
        private MethodInfo bulkClientDataMethod;

        private FileSystemWatcher fileSystemWatcher;
        private OsuDatabase osuDatabase;
        private string databaseHash;

        public OsuProcess OsuProcess { get; private set; }

        public OsuWindow OsuWindow { get; private set; }

        public bool UsingIPCFallback { get; private set; }

        public int CurrentTime
        {
            get
            {
                if (!UsingIPCFallback)
                    return OsuProcess.ReadInt32(audioTimeAddress);

                var data = bulkClientDataMethod.Invoke(interProcessOsu, null);
                return (int)data.GetType().GetField("MenuTime").GetValue(data);
            }
        }

        public Mods CurrentMods
        {
            get
            {
                if (!UsingIPCFallback)
                    return (Mods)OsuProcess.ReadInt32(modsAddress);

                return Mods.None;
            }
        }

        public bool IsPaused
        {
            get
            {
                if (!UsingIPCFallback)
                    return !OsuProcess.ReadBool(isAudioPlayingAddress);

                var data = bulkClientDataMethod.Invoke(interProcessOsu, null);
                return !(bool)data.GetType().GetField("AudioPlaying").GetValue(data);
            }
        }

        public bool IsPlayerLoaded
        {
            get
            {
                if (!UsingIPCFallback)
                {
                    OsuProcess.Process.Refresh();
                    return OsuProcess.Process.MainWindowTitle.Contains('-');
                }

                //TODO: remove in next release
                var data = bulkClientDataMethod.Invoke(interProcessOsu, null);
                return (bool)data.GetType().GetField("LPlayerLoaded").GetValue(data);
            }
        }
        
        public bool IsInReplayMode
        {
            get
            {
                if (!UsingIPCFallback)
                    return OsuProcess.ReadBool(replayModeAddress);

                var data = bulkClientDataMethod.Invoke(interProcessOsu, null);
                return (bool)data.GetType().GetField("LReplayMode").GetValue(data);
            }
        }

        public string BeatmapChecksum
        {
            get
            {
                var data = bulkClientDataMethod.Invoke(interProcessOsu, null);
                return (string)data.GetType().GetField("BeatmapChecksum").GetValue(data);
            }
        }

        private List<(string MD5, Beatmap Beatmap)> beatmapCache = new List<(string, Beatmap)>();
        private List<(string MD5, string PathToBeatmap)> newlyImported = new List<(string, string)>();
        public Beatmap CurrentBeatmap
        {
            get
            {
                updateDatabase();

                (string MD5, Beatmap Beatmap) beatmap = (string.Empty, null);

                if (beatmapCache.Find(b => b.MD5 == BeatmapChecksum) is var cachedBeatmap && cachedBeatmap != default)
                    beatmap = cachedBeatmap;
                else if (osuDatabase.Beatmaps.Find(b => b.MD5Hash == BeatmapChecksum) is var dbBeatmap && dbBeatmap != default)
                    beatmap = (dbBeatmap.MD5Hash, BeatmapDecoder.Decode($@"{SongsPath}\{dbBeatmap.FolderName}\{dbBeatmap.FileName}"));
                else if (newlyImported.Find(b => b.MD5 == BeatmapChecksum) is var importedBeatmap && importedBeatmap != default)
                    beatmap = (importedBeatmap.MD5, BeatmapDecoder.Decode(importedBeatmap.PathToBeatmap));

                if (beatmap != (string.Empty, null) && !beatmapCache.Contains(beatmap))
                    beatmapCache.Add(beatmap);

                return beatmap.Beatmap;
            }
        }

        public OsuStates CurrentState
        {
            get
            {
                if (!UsingIPCFallback)
                    return (OsuStates)OsuProcess.ReadInt32(gameStateAddress);

                var data = bulkClientDataMethod.Invoke(interProcessOsu, null);
                return (OsuStates)data.GetType().GetField("Mode").GetValue(data);
            }
        }

        public bool CanPlay
        {
            get => CurrentState == OsuStates.Play && IsPlayerLoaded && !IsInReplayMode;
        }

        public Vector2 CursorPosition //relative to playfield
        {
            get
            {
                if (!UsingIPCFallback)
                {
                    float x = OsuProcess.ReadFloat(cursorPositionXAddress);
                    float y = OsuProcess.ReadFloat(cursorPositionYAddress);

                    return new Vector2(x, y) - OsuWindow.PlayfieldPosition;
                }

                GetCursorPos(out var pos);
                return pos.ToVector2() - (OsuWindow.WindowPosition + OsuWindow.PlayfieldPosition);
            }
        }

        public string PathToOsu { get; private set; }

        public string SongsPath { get; private set; }

        public int HitWindow300(double od) => (int)DifficultyRange(od, 80, 50, 20);
        public int HitWindow100(double od) => (int)DifficultyRange(od, 140, 100, 60);
        public int HitWindow50(double od) => (int)DifficultyRange(od, 200, 150, 100);

        public double AdjustDifficulty(double difficulty) => (ApplyModsToDifficulty(difficulty, 1.3) - 5) / 5;

        public double ApplyModsToDifficulty(double difficulty, double hardrockFactor)
        {
            if (CurrentMods.HasFlag(Mods.Easy))
                difficulty = Math.Max(0, difficulty / 2);
            if (CurrentMods.HasFlag(Mods.HardRock))
                difficulty = Math.Min(10, difficulty * hardrockFactor);

            return difficulty;
        }

        public double DifficultyRange(double difficulty, double min, double mid, double max)
        {
            difficulty = ApplyModsToDifficulty(difficulty, 1.4);

            if (difficulty > 5)
                return mid + (max - mid) * (difficulty - 5) / 5;
            if (difficulty < 5)
                return mid - (mid - min) * (5 - difficulty) / 5;
            return mid;
        }

        public bool Initialize()
        {
            Console.WriteLine("Initializing...");

            var osuProcess = Process.GetProcessesByName("osu!").FirstOrDefault();

            if (osuProcess == default)
            {
                Console.WriteLine("\nosu! process not found! Please launch osu! first!");
                return false;
            }

            osuProcess.EnableRaisingEvents = true;
            osuProcess.Exited += (o, e) => Environment.Exit(0);
            OsuProcess = new OsuProcess(osuProcess);

            OsuWindow = new OsuWindow(osuProcess.MainWindowHandle);

            PathToOsu = Path.GetDirectoryName(OsuProcess.Process.MainModule.FileName);
            parseConfig();

            scanMemory();
            connectToIPC();

            initializeBeatmapWatcher();

            return true;
        }

        private void parseConfig()
        {
            Console.WriteLine("\nParsing osu! config...");

            string pathToConfig = $@"{PathToOsu}\osu!.{Environment.UserName}.cfg";

            if (File.Exists(pathToConfig))
            {
                foreach (string line in File.ReadAllLines(pathToConfig))
                {
                    if (line.StartsWith("BeatmapDirectory"))
                    {
                        string path = line.Split('=')[1].Trim();
                        if (!path.Contains(":\\"))
                            path = Path.Combine(PathToOsu, path);

                        SongsPath = Path.GetFullPath(path);
                    }
                }
            }
            else
                SongsPath = $@"{PathToOsu}\Songs";
        }

        private void scanMemory()
        {
            try
            {
                Console.Write("\nScanning for memory addresses.");
                audioTimeAddress = (IntPtr)OsuProcess.ReadInt32(OsuProcess.FindPattern(Constants.AudioTimePattern) + Constants.AudioTimeOffset);
                isAudioPlayingAddress = audioTimeAddress + Constants.IsAudioPlayingOffset;

                Console.Write('.');
                gameStateAddress = (IntPtr)OsuProcess.ReadInt32(OsuProcess.FindPattern(Constants.GameStatePattern) + Constants.GameStateOffset);

                Console.Write('.');
                modsAddress = (IntPtr)OsuProcess.ReadInt32(OsuProcess.FindPattern(Constants.ModsPattern) + Constants.ModsOffset);

                Console.Write('.');
                replayModeAddress = (IntPtr)OsuProcess.ReadInt32(OsuProcess.FindPattern(Constants.ReplayModePattern) + Constants.ReplayModeOffset);

                Console.WriteLine('.');
                cursorPositionXAddress = OsuProcess.FindPattern(Constants.CursorPositionXPattern) + Constants.CursorPositionXOffset;
                cursorPositionYAddress = cursorPositionXAddress + Constants.CursorPositionYOffset;

            }
            catch (Exception ex)
            {
                Console.WriteLine($"\nAn error occured (please report this): {ex.ToString()}");
                Thread.Sleep(3000);
            }

            if (audioTimeAddress == IntPtr.Zero || isAudioPlayingAddress == IntPtr.Zero
                || gameStateAddress == IntPtr.Zero || modsAddress == IntPtr.Zero || replayModeAddress == IntPtr.Zero
                || cursorPositionXAddress == IntPtr.Zero || cursorPositionYAddress == IntPtr.Zero)
            {
                Console.WriteLine("\nScanning failed! Using IPC fallback...");
                UsingIPCFallback = true;
            }
        }

        private void connectToIPC()
        {
            Console.WriteLine("\nConnecting to IPC...");

            string assemblyPath = OsuProcess.Process.MainModule.FileName;

            var assembly = Assembly.LoadFrom(assemblyPath);
            var interProcessOsuType = assembly.ExportedTypes.First(a => a.FullName == "osu.Helpers.InterProcessOsu");

            AppDomain.CurrentDomain.AssemblyResolve += (sender, eventArgs) => eventArgs.Name.Contains("osu!") ? Assembly.LoadFrom(assemblyPath) : null;

            interProcessOsu = Activator.GetObject(interProcessOsuType, "ipc://osu!/loader");
            bulkClientDataMethod = interProcessOsuType.GetMethod("GetBulkClientData");
        }

        private void initializeBeatmapWatcher()
        {
            Console.WriteLine("\nLooking for beatmaps...");

            updateDatabase();

            fileSystemWatcher = new FileSystemWatcher(SongsPath);
            fileSystemWatcher.Created += (object sender, FileSystemEventArgs e) => onNewBeatmapImport(e.FullPath);
            fileSystemWatcher.Changed += (object sender, FileSystemEventArgs e) => onNewBeatmapImport(e.FullPath);
            fileSystemWatcher.EnableRaisingEvents = true;
            fileSystemWatcher.IncludeSubdirectories = true;

            var lastModified = osuDatabase.Beatmaps.Max(b => b.LastModifiedTime);
            foreach (var dir in new DirectoryInfo(SongsPath).EnumerateDirectories().OrderByDescending(d => d.LastWriteTime))
                if (dir.LastWriteTime >= lastModified)
                    dir.EnumerateFiles(".osu").ToList().ForEach(f => onNewBeatmapImport(f.FullName));
        }

        private void updateDatabase()
        {
            string currentDatabaseHash = CryptoHelper.GetMD5String(File.ReadAllBytes($@"{PathToOsu}\osu!.db"));
            if (currentDatabaseHash != databaseHash)
            {
                databaseHash = currentDatabaseHash;
                osuDatabase = DatabaseDecoder.DecodeOsu($@"{PathToOsu}\osu!.db");
            }
        }

        private void onNewBeatmapImport(string path)
        {
            if (path.EndsWith(".osu"))
            {
                try
                {
                    (string MD5, string PathToBeatmap) beatmap = (CryptoHelper.GetMD5String(File.ReadAllBytes(path)), path);
                    if (newlyImported.Exists(b => b.MD5 == beatmap.MD5))
                        newlyImported.RemoveAll(b => b.MD5 == beatmap.MD5);

                    newlyImported.Add(beatmap);
                }
                catch (IOException) //try again if file is already being used
                {
                    Thread.Sleep(500);
                    onNewBeatmapImport(path);
                }
            }
        }
    }
}