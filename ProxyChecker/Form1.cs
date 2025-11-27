using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Drawing;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace ProxyChecker
{
    public partial class Form1 : Form
    {
        // 存储代理列表（原始格式）
        private List<string> _proxyList = new List<string>();
        // 检测完成的代理结果（仅保留可用的）- 确保在UI线程初始化
        private BindingList<ProxyResult> _validProxyResults;
        // 并发数（根据CPU调整，建议8-15）
        private const int ThreadCount = 10;
        // 国内快速测试地址（新增http://icanhazip.com，优先HTTPS）
        private readonly List<string> _testUrls = new List<string>
        {
            "https://ipv4.icanhazip.com/",
            "https://httpbin.org/ip",
            "https://ipv4.icanhazip.com",
            "http://icanhazip.com" // 兼容用户提到的测试地址
        };
        // 超时时间（10秒，适配国内网络）
        private const int Timeout = 10000;
        // IP验证正则（增强匹配，支持带端口的IP）
        private static readonly Regex IpRegex = new Regex(@"^\d{1,3}\.\d{1,3}\.\d{1,3}\.\d{1,3}(?::\d{1,5})?$", RegexOptions.Compiled);
        // 无协议代理正则
        private static readonly Regex ProxyWithoutProtocolRegex = new Regex(@"^(?<ip>\d{1,3}\.\d{1,3}\.\d{1,3}\.\d{1,3}):(?<port>\d{1,5})$", RegexOptions.Compiled);
        // 百度IP查询API地址（固定）
        private const string BaiduIpApiUrl = "https://opendata.baidu.com/api.php?query={0}&co=&resource_id=6006&oe=utf8";

        // UI控制变量
        private int _testedCount;
        private int _validCount;
        private readonly object _statLock = new object();
        private const int UiUpdateInterval = 300;
        private DateTime _lastUiUpdateTime = DateTime.Now;

        // 停止令牌
        private CancellationTokenSource _cancelTokenSource;
        // HttpClient实例（延迟初始化）- 用于主请求
        private HttpClient _httpClient;
        // 单独的HttpClient用于IP查询（避免并发冲突）
        private HttpClient _ipQueryHttpClient;

        // 您指定的代理列表URL（已修正拼写错误）
        private const string TargetProxyUrl = "https://raw.githubusercontent.com/dpangestuw/Free-Proxy/refs/heads/main/allive.txt";

        public Form1()
        {
            InitializeComponent();
            // 关键：在UI线程初始化BindingList（确保线程关联）
            _validProxyResults = new BindingList<ProxyResult>();
            this.Load += Form1_Load;
            this.FormClosing += Form1_FormClosing;
            // 绑定CellFormatting事件（设置可用状态文字颜色）
            this.dgvResults.CellFormatting += dgvResults_CellFormatting;
        }

        // 释放资源
        private void Form1_FormClosing(object sender, FormClosingEventArgs e)
        {
            _httpClient?.Dispose();
            _ipQueryHttpClient?.Dispose();
            _cancelTokenSource?.Dispose();
        }

        /// <summary>
        /// Form加载初始化
        /// </summary>
        private void Form1_Load(object sender, EventArgs e)
        {
            try
            {
                InitializeHttpClients();
                InitializeUI();
            }
            catch (Exception ex)
            {
                ShowMessageBoxSafe($"初始化失败：{ex.Message}\n详细原因：{ex.InnerException?.Message}", "错误");
                AddLog($"初始化失败：{ex.Message}", LogLevel.Error);
                this.Close();
            }
        }

        /// <summary>
        /// 初始化两个HttpClient（主请求+IP查询）
        /// </summary>
        private void InitializeHttpClients()
        {
            // 主请求HttpClient
            _httpClient = new HttpClient(new HttpClientHandler
            {
                AllowAutoRedirect = true,
                UseCookies = true,
                MaxConnectionsPerServer = 20,
                Proxy = null,
                UseProxy = false,
                AutomaticDecompression = System.Net.DecompressionMethods.GZip | System.Net.DecompressionMethods.Deflate,
                ServerCertificateCustomValidationCallback = (sender, cert, chain, sslPolicyErrors) => true
            })
            {
                Timeout = TimeSpan.FromSeconds(15)
            };

            // IP查询专用HttpClient（单独实例避免并发冲突）
            _ipQueryHttpClient = new HttpClient(new HttpClientHandler
            {
                AllowAutoRedirect = true,
                UseCookies = true,
                MaxConnectionsPerServer = 10,
                Proxy = null,
                UseProxy = false,
                AutomaticDecompression = System.Net.DecompressionMethods.GZip | System.Net.DecompressionMethods.Deflate,
                ServerCertificateCustomValidationCallback = (sender, cert, chain, sslPolicyErrors) => true
            })
            {
                Timeout = TimeSpan.FromSeconds(5) // IP查询超时5秒，避免阻塞
            };

            // 设置UserAgent
            string userAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36";
            if (!_httpClient.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", userAgent))
            {
                AddLog("设置UserAgent失败，使用默认值", LogLevel.Warning);
            }
            if (!_ipQueryHttpClient.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", userAgent))
            {
                AddLog("IP查询HttpClient设置UserAgent失败", LogLevel.Warning);
            }

            AddLog("HttpClient初始化成功（主请求+IP查询双实例）", LogLevel.Info);
        }

        /// <summary>
        /// 初始化界面（调整列标题为“IP信息”）
        /// </summary>
        private void InitializeUI()
        {
            if (dgvResults == null || rtbLog == null || txtProxySource == null ||
                progressBar1 == null || lblStatus == null || btnStart == null ||
                btnStop == null || btnClearLog == null || btnSave == null)
            {
                throw new InvalidOperationException("部分控件未初始化");
            }

            // 配置DataGridView（关键：列标题改为“IP信息”）
            ConfigureDataGridViewColumns();
            dgvResults.AutoGenerateColumns = false;
            dgvResults.DataSource = _validProxyResults;
            dgvResults.Refresh();

            dgvResults.RowHeadersVisible = false;
            dgvResults.AllowUserToAddRows = false;
            dgvResults.AllowUserToDeleteRows = false;
            dgvResults.ReadOnly = true;
            dgvResults.ScrollBars = ScrollBars.Vertical;
            dgvResults.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.None;
            TryEnableDataGridViewDoubleBuffering();
            dgvResults.RowTemplate.Height = 23;

            // 固定代理列表URL
            SetControlTextSafe(txtProxySource, TargetProxyUrl);
            txtProxySource.ReadOnly = false;

            // 按钮状态
            SetControlEnabledSafe(btnStop, false);
            SetControlEnabledSafe(btnClearLog, true);
            SetControlEnabledSafe(btnSave, false);

            // 初始化进度条
            SetProgressBarMaximumSafe(progressBar1, 100);
            SetProgressBarValueSafe(progressBar1, 0);

            // 日志初始化
            AddLog("日志初始化完成，等待开始检测...", LogLevel.Info);
            AddLog($"配置信息：并发{ThreadCount}线程 | 超时{Timeout / 1000}秒 | 测试地址{string.Join("、", _testUrls)} | 过滤80端口", LogLevel.Info);
            AddLog($"代理列表URL：{TargetProxyUrl}", LogLevel.Info);
            AddLog("提示：IP信息列显示「IP - 地理位置 运营商」（通过百度API查询）", LogLevel.Info);

            // 初始化状态文本
            SetControlTextSafe(lblStatus, "未开始");
        }

        /// <summary>
        /// 配置DataGridView列（列标题改为“IP信息”，调整宽度适配长文本）
        /// </summary>
        private void ConfigureDataGridViewColumns()
        {
            dgvResults.Columns.Clear();

            var columns = new List<DataGridViewTextBoxColumn>
            {
                new DataGridViewTextBoxColumn
                {
                    Name = "OriginalProxy",
                    DataPropertyName = "OriginalProxy",
                    HeaderText = "原始代理地址",
                    Width = 130,
                    Visible = true
                },
                new DataGridViewTextBoxColumn
                {
                    Name = "Proxy",
                    DataPropertyName = "Proxy",
                    HeaderText = "统一格式",
                    Width = 150,
                    Visible = true
                },
                new DataGridViewTextBoxColumn
                {
                    Name = "Ip",
                    DataPropertyName = "Ip",
                    HeaderText = "代理IP",
                    Width = 110,
                    Visible = true
                },
                new DataGridViewTextBoxColumn
                {
                    Name = "Port",
                    DataPropertyName = "Port",
                    HeaderText = "端口",
                    Width = 50,
                    Visible = true
                },
                new DataGridViewTextBoxColumn
                {
                    Name = "ResponseTime",
                    DataPropertyName = "ResponseTime",
                    HeaderText = "响应时间(ms)",
                    Width = 90,
                    Visible = true
                },
                new DataGridViewTextBoxColumn
                {
                    Name = "Status",
                    DataPropertyName = "Status",
                    HeaderText = "状态",
                    Width = 70,
                    Visible = true
                },
                new DataGridViewTextBoxColumn
                {
                    Name = "IpAddress",
                    DataPropertyName = "IpAddress",
                    HeaderText = "IP信息", // 列标题修改为“IP信息”
                    Width = 180, // 加宽适配地理位置文本
                    Visible = true
                }
            };

            foreach (var col in columns)
            {
                dgvResults.Columns.Add(col);
            }

            AddLog("DataGridView列配置完成（IP信息列宽180px，显示IP+地理位置）", LogLevel.Info);
        }

        /// <summary>
        /// 百度IP查询API调用（核心：根据IP获取fetchkey和location）
        /// </summary>
        private async Task<string> GetIpLocationAsync(string ip)
        {
            if (string.IsNullOrWhiteSpace(ip) || !IpRegex.IsMatch(ip))
            {
                AddLog($"[IP查询] 无效IP：{ip}", LogLevel.Warning);
                return $"{ip} - 无效IP";
            }

            string apiUrl = string.Format(BaiduIpApiUrl, ip);
            try
            {
                AddLog($"[IP查询] 调用百度API：{apiUrl}", LogLevel.Info);
                var response = await _ipQueryHttpClient.GetAsync(apiUrl, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();

                var json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                AddLog($"[IP查询] API返回：{json}", LogLevel.Info);

                // 解析JSON（兼容中文和特殊字符）
                using (var doc = JsonDocument.Parse(json))
                {
                    var root = doc.RootElement;
                    if (root.TryGetProperty("status", out var statusElem) && statusElem.GetString() == "0")
                    {
                        if (root.TryGetProperty("data", out var dataElem) && dataElem.ValueKind == JsonValueKind.Array && dataElem.GetArrayLength() > 0)
                        {
                            var firstData = dataElem[0];
                            string fetchkey = firstData.TryGetProperty("fetchkey", out var fetchkeyElem) ? fetchkeyElem.GetString() : ip;
                            string location = firstData.TryGetProperty("location", out var locationElem) ? locationElem.GetString() : "未知位置";

                            string result = $"{fetchkey} - {location}";
                            AddLog($"[IP查询成功] {result}", LogLevel.Success);
                            return result;
                        }
                    }

                    AddLog($"[IP查询] API返回无效数据：{json}", LogLevel.Warning);
                    return $"{ip} - 查询失败（数据无效）";
                }
            }
            catch (TaskCanceledException)
            {
                AddLog($"[IP查询] 超时（5秒）：{ip}", LogLevel.Error);
                return $"{ip} - 查询超时";
            }
            catch (Exception ex)
            {
                AddLog($"[IP查询异常] {ip}：{ex.Message.Split('\n').First()}", LogLevel.Error);
                return $"{ip} - 查询异常";
            }
        }

        /// <summary>
        /// DataGridView单元格格式化事件
        /// </summary>
        private void dgvResults_CellFormatting(object sender, DataGridViewCellFormattingEventArgs e)
        {
            if (dgvResults == null || dgvResults.Columns == null || e.RowIndex < 0 || e.ColumnIndex < 0)
                return;

            if (dgvResults.Columns[e.ColumnIndex].Name == "Status")
            {
                string status = e.Value?.ToString() ?? "";
                if (status == "可用")
                {
                    e.CellStyle.ForeColor = Color.Green;
                    e.CellStyle.Font = new Font(dgvResults.Font, FontStyle.Bold);
                }
                else
                {
                    e.CellStyle.ForeColor = Color.Black;
                    e.CellStyle.Font = dgvResults.Font;
                }
            }
        }

        /// <summary>
        /// 启用DataGridView双缓冲
        /// </summary>
        private void TryEnableDataGridViewDoubleBuffering()
        {
            try
            {
                var doubleBufferedProperty = typeof(DataGridView).GetProperty("DoubleBuffered",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                if (doubleBufferedProperty != null)
                {
                    doubleBufferedProperty.SetValue(dgvResults, true);
                    AddLog("DataGridView双缓冲已启用", LogLevel.Info);
                }
            }
            catch (Exception ex)
            {
                AddLog($"启用双缓冲失败：{ex.Message}", LogLevel.Warning);
            }
        }

        #region 日志相关（保持不变）
        private enum LogLevel
        {
            Info,
            Success,
            Error,
            Warning
        }

        private void AddLog(string message, LogLevel level = LogLevel.Info)
        {
            if (rtbLog == null || rtbLog.IsDisposed) return;

            if (rtbLog.InvokeRequired)
            {
                try
                {
                    string tempMsg = message;
                    LogLevel tempLevel = level;
                    rtbLog.Invoke(new Action(() => AddLog(tempMsg, tempLevel)));
                }
                catch (ObjectDisposedException) { }
                return;
            }

            try
            {
                if ((DateTime.Now - _lastUiUpdateTime).TotalMilliseconds < 200)
                    return;

                switch (level)
                {
                    case LogLevel.Info: rtbLog.SelectionColor = Color.Gray; break;
                    case LogLevel.Success: rtbLog.SelectionColor = Color.Green; break;
                    case LogLevel.Error: rtbLog.SelectionColor = Color.Red; break;
                    case LogLevel.Warning: rtbLog.SelectionColor = Color.Orange; break;
                }

                string logLine = $"[{DateTime.Now:HH:mm:ss}] {message}{Environment.NewLine}";
                if (rtbLog.Lines.Length > 2000)
                {
                    var lines = rtbLog.Lines.Skip(rtbLog.Lines.Length - 2000).ToArray();
                    rtbLog.Lines = lines;
                }
                rtbLog.AppendText(logLine);
                rtbLog.SelectionStart = rtbLog.TextLength;
                rtbLog.ScrollToCaret();

                _lastUiUpdateTime = DateTime.Now;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"日志输出失败：{ex.Message}");
            }
        }

        private void ClearLog()
        {
            if (rtbLog == null || rtbLog.IsDisposed) return;

            if (rtbLog.InvokeRequired)
            {
                try
                {
                    rtbLog.Invoke(new Action(ClearLog));
                }
                catch (ObjectDisposedException) { }
                return;
            }
            rtbLog.Clear();
            AddLog("日志已清空", LogLevel.Info);
            AddLog($"配置信息：并发{ThreadCount}线程 | 超时{Timeout / 1000}秒 | 测试地址{string.Join("、", _testUrls)} | 过滤80端口", LogLevel.Info);
        }
        #endregion

        #region 线程安全辅助方法（保持不变）
        private void AddProxyResultSafe(ProxyResult result)
        {
            if (result == null) return;

            if (this.InvokeRequired)
            {
                try
                {
                    ProxyResult tempResult = result;
                    this.Invoke(new Action(() => AddProxyResultSafe(tempResult)));
                }
                catch (ObjectDisposedException) { }
                return;
            }

            if (_validProxyResults != null)
            {
                if (result.Status == "可用")
                {
                    _validProxyResults.Add(result);
                    AddLog($"[UI显示] 已添加可用代理：{result.Proxy} | 响应时间：{result.ResponseTime}ms | IP信息：{result.IpAddress}", LogLevel.Success);

                    dgvResults.ResetBindings();

                    if (dgvResults.Rows.Count > 0 && !dgvResults.IsDisposed)
                    {
                        dgvResults.FirstDisplayedScrollingRowIndex = dgvResults.Rows.Count - 1;
                    }

                    lock (_statLock)
                    {
                        _validCount++;
                    }
                    SetControlEnabledSafe(btnSave, _validCount > 0);
                }
                else
                {
                    AddLog($"[已过滤] 代理：{result.Proxy} | 状态：{result.Status} | IP信息：{result.IpAddress}", LogLevel.Warning);
                }
            }
        }

        private void SetControlTextSafe(Control control, string text)
        {
            if (control == null || control.IsDisposed || string.Equals(control.Text, text, StringComparison.Ordinal))
                return;

            if (control.InvokeRequired)
            {
                try
                {
                    Control tempCtrl = control;
                    string tempText = text;
                    tempCtrl.Invoke(new Action(() => SetControlTextSafe(tempCtrl, tempText)));
                }
                catch (ObjectDisposedException) { }
                return;
            }

            control.Text = text;
        }

        private void SetControlEnabledSafe(Control control, bool enabled)
        {
            if (control == null || control.IsDisposed || control.Enabled == enabled)
                return;

            if (control.InvokeRequired)
            {
                try
                {
                    Control tempCtrl = control;
                    bool tempEnabled = enabled;
                    tempCtrl.Invoke(new Action(() => SetControlEnabledSafe(tempCtrl, tempEnabled)));
                }
                catch (ObjectDisposedException) { }
                return;
            }

            control.Enabled = enabled;
        }

        private void SetProgressBarMaximumSafe(ProgressBar progressBar, int maximum)
        {
            if (progressBar == null || progressBar.IsDisposed || progressBar.Maximum == maximum)
                return;

            if (progressBar.InvokeRequired)
            {
                try
                {
                    ProgressBar tempBar = progressBar;
                    int tempMax = maximum;
                    tempBar.Invoke(new Action(() => SetProgressBarMaximumSafe(tempBar, tempMax)));
                }
                catch (ObjectDisposedException) { }
                return;
            }

            progressBar.Maximum = maximum;
        }

        private void SetProgressBarValueSafe(ProgressBar progressBar, int value)
        {
            if (progressBar == null || progressBar.IsDisposed || progressBar.Value == value)
                return;

            value = Math.Max(progressBar.Minimum, Math.Min(value, progressBar.Maximum));

            if (progressBar.InvokeRequired)
            {
                try
                {
                    ProgressBar tempBar = progressBar;
                    int tempValue = value;
                    tempBar.Invoke(new Action(() => SetProgressBarValueSafe(tempBar, tempValue)));
                }
                catch (ObjectDisposedException) { }
                return;
            }

            progressBar.Value = value;
        }

        private void ShowMessageBoxSafe(string message, string title, MessageBoxIcon icon = MessageBoxIcon.Error)
        {
            if (this.IsDisposed) return;

            if (this.InvokeRequired)
            {
                try
                {
                    string tempMsg = message;
                    string tempTitle = title;
                    MessageBoxIcon tempIcon = icon;
                    this.Invoke(new Action(() => MessageBox.Show(this, tempMsg, tempTitle, MessageBoxButtons.OK, tempIcon)));
                }
                catch (Exception) { }
            }
            else
            {
                MessageBox.Show(this, message, title, MessageBoxButtons.OK, icon);
            }
        }
        #endregion

        #region 业务辅助方法（修改IP解析逻辑）
        private bool ParseProxy(string originalProxy, out string parsedProxy, out string ip, out int port)
        {
            parsedProxy = null;
            ip = null;
            port = 0;

            if (string.IsNullOrWhiteSpace(originalProxy))
                return false;

            string proxy = originalProxy.Trim();

            if (Uri.TryCreate(proxy, UriKind.Absolute, out var uri) &&
                (uri.Scheme.Equals("http", StringComparison.OrdinalIgnoreCase) ||
                 uri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase)))
            {
                ip = uri.Host;
                port = uri.Port;
                parsedProxy = uri.ToString().ToLower();
                return true;
            }

            var match = ProxyWithoutProtocolRegex.Match(proxy);
            if (match.Success &&
                int.TryParse(match.Groups["port"].Value, out int parsedPort) &&
                parsedPort > 0 && parsedPort <= 65535)
            {
                ip = match.Groups["ip"].Value;
                port = parsedPort;
                parsedProxy = $"http://{ip}:{port}";
                return true;
            }

            return false;
        }

        private void SafeUpdateUI(ProxyResult result = null)
        {
            if (dgvResults == null || dgvResults.IsDisposed || progressBar1 == null || progressBar1.IsDisposed || lblStatus == null || lblStatus.IsDisposed)
                return;

            if (dgvResults.InvokeRequired)
            {
                try
                {
                    ProxyResult tempResult = result;
                    dgvResults.Invoke(new Action(() => SafeUpdateUI(tempResult)));
                }
                catch (ObjectDisposedException) { }
                return;
            }

            try
            {
                dgvResults.SuspendLayout();

                if (result != null)
                {
                    AddProxyResultSafe(result);
                }

                int testedCount, validCount, totalCount;
                lock (_statLock)
                {
                    testedCount = _testedCount;
                    validCount = _validCount;
                    totalCount = _proxyList?.Count ?? 0;
                }

                SetProgressBarValueSafe(progressBar1, testedCount);
                SetControlTextSafe(lblStatus, $"检测中...（已检测：{testedCount}/{totalCount} | 可用：{validCount}）");

                dgvResults.ResumeLayout(false);
                dgvResults.Refresh();

                _lastUiUpdateTime = DateTime.Now;
            }
            catch (Exception ex)
            {
                AddLog($"UI更新失败：{ex.Message}", LogLevel.Error);
            }
        }

        private async Task<string> GetAvailableTestUrl()
        {
            foreach (var url in _testUrls)
            {
                try
                {
                    AddLog($"测试地址可用性：{url}", LogLevel.Info);
                    var response = await _httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
                    if (response.IsSuccessStatusCode)
                    {
                        AddLog($"测试地址可用：{url}（状态码：{response.StatusCode}）", LogLevel.Success);
                        return url;
                    }
                    else
                    {
                        AddLog($"测试地址{url}不可用（状态码：{response.StatusCode}）", LogLevel.Warning);
                    }
                }
                catch (Exception ex)
                {
                    AddLog($"测试地址{url}不可用：{ex.Message.Split('\n').First()}", LogLevel.Warning);
                    continue;
                }
            }
            throw new Exception("所有测试地址均不可访问，请检查网络");
        }

        /// <summary>
        /// 解析测试地址返回的IP（仅获取原始IP，后续通过百度API查询）
        /// </summary>
        private string ParseIpFromResponse(string content, string testUrl)
        {
            try
            {
                AddLog($"[测试地址IP解析] 测试地址：{testUrl} | 返回内容：{content.Trim()}", LogLevel.Info);

                // 处理JSON格式（ipify.org、httpbin.org）
                if (testUrl.Contains("ipify.org") || testUrl.Contains("httpbin.org"))
                {
                    var match = Regex.Match(content, @"""ip"":""([^""]+)""|""origin"":""([^""]+)""");
                    if (match.Success)
                    {
                        string ip = match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value;
                        AddLog($"[测试地址IP解析成功] JSON格式 -> {ip}", LogLevel.Info);
                        return ip;
                    }
                }
                // 处理纯文本格式（icanhazip.com）
                else if (testUrl.Contains("icanhazip.com"))
                {
                    string ip = content.Trim();
                    if (IpRegex.IsMatch(ip))
                    {
                        AddLog($"[测试地址IP解析成功] 纯文本格式 -> {ip}", LogLevel.Info);
                        return ip;
                    }
                    else
                    {
                        AddLog($"[测试地址IP解析失败] 纯文本格式但不是IP：{ip}", LogLevel.Warning);
                        return ip;
                    }
                }

                // 通用解析
                var ipMatch = IpRegex.Match(content);
                if (ipMatch.Success)
                {
                    AddLog($"[测试地址IP解析成功] 通用匹配 -> {ipMatch.Value}", LogLevel.Info);
                    return ipMatch.Value;
                }

                AddLog($"[测试地址IP解析失败] 未找到IP：{content.Trim()}", LogLevel.Warning);
                return "解析失败";
            }
            catch (Exception ex)
            {
                AddLog($"[测试地址IP解析异常] {ex.Message}", LogLevel.Error);
                return "解析异常";
            }
        }
        #endregion

        /// <summary>
        /// 从指定URL获取代理列表（保持不变）
        /// </summary>
        private async Task<List<string>> FetchProxiesAsync()
        {
            var proxies = new List<string>();

            if (_httpClient == null)
            {
                AddLog("获取代理失败：HttpClient未初始化", LogLevel.Error);
                return proxies;
            }

            try
            {
                AddLog($"开始获取代理列表：{TargetProxyUrl}", LogLevel.Info);

                var response = await _httpClient.GetAsync(TargetProxyUrl, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();

                var content = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                var lines = content.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                AddLog($"成功读取 {lines.Length} 行数据，开始解析（过滤80端口）...", LogLevel.Info);

                int validCount = 0;
                int filteredCount = 0;

                Parallel.ForEach(lines, line =>
                {
                    string originalProxy = line?.Trim();
                    if (string.IsNullOrWhiteSpace(originalProxy))
                        return;

                    if (ParseProxy(originalProxy, out string parsedProxy, out _, out int port))
                    {
                        if (port != 80)
                        {
                            lock (proxies)
                            {
                                proxies.Add(parsedProxy);
                                validCount++;
                            }
                            AddLog($"解析成功：{originalProxy} -> {parsedProxy}（端口{port}，未过滤）", LogLevel.Info);
                        }
                        else
                        {
                            filteredCount++;
                            AddLog($"过滤80端口：{originalProxy}", LogLevel.Warning);
                        }
                    }
                });

                AddLog($"解析完成：有效代理{validCount}个 | 过滤80端口{filteredCount}个", LogLevel.Success);
                return proxies.Distinct().OrderBy(x => Guid.NewGuid()).ToList();
            }
            catch (HttpRequestException ex)
            {
                string errorMsg = $"获取失败（网络错误）：{ex.Message.Split('\n').First()}";
                AddLog(errorMsg, LogLevel.Error);
                ShowMessageBoxSafe(errorMsg, "获取代理列表失败");
            }
            catch (TaskCanceledException)
            {
                string errorMsg = "获取超时（超过15秒）";
                AddLog(errorMsg, LogLevel.Error);
                ShowMessageBoxSafe(errorMsg, "获取代理列表失败");
            }
            catch (Exception ex)
            {
                string errorMsg = $"获取失败：{ex.Message.Split('\n').First()}";
                AddLog(errorMsg, LogLevel.Error);
                ShowMessageBoxSafe(errorMsg, "获取代理列表失败");
            }
            return proxies;
        }

        /// <summary>
        /// 检测单个代理可用性（核心修改：调用百度API查询IP信息）
        /// </summary>
        private async Task<ProxyResult> TestProxyAsync(string proxyUrl, string testUrl)
        {
            if (string.IsNullOrWhiteSpace(proxyUrl) || string.IsNullOrWhiteSpace(testUrl))
                return null;

            AddLog($"开始检测：{proxyUrl}（测试地址：{testUrl}）", LogLevel.Info);

            var result = new ProxyResult
            {
                OriginalProxy = proxyUrl,
                Proxy = proxyUrl,
                Ip = "未知",
                Port = 0,
                ResponseTime = 0,
                Status = "检测中",
                IpAddress = "查询中..."
            };

            if (!ParseProxy(proxyUrl, out _, out string ip, out int port))
            {
                AddLog($"[{proxyUrl}] 格式无效，跳过", LogLevel.Warning);
                result.Status = "格式无效";
                result.IpAddress = "无效代理";
                return result;
            }
            result.Ip = ip;
            result.Port = port;

            var proxy = new WebProxy(ip, port) { UseDefaultCredentials = false };
            var client = CreateProxyHttpClient(proxy);
            if (client == null)
            {
                AddLog($"[{proxyUrl}] 创建客户端失败，跳过", LogLevel.Error);
                result.Status = "客户端创建失败";
                result.IpAddress = "创建失败";
                return result;
            }

            using (client)
            {
                try
                {
                    var startTime = DateTime.UtcNow;
                    var response = await client.GetAsync(testUrl, _cancelTokenSource?.Token ?? CancellationToken.None).ConfigureAwait(false);

                    if (!response.IsSuccessStatusCode)
                    {
                        AddLog($"[{proxyUrl}] 状态码{response.StatusCode}，失败", LogLevel.Error);
                        result.Status = $"状态码{response.StatusCode}";
                        result.IpAddress = "连接失败";
                        return result;
                    }

                    var responseTime = (int)(DateTime.UtcNow - startTime).TotalMilliseconds;
                    var content = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

                    // 步骤1：从测试地址获取原始IP
                    string realIp = ParseIpFromResponse(content, testUrl);
                    // 步骤2：调用百度API查询IP信息（fetchkey + location）
                    string ipInfo = await GetIpLocationAsync(realIp).ConfigureAwait(false);

                    // 步骤3：赋值给结果
                    result.IpAddress = ipInfo;
                    result.ResponseTime = responseTime;
                    result.Status = "可用";
                    AddLog($"[{proxyUrl}] 检测成功 - 响应{responseTime}ms | IP信息：{result.IpAddress} | 状态：{result.Status}", LogLevel.Success);
                    return result;
                }
                catch (TaskCanceledException)
                {
                    AddLog($"[{proxyUrl}] 超时（{Timeout / 1000}秒）", LogLevel.Error);
                    result.Status = "超时";
                    result.IpAddress = "超时未查询";
                    return result;
                }
                catch (WebException ex)
                {
                    string errorMsg = ex.Status switch
                    {
                        WebExceptionStatus.ConnectFailure => "连接拒绝",
                        WebExceptionStatus.ProxyNameResolutionFailure => "解析失败",
                        _ => ex.Message.Split('\n').First()
                    };
                    AddLog($"[{proxyUrl}] 失败：{errorMsg}", LogLevel.Error);
                    result.Status = errorMsg;
                    result.IpAddress = "连接失败";
                    return result;
                }
                catch (Exception ex)
                {
                    string errorMsg = $"[{proxyUrl}] 失败：{ex.Message.Split('\n').First()}";
                    AddLog(errorMsg, LogLevel.Error);
                    result.Status = "未知错误";
                    result.IpAddress = "查询失败";
                    return result;
                }
            }
        }

        /// <summary>
        /// 创建代理专用HttpClient（保持不变）
        /// </summary>
        private HttpClient CreateProxyHttpClient(WebProxy proxy)
        {
            try
            {
                return new HttpClient(new HttpClientHandler
                {
                    Proxy = proxy,
                    UseProxy = true,
                    AllowAutoRedirect = true,
                    UseCookies = true,
                    MaxConnectionsPerServer = 10,
                    AutomaticDecompression = System.Net.DecompressionMethods.GZip | System.Net.DecompressionMethods.Deflate,
                    ServerCertificateCustomValidationCallback = (sender, cert, chain, sslPolicyErrors) => true
                })
                {
                    Timeout = TimeSpan.FromMilliseconds(Timeout)
                };
            }
            catch (Exception ex)
            {
                AddLog($"创建客户端失败：{ex.Message}", LogLevel.Error);
                return null;
            }
        }

        /// <summary>
        /// 批量检测代理（保持不变）
        /// </summary>
        private async Task BatchTestProxiesAsync()
        {
            if (btnStart == null || btnStop == null || btnClearLog == null || btnSave == null ||
                progressBar1 == null || lblStatus == null)
            {
                AddLog("检测失败：部分控件未初始化", LogLevel.Error);
                return;
            }

            SetControlEnabledSafe(btnStart, false);
            SetControlEnabledSafe(btnStop, true);
            SetControlEnabledSafe(btnClearLog, false);
            SetControlEnabledSafe(btnSave, false);

            _validProxyResults.Clear();
            dgvResults.ResetBindings();
            SetProgressBarMaximumSafe(progressBar1, 100);
            SetProgressBarValueSafe(progressBar1, 0);

            lock (_statLock)
            {
                _testedCount = 0;
                _validCount = 0;
            }
            _lastUiUpdateTime = DateTime.Now;
            ClearLog();

            AddLog("===== 开始代理检测流程 =====", LogLevel.Info);
            AddLog("提示：超时/错误代理将自动过滤，仅显示可用代理", LogLevel.Info);

            try
            {
                string testUrl = await GetAvailableTestUrl().ConfigureAwait(false);

                SetControlTextSafe(lblStatus, "正在获取代理列表...");
                _proxyList = await FetchProxiesAsync().ConfigureAwait(false);

                if (_proxyList == null || _proxyList.Count == 0)
                {
                    string msg = "未获取到有效代理（已过滤80端口），检测终止";
                    SetControlTextSafe(lblStatus, msg);
                    AddLog(msg, LogLevel.Warning);
                    ShowMessageBoxSafe(msg, "提示", MessageBoxIcon.Information);
                    ResetControlsState();
                    return;
                }

                string startMsg = $"开始检测 {_proxyList.Count} 个代理（并发{ThreadCount}线程）| 测试地址：{testUrl}";
                SetControlTextSafe(lblStatus, startMsg);
                AddLog(startMsg, LogLevel.Info);

                SetProgressBarMaximumSafe(progressBar1, _proxyList.Count);
                SetProgressBarValueSafe(progressBar1, 0);

                var semaphore = new SemaphoreSlim(ThreadCount, ThreadCount);
                var tasks = new List<Task>();
                _cancelTokenSource = new CancellationTokenSource();

                foreach (var proxy in _proxyList)
                {
                    if (_cancelTokenSource.Token.IsCancellationRequested)
                        break;

                    await semaphore.WaitAsync(_cancelTokenSource.Token).ConfigureAwait(false);

                    string currentProxy = proxy;
                    tasks.Add(Task.Run(async () =>
                    {
                        try
                        {
                            if (_cancelTokenSource.Token.IsCancellationRequested)
                                return;

                            var proxyResult = await TestProxyAsync(currentProxy, testUrl).ConfigureAwait(false);

                            lock (_statLock)
                            {
                                _testedCount++;
                            }

                            if (proxyResult != null)
                            {
                                SafeUpdateUI(proxyResult);
                            }
                        }
                        catch (Exception ex)
                        {
                            AddLog($"[{currentProxy}] 异常：{ex.Message}", LogLevel.Error);
                        }
                        finally
                        {
                            semaphore.Release();
                        }
                    }, _cancelTokenSource.Token));
                }

                try
                {
                    await Task.WhenAll(tasks).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    AddLog("检测任务已被取消", LogLevel.Warning);
                    ShowMessageBoxSafe("检测已取消", "提示", MessageBoxIcon.Information);
                }

                int finalValidCount = _validCount;
                int finalTestedCount = _testedCount;
                string finishMsg = $"===== 检测完成！共检测 {finalTestedCount} 个 - 可用{finalValidCount}个 =====";
                SetControlTextSafe(lblStatus, finishMsg);
                AddLog(finishMsg, LogLevel.Success);

                if (dgvResults.InvokeRequired)
                {
                    dgvResults.Invoke(new Action(() =>
                    {
                        dgvResults.ResetBindings();
                        dgvResults.Refresh();
                    }));
                }
                else
                {
                    dgvResults.ResetBindings();
                    dgvResults.Refresh();
                }

                if (finalValidCount == 0)
                {
                    AddLog("未检测到可用代理，可能原因：1.代理过期 2.网络限制 3.代理类型不匹配", LogLevel.Warning);
                    ShowMessageBoxSafe("未检测到可用代理", "提示", MessageBoxIcon.Information);
                }
                else
                {
                    AddLog($"可用代理已全部显示在表格中（共{finalValidCount}个，状态为绿色加粗）", LogLevel.Success);
                    ShowMessageBoxSafe($"检测完成！共找到 {finalValidCount} 个可用代理", "成功", MessageBoxIcon.Information);
                }

                ResetControlsState();
            }
            catch (Exception ex)
            {
                AddLog($"检测异常：{ex.Message}", LogLevel.Error);
                ShowMessageBoxSafe(ex.Message, "检测失败");
                SetControlTextSafe(lblStatus, "检测失败");
                ResetControlsState();
            }
            finally
            {
                _cancelTokenSource?.Dispose();
                _cancelTokenSource = null;
            }
        }

        /// <summary>
        /// 重置控件状态（保持不变）
        /// </summary>
        private void ResetControlsState()
        {
            SetControlEnabledSafe(btnStart, true);
            SetControlEnabledSafe(btnStop, false);
            SetControlEnabledSafe(btnClearLog, true);
            SetControlEnabledSafe(btnSave, _validCount > 0);
        }

        #region 按钮点击事件（保持不变）
        private async void btnStart_Click(object sender, EventArgs e)
        {
            if (this.InvokeRequired)
            {
                this.Invoke(new Action(async () => await BatchTestProxiesAsync().ConfigureAwait(false)));
            }
            else
            {
                await BatchTestProxiesAsync().ConfigureAwait(false);
            }
        }

        private void btnStop_Click(object sender, EventArgs e)
        {
            try
            {
                if (_cancelTokenSource != null && !_cancelTokenSource.IsCancellationRequested)
                {
                    _cancelTokenSource.Cancel();
                    AddLog("正在停止检测...", LogLevel.Warning);
                    SetControlTextSafe(lblStatus, "正在停止检测...");
                    SetControlEnabledSafe(btnStop, false);
                }
                else
                {
                    AddLog("无正在执行的检测任务", LogLevel.Info);
                }
            }
            catch (Exception ex)
            {
                ShowMessageBoxSafe($"停止失败：{ex.Message}", "错误");
                AddLog($"停止失败：{ex.Message}", LogLevel.Error);
            }
        }

        private void btnClearLog_Click(object sender, EventArgs e)
        {
            ClearLog();
        }

        private void btnSave_Click(object sender, EventArgs e)
        {
            if (_validProxyResults == null || _validProxyResults.Count == 0)
            {
                ShowMessageBoxSafe("没有可用代理可保存", "提示", MessageBoxIcon.Information);
                AddLog("保存失败：无可用代理", LogLevel.Warning);
                return;
            }

            using (var sfd = new SaveFileDialog())
            {
                sfd.Filter = "文本文件 (*.txt)|*.txt|所有文件 (*.*)|*.*";
                sfd.Title = "保存可用代理";
                sfd.FileName = $"可用代理_{DateTime.Now:yyyyMMddHHmmss}.txt";

                if (sfd.ShowDialog() == DialogResult.OK)
                {
                    try
                    {
                        var content = new StringBuilder();
                        content.AppendLine($"===== 可用代理列表（{DateTime.Now:yyyy-MM-dd HH:mm:ss}）=====");
                        content.AppendLine($"共 {_validProxyResults.Count} 个（按响应时间排序）：");
                        content.AppendLine("原始地址 | 统一格式 | 响应时间(ms) | IP信息（IP-地理位置 运营商）");
                        content.AppendLine("----------------------------------------------------------------");

                        foreach (var proxy in _validProxyResults.Where(r => r.Status == "可用").OrderBy(r => r.ResponseTime))
                        {
                            content.AppendLine($"{proxy.OriginalProxy} | {proxy.Proxy} | {proxy.ResponseTime} | {proxy.IpAddress}");
                        }

                        System.IO.File.WriteAllText(sfd.FileName, content.ToString(), Encoding.UTF8);
                        ShowMessageBoxSafe($"成功保存 {_validProxyResults.Count} 个代理到：{sfd.FileName}", "成功", MessageBoxIcon.Information);
                        AddLog($"保存成功：{sfd.FileName}", LogLevel.Success);
                    }
                    catch (Exception ex)
                    {
                        string errorMsg = $"保存失败：{ex.Message}";
                        ShowMessageBoxSafe(errorMsg, "错误");
                        AddLog(errorMsg, LogLevel.Error);
                    }
                }
                else
                {
                    AddLog("用户取消保存", LogLevel.Info);
                }
            }
        }
        #endregion
    }

    /// <summary>
    /// 代理检测结果实体类（保持不变）
    /// </summary>
    public class ProxyResult
    {
        public string OriginalProxy { get; set; } = "";
        public string Proxy { get; set; } = "";
        public string Ip { get; set; } = "";
        public int Port { get; set; } = 0;
        public int ResponseTime { get; set; } = 0;
        public string Status { get; set; } = "";
        public string IpAddress { get; set; } = "";
    }
}