// 音乐盒的启动更新检测：每次启动拉一次本仓库最新的 GitHub Release，按严格 semver 和本机版本号比大小，
// 有新版就上报游戏的统一更新提醒。和 MusicBoxEntry.cs 是同一个类的另一半（partial）——面板、音频、
// 场景钩子那些逻辑在那边，这里只有更新源、请求重试和版本比较。
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

        // 启动更新检测：面板总开关键 + 更新源地址。
        // 更新源就是本仓库的 GitHub Release：最新 Release 的 tag 名当最新模组版本、Release 页当下载页，
        // 所以发新版只要建一个 Release。tag 一律写**严格 semver**（SemVer 2.0）：
        //   正式版三段数字（1.2.2、1.3.0）；内测按预发布标记分三档（1.3.0-alpha.1 / -beta.1 / -rc.1）；
        //   tag 只写版本本身：构建号已经取消，而 semver 的「+构建元数据」按规范本来就不参与比较。
        // GitHub 上勾了 pre-release 的 Release 不会被 /releases/latest 返回（官方文档：最新的
        // 非草稿、非预发布那条），所以内测包天然不会推给玩家，玩家只会收到正式版的提醒。
        // 仓库里不再有 update.txt。
        // 请求走宿主自己的 NativeHttpRequest 节点：游戏导出裁剪了 BCL，泛型 HttpClient 的
        // 无参构造在运行时是「Method not found」，只有宿主自己用过的 API 才活得下来。
        // GitHub API 不给 User-Agent 会直接 403，这个头是必带的。
        private const string UpdateCheckSetting = "enableUpdateCheck";
        private const string UpdateFeedUrl = "https://api.github.com/repos/NaiyxYin/PVZHE_MusicBox/releases/latest";
        private static readonly string[] UpdateFeedHeaders = { "User-Agent: PVZHE_MusicBox" };

        // 更新检测的请求节点与重试状态；回调由宿主节点投递到主线程，全程不碰后台线程。
        private NativeHttpRequest _updateRequest;
        private int _updateRetriesLeft = 1;
        private readonly List<string> _updateFailures = new();

        // ---------- 启动更新检测 ----------

        // 每次启动只查一次：拉本仓库最新的 GitHub Release（tag 名当最新版、Release 页当下载页），
        // 与本机模组的版本按严格 semver 相比；tag 只写版本本身，构建元数据按规范不参与比较。
        // 任何失败都只写一条日志、绝不弹窗；发现新版一律上报游戏的统一更新提醒，
        // 老游戏没有这套通道时也只记日志说明，不自带弹窗。
        private void StartUpdateCheck()
        {
            if (_context == null)
                return;
            bool enabled = true;
            try
            {
                enabled = _settings == null || _settings.Get<bool>(UpdateCheckSetting);
            }
            catch (Exception exception)
            {
                _context.Warn("读取更新检测开关失败，按开启处理：" + exception.GetBaseException().Message);
            }
            if (!enabled)
            {
                _context.Log("更新检测已在配置里关闭，本次启动不检查。");
                return;
            }
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

        // 失败 3 秒后重试一轮，再失败就静默记日志收尾。
        private void HandleUpdateResult(long resultCode, long responseCode, byte[] body)
        {
            if (_updateRequest == null)
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
                        _context?.Log($"更新检测：发现新版本 v{latest}（当前 v{current}），已交给游戏的统一更新提醒。");
                    else
                        _context?.Log($"更新检测：发现新版本 v{latest}（当前 v{current}），但当前游戏没有统一更新提醒，本模组不自行弹窗。");
                }
                else
                {
                    _context?.Log($"更新检测：当前 v{current} 已是最新（服务器 v{latest}）。");
                }
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
                var timer = tree.CreateTimer(3.0);
                timer.Timeout += () =>
                {
                    if (_updateRequest != null)
                        _updateRequest.Request(UpdateFeedUrl, UpdateFeedHeaders);
                };
                return;
            }
            FinishUpdateRequest();
            _context?.Log("更新检测未完成（不影响游戏）：" + string.Join("；", _updateFailures));
        }

        private void FinishUpdateRequest()
        {
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
            ParseSemVer(left, out int[] a, out string[] aPre);
            ParseSemVer(right, out int[] b, out string[] bPre);
            // 缺的那段按 0 补：tag 写 1.2 与本机 1.2.0 视为同一版；
            // 换成 semver 之前发出去的四位老包（1.2.2.6）也照样读，多出来那段留在数字段里按位置比。
            for (int index = 0; index < Math.Max(a.Length, b.Length); index++)
            {
                int x = index < a.Length ? a[index] : 0;
                int y = index < b.Length ? b[index] : 0;
                if (x != y)
                    return x.CompareTo(y);
            }
            return ComparePrerelease(aPre, bPre);
        }

        // 拆一个版本号：'+' 之后整段丢弃，第一个 '-' 之后是预发布标记，剩下的是点分的数字段。
        // 逐字符扫描而不是用 Split 重载：三字符的 Split 会被编译器绑到 String.Split(ReadOnlySpan<char>)，
        // 而那个重载在裁剪后的游戏运行时里不存在。
        private static void ParseSemVer(string version, out int[] numeric, out string[] prerelease)
        {
            string text = (version ?? "").Trim().TrimStart('v', 'V');
            int plus = text.IndexOf('+');
            if (plus >= 0)
                text = text.Substring(0, plus);
            int hyphen = text.IndexOf('-');
            string labels = hyphen < 0 ? "" : text.Substring(hyphen + 1);
            if (hyphen >= 0)
                text = text.Substring(0, hyphen);

            var numbers = new List<int>();
            foreach (string token in CutAtDots(text))
            {
                if (!TryReadNumber(token, out int value))
                    break;
                numbers.Add(value);
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
                bool leftNumber = TryReadNumber(left[index], out int x);
                bool rightNumber = TryReadNumber(right[index], out int y);
                int result = leftNumber && rightNumber
                    ? x.CompareTo(y)
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
            return plus < 0 || IsIdentifierList(build);
        }

        // 主/次/补丁那一段：全数字，且不许「01」这种前导零写法。
        private static bool IsVersionNumber(string text)
        {
            return text.Length > 0
                && !(text.Length > 1 && text[0] == '0')
                && text.All(char.IsAsciiDigit);
        }

        // 预发布 / 构建元数据的点分标识符列表：每段非空、只用 ASCII 字母数字和 '-'，纯数字段同样不许前导零。
        private static bool IsIdentifierList(string text)
        {
            return CutAtDots(text).All(item => item.Length > 0 && item.All(IsIdentifierChar)
                && !(item.All(char.IsAsciiDigit) && item.Length > 1 && item[0] == '0'));
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

        private static bool TryReadNumber(string token, out int value)
        {
            value = 0;
            if (token.Length == 0)
                return false;
            for (int index = 0; index < token.Length; index++)
            {
                if (!char.IsAsciiDigit(token[index]))
                    return false;
                long next = value * 10L + (token[index] - '0');
                if (next > int.MaxValue)
                    return false;
                value = (int)next;
            }
            return true;
        }

        // 桥接游戏的统一更新提醒（宿主的 XWModRuntimeContext.Updates）：多个 Mod 的更新由游戏
        // 汇总成一份更新列表、一次弹窗，免得各弹各的。当前流传的游戏客户端还没有这套 API，
        // 也不该让模组程序集绑死宿主 DLL 的新旧，所以整段走运行时反射——通道在就上报，
        // 缺就返回 false，由调用方只记一条日志（发现新版一律以统一接口为准，不自带弹窗）。
        // 上报在主线程直接调用即可，宿主自己排队受理；回执是异步的，这里不等它。
        private bool TryReportToHostUpdates(string latest, string current, string url)
        {
            if (_context == null)
                return false;
            try
            {
                System.Reflection.PropertyInfo updatesProperty = _context.GetType().GetProperty("Updates");
                if (updatesProperty == null)
                    return false;
                object service = updatesProperty.GetValue(_context);
                if (service == null)
                    return false;
                System.Reflection.MethodInfo reportMethod = service.GetType().GetMethod("ReportAvailableAsync");
                if (reportMethod == null)
                    return false;
                System.Reflection.ParameterInfo[] parameters = reportMethod.GetParameters();
                if (parameters.Length != 1)
                    return false;
                object notice = Activator.CreateInstance(
                    parameters[0].ParameterType, latest, $"当前 v{current}，可前往下载页更新。", url);
                if (notice == null)
                    return false;
                reportMethod.Invoke(service, new object[] { notice });
                return true;
            }
            catch (Exception exception)
            {
                _context?.Warn("统一更新提醒上报失败（不影响游戏）：" + exception.GetBaseException().Message);
                return false;
            }
        }
    }
}

