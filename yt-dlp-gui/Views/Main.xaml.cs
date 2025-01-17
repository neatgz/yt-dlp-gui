﻿using Libs;
using Libs.Yaml;
using Microsoft.Toolkit.Uwp.Notifications;
using Newtonsoft.Json;
using Swordfish.NET.Collections.Auxiliary;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Forms;
using System.Windows.Media;
using System.Windows.Shell;
using WK.Libraries.SharpClipboardNS;
using yt_dlp_gui.Controls;
using yt_dlp_gui.Models;
using yt_dlp_gui.Wrappers;

namespace yt_dlp_gui.Views {
    public partial class Main :Window {
        private readonly ViewData Data = new();
        private List<DLP> RunningDLP = new();
        public Main() {
            InitializeComponent();
            DataContext = Data;

            //Load Configs
            InitGUIConfig();

            Topmost = Data.AlwaysOnTop;
            if (Data.RememberWindowStatePosition) {
                Top = Data.Top;
                Left = Data.Left;
            }
            if (Data.RememberWindowStateSize) {
                Width = Data.Width;
                Height = Data.Height;
            } else {
                Width = 600 * (Data.Scale / 100d);
                Height = 380 * (Data.Scale / 100d);
            }

            //Configuration Checking
            InitConfiguration();

            //ScanDeps
            ScanDepends();

            //if `Target` Not exist, default app location
            if (!Directory.Exists(Data.TargetPath)) {
                Data.TargetPath = App.AppPath;
            }
            if (string.IsNullOrWhiteSpace(Data.PathTEMP) || !Directory.Exists(GetTempPath)) {
                Data.PathTEMP = "%YTDLPGUI_TARGET%";
            }

            InitClipboard();

            //run update check
            Task.Run(Inits);
        }
        private void ChangeScale(int present) {
            var scaleRatio = present / 100d;
            var grid = Template.FindName("MainGrid", this) as Grid;
            if (grid != null) {
                var scaleTransform = new ScaleTransform(scaleRatio, scaleRatio);
                grid.LayoutTransform = scaleTransform;

                WindowChrome.SetWindowChrome(this, new() {
                    CaptionHeight = 22 * scaleRatio,
                    ResizeBorderThickness = new Thickness(6),
                    CornerRadius = new CornerRadius(0),
                    GlassFrameThickness = new Thickness(1),
                    NonClientFrameEdges = NonClientFrameEdges.Left,
                    UseAeroCaptionButtons = false
                });
                grid.UpdateLayout();
            }
        }
        private string GetEnvPath(string path) {
            Dictionary<string, string> replacements = new() {
                {"%YTDLPGUI_TARGET%", Data.TargetPath},
                {"%YTDLPGUI_LOCALE%", App.AppPath}
            };
            foreach (KeyValuePair<string, string> pair in replacements) {
                string placeholder = pair.Key;
                string replacement = pair.Value;

                // Replace the placeholder with the replacement string
                path = path.Replace(placeholder, replacement);

                // Remove the part to the left of the replacement string
                int index = path.IndexOf(replacement);
                if (index >= 0) {
                    path = path.Substring(index);
                }

                // Remove duplicate directory separators
                path = path.Replace('/', '\\');
                while (path.Contains("\\\\")) {
                    path = path.Replace("\\\\", "\\");
                }
            }
            return Environment.ExpandEnvironmentVariables(path);
        }
        private string GetTempPath {
            get => GetEnvPath(Data.PathTEMP);
        }
        private Regex _frgPat = new Regex("<!--StartFragment-->(.*)<!--EndFragment-->", RegexOptions.Multiline | RegexOptions.Compiled);
        private Regex _matchUrls = new Regex(@"(https?|ftp|file)\://[A-Za-z0-9\.\-]+(/[A-Za-z0-9\?\&\=;\+!'\(\)\*\-\._~%]*)*", RegexOptions.Compiled);
        public void InitClipboard() {
            Data.PropertyChanged += (s, e) => {
                switch (e.PropertyName) {
                    case nameof(Data.ClipboardText):
                        int maxTries = 10;
                        int delayTime = 1000; // milliseconds

                        int numTries = 0;
                        while (numTries < maxTries) {
                            try {
                                var content = System.Windows.Clipboard.GetText(System.Windows.TextDataFormat.Html);
                                if (!string.IsNullOrWhiteSpace(content)) {
                                    content = _frgPat.Match(content).Groups?[1].Value.Trim() ?? "";
                                } else {
                                    content = System.Windows.Clipboard.GetText(System.Windows.TextDataFormat.Text);
                                }
                                var m = _matchUrls.Match(content);
                                if (m.Success) {
                                    var capUrl = m.Value;
                                    if (Util.UrlVaild(capUrl)) {
                                        Data.Url = capUrl;
                                        Analyze_Start();
                                    }
                                }
                                numTries = 0;
                                break;
                            } catch (Exception) {
                                numTries++;
                                Thread.Sleep(delayTime);
                            }
                        }
                        break;
                    case nameof(Data.AlwaysOnTop):
                        Topmost = Data.AlwaysOnTop;
                        break;
                }
            };

            var sc = new SharpClipboard();
            sc.ClipboardChanged += (s, e) => {
                if (!Data.IsMonitor || Data.IsAnalyze || Data.IsDownload) return;
                if (e.ContentType == SharpClipboard.ContentTypes.Text) {
                    var text = System.Windows.Clipboard.GetText(System.Windows.TextDataFormat.Text);
                    Data.ClipboardText = text;
                }
            };
        }
        public void InitGUIConfig() {
            Data.GUIConfig.Load(App.Path(App.Folders.root, App.AppName + ".yaml"));
            Util.PropertyCopy(Data.GUIConfig, Data);
            //Loaded and enabled auto save config
            Data.AutoSaveConfig = true;
        }
        public void InitConfiguration() {
            Data.Configs.Clear();
            Data.Configs.Add(new Config() { name = App.Lang.Main.ConfigurationNone });
            var cp = App.Path(App.Folders.configs);
            var fs = Directory.Exists(cp)
                ? Directory.EnumerateFiles(cp).OrderBy(x => x)
                : Enumerable.Empty<string>();
            fs.ForEach(x => {
                Data.Configs.Add(new Config() {
                    name = Path.GetFileNameWithoutExtension(x),
                    file = x
                });
            });
            Data.selectedConfig = Data.Configs.FirstOrDefault(x => x.file == Data.GUIConfig.ConfigurationFile, Data.Configs.First());
        }
        public void ScanDepends() {
            var isYoutubeDl = @"^youtube-dl\.exe";
            if (!string.IsNullOrWhiteSpace(Data.PathYTDLP) && File.Exists(Data.PathYTDLP)) {
                DLP.Path_DLP = Data.PathYTDLP;
            }
            if (!string.IsNullOrWhiteSpace(Data.PathAria2) && File.Exists(Data.PathAria2)) {
                DLP.Path_Aria2 = Data.PathAria2;
            }
            if (!string.IsNullOrWhiteSpace(Data.PathFFMPEG) && File.Exists(Data.PathFFMPEG)) {
                FFMPEG.Path_FFMPEG = Data.PathFFMPEG;
            }
            if (string.IsNullOrWhiteSpace(DLP.Path_DLP) ||
                string.IsNullOrWhiteSpace(DLP.Path_Aria2) ||
                string.IsNullOrWhiteSpace(FFMPEG.Path_FFMPEG)) {
                var deps = Directory.EnumerateFiles(App.AppPath, "*.exe", SearchOption.AllDirectories).ToList();
                deps = deps.Where(x => Path.GetFileName(App.AppExe) != Path.GetFileName(x)).ToList();
                var dep_ytdlp = deps.FirstOrDefault(x => Regex.IsMatch(Path.GetFileName(x), @"^(yt-dlp(_min|_x86|_x64)?|ytdl-patched.*?)\.exe"), "");
                var dep_ffmpeg = deps.FirstOrDefault(x => Regex.IsMatch(Path.GetFileName(x), @"^ffmpeg"), "");
                var dep_aria2 = deps.FirstOrDefault(x => Regex.IsMatch(Path.GetFileName(x), @"^aria2"), "");
                var dep_youtubedl = deps.FirstOrDefault(x => Regex.IsMatch(Path.GetFileName(x), isYoutubeDl), "");
                if (string.IsNullOrWhiteSpace(DLP.Path_DLP)) {
                    if (!string.IsNullOrWhiteSpace(dep_ytdlp)) {
                        Data.PathYTDLP = DLP.Path_DLP = dep_ytdlp;
                    } else if (!string.IsNullOrWhiteSpace(dep_youtubedl)) {
                        Data.PathYTDLP = DLP.Path_DLP = dep_youtubedl;
                    }

                }
                if (Regex.IsMatch(DLP.Path_DLP, isYoutubeDl)) DLP.Type = DLP.DLPType.youtube_dl;
                if (string.IsNullOrWhiteSpace(DLP.Path_Aria2)) {
                    Data.PathAria2 = DLP.Path_Aria2 = dep_aria2;
                }
                if (string.IsNullOrWhiteSpace(FFMPEG.Path_FFMPEG)) {
                    Data.PathFFMPEG = FFMPEG.Path_FFMPEG = dep_ffmpeg;
                }
            }
        }
        public async void Inits() {
            //check update
            var needcheck = false;
            var currentDate = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd"); //"";

            if (!string.IsNullOrWhiteSpace(Data.LastVersion)) needcheck = true; //not yaml
            if (currentDate != Data.LastCheckUpdate) needcheck = true; //cross date

            if (needcheck) {
                var releaseData = await Web.GetLastTag();
                var last = releaseData.FirstOrDefault();
                if (last != null) {
                    Data.ReleaseData = releaseData;
                    Data.LastVersion = last.tag_name;
                    Data.LastCheckUpdate = currentDate;
                }
            }
            if (string.Compare(App.CurrentVersion, Data.LastVersion) < 0) {
                Data.NewVersion = true;
            }
        }
        private void Button_Analyze(object sender, RoutedEventArgs e) {
            Analyze_Start();
        }
        private void Analyze_Start() {
            Data.IsAnalyze = true;
            cc.SelectedIndex = -1;
            cv.SelectedIndex = -1;
            ca.SelectedIndex = -1;
            cs.SelectedIndex = -1;
            Data.Thumbnail = null;
            Data.Video = new();
            Data.NeedCookie = Data.UseCookie == UseCookie.Always;

            Task.Run(() => {
                GetInfo();
                Data.IsAnalyze = false;

                if (Data.AutoDownloadAnalysed) {
                    Download_Start();
                }
            });
        }
        private void GetInfo() {
            //Analyze
            var dlp = new DLP(Data.Url);
            if (Data.NeedCookie) dlp.Cookie(Data.CookieType);
            if (Data.ProxyEnabled && !string.IsNullOrWhiteSpace(Data.ProxyUrl)) dlp.Proxy(Data.ProxyUrl);
            dlp.GetInfo();
            if (!string.IsNullOrWhiteSpace(Data.selectedConfig.file)) {
                dlp.LoadConfig(Data.selectedConfig.file);
            }
            if (Data.UseOutput) dlp.Output("%(title)s.%(ext)s"); //if not used config, default template
            ClearStatus();
            dlp.Exec(null, std => {
                //取得JSON
                Data.Video = JsonConvert.DeserializeObject<Video>(std, new JsonSerializerSettings() {
                    NullValueHandling = NullValueHandling.Ignore
                });
                //Reading Chapters
                {
                    Data.Chapters.Clear();
                    if (Data.Video.chapters != null && Data.Video.chapters.Any()) {
                        Data.Chapters.Add(new Chapters() { title = App.Lang.Main.ChaptersAll, type = ChaptersType.None });
                        Data.Chapters.Add(new Chapters() { title = App.Lang.Main.ChaptersSplite, type = ChaptersType.Split });
                        Data.Chapters.AddRange(Data.Video.chapters);
                    } else {
                        Data.Chapters.Add(new Chapters() { title = App.Lang.Main.ChaptersNone, type = ChaptersType.None });
                    }
                    //Data.selectedChapter = Data.Chapters.First();
                }
                //读取 Formats 与 Thumbnails
                {
                    Debug.WriteLine(JsonConvert.SerializeObject(Data.Video.chapters, Formatting.Indented));
                    Data.Formats.LoadFromVideo(Data.Video.formats);
                    Data.Thumbnails.Reset(Data.Video.thumbnails);
                    Data.RequestedFormats.LoadFromVideo(Data.Video.requested_formats);
                }
                //读取 Subtitles
                {
                    var subs = Data.Video.subtitles.Select(x => {
                        var s = x.Value.FirstOrDefault(y => y.ext == "vtt");
                        if (s == null) return null;
                        s.key = x.Key;
                        return s;
                    }).Where(x => x != null).ToList();
                    Data.Subtitles.Clear();
                    if (subs.Any()) {
                        Data.Subtitles.Add(new Subs() { name = App.Lang.Main.SubtitleIgnore });
                    } else {
                        Data.Subtitles.Add(new Subs() { name = App.Lang.Main.SubtitleNone });
                    }
                    Data.Subtitles.AddRange(subs);
                }
                var BestUrl = Data.Thumbnails.LastOrDefault()?.url;
                //var ThumbUrl = string.Empty;
                if (BestUrl != null && Web.Head(BestUrl)) {
                    Data.Thumbnail = BestUrl;
                    //ThumbUrl = BestUrl;
                } else {
                    Data.Thumbnail = Data.Video.thumbnail;
                    //ThumbUrl = Data.Video.thumbnail;
                }
                //Download Thumb to Temp Folder
                /*
                Directory.CreateDirectory(App.Path(App.Folders.temp));
                var ThumbPath = App.Path(App.Folders.temp, Path.ChangeExtension(Data.Video.id, ".jpg"));
                FFMPEG.DownloadUrl(ThumbUrl, ThumbPath);
                Data.Thumbnail = ThumbPath;
                */

                Data.SelectFormatBest(); //Make ComboBox Selected Item
                var full = string.Empty;
                if (Path.IsPathRooted(Data.Video._filename)) {
                    full = Path.GetFullPath(Data.Video._filename);
                } else {
                    full = Path.Combine(Data.TargetPath, Data.Video._filename);
                }
                //Data.TargetName = GetValidFileName(Data.Video.title) + ".tmp"; //预设挡案名称
                Data.TargetName = full; //预设挡案名称
            });
            dlp.Err(DLP.DLPError.Sign, () => {
                if (Data.UseCookie == UseCookie.WhenNeeded) {
                    Data.NeedCookie = true;
                    GetInfo();
                } else if (Data.UseCookie == UseCookie.Ask) {
                    var mb = System.Windows.Forms.MessageBox.Show(
                        "Cookies are required, Use it?\n",
                        "yt-dlp-gui",
                        MessageBoxButtons.YesNo);

                    if (mb == System.Windows.Forms.DialogResult.Yes) {
                        Data.NeedCookie = true;
                        GetInfo();
                    }
                }
            });
        }
        private void ClearStatus() {
            Data.DNStatus_Infos.Clear();
            Data.DNStatus_Video = new();
            Data.DNStatus_Audio = new();
            Data.VideoPersent = Data.AudioPersent = 0;
        }
        private Regex regDLP = new Regex(@"^\[yt-dlp]");
        private Regex regAria = new Regex(@"(?<=\[#\w{6}).*?(?<downloaded>[\w]+).*?\/(?<total>[\w]+).*?(?<persent>[\w.]+)%.*?CN:(?<cn>\d+).*DL:(?<speed>\w+)(.*?ETA:(?<eta>\w+))?");
        private Regex regFF = new Regex(@"frame=.*?(?<frame>\d+).*?fps=.*?(?<fps>[\d.]+).*?size=.*?(?<size>\w+).*?time=(?<time>\S+).*?bitrate=(?<bitrate>\S+)");
        private Regex regYTDL = new Regex(@"^\[download\].*?(?<persent>[\d\.]+).*?(?<=of).*?(?<total>\S+).*?(?<=at).*?(?<speed>\S+).*?(?<=ETA).*?(?<eta>\S+)");
        private void GetStatus(string std, int chn = 0) {
            //Debug.WriteLine(std, "STATUS");
            if (regDLP.IsMatch(std)) {
                // yt-dlp
                if (!Data.DNStatus_Infos.ContainsKey("Downloader")) Data.DNStatus_Infos["Downloader"] = "Native";
                var d = std.Split(',');
                var s = (chn == 0) ? Data.DNStatus_Video : Data.DNStatus_Audio;
                if (decimal.TryParse(d[4], out decimal d_total)) {
                    s.Total = d_total;
                    s.Persent = decimal.Parse(d[3]) / d_total * 100; ;
                } else {
                    if (decimal.TryParse(d[1].TrimEnd('%'), out decimal d_persent)) {
                        s.Persent = d_persent;
                    }
                }
                s.Downloaded = decimal.Parse(d[3]);
                if (decimal.TryParse(d[5], out decimal d_speed)) s.Speed = d_speed;
                if (decimal.TryParse(d[6], out decimal d_elapsed)) s.Elapsed = d_elapsed;
                if (chn == 0) {
                    Data.VideoPersent = s.Persent;
                } else {
                    Data.AudioPersent = s.Persent;
                }
                if (Data.DNStatus_Infos.ContainsKey("Downloader") && Data.DNStatus_Infos["Downloader"] != "Native") return;
                Data.DNStatus_Infos["Downloaded"] = Util.GetAutoUnit((long)Data.DNStatus_Video.Downloaded + (long)Data.DNStatus_Audio.Downloaded);
                Data.DNStatus_Infos["Total"] = Util.GetAutoUnit((long)Data.DNStatus_Video.Total + (long)Data.DNStatus_Audio.Total);
                Data.DNStatus_Infos["Speed"] = Util.GetAutoUnit((long)Data.DNStatus_Video.Speed + (long)Data.DNStatus_Audio.Speed);
                Data.DNStatus_Infos["Elapsed"] = Util.SecToStr(Data.DNStatus_Video.Elapsed + Data.DNStatus_Audio.Elapsed);
                Data.DNStatus_Infos["Status"] = "Downloading";
            } else if (regAria.IsMatch(std)) {
                // aria2
                if (!Data.DNStatus_Infos.ContainsKey("Downloader")) Data.DNStatus_Infos["Downloader"] = "aria2c";
                var d = Util.GetGroup(regAria, std);
                if (chn == 0) {
                    if (decimal.TryParse(d["persent"], out decimal o_persent)) Data.VideoPersent = o_persent;
                    Data.DNStatus_Infos["Downloaded"] = d["downloaded"];
                    Data.DNStatus_Infos["Total"] = d["total"];
                    Data.DNStatus_Infos["Speed"] = d["speed"];
                    Data.DNStatus_Infos["Elapsed"] = d.GetValueOrDefault("eta", "0s");
                    Data.DNStatus_Infos["Connections"] = d["cn"];
                } else {
                    if (decimal.TryParse(d["persent"], out decimal o_persent)) Data.AudioPersent = o_persent;
                }
                Data.DNStatus_Infos["Status"] = "Downloading";
            } else if (regFF.IsMatch(std)) {
                // ffmpeg
                if (!Data.DNStatus_Infos.ContainsKey("Downloader")) Data.DNStatus_Infos["Downloader"] = "FFMPEG";
                var d = Util.GetGroup(regFF, std);
                Data.DNStatus_Infos["Downloaded"] = d.GetValueOrDefault("size", "");
                Data.DNStatus_Infos["Speed"] = d.GetValueOrDefault("bitrate", "");
                Data.DNStatus_Infos["Frame"] = d.GetValueOrDefault("frame", "");
                Data.DNStatus_Infos["FPS"] = d.GetValueOrDefault("fps", "");
                Data.DNStatus_Infos["Time"] = d.GetValueOrDefault("time", "");
                Data.DNStatus_Infos["Status"] = "Downloading";
            } else if (regYTDL.IsMatch(std)) {
                // youtube-dl
                if (!Data.DNStatus_Infos.ContainsKey("Downloader")) Data.DNStatus_Infos["Downloader"] = "youtube-dl";
                var d = Util.GetGroup(regYTDL, std);
                if (chn == 0) {
                    if (decimal.TryParse(d["persent"], out decimal o_persent)) Data.VideoPersent = o_persent;
                } else {
                    if (decimal.TryParse(d["persent"], out decimal o_persent)) Data.AudioPersent = o_persent;
                }
                Data.DNStatus_Infos["Total"] = d.GetValueOrDefault("total", "");
                Data.DNStatus_Infos["Speed"] = d.GetValueOrDefault("speed", "");
                Data.DNStatus_Infos["Elapsed"] = d.GetValueOrDefault("eta", "");
                Data.DNStatus_Infos["Status"] = "Downloading";
            }
        }
        private void Button_SaveVideo(object sender, RoutedEventArgs e) {
            SaveStream(0);

        }
        private void Button_SaveAudio(object sender, RoutedEventArgs e) {
            SaveStream(1);
        }
        private void SaveStream(int ch = 0) {
            var dialog = new SaveFileDialog();
            dialog.InitialDirectory = Path.GetDirectoryName(Data.TargetFile);
            dialog.DefaultExt = ch == 0
                ? Data.selectedVideo.video_ext
                : Data.selectedAudio.audio_ext;
            var useExt = "." + dialog.DefaultExt;
            if (ch == 1 && useExt.ToLower() == ".webm") useExt = ".opus";
            dialog.Filter = "MediaFile | *" + useExt;
            dialog.FileName = Path.ChangeExtension(Path.GetFileName(Data.TargetFile), useExt);
            if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK) {
                var target = dialog.FileName;
                if (!Path.HasExtension(target)) {
                    target = Path.ChangeExtension(dialog.FileName, "." + dialog.DefaultExt);
                }
                //var target = Path.ChangeExtension(dialog.FileName, "." + dialog.DefaultExt);
                RunningDLP.Clear();
                Data.IsDownload = true;
                ClearStatus();
                Task.Run(() => {
                    //任務池
                    List<Task> tasks = new();
                    tasks.Add(Task.Run(() => {
                        var dlp = new DLP(Data.Url);
                        RunningDLP.Add(dlp);
                        if (!string.IsNullOrWhiteSpace(Data.selectedConfig.file)) dlp.LoadConfig(Data.selectedConfig.file);
                        if (Data.NeedCookie) dlp.Cookie(Data.CookieType);
                        if (Data.ProxyEnabled && !string.IsNullOrWhiteSpace(Data.ProxyUrl)) dlp.Proxy(Data.ProxyUrl);
                        if (Data.UseAria2) dlp.UseAria2();
                        if (!string.IsNullOrWhiteSpace(Data.LimitRate)) dlp.LimitRate(Data.LimitRate);
                        if (ch == 1) dlp.ExtractAudio(Path.GetExtension(target));
                        dlp.IsLive = Data.Video.is_live;

                        var vid = ch == 0
                        ? Data.selectedVideo.format_id
                        : Data.selectedAudio.format_id;

                        var tr = Data.TimeRange.Trim();
                        if (!string.IsNullOrWhiteSpace(tr)) {
                            dlp.DownloadSections(tr);
                        }
                        if (Data.selectedChapter != null && Data.selectedChapter.type == ChaptersType.Segment) {
                            dlp.DownloadSections(Data.selectedChapter.title);
                        }

                        dlp.DownloadFormat(vid, target);
                        dlp.Exec(std => {
                            GetStatus(std, ch);
                        });
                    }));
                    //WaitAll Downloads, Merger Video and Audio
                    Task.WaitAll(tasks.ToArray());
                    if (!Data.IsAbouted) Data.DNStatus_Infos["Status"] = "Done";
                    Data.IsDownload = false;
                });
            }
        }
        private void Button_ExplorerTarget(object sender, RoutedEventArgs e) {
            Util.Explorer(Data.TargetFile);
        }
        private void Button_Cancel(object sender, RoutedEventArgs e) {
            if (Data.IsDownload) {
                Data.IsAbouted = true;
                foreach (var dlp in RunningDLP) {
                    dlp.Close();
                }
            }
        }
        private void Button_Download(object sender, RoutedEventArgs e) {
            Download_Start();
        }
        private void Download_Start() {
            Data.CanCancel = false;
            Data.IsAbouted = false;
            if (Data.IsDownload) {
                Data.IsAbouted = true;
                foreach (var dlp in RunningDLP) {
                    dlp.Close();
                }
            } else {
                var overwrite = true;
                RunningDLP.Clear();
                //如果檔案已存在
                if (File.Exists(Data.TargetFile)) {
                    var mb = System.Windows.Forms.MessageBox.Show(
                        "File Already exist. Overwrite it?\n",
                        "yt-dlp-gui",
                        MessageBoxButtons.YesNo);
                    overwrite = mb == System.Windows.Forms.DialogResult.Yes;
                    if (!overwrite) return; //不要复写
                }
                Data.IsDownload = true;
                //進度更新為0
                ClearStatus();
                Data.CheckExtension();

                var tr = Data.TimeRange.Trim();
                var isSingle = false;
                if (Data.selectedVideo.type == FormatType.package) isSingle = true;
                if (!string.IsNullOrWhiteSpace(tr)) isSingle = true;
                if (Data.selectedChapter != null && Data.selectedChapter.type == ChaptersType.Segment) isSingle = true;


                Task.Run(() => {
                    //任務池
                    List<Task> tasks = new();
                    var tmp_video_path = string.Empty;
                    var tmp_audio_path = string.Empty;
                    //Download Video (or Packaged)
                    tasks.Add(Task.Run(() => {
                        var dlp = new DLP(Data.Url);
                        RunningDLP.Add(dlp);
                        if (!string.IsNullOrWhiteSpace(Data.selectedConfig.file)) dlp.LoadConfig(Data.selectedConfig.file);
                        if (Data.NeedCookie) dlp.Cookie(Data.CookieType);
                        if (Data.ProxyEnabled && !string.IsNullOrWhiteSpace(Data.ProxyUrl)) dlp.Proxy(Data.ProxyUrl);
                        if (Data.UseAria2) dlp.UseAria2();
                        if (!string.IsNullOrWhiteSpace(Data.LimitRate)) dlp.LimitRate(Data.LimitRate);
                        dlp.IsLive = Data.Video.is_live;

                        var vid = Data.selectedVideo.format_id;
                        if (!string.IsNullOrWhiteSpace(tr)) {
                            vid = Data.selectedVideo.format_id + "+" + Data.selectedAudio.format_id;
                            dlp.DownloadSections(tr);
                        }
                        if (Data.selectedChapter != null && Data.selectedChapter.type == ChaptersType.Segment) {
                            vid = Data.selectedVideo.format_id + "+" + Data.selectedAudio.format_id;
                            dlp.DownloadSections(Data.selectedChapter.title);
                        }

                        tmp_video_path = Path.Combine(GetTempPath, $"{Data.Video.id}.{vid}.{Data.selectedVideo.video_ext}");
                        if (dlp.IsLive) {
                            tmp_video_path = Data.TargetFile;
                        }
                        dlp.DownloadFormat(vid, tmp_video_path);
                        Debug.WriteLine("Download Video");
                        dlp.Exec(std => {
                            Debug.WriteLine(std, "V");
                            GetStatus(std, 0);
                        });
                    }));
                    //Download Audio
                    if (!isSingle) {
                        tasks.Add(Task.Run(() => {
                            var dlp = new DLP(Data.Url);
                            RunningDLP.Add(dlp);
                            if (!string.IsNullOrWhiteSpace(Data.selectedConfig.file)) dlp.LoadConfig(Data.selectedConfig.file);
                            if (Data.NeedCookie) dlp.Cookie(Data.CookieType);
                            if (Data.ProxyEnabled && !string.IsNullOrWhiteSpace(Data.ProxyUrl)) dlp.Proxy(Data.ProxyUrl);
                            if (Data.UseAria2) dlp.UseAria2();
                            if (!string.IsNullOrWhiteSpace(Data.LimitRate)) dlp.LimitRate(Data.LimitRate);
                            dlp.IsLive = Data.Video.is_live;

                            var aid = Data.selectedAudio.format_id;
                            tmp_audio_path = Path.Combine(GetTempPath, $"{Data.Video.id}.{aid}.{Data.selectedAudio.audio_ext}");
                            dlp.DownloadFormat(aid, tmp_audio_path);
                            Debug.WriteLine("Download Audio");
                            dlp.Exec(std => {
                                //Debug.WriteLine(std, "A");
                                GetStatus(std, 1);
                            });
                        }));
                    }
                    //Download Subtitle
                    var subpath = string.Empty;
                    if (!string.IsNullOrWhiteSpace(Data.selectedSub?.url)) {
                        Data.SubtitlePersent = 0;
                        subpath = Path.ChangeExtension(Data.TargetFile, Data.selectedSub.key + ".srt");
                        FFMPEG.DownloadUrl(Data.selectedSub.url, subpath);
                        Data.SubtitlePersent = 100;
                    }
                    //Download Thumbnail
                    if (Data.SaveThumbnail) {
                        if (!string.IsNullOrWhiteSpace(Data.Thumbnail)) {
                            var thumbpath = Path.ChangeExtension(Data.TargetFile, ".jpg");
                            //FFMPEG.DownloadUrl(Data.Thumbnail, thumbpath);
                            DownloadThumbnail(thumbpath);
                        }
                    }

                    //WaitAll Downloads, Merger Video and Audio
                    Data.CanCancel = true;
                    Task.WaitAll(tasks.ToArray());
                    if (!Data.IsAbouted) {
                        //Download Complete
                        if (!isSingle) {
                            if (Data.EmbedSub && File.Exists(subpath)) {
                                //Subtitle
                                FFMPEG.Merger(overwrite, Data.TargetFile, tmp_video_path, tmp_audio_path, subpath);
                                File.Delete(subpath);
                            } else {
                                FFMPEG.Merger(overwrite, Data.TargetFile, tmp_video_path, tmp_audio_path);
                            }
                            if (File.Exists(tmp_video_path)) File.Delete(tmp_video_path);
                            if (File.Exists(tmp_audio_path)) File.Delete(tmp_audio_path);
                        } else {
                            if (Data.EmbedSub && File.Exists(subpath)) {
                                //Subtitle
                                FFMPEG.Merger(overwrite, Data.TargetFile, tmp_video_path, subpath);
                                File.Delete(subpath);
                            } else {
                                File.Move(tmp_video_path, Data.TargetFile, true);
                            }
                            if (File.Exists(tmp_video_path)) File.Delete(tmp_video_path);
                        }
                        //Splite By Chapters
                        if (Data.selectedChapter != null && Data.selectedChapter.type == ChaptersType.Split) {
                            var tar_info = new FileInfo(Data.TargetFile);
                            var tar_name = Path.GetFileNameWithoutExtension(tar_info.Name);
                            var tar_path = tar_info.Directory.FullName;
                            var tar_exts = tar_info.Extension;
                            var cidx = 0;
                            foreach (var c in Data.Video.chapters) {
                                cidx++;
                                var tar_seg_path = Path.Combine(tar_path, $"{tar_name} - {cidx}{tar_exts}");
                                Debug.WriteLine(tar_seg_path);
                                FFMPEG.Split(tar_seg_path, Data.TargetFile, c);
                            }
                            if (File.Exists(Data.TargetFile)) File.Delete(Data.TargetFile);
                        }

                        Data.DNStatus_Infos["Status"] = "Done";

                        //Send notification when download completed
                        try {
                            if (Data.UseNotifications) {
                                new ToastContentBuilder()
                                    .AddArgument("conversationId", 2333)
                                    .AddText(Data.Video.title)
                                    .AddText("Video Download Completed!")
                                    .Show();
                            }
                        } catch (Exception ex) { }
                    }
                    //Clear downloading status
                    Data.IsDownload = false;
                });
            }
        }


        private void Button_Browser(object sender, RoutedEventArgs e) {
            if (string.IsNullOrWhiteSpace(Data.TargetName)) {
                var dialog = new FolderBrowserDialog();
                dialog.SelectedPath = Data.TargetPath;
                if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK) {
                    Data.TargetPath = dialog.SelectedPath;
                }
            } else {
                var dialog = new SaveFileDialog();
                dialog.InitialDirectory = Path.GetDirectoryName(Data.TargetFile);
                dialog.FileName = Path.GetFileName(Data.TargetFile);
                if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK) {
                    Data.TargetPath = Path.GetDirectoryName(dialog.FileName);
                    if ((new string[] { ".mp4", ".webm", ".3gp", ".mkv" }).Any(x => Path.GetExtension(dialog.FileName).ToLower() == x)) {
                        Data.TargetName = Path.GetFileName(dialog.FileName);
                    } else {
                        Data.TargetName = Path.GetFileName(dialog.FileName) + ".tmp";
                    }
                }
            }
        }

        private static Regex RegexValues = new Regex(@"\${(.+?)}", RegexOptions.Compiled);
        private string GetValidFileName(string filename) {
            var regexSearch = new string(Path.GetInvalidFileNameChars());
            return Regex.Replace(filename, string.Format("[{0}]", Regex.Escape(regexSearch)), "_");
        }
        private async void CommandBinding_SaveAs_Executed(object sender, System.Windows.Input.ExecutedRoutedEventArgs e) {
            var dialog = new SaveFileDialog();
            dialog.InitialDirectory = Path.GetDirectoryName(Data.TargetFile);
            var OrigExt = Path.GetExtension(Data.Thumbnail);
            var OrigFileName = Path.ChangeExtension(Path.GetFileName(Data.TargetFile), OrigExt);
            dialog.DefaultExt = ".jpg";
            dialog.Filter = "Image File|*.jpg;*.webp";
            dialog.FileName = Path.ChangeExtension(OrigFileName, ".jpg");
            if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK) {
                DownloadThumbnail(dialog.FileName);
            }
        }
        private void DownloadThumbnail(string toFile) {
            var origExt = Path.GetExtension(Data.Thumbnail);
            var origin = Path.ChangeExtension(Data.TargetFile, origExt);
            //var target = Path.ChangeExtension(Data.TargetFile, ".jpg");
            var target = toFile;
            var progress = new Progress<double>(percentage => {
                Debug.Write($"\rDownloading... {percentage:0.00}%");
            });
            Web.Download(Data.Thumbnail, origin, progress, Data.ProxyEnabled ? Data.ProxyUrl : null).Wait();
            //convert to target ext
            if (Path.GetExtension(origin).ToLower() != Path.GetExtension(target)) {
                FFMPEG.DownloadUrl(origin, target);
                File.Delete(origin);
            }
        }

        private void CommandBinding_SaveAs_CanExecute(object sender, System.Windows.Input.CanExecuteRoutedEventArgs e) {
            e.CanExecute = !string.IsNullOrWhiteSpace(Data.Thumbnail);
        }

        private void Button_Subtitle(object sender, RoutedEventArgs e) {
            var dialog = new SaveFileDialog();
            dialog.InitialDirectory = Path.GetDirectoryName(Data.TargetFile);
            dialog.DefaultExt = ".srt";
            dialog.Filter = "SubRip | *.srt";
            dialog.FileName = Path.ChangeExtension(Path.GetFileName(Data.TargetFile), Data.selectedSub.key + ".srt");
            if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK) {
                var target = Path.ChangeExtension(dialog.FileName, ".srt");
                FFMPEG.DownloadUrl(Data.selectedSub.url, target);
            }
        }

        private void MenuItem_About_Click(object sender, RoutedEventArgs e) {
            var win = new About();
            win.Owner = GetWindow(this);
            win.ShowDialog();
        }

        private void Button_Release(object sender, RoutedEventArgs e) {
            var win = new Release();
            win.Owner = GetWindow(this);
            win.ShowDialog();
        }

        private void Window_Closed(object sender, EventArgs e) {
            Data.Left = Left;
            Data.Top = Top;
            Data.Width = Width;
            Data.Height = Height;
        }

        private void slider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e) {
            ChangeScale(Data.Scale);
        }
        private void ComboBox_TextChanged(object sender, TextChangedEventArgs e) {
            var combo = sender as System.Windows.Controls.ComboBox;
            if (combo.SelectedIndex == -1) {
                Data.PathTEMP = combo.Text;
            } else {
                Data.PathTEMP = combo.SelectedValue.ToString();
            }
        }

        private void ToggleButton_Checked(object sender, RoutedEventArgs e) {
            var b = sender as ToggleButton;
            if (b.IsChecked == true) {
                var menu = new List<MenuDataItem>() {
                    (App.Lang.Main.TemporaryTarget, () => { Data.PathTEMP = "%YTDLPGUI_TARGET%"; }),
                    (App.Lang.Main.TemporaryLocale, () => { Data.PathTEMP = "%YTDLPGUI_LOCALE%"; }),
                    (App.Lang.Main.TemporaryTarget, () => { Data.PathTEMP = "%TEMP%"; }),
                    ("-"),
                    (App.Lang.Main.TemporaryBrowse, () => {
                        var dialog = new FolderBrowserDialog();
                        dialog.SelectedPath = GetEnvPath(Data.PathTEMP);
                        if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK) {
                            Data.PathTEMP = dialog.SelectedPath;
                        }
                    })
                };
                Controls.Menu.Open(menu, b, MenuPlacement.BottomLeft);
            }
        }
    }
}

