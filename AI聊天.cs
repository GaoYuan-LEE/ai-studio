using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

[assembly: AssemblyTitle("AI聊天")]
[assembly: AssemblyDescription("支持 OpenAI 兼容接口和 Markdown 流式输出的本地 AI 工作台")]
[assembly: AssemblyProduct("AI聊天")]
[assembly: AssemblyCompany("本地工具")]
[assembly: AssemblyVersion("1.0.0.0")]
[assembly: AssemblyFileVersion("1.0.0.0")]

namespace AiWorkbench
{
    internal static class Program
    {
        [STAThread]
        private static int Main(string[] args)
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;


            if (args != null && args.Length >= 3 &&
                string.Equals(args[0], "--self-test", StringComparison.OrdinalIgnoreCase))
            {
                string outputPath = args[2];
                try
                {
                    List<ChatMessage> messages = new List<ChatMessage>();
                    messages.Add(new ChatMessage { Role = "user", Content = "测试流式输出" });

                    StringBuilder collected = new StringBuilder();
                    AppSettings settings = new AppSettings();
                    settings.BaseUrl = args[1];
                    settings.Model = "mock-model";
                    settings.ApiKey = "";

                    AiClient.StreamChatAsync(settings, messages, delegate(string text)
                    {
                        collected.Append(text);
                    }, CancellationToken.None).GetAwaiter().GetResult();

                    File.WriteAllText(outputPath, collected.ToString(), new UTF8Encoding(false));
                    return 0;
                }
                catch (Exception ex)
                {
                    File.WriteAllText(outputPath, ex.ToString(), new UTF8Encoding(false));
                    return 1;
                }
            }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new ChatForm());
            return 0;
        }
    }

    internal sealed class ChatMessage
    {
        public string Role;
        public string Content;
    }

    internal sealed class AppSettings
    {
        public string BaseUrl;
        public string Model;
        public string ApiKey;
    }

    internal sealed class StoredSettings
    {
        public string BaseUrl;
        public string Model;
        public string ProtectedApiKey;
    }

    internal static class SettingsStore
    {
        private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("AIWorkbench.Settings.v1");
        private static readonly JavaScriptSerializer Serializer = new JavaScriptSerializer();

        public static string SettingsPath
        {
            get
            {
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "AIWorkbench", "settings.json");
            }
        }

        public static AppSettings Load(out bool firstRun)
        {
            AppSettings settings = new AppSettings();
            settings.BaseUrl = "https://api.deepseek.com/v1";
            settings.Model = "deepseek-chat";
            settings.ApiKey = "";
            firstRun = true;

            try
            {
                if (!File.Exists(SettingsPath))
                {
                    return settings;
                }

                StoredSettings stored = Serializer.Deserialize<StoredSettings>(
                    File.ReadAllText(SettingsPath, Encoding.UTF8));
                if (stored == null)
                {
                    return settings;
                }

                if (!string.IsNullOrWhiteSpace(stored.BaseUrl))
                {
                    settings.BaseUrl = stored.BaseUrl;
                }
                if (!string.IsNullOrWhiteSpace(stored.Model))
                {
                    settings.Model = stored.Model;
                }
                settings.ApiKey = Unprotect(stored.ProtectedApiKey);
                firstRun = false;
            }
            catch
            {
                firstRun = true;
            }

            return settings;
        }

        public static void Save(AppSettings settings)
        {
            string folder = Path.GetDirectoryName(SettingsPath);
            Directory.CreateDirectory(folder);

            StoredSettings stored = new StoredSettings();
            stored.BaseUrl = settings.BaseUrl;
            stored.Model = settings.Model;
            stored.ProtectedApiKey = Protect(settings.ApiKey);

            string json = Serializer.Serialize(stored);
            File.WriteAllText(SettingsPath, json, new UTF8Encoding(false));
        }

        private static string Protect(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return "";
            }

            byte[] clear = Encoding.UTF8.GetBytes(value);
            byte[] encrypted = ProtectedData.Protect(clear, Entropy, DataProtectionScope.CurrentUser);
            return Convert.ToBase64String(encrypted);
        }

        private static string Unprotect(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return "";
            }

            try
            {
                byte[] encrypted = Convert.FromBase64String(value);
                byte[] clear = ProtectedData.Unprotect(encrypted, Entropy, DataProtectionScope.CurrentUser);
                return Encoding.UTF8.GetString(clear);
            }
            catch
            {
                return "";
            }
        }
    }

    internal static class AiClient
    {
        private static readonly HttpClient Client;
        private static readonly JavaScriptSerializer Serializer = new JavaScriptSerializer();

        static AiClient()
        {
            HttpClientHandler handler = new HttpClientHandler();
            handler.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;

            Client = new HttpClient(handler);
            Client.Timeout = TimeSpan.FromMinutes(6);
            Client.DefaultRequestHeaders.UserAgent.ParseAdd("AIWorkbench/1.0");
        }

        public static async Task StreamChatAsync(
            AppSettings settings,
            List<ChatMessage> messages,
            Action<string> onDelta,
            CancellationToken cancellationToken)
        {
            if (settings == null)
            {
                throw new InvalidOperationException("API 设置不存在。");
            }
            if (string.IsNullOrWhiteSpace(settings.BaseUrl))
            {
                throw new InvalidOperationException("API 地址为空。");
            }
            if (string.IsNullOrWhiteSpace(settings.Model))
            {
                throw new InvalidOperationException("模型名称为空。");
            }
            if (messages == null || messages.Count == 0)
            {
                throw new InvalidOperationException("没有可发送的消息。");
            }

            string endpoint = BuildEndpoint(settings.BaseUrl);
            Dictionary<string, object> payload = new Dictionary<string, object>();
            payload["model"] = settings.Model;
            payload["stream"] = true;

            List<object> messagePayload = new List<object>();
            foreach (ChatMessage message in messages)
            {
                if (message == null || string.IsNullOrEmpty(message.Role) || message.Content == null)
                {
                    continue;
                }
                Dictionary<string, string> item = new Dictionary<string, string>();
                item["role"] = message.Role;
                item["content"] = message.Content;
                messagePayload.Add(item);
            }
            payload["messages"] = messagePayload;

            string body = Serializer.Serialize(payload);

            using (HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Post, endpoint))
            {
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
                if (!string.IsNullOrWhiteSpace(settings.ApiKey))
                {
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings.ApiKey.Trim());
                }
                request.Content = new StringContent(body, Encoding.UTF8, "application/json");

                using (HttpResponseMessage response = await Client.SendAsync(
                    request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false))
                {
                    if (!response.IsSuccessStatusCode)
                    {
                        string errorBody;
                        try
                        {
                            errorBody = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                        }
                        catch
                        {
                            errorBody = "";
                        }

                        string message = "HTTP " + (int)response.StatusCode + " " + response.ReasonPhrase;
                        if (!string.IsNullOrWhiteSpace(errorBody))
                        {
                            message += "\n" + Trim(errorBody, 900);
                        }
                        throw new InvalidOperationException(message);
                    }

                    using (Stream stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
                    using (StreamReader reader = new StreamReader(stream, Encoding.UTF8))
                    {
                        while (!reader.EndOfStream)
                        {
                            cancellationToken.ThrowIfCancellationRequested();

                            string line = await reader.ReadLineAsync().ConfigureAwait(false);
                            if (line == null)
                            {
                                break;
                            }

                            if (line.Length == 0 || line[0] == ':')
                            {
                                continue;
                            }
                            if (!line.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                            {
                                continue;
                            }

                            string data = line.Substring(5).Trim();
                            if (data.Length == 0)
                            {
                                continue;
                            }
                            if (string.Equals(data, "[DONE]", StringComparison.OrdinalIgnoreCase))
                            {
                                break;
                            }

                            string delta = ParseDelta(data);
                            if (!string.IsNullOrEmpty(delta) && onDelta != null)
                            {
                                onDelta(delta);
                            }
                        }
                    }
                }
            }
        }

        private static string ParseDelta(string data)
        {
            Dictionary<string, object> root;
            try
            {
                root = Serializer.Deserialize<Dictionary<string, object>>(data);
            }
            catch
            {
                return null;
            }

            if (root == null)
            {
                return null;
            }

            object errorValue;
            if (root.TryGetValue("error", out errorValue))
            {
                throw new InvalidOperationException(ExtractError(errorValue));
            }

            object choicesValue;
            if (!root.TryGetValue("choices", out choicesValue))
            {
                return null;
            }

            IList choices = choicesValue as IList;
            if (choices == null || choices.Count == 0)
            {
                return null;
            }

            Dictionary<string, object> choice = choices[0] as Dictionary<string, object>;
            if (choice == null)
            {
                return null;
            }

            object deltaValue;
            if (!choice.TryGetValue("delta", out deltaValue))
            {
                return null;
            }

            Dictionary<string, object> delta = deltaValue as Dictionary<string, object>;
            if (delta == null)
            {
                return null;
            }

            object contentValue;
            if (!delta.TryGetValue("content", out contentValue) || contentValue == null)
            {
                return null;
            }

            return contentValue as string;
        }

        private static string ExtractError(object errorValue)
        {
            Dictionary<string, object> error = errorValue as Dictionary<string, object>;
            if (error != null)
            {
                object message;
                if (error.TryGetValue("message", out message) && message != null)
                {
                    return message.ToString();
                }
            }

            string text = errorValue == null ? "" : errorValue.ToString();
            return string.IsNullOrEmpty(text) ? "接口返回了错误。" : text;
        }

        private static string BuildEndpoint(string baseUrl)
        {
            string value = baseUrl.Trim().TrimEnd('/');
            if (value.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase))
            {
                return value;
            }
            return value + "/chat/completions";
        }

        private static string Trim(string value, int length)
        {
            if (value == null)
            {
                return "";
            }
            value = value.Replace("\r", " ").Replace("\n", " ");
            return value.Length <= length ? value : value.Substring(0, length) + "…";
        }
    }

    internal sealed class ChatForm : Form
    {
        private readonly JavaScriptSerializer _json = new JavaScriptSerializer();
        private readonly AppSettings _settings;
        private bool _firstRun;
        private WebView2 _webView;
        private CancellationTokenSource _streamCts;
        private string _streamRequestId;

        public ChatForm()
        {
            bool firstRun;
            _settings = SettingsStore.Load(out firstRun);
            _firstRun = firstRun;

            Text = "AI 聊天";
            ClientSize = new Size(1120, 780);
            MinimumSize = new Size(820, 560);
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = Color.FromArgb(245, 247, 251);
            Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Regular, GraphicsUnit.Point);
            AutoScaleMode = AutoScaleMode.Dpi;
            AutoScaleDimensions = new SizeF(96F, 96F);

            try
            {
                string iconPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "web.ico");
                if (File.Exists(iconPath))
                {
                    Icon = new Icon(iconPath);
                }
                else
                {
                    Icon = System.Drawing.Icon.ExtractAssociatedIcon(Application.ExecutablePath);
                }
            }
            catch { }

            _webView = new WebView2();
            _webView.Dock = DockStyle.Fill;
            _webView.DefaultBackgroundColor = Color.FromArgb(245, 247, 251);
            Controls.Add(_webView);

            Load += ChatForm_Load;
            FormClosing += ChatForm_FormClosing;
        }

        private async void ChatForm_Load(object sender, EventArgs e)
        {
            try
            {
                string uiFolder = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ai-ui");
                string indexFile = Path.Combine(uiFolder, "index.html");
                if (!File.Exists(indexFile))
                {
                    throw new FileNotFoundException("找不到界面文件：" + indexFile);
                }

                string userDataFolder = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "AIWorkbench", "WebView2");
                Directory.CreateDirectory(userDataFolder);

                CoreWebView2Environment environment =
                    await CoreWebView2Environment.CreateAsync(null, userDataFolder, null);
                await _webView.EnsureCoreWebView2Async(environment);

                if (IsDisposed)
                {
                    return;
                }

                ConfigureWebView(uiFolder);
            }
            catch (WebView2RuntimeNotFoundException)
            {
                MessageBox.Show(this,
                    "没有找到 Microsoft Edge WebView2 运行时，界面无法启动。\n\n" +
                    "请安装 Windows 的 Edge WebView2 运行时后重试。",
                    "缺少浏览器内核", MessageBoxButtons.OK, MessageBoxIcon.Error);
                Close();
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "AI 工作台启动失败：\n\n" + ex.Message,
                    "启动失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
                Close();
            }
        }

        private void ConfigureWebView(string uiFolder)
        {
            CoreWebView2 core = _webView.CoreWebView2;
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.AreDefaultContextMenusEnabled = true;
            core.Settings.IsZoomControlEnabled = true;
            core.Settings.AreBrowserAcceleratorKeysEnabled = true;

            core.SetVirtualHostNameToFolderMapping(
                "ai.local", uiFolder, CoreWebView2HostResourceAccessKind.Allow);
            core.WebMessageReceived += Core_WebMessageReceived;
            core.NavigationStarting += Core_NavigationStarting;
            core.NewWindowRequested += Core_NewWindowRequested;
            core.ProcessFailed += Core_ProcessFailed;
            core.Navigate("https://ai.local/index.html");
        }

        private void Core_WebMessageReceived(object sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            try
            {
                Dictionary<string, object> message =
                    _json.Deserialize<Dictionary<string, object>>(e.WebMessageAsJson);
                string type = GetString(message, "type");

                if (string.Equals(type, "ready", StringComparison.Ordinal))
                {
                    SendConfig();
                }
                else if (string.Equals(type, "saveSettings", StringComparison.Ordinal))
                {
                    SaveSettings(message);
                }
                else if (string.Equals(type, "send", StringComparison.Ordinal))
                {
                    StartStream(message);
                }
                else if (string.Equals(type, "stop", StringComparison.Ordinal))
                {
                    StopStream(GetString(message, "requestId"));
                }
                else if (string.Equals(type, "openExternal", StringComparison.Ordinal))
                {
                    OpenExternal(GetString(message, "url"));
                }
            }
            catch (Exception ex)
            {
                PostMessage(new Dictionary<string, object>
                {
                    { "type", "error" },
                    { "message", "处理界面消息失败：" + ex.Message }
                });
            }
        }

        private void SendConfig()
        {
            Dictionary<string, object> payload = new Dictionary<string, object>();
            payload["type"] = "config";
            payload["baseUrl"] = _settings.BaseUrl;
            payload["model"] = _settings.Model;
            payload["apiKey"] = _settings.ApiKey;
            payload["firstRun"] = _firstRun;
            PostMessage(payload);
        }

        private void SaveSettings(Dictionary<string, object> message)
        {
            string baseUrl = (GetString(message, "baseUrl") ?? "").Trim().TrimEnd('/');
            string model = (GetString(message, "model") ?? "").Trim();
            string apiKey = (GetString(message, "apiKey") ?? "").Trim();

            Uri uri;
            if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out uri) ||
                (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            {
                PostMessage(new Dictionary<string, object>
                {
                    { "type", "settingsError" },
                    { "message", "API 地址必须以 http:// 或 https:// 开头。" }
                });
                return;
            }
            if (model.Length == 0)
            {
                PostMessage(new Dictionary<string, object>
                {
                    { "type", "settingsError" },
                    { "message", "模型名称不能为空。" }
                });
                return;
            }

            AppSettings updated = new AppSettings();
            updated.BaseUrl = baseUrl;
            updated.Model = model;
            updated.ApiKey = apiKey;

            try
            {
                SettingsStore.Save(updated);
                _settings.BaseUrl = updated.BaseUrl;
                _settings.Model = updated.Model;
                _settings.ApiKey = updated.ApiKey;
                _firstRun = false;

                Dictionary<string, object> payload = new Dictionary<string, object>();
                payload["type"] = "settingsSaved";
                payload["baseUrl"] = updated.BaseUrl;
                payload["model"] = updated.Model;
                payload["apiKey"] = updated.ApiKey;
                PostMessage(payload);
            }
            catch (Exception ex)
            {
                PostMessage(new Dictionary<string, object>
                {
                    { "type", "settingsError" },
                    { "message", "保存设置失败：" + ex.Message }
                });
            }
        }

        private void StartStream(Dictionary<string, object> message)
        {
            string requestId = GetString(message, "requestId");
            if (string.IsNullOrEmpty(requestId))
            {
                return;
            }

            List<ChatMessage> messages = new List<ChatMessage>();
            object rawMessages;
            if (message.TryGetValue("messages", out rawMessages))
            {
                IList items = rawMessages as IList;
                if (items != null)
                {
                    foreach (object item in items)
                    {
                        Dictionary<string, object> entry = item as Dictionary<string, object>;
                        if (entry == null)
                        {
                            continue;
                        }
                        string role = GetString(entry, "role");
                        string content = GetString(entry, "content");
                        if (string.IsNullOrEmpty(role) || string.IsNullOrEmpty(content))
                        {
                            continue;
                        }
                        messages.Add(new ChatMessage { Role = role, Content = content });
                    }
                }
            }

            if (messages.Count == 0)
            {
                PostMessage(new Dictionary<string, object>
                {
                    { "type", "error" },
                    { "requestId", requestId },
                    { "message", "没有可发送的消息。" }
                });
                return;
            }

            CancelActiveStream();
            CancellationTokenSource cts = new CancellationTokenSource();
            _streamCts = cts;
            _streamRequestId = requestId;
            RunStreamAsync(requestId, messages, cts);
        }

        private async void RunStreamAsync(
            string requestId, List<ChatMessage> messages, CancellationTokenSource cts)
        {
            try
            {
                await AiClient.StreamChatAsync(_settings, messages, delegate(string text)
                {
                    if (requestId == _streamRequestId)
                    {
                        PostMessage(new Dictionary<string, object>
                        {
                            { "type", "delta" },
                            { "requestId", requestId },
                            { "text", text }
                        });
                    }
                }, cts.Token);

                if (requestId == _streamRequestId)
                {
                    PostMessage(new Dictionary<string, object>
                    {
                        { "type", "done" },
                        { "requestId", requestId },
                        { "cancelled", false }
                    });
                }
            }
            catch (OperationCanceledException)
            {
                if (requestId == _streamRequestId)
                {
                    PostMessage(new Dictionary<string, object>
                    {
                        { "type", "done" },
                        { "requestId", requestId },
                        { "cancelled", true }
                    });
                }
            }
            catch (Exception ex)
            {
                if (requestId == _streamRequestId)
                {
                    PostMessage(new Dictionary<string, object>
                    {
                        { "type", "error" },
                        { "requestId", requestId },
                        { "message", ex.Message }
                    });
                }
            }
            finally
            {
                if (_streamCts == cts)
                {
                    _streamCts = null;
                    _streamRequestId = null;
                }
                cts.Dispose();
            }
        }

        private void StopStream(string requestId)
        {
            if (_streamCts == null || _streamRequestId == null)
            {
                return;
            }
            if (!string.IsNullOrEmpty(requestId) && requestId != _streamRequestId)
            {
                return;
            }

            try { _streamCts.Cancel(); }
            catch { }
        }

        private void CancelActiveStream()
        {
            if (_streamCts == null)
            {
                return;
            }
            try { _streamCts.Cancel(); }
            catch { }
            _streamCts = null;
            _streamRequestId = null;
        }

        private void PostMessage(object payload)
        {
            if (IsDisposed)
            {
                return;
            }

            if (InvokeRequired)
            {
                try
                {
                    BeginInvoke(new Action<object>(PostMessage), payload);
                }
                catch { }
                return;
            }

            if (_webView == null || _webView.CoreWebView2 == null)
            {
                return;
            }

            try
            {
                _webView.CoreWebView2.PostWebMessageAsJson(_json.Serialize(payload));
            }
            catch { }
        }

        private void Core_NavigationStarting(object sender, CoreWebView2NavigationStartingEventArgs e)
        {
            if (e.Uri.StartsWith("https://ai.local/", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            e.Cancel = true;
            OpenExternal(e.Uri);
        }

        private void Core_NewWindowRequested(object sender, CoreWebView2NewWindowRequestedEventArgs e)
        {
            e.Handled = true;
            OpenExternal(e.Uri);
        }

        private void Core_ProcessFailed(object sender, CoreWebView2ProcessFailedEventArgs e)
        {
            PostMessage(new Dictionary<string, object>
            {
                { "type", "error" },
                { "message", "界面进程异常，请重新打开程序。" }
            });
        }

        private void OpenExternal(string url)
        {
            Uri uri;
            if (!Uri.TryCreate(url, UriKind.Absolute, out uri))
            {
                return;
            }
            if (uri.Scheme != Uri.UriSchemeHttp &&
                uri.Scheme != Uri.UriSchemeHttps &&
                uri.Scheme != "mailto")
            {
                return;
            }

            try
            {
                ProcessStartInfo info = new ProcessStartInfo(uri.AbsoluteUri);
                info.UseShellExecute = true;
                Process.Start(info);
            }
            catch { }
        }

        private static string GetString(Dictionary<string, object> dictionary, string key)
        {
            object value;
            if (dictionary != null && dictionary.TryGetValue(key, out value) && value != null)
            {
                return value.ToString();
            }
            return null;
        }

        private void ChatForm_FormClosing(object sender, FormClosingEventArgs e)
        {
            CancelActiveStream();
            if (_webView != null)
            {
                try { _webView.Dispose(); }
                catch { }
            }
        }
    }
}
