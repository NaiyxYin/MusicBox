"""对实际源码中的纯托管逻辑做回归；不启动 Godot，不修改游戏存档。"""
import json
import os
import pathlib
import subprocess
import sys
import uuid
import zipfile

proj = pathlib.Path(__file__).resolve().parents[1]
source = (proj / "Scripts/MusicBoxEntry.cs").read_text(encoding="utf-8")
player_source = (proj / "Scripts/MusicBoxPlayer.cs").read_text(encoding="utf-8")
work = proj / ".build/TEMP/runtime-regression"
work.mkdir(parents=True, exist_ok=True)


def region(start, end):
    a = source.index(start)
    return source[a:source.index(end, a)]


body = "\n".join([
    region("        private static readonly (string Setting, string Screen)[] ScreenSettings", "        private enum ScreenMode"),
    region("        private static IEnumerable<string> EnumerateFiles", "        // 能不能被游戏读出来"),
    region("        private static string MusicStamp()", "        // ---------- 面板选项 -> 音频 ----------"),
    region("        private bool ReadStoredValues", "        private static string StorageHash"),
    region("        private static bool SameOptionValues", "        private string FindOwnPackage"),
    region("        private static string DetectAudioExtension", "        // FLAC 的头"),
    region("        private static string DescribeFlacRejection", "        // 同一首歌的同一句提示"),
])
lookup_start = player_source.index("        private static int FindTrackIndex")
lookup_end = player_source.index("        private sealed partial class MenuPlayer", lookup_start)
body += player_source[lookup_start:lookup_end]
program = r'''
using System;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

static class ProjectSettings
{
    public static string Root;
    public static string GlobalizePath(string path) => Root;
}
class Regression
{
    const string BgmRootVirtualPath = "unused", OffOption = "off";
    const string MenuGroupSwitch = "enableMenuGroup", LevelGroupSwitch = "enableLevelGroup";
    const long MaxFlacDecodedPcmBytes = 128L * 1024 * 1024;
    static readonly string[] AudioExtensions = { ".wav", ".ogg", ".mp3", ".flac" };
    readonly Dictionary<string, string> _panelScalar = new(StringComparer.Ordinal);
    readonly Dictionary<string, bool> _panelBool = new(StringComparer.Ordinal);
    sealed class FakeContext { public string ModId = "musicbox"; }
    readonly FakeContext _context = new();
    void WarnOnce(string identity, string message) { }
__BODY__
    static void Require(bool condition) { if (!condition) throw new Exception("assertion failed"); }
    static int Main(string[] args)
    {
        int bad = 0, count = 0;
        void Test(string name, Action check)
        {
            count++;
            try { check(); }
            catch (Exception e) { bad++; Console.WriteLine("FAIL " + name + ": " + e.Message); }
        }
        var subject = new Regression();
        string root = Path.Combine(args[0], "fixtures-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string config = Path.Combine(root, "settings.json");
        void Read(string json)
        {
            var record = JsonNode.Parse(json)!.AsObject();
            record["version"] = 1; record["modId"] = "musicbox";
            File.WriteAllText(config, record.ToJsonString()); Require(subject.ReadStoredValues(config));
        }
        Test("valid-config", () => {
            Read("{\"values\":{\"menuMainMenu\":\"song\",\"enableMenuGroup\":true}}");
            Require(subject._panelScalar["menuMainMenu"] == "song" && subject._panelBool[MenuGroupSwitch]);
        });
        Test("broken-json-keeps-state", () => {
            File.WriteAllText(config, "{");
            Require(!subject.ReadStoredValues(config));
            Require(subject._panelScalar["menuMainMenu"] == "song" && subject._panelBool[MenuGroupSwitch]);
        });
        Test("invalid-root-keeps-state", () => {
            File.WriteAllText(config, "[]");
            Require(!subject.ReadStoredValues(config) && subject._panelScalar["menuMainMenu"] == "song");
        });
        Test("invalid-values-keeps-state", () => {
            File.WriteAllText(config, "{\"values\":[]}");
            Require(!subject.ReadStoredValues(config) && subject._panelScalar["menuMainMenu"] == "song");
        });
        Test("wrong-owner-keeps-state", () => {
            File.WriteAllText(config, "{\"version\":1,\"modId\":\"other\",\"values\":{}}");
            Require(!subject.ReadStoredValues(config) && subject._panelScalar["menuMainMenu"] == "song");
        });
        Test("unknown-off-not-a-valid-option", () => Require(!subject.IsKnownOption("unknown", "off")));
        Test("false-and-trim", () => {
            Read("{\"values\":{\"menuMainMenu\":\" new song \",\"enableMenuGroup\":false}}");
            Require(subject._panelScalar["menuMainMenu"] == "new song" && !subject._panelBool[MenuGroupSwitch]);
        });
        Test("removed-values-clear-old-copy", () => {
            Read("{\"values\":{\"unknown\":\"song\"}}");
            Require(subject._panelScalar.Count == 0 && subject._panelBool.Count == 0);
        });
        ProjectSettings.Root = root;
        string song = Path.Combine(root, "song.mp3");
        File.WriteAllBytes(song, new byte[] { 1 });
        DateTime stamp = File.GetLastWriteTimeUtc(song);
        Test("fingerprint-includes-size", () => {
            string before = MusicStamp();
            File.WriteAllBytes(song, new byte[] { 1, 2 }); File.SetLastWriteTimeUtc(song, stamp);
            Require(before != MusicStamp());
        });
        Test("fingerprint-includes-relative-location", () => {
            string before = MusicStamp();
            string sub = Path.Combine(root, "sub"); Directory.CreateDirectory(sub);
            File.Move(song, Path.Combine(sub, "song.mp3"));
            Require(before != MusicStamp());
        });
        Test("fingerprint-stable-and-ignores-nonaudio", () => {
            string before = MusicStamp(); File.WriteAllText(Path.Combine(root, "notes.txt"), "ignored");
            Require(before == MusicStamp());
        });
        Test("labels-are-part-of-options", () => {
            var a = JsonNode.Parse("[{\"value\":\"song\",\"label\":\"old\"}]")!.AsArray();
            var b = JsonNode.Parse("[{\"value\":\"song\",\"label\":\"new\"}]")!.AsArray();
            Require(!SameOptionValues(a, b) && SameOptionValues(a, a));
        });
        Test("sniff-mislabeled-flac", () => Require(DetectAudioExtension(Encoding.ASCII.GetBytes("fLaC"), 4, ".mp3") == ".flac"));
        Test("sniff-short-header", () => Require(DetectAudioExtension(new byte[] { 0xff }, 1, ".wav") == ".wav"));
        Test("reject-incomplete-flac", () => Require(DescribeFlacRejection(new byte[64], 8) != null));
        Test("track-lookup-empty", () => Require(FindTrackIndex(Array.Empty<string>(), "x") == -1));
        Test("track-lookup-missing", () => Require(FindTrackIndex(new[] { "one", "two" }, "missing") == -1));
        Test("track-lookup-first", () => Require(FindTrackIndex(new[] { "one", "two" }, "one") == 0));
        Test("track-lookup-last", () => Require(FindTrackIndex(new[] { "one", "two" }, "two") == 1));
        Test("track-lookup-null-selection", () => Require(FindTrackIndex(new[] { "one" }, null) == -1));
        Test("player-visibility-false", () => { Read("{\"values\":{\"showMenuPlayer\":false}}"); Require(subject._panelBool["showMenuPlayer"] == false); });
        Test("player-visibility-true", () => { Read("{\"values\":{\"showMenuPlayer\":true}}"); Require(subject._panelBool["showMenuPlayer"]); });
        Console.WriteLine($"Managed regression: {count - bad}/{count} PASS");
        return bad == 0 ? 0 : 1;
    }
}
'''.replace("__BODY__", body)
(work / "Program.cs").write_text(program, encoding="utf-8")
(work / "Regression.csproj").write_text(
    '<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType>'
    '<TargetFramework>net9.0</TargetFramework><Nullable>disable</Nullable><EnableDefaultCompileItems>false</EnableDefaultCompileItems>'
    '</PropertyGroup><ItemGroup><Compile Include="Program.cs" /></ItemGroup></Project>', encoding="utf-8")
env = dict(os.environ)
env.update(DOTNET_CLI_HOME=str(proj / ".build/tmp"), NUGET_PACKAGES=str(proj / ".build/tmp/nuget"),
           TEMP=str(proj / ".build/TEMP"), TMP=str(proj / ".build/TEMP"))
subprocess.run(["dotnet", "run", "--project", str(work), "-c", "Release",
                "-p:UseSharedCompilation=false", "--", str(work)], env=env, check=True)

# 在隔离的项目副本里验证退出码，不破坏真实发布包。
fixture = work / ("package-" + uuid.uuid4().hex)
for folder in (".build/TEMP", "Scripts", "dist"):
    (fixture / folder).mkdir(parents=True, exist_ok=True)
verify = (proj / ".build/verify.py").read_text(encoding="utf-8")
(fixture / ".build/verify.py").write_text(verify, encoding="utf-8")
for name in ("mod.json", "changelog.txt", "Scripts/MusicBoxEntry.cs", "Scripts/MusicBoxUpdate.cs", "Scripts/MusicBoxPlayer.cs"):
    (fixture / name).write_bytes((proj / name).read_bytes())
manifest = json.loads((proj / "mod.json").read_text(encoding="utf-8"))
pkg = fixture / "dist" / (manifest["id"] + "-" + manifest["version"] + ".pmod")
dll = (proj / ".build/bin/Release/ModAssembly.dll").read_bytes()


def package(record):
    with zipfile.ZipFile(pkg, "w") as z:
        z.writestr("mod.json", json.dumps(record, ensure_ascii=False))
        z.writestr("Runtime/ModAssembly.dll", dll)
        z.writestr("Assets/ui-layout.json", (proj / "Assets/ui-layout.json").read_bytes())


def verify_code(expected):
    result = subprocess.run([sys.executable, str(fixture / ".build/verify.py")],
                            capture_output=True, text=True, encoding="utf-8")
    if result.returncode != expected:
        raise AssertionError("package gate returned %s, expected %s: %s%s" % (
            result.returncode, expected, result.stdout, result.stderr))
    return result.stdout


package(manifest)
verify_code(0)
manifest["playInstructions"] = ""
package(manifest)
assert "PI_HAS_FORMAT_HINT" in verify_code(1)
print("Package gate regression: valid=0, invalid=1 PASS")
