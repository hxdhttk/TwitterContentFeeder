using Microsoft.Windows.Sdk;
using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;

namespace TwitterContentFeeder
{
    public static class Program
    {
        private static string _configPath = Path.Combine(Directory.GetCurrentDirectory(), "config.json");

        private static Config _config;

        private static Regex _patternRegex;

        private static HttpClient _httpClient;

        private static ConcurrentQueue<(string url, string fileName, string fileExtension)> _imgs;

        private static AutoResetEvent _downloaderWaiter = new AutoResetEvent(false);

        private static Thread _notificationThread;

        private static NotificationForm _notificationForm;

        public static void Main(string[] args)
        {
            _config = GetConfig();

            Logger.Start();
            AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;

            _patternRegex = new Regex(_config.UrlPattern);
            _imgs = new ConcurrentQueue<(string, string, string)>();
            _httpClient = new HttpClient(new HttpClientHandler { Proxy = new WebProxy(_config.Proxy) });

            _notificationThread = new Thread(StartNotificationForm);
            _notificationThread.Start();

            DownloadImages();
        }

        private static Config GetConfig()
        {
            var configContent = File.ReadAllText(_configPath);
            return JsonSerializer.Deserialize<Config>(configContent);
        }

        private static void StartNotificationForm()
        {
            _notificationForm = new NotificationForm();
            _notificationForm.ContentChanged += OnContentChanged;

            Logger.Log("Start notification from...");
            Application.Run(_notificationForm);
        }

        private static void OnContentChanged(object _, string text)
        {
            ExtractImgAsset(text);
        }

        private static void OnUnhandledException(object _, UnhandledExceptionEventArgs e)
        {
            Logger.Log("Unhandled exception thrown:");
            Logger.Log($"{e.ExceptionObject as Exception}");
        }

        private static void ExtractImgAsset(string imgUrl)
        {
            var result = _patternRegex.Match(imgUrl);
            if (result.Success)
            {
                Logger.Log($"Get matched URL: \"{imgUrl}\".");
                var fileName = result.Groups[1].Value;
                var fileExtension = result.Groups[2].Value;
                _imgs.Enqueue((imgUrl, fileName, fileExtension));
            }

            if (!_imgs.IsEmpty)
            {
                _downloaderWaiter.Set();
            }
        }

        private static void DownloadImages()
        {
            while (_downloaderWaiter.WaitOne())
            {
                Logger.Log("Start to download queued items...");
                while (_imgs.TryDequeue(out var imgAsset))
                {
                    try
                    {
                        Logger.Log($"Downloading \"{imgAsset.url}\"...");
                        DownloadImage(imgAsset.url, imgAsset.fileName, imgAsset.fileExtension);
                    }
                    catch
                    {
                        Logger.Log($"Downloading \"{imgAsset.url}\" failed with exceptions, added to the queue...");
                        _imgs.Enqueue(imgAsset);
                    }
                }

                if (!_imgs.IsEmpty)
                {
                    _downloaderWaiter.Set();
                }
            }
        }

        private static void DownloadImage(string imgUrl, string fileName, string fileExtension)
        {
            var filePath = Path.Combine(_config.TempPath, $"{fileName}.{fileExtension}");
            if (File.Exists(filePath))
            {
                Logger.Log($"{filePath} already exists, skip...");
                return;
            }

            lock (_httpClient)
            {
                var response = _httpClient.GetByteArrayAsync(imgUrl).GetAwaiter().GetResult();
                using var imageFile = new FileStream(filePath, FileMode.OpenOrCreate);
                imageFile.Write(response, 0, response.Length);
                Logger.Log($"Downloading \"{imgUrl}\" succeeded.");
            }
        }
    }

    public class Config
    {
        public string TempPath { get; set; }

        public string UrlPattern { get; set; }

        public string Proxy { get; set; }
    }

    public static class Logger
    {
        private static string _logPath = Path.Combine(Directory.GetCurrentDirectory(), @"log.txt");

        private static StreamWriter _logStream;

        public static void Start()
        {
            var logFile = new FileStream(_logPath, FileMode.Create, FileAccess.Write, FileShare.Read);
            _logStream = new StreamWriter(logFile);
        }

        public static void Log(string msg)
        {
            lock (_logStream)
            {
                _logStream.WriteLine($"{DateTime.Now} {msg}");
                _logStream.Flush();
            }
        }
    }

    public class NotificationForm : Form
    {
        public event EventHandler<string> ContentChanged;

        public NotificationForm()
        {
            PInvoke.SetParent(new HWND(Handle), new HWND(-3));
            PInvoke.AddClipboardFormatListener(new HWND(Handle));
        }

        public static string GetText()
        {
            var returnValue = string.Empty;
            var staThread = new Thread(
                () =>
                {
                    if (Clipboard.ContainsText())
                    {
                        returnValue = Clipboard.GetText();
                    }
                });
            staThread.SetApartmentState(ApartmentState.STA);
            staThread.Start();
            staThread.Join();

            return returnValue;
        }

        protected override void WndProc(ref Message m)
        {
            const int WM_CLIPBOARDUPDATE = 0x031D;

            if (m.Msg == WM_CLIPBOARDUPDATE)
            {
                var text = GetText();
                if (!string.IsNullOrEmpty(text))
                {
                    ContentChanged?.Invoke(this, text);
                }
            }

            base.WndProc(ref m);
        }
    }
}