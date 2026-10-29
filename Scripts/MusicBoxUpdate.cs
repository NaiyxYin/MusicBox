// 音乐盒的启动更新检测：面板「关于」里那个渠道下拉决定走哪条路（关闭 / 官网 / Github）。
// 只有 Github 那条是本模组自己联网，结果上报游戏的统一更新提醒；官网那条请求由游戏自己发、提醒由游戏自己出，
// 而「关闭」会把包里的 postId 一起清掉，让游戏也别去问官网。和 MusicBoxEntry.cs 是同一个类的另一半（partial）——
// 面板、音频、场景钩子那些逻辑在那边，这里只有渠道分派、请求重试和版本比较。
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Godot;

namespace MusicBoxMod
{
    public sealed partial class MusicBoxEntry
    {

        // 渠道下拉的三个值。面板项 key 是 updateChannel，改动要下次启动才生效（applyMode=restart）。
        //   off       模组不联网；同时把包里的官网投稿 ID 清掉，让游戏的官网检查也扫不到本模组。
        //             （清空要重启一次才落到包上：游戏这次读的是已在内存里的清单。）
        //   official  模组一条 GitHub 请求都不发，更新结果全部由游戏的官网检查给出。这一档模组不替游戏发请求，
        //             也不自己上报——本模组成功上报过一次（有没有更新都算），游戏就会忽略这个 Mod 的官网结果。
        //   github    模组自己查本仓库最新的 GitHub Release：tag 名当最新版、html_url 当下载页。
        //             这是唯一由模组发起的网络请求，而且只连本仓库。
        // tag 一律要求**严格 semver**（SemVer 2.0）：正式版三段数字（1.2.2、1.3.0），
        // 内测按预发布标记分三档（1.3.0-alpha.1 / -beta.1 / -rc.1），构建号已经取消。
        // 请求走宿主自己的 NativeHttpRequest 节点：游戏导出裁剪了 BCL，泛型 HttpClient 的
        // 无参构造在运行时是「Method not found」，只有宿主自己用过的 API 才活得下来。
        // GitHub API 不给 User-Agent 会直接 403，这个头是必带的。
        private const string UpdateChannelSetting = "updateChannel";
        private const string ChannelOff = "off";
        private const string ChannelOfficial = "official";
        private const string ChannelGitHub = "github";
        // 本模组在官网的投稿 ID，必须和 mod.json 里那个 postId 一致（闸门会核对）。
        // 「关闭」渠道要把它从安装包里清掉、另两档要还原，还原时没地方可查，所以只能在这里也留一份。
        private const string OfficialPostId = "2";
        private const string UpdateFeedUrl = "https://api.github.com/repos/NaiyxYin/PVZHE_MusicBox/releases/latest";
        private static readonly string[] UpdateFeedHeaders = { "User-Agent: PVZHE_MusicBox" };

        // 更新检测的请求节点与重试状态；回调由宿主节点投递到主线程，全程不碰后台线程。
        private NativeHttpRequest _updateRequest;
        private SceneTreeTimer _updateRetryTimer;
        private Action _updateRetryTimeout;
        private int _updateRetriesLeft = 1;
        private readonly List<string> _updateFailures = new();
        // 本次启动走的是哪条渠道，日志里要写清这条结果是谁给的。
        private string _updateChannel = ChannelGitHub;

        // ---------- 启动更新检测 ----------

        // 每次启动按面板选的渠道走一次：只有 github 会联网，另外两档各只写一条日志说明。
        // 任何失败都只写一条日志、绝不弹窗；发现新版一律上报游戏的统一更新提醒，
        // 老游戏没有这套通道时也只记一条日志说明。
        private void StartUpdateCheck()
        {
            if (!_running || _context == null)
                return;
            _updateRetriesLeft = 1;
            _updateFailures.Clear();
            _updateChannel = ReadUpdateChannel();
            switch (_updateChannel)
            {
                case ChannelOff:
                    _context.Log("更新检测已关闭（面板渠道=关闭）：本模组不联网，包里的官网投稿 ID 也已清掉，"
                                 + "游戏的官网检查下次启动起不再包含本模组。");
                    return;
                case ChannelOfficial:
                    _context.Log("更新检测交给游戏的官网检查（面板渠道=官网）：本模组不联网、不自行上报。");
                    return;
                default:
                    StartGitHubCheck();
                    return;
            }
        }

        // 渠道值只认这三个：旧存档里残留的开关值、或别的 Mod 改写过配置时，宁可回到默认的 Github 也不要猜。
        private static string NormalizeChannel(string value)
        {
            string text = (value ?? "").Trim();
            return text is ChannelOff or ChannelOfficial or ChannelGitHub ? text : ChannelGitHub;
        }

        // 面板渠道 → 包里的 postId 该写什么。读不到设置（面板还没落盘之类）时按声明的默认值 Github 处理，
        // 也就是保持绑定，不至于因为一次读取失败就把模组从官网检查里摘出去。
        private string ReadUpdateChannel()
        {
            try
            {
                return NormalizeChannel(_settings == null ? null : _settings.Get<string>(UpdateChannelSetting));
            }
            catch (Exception exception)
            {
                WarnOnce("更新渠道", "读取更新检测渠道失败，按 Github 处理：" + exception.GetBaseException().Message);
                return ChannelGitHub;
            }
        }

        private string DesiredPostId() => ReadUpdateChannel() == ChannelOff ? "" : OfficialPostId;

        private void StartGitHubCheck()
        {
            if (Engine.GetMainLoop() is not SceneTree tree)
                return;
            _updateRequest = new NativeHttpRequest();
            _updateRequest.RequestCompleted += OnUpdateRequestCompleted;
            tree.Root.AddChild(_updateRequest);
            _updateRequest.Request(UpdateFeedUrl, UpdateFeedHeaders);
        }




        // 回调已在主线程；任何意外（例如再撞上裁剪问题）都降级为一条日志，不惊扰玩家。
        private void OnUpdateRequestCompleted(long resultCode, long responseCode, string[] headers, byte[] body)
        {
            try
            {
                HandleUpdateResult(resultCode, responseCode, body);
            }
            catch (Exception exception)
            {
                FinishUpdateRequest();
                _context?.Warn("更新检测处理失败（不影响游戏）：" + exception.GetBaseException().Message);
            }
        }

        // github 渠道的收尾：读到合法的 Release 就按严格 semver 比大小，有新版才上报；
        // 失败 3 秒后重试一轮，再失败就静默记日志收尾。另两条渠道根本不走这里。
        private void HandleUpdateResult(long resultCode, long responseCode, byte[] body)
        {
            if (!_running || _updateRequest == null)
                return;
            string problem = null;
            if (resultCode == (long)NativeHttpRequest.Result.Success && responseCode == 200
                && TryReadReleaseRecord(Encoding.UTF8.GetString(body ?? Array.Empty<byte>()),
                    out string latest, out string url, out problem))
            {
                FinishUpdateRequest();
                string current = (_context?.ModVersion ?? "").Trim();
                if (CompareVersions(latest, current) > 0)
                {
                    if (TryReportToHostUpdates(latest, current, url))
                        _context?.Log($"更新检测：发现新版本 v{latest}（当前 v{current}），已提交游戏统一更新服务（显示由游戏决定）。");
                    else
                        _context?.Log($"更新检测：发现新版本 v{latest}（当前 v{current}），但当前游戏没有统一更新提醒，本模组不自行弹窗。");
                }
                else
                    ReportHostUpToDate();
                return;
            }
            problem ??= resultCode != (long)NativeHttpRequest.Result.Success
                ? $"请求失败（结果 {resultCode}）"
                : responseCode == 404
                    ? "还没建正式版 Release（草稿和勾了预发布的都不算）"
                    : $"HTTP 状态 {responseCode}";
            _updateFailures.Add(problem);
            if (_updateRetriesLeft-- > 0 && Engine.GetMainLoop() is SceneTree tree)
            {
                int generation = _generation;
                _updateRetryTimer = tree.CreateTimer(3.0);
                _updateRetryTimeout = () =>
                {
                    if (!_running || generation != _generation)
                        return;
                    _updateRetryTimer = null;
                    _updateRetryTimeout = null;
                    try
                    {
                        if (_updateRequest != null)
                            _updateRequest.Request(UpdateFeedUrl, UpdateFeedHeaders);
                    }
                    catch (Exception exception)
                    {
                        FinishUpdateRequest();
                        _context?.Warn("更新检测重试失败：" + exception.GetBaseException().Message);
                    }
                };
                _updateRetryTimer.Timeout += _updateRetryTimeout;
                return;
            }
            FinishUpdateRequest();
            _context?.Log("更新检测未完成（不影响游戏）：" + string.Join("；", _updateFailures));
        }

        private void FinishUpdateRequest()
        {
            if (GodotObject.IsInstanceValid(_updateRetryTimer) && _updateRetryTimeout != null)
                _updateRetryTimer.Timeout -= _updateRetryTimeout;
            _updateRetryTimer = null;
            _updateRetryTimeout = null;
            if (_updateRequest == null)
                return;
            _updateRequest.RequestCompleted -= OnUpdateRequestCompleted;
            if (GodotObject.IsInstanceValid(_updateRequest))
                _updateRequest.QueueFree();
            _updateRequest = null;
        }

        // 更新源就是仓库的 GitHub Release：/releases/latest 那段 JSON 里，tag_name 当最新版、html_url 当下载页。
        // tag 前缀的 v 只当装饰去掉，其余部分必须是严格 semver，否则宁可当成「更新源没配好」记一条日志——
        // 比不了大小就不要报版本号，免得把玩家引到一个说不清是新版还是旧版的 Release 上。
        // 仓库还没有 Release 时这个地址是 404，由调用方记一条日志，跟连不上一样不打扰玩家。
        private static bool TryReadReleaseRecord(string body, out string latest, out string url, out string problem)
        {
            latest = "";
            url = "";
            JsonNode record;
            try
            {
                record = JsonNode.Parse(body ?? "");
            }
            catch (Exception exception)
            {
                problem = "更新源返回的不是 JSON：" + exception.GetBaseException().Message;
                return false;
            }
            if (record == null || record.GetValueKind() != JsonValueKind.Object)
            {
                problem = "更新源返回的不是一条 Release 记录";
                return false;
            }
            latest = (TextOf(record["tag_name"]) ?? "").Trim().TrimStart('v', 'V');
            url = (TextOf(record["html_url"]) ?? "").Trim();
            if (string.IsNullOrEmpty(latest))
            {
                problem = "最新的 Release 没有 tag_name（建 Release 时要把版本号打成 tag）";
                return false;
            }
            if (!IsStrictSemVer(latest))
            {
                problem = "最新的 Release 的 tag 不是严格 semver（正式版写 1.2.3，内测写 1.2.3-beta.1）：" + latest;
                return false;
            }
            if (string.IsNullOrEmpty(url))
            {
                problem = "最新的 Release 没有 html_url";
                return false;
            }
            // 下载页只会用于打开浏览器（宿主通道也按 http/https 验收）：本地路径、其他协议一律不认。
            if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                && !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                problem = "Release 页地址不是网页链接：" + url;
                return false;
            }
            problem = null;
            return true;
        }

        // 版本比较照严格 semver（SemVer 2.0）：主.次.补丁逐段比数字，「-预发布」排在同版本号的正式版之前
        // （1.3.0-alpha.1 < 1.3.0-beta.2 < 1.3.0-beta.10 < 1.3.0-rc.1 < 1.3.0），
        // 「+构建元数据」按规范整个丢掉不比——所以同一个版的第 1 次和第 5 次构建都不会互相提示有更新。
        private static int CompareVersions(string left, string right)
        {
            ParseSemVer(left, out string[] a, out string[] aPre);
            ParseSemVer(right, out string[] b, out string[] bPre);
            // 缺的那段按 0 补：tag 写 1.2 与本机 1.2.0 视为同一版；
            // 换成 semver 之前发出去的四位老包（1.2.2.6）也照样读，多出来那段留在数字段里按位置比。
            for (int index = 0; index < Math.Max(a.Length, b.Length); index++)
            {
                string x = index < a.Length ? a[index] : "0";
                string y = index < b.Length ? b[index] : "0";
                int result = CompareNumericIdentifiers(x, y);
                if (result != 0)
                    return result;
            }
            return ComparePrerelease(aPre, bPre);
        }

        // 拆一个版本号：'+' 之后整段丢弃，第一个 '-' 之后是预发布标记，剩下的是点分的数字段。
        // 逐字符扫描而不是用 Split 重载：三字符的 Split 会被编译器绑到 String.Split(ReadOnlySpan<char>)，
        // 而那个重载在裁剪后的游戏运行时里不存在。
        private static void ParseSemVer(string version, out string[] numeric, out string[] prerelease)
        {
            string text = (version ?? "").Trim().TrimStart('v', 'V');
            int plus = text.IndexOf('+');
            if (plus >= 0)
                text = text.Substring(0, plus);
            int hyphen = text.IndexOf('-');
            string labels = hyphen < 0 ? "" : text.Substring(hyphen + 1);
            if (hyphen >= 0)
                text = text.Substring(0, hyphen);

            var numbers = new List<string>();
            foreach (string token in CutAtDots(text))
            {
                if (!IsNumericIdentifier(token))
                    break;
                numbers.Add(token);
            }
            numeric = numbers.ToArray();
            prerelease = labels.Length == 0 ? Array.Empty<string>() : CutAtDots(labels);
        }

        // 预发布标记的比较照 semver 规范：没有标记的正式版最大；点分标识符逐个比，
        // 纯数字标识符按数值比（所以 beta.2 小于 beta.10）且永远小于含字母的标识符；前面全相等时标识符少的在前。
        private static int ComparePrerelease(string[] left, string[] right)
        {
            if (left.Length == 0 || right.Length == 0)
                return left.Length == right.Length ? 0 : left.Length == 0 ? 1 : -1;
            for (int index = 0; index < Math.Min(left.Length, right.Length); index++)
            {
                bool leftNumber = IsNumericIdentifier(left[index]);
                bool rightNumber = IsNumericIdentifier(right[index]);
                int result = leftNumber && rightNumber
                    ? CompareNumericIdentifiers(left[index], right[index])
                    : leftNumber != rightNumber
                        ? (leftNumber ? -1 : 1)
                        : string.CompareOrdinal(left[index], right[index]);
                if (result != 0)
                    return result;
            }
            return left.Length.CompareTo(right.Length);
        }

        // 更新源的 tag 必须是严格 semver：三段十进制数字（多段时不许前导零），可选的 -预发布 与 +构建元数据，
        // 标识符只许 [0-9A-Za-z-] 且不许有空段。这套规则宿主依赖比较用的正则（XWModLoadPlanVersion）完全一致，
        // 所以将来别的 Mod 按版本区间依赖音乐盒时，读到的也是同一个答案。
        private static bool IsStrictSemVer(string version)
        {
            string text = (version ?? "").Trim().TrimStart('v', 'V');
            if (text.Length == 0 || text.Length > 256)
                return false;
            int plus = text.IndexOf('+');
            string build = plus < 0 ? "" : text.Substring(plus + 1);
            if (plus >= 0)
                text = text.Substring(0, plus);
            int hyphen = text.IndexOf('-');
            string prerelease = hyphen < 0 ? "" : text.Substring(hyphen + 1);
            if (hyphen >= 0)
                text = text.Substring(0, hyphen);

            string[] core = CutAtDots(text);
            if (core.Length != 3 || !core.All(IsVersionNumber))
                return false;
            // 按分隔符在不在来验，而不是按内容长不长：`1.2.3-`、`1.2.3+` 这种空尾巴同样不规范。
            if (hyphen >= 0 && !IsIdentifierList(prerelease))
                return false;
            return plus < 0 || IsIdentifierList(build, allowLeadingZeros: true);
        }

        // 主/次/补丁那一段：全数字，且不许「01」这种前导零写法。
        private static bool IsVersionNumber(string text)
        {
            return text.Length > 0
                && !(text.Length > 1 && text[0] == '0')
                && text.All(char.IsAsciiDigit);
        }

        // 预发布数字段禁止前导零，构建元数据允许；两者都只接受非空 ASCII 标识符。
        private static bool IsIdentifierList(string text, bool allowLeadingZeros = false)
        {
            return CutAtDots(text).All(item => item.Length > 0 && item.All(IsIdentifierChar)
                && (allowLeadingZeros || !(item.All(char.IsAsciiDigit) && item.Length > 1 && item[0] == '0')));
        }

        private static bool IsIdentifierChar(char value) =>
            (value >= '0' && value <= '9') || (value >= 'a' && value <= 'z') || (value >= 'A' && value <= 'Z') || value == '-';

        // 按 '.' 切段（不用 Split，理由见 ParseSemVer）。
        private static string[] CutAtDots(string text)
        {
            var items = new List<string>();
            int start = 0;
            while (true)
            {
                int dot = text.IndexOf('.', start);
                if (dot < 0)
                {
                    items.Add(text.Substring(start));
                    break;
                }
                items.Add(text.Substring(start, dot - start));
                start = dot + 1;
            }
            return items.ToArray();
        }

        private static bool IsNumericIdentifier(string text) =>
            text.Length > 0 && text.All(char.IsAsciiDigit);

        // SemVer 数字没有 Int32 上限；去掉兼容旧版本的前导零后按长度和字典序比较。
        private static int CompareNumericIdentifiers(string left, string right)
        {
            string a = left.TrimStart('0');
            string b = right.TrimStart('0');
            int length = a.Length.CompareTo(b.Length);
            return length != 0 ? length : string.CompareOrdinal(a, b);
        }

        // 桥接游戏的统一更新提醒：新宿主走公开 Updates 服务；旧发行仅此兼容层使用反射。
        // 按 XWModUpdateService 契约在 Initialize 主线程取得服务，不在网络回调首次创建。
        private object _hostUpdates;
        private System.Reflection.MethodInfo _reportAvailable;
        private System.Reflection.MethodInfo _reportUpToDate;

        private void PrepareHostUpdates()
        {
            _hostUpdates = null;
            _reportAvailable = null;
            _reportUpToDate = null;
            try
            {
                var property = _context.GetType().GetProperty("Updates");
                if (property == null) return;
                _hostUpdates = property.GetValue(_context);
                _reportAvailable = _hostUpdates?.GetType().GetMethod("ReportAvailableAsync");
                _reportUpToDate = _hostUpdates?.GetType().GetMethod("ReportUpToDateAsync");
            }
            catch (Exception exception)
            {
                WarnOnce("更新服务初始化", "统一更新服务不可用：" + exception.GetBaseException().Message);
            }
        }

        private bool TryReportToHostUpdates(string latest, string current, string url)
        {
            if (!_running || _hostUpdates == null || _reportAvailable == null)
                return false;
            try
            {
                var parameters = _reportAvailable.GetParameters();
                if (parameters.Length != 1) return false;
                object notice = Activator.CreateInstance(parameters[0].ParameterType,
                    latest, $"当前 v{current}，可前往下载页更新。", url);
                return ObserveHostUpdateReceipt(_reportAvailable.Invoke(_hostUpdates, new[] { notice }));
            }
            catch (Exception exception)
            {
                WarnOnce("更新上报", "统一更新提醒上报失败：" + exception.GetBaseException().Message);
                return false;
            }
        }

        private void ReportHostUpToDate()
        {
            // 仅用于请求成功且版本比较确认无更新；关闭、超时、格式错误不允许冒报“已是最新”。
            if (!_running || _hostUpdates == null || _reportUpToDate == null) return;
            try { ObserveHostUpdateReceipt(_reportUpToDate.Invoke(_hostUpdates, null)); }
            catch (Exception exception)
            {
                WarnOnce("更新无新版上报", "更新状态上报失败：" + exception.GetBaseException().Message);
            }
        }

        private bool ObserveHostUpdateReceipt(object receipt)
        {
            if (receipt is not System.Threading.Tasks.Task task) return false;
            ObserveHostUpdateReceiptAsync(task, _generation);
            return true; // 只表示已提交，不能声称弹窗已经显示。
        }

        private async void ObserveHostUpdateReceiptAsync(System.Threading.Tasks.Task task, int generation)
        {
            try
            {
                // 从 Godot 主线程进入并保留其同步上下文，完成后才读取受理回执。
                await task;
                if (!_running || generation != _generation) return;
                object result = task.GetType().GetProperty("Result")?.GetValue(task);
                if (result?.GetType().GetProperty("Succeeded")?.GetValue(result) is not true)
                {
                    string diagnostic = result?.GetType().GetProperty("Diagnostic")?.GetValue(result) as string;
                    WarnOnce("更新回执", "统一更新服务未受理：" + (diagnostic ?? "没有成功回执"));
                }
            }
            catch (Exception exception)
            {
                if (_running && generation == _generation)
                    WarnOnce("更新回执", "统一更新回执失败：" + exception.GetBaseException().Message);
            }
        }
    }
}

