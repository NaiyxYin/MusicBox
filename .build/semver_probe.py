# 把 MusicBoxUpdate.cs 里那套 semver 比较原样抽出来，在裁剪无关的普通 .NET 里跑一遍真值表。
# 只读源文件、不改源文件；生成物都在 .build/TEMP 下，不进仓库。
import pathlib, subprocess, os, sys

proj = pathlib.Path(__file__).resolve().parents[1]
src = (proj / "Scripts" / "MusicBoxUpdate.cs").read_text(encoding="utf-8")
start = src.index("// 版本比较照严格 semver")
end = src.index("// 桥接游戏的统一更新提醒")
body = "\n".join(line[8:] if line.startswith(" " * 8) else line for line in src[start:end].rstrip().splitlines())

cases = [
    ("1.2.2", "1.2.2", 0), ("1.2.3", "1.2.2", 1), ("1.2.2", "1.2.3", -1),
    ("1.3.0-beta.1", "1.3.0", -1), ("1.3.0", "1.3.0-beta.1", 1),
    ("1.3.0-beta.2", "1.3.0-beta.10", -1), ("1.3.0-alpha.1", "1.3.0-beta.1", -1),
    ("1.3.0-rc.1", "1.3.0-beta.9", 1), ("1.3.0-beta.1", "1.3.0-beta.1", 0),
    ("1.2.2+5", "1.2.2+1", 0), ("1.2.3-rc.1+7", "1.2.3-rc.1", 0),
    ("1.2.2", "1.2.2.6", -1), ("1.2.2.6", "1.2.2", 1), ("1.2.2.6", "1.2.3", -1),
    ("v1.2.3", "1.2.2", 1), ("1.2", "1.2.0", 0), ("1.2.10", "1.2.9", 1),
    ("2147483648.0.0", "1.0.0", 1), ("1.2147483648.0", "1.2147483647.0", 1),
    ("1.0.0-9999999999", "1.0.0-10000000000", -1),
    ("1.0.0-9999999999", "1.0.0-alpha", -1),
    ("1.0.0-alpha", "1.0.0-9999999999", 1),
    ("1.0.0+001", "1.0.0+002", 0),
    ("1.0.0-alpha", "1.0.0-alpha.1", -1), ("1.0.0-beta.11", "1.0.0-rc.1", -1),
]
tags = [("1.2.3", True), ("1.2.2", True), ("1.3.0-beta.1", True), ("1.3.0-rc.12", True),
        ("1.2.3+1", True), ("1.2.3+build.1187", True), ("v1.2.3", True), ("1.2.3-alpha+build.9", True),
        ("1.2.3.0", False), ("1.2", False), ("1", False), ("01.2.3", False), ("1.2.03", False),
        ("1.2.3-", False), ("1.2.3+", False), ("1.2.3-+1", False),
        ("1.2.3-beta..1", False), ("1.2.3-01", False), ("1.2.3-beta_1", False),
        ("latest", False), ("", False), ("1.2.3 1.2.4", False),
         ("1.2.3+001", True), ("1.2.3+build.001", True), ("2147483648.0.0", True),
         ("1.0.0-9999999999", True), ("1.2.3-00", False), ("1.2.3+build..1", False)]

program = """
using System;
using System.Collections.Generic;
using System.Linq;

static class Probe
{
%s

    static int Main()
    {
        int bad = 0;
        foreach (var c in new (string, string, int)[] { %s })
        {
            int sign = CompareVersions(c.Item1, c.Item2).CompareTo(0);
            if (sign != c.Item3) { bad++; Console.WriteLine("FAIL compare [{0}] vs [{1}]: got {2} want {3}", c.Item1, c.Item2, sign, c.Item3); }
        }
        foreach (var t in new (string, bool)[] { %s })
        {
            bool got = IsStrictSemVer(t.Item1);
            if (got != t.Item2) { bad++; Console.WriteLine("FAIL tag [{0}]: semver={1} want {2}", t.Item1, got, t.Item2); }
        }
        Console.WriteLine(bad == 0 ? "ALL PASS" : "FAILURES: " + bad);
        return bad == 0 ? 0 : 1;
    }
}
""" % (
    body,
    ", ".join('("%s", "%s", %d)' % c for c in cases),
    ", ".join('("%s", %s)' % (t[0], "true" if t[1] else "false") for t in tags),
)

work = proj / ".build" / "TEMP" / "semver-probe"
work.mkdir(parents=True, exist_ok=True)
(work / "Program.cs").write_text(program, encoding="utf-8")
(work / "probe.csproj").write_text(
    "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup>"
    "<OutputType>Exe</OutputType><TargetFramework>net9.0</TargetFramework>"
    "<Nullable>disable</Nullable><AssemblyName>probe</AssemblyName>"
    "</PropertyGroup></Project>\n", encoding="utf-8")

env = dict(os.environ)
env.update(DOTNET_CLI_HOME=str(proj / ".build" / "tmp"),
           NUGET_PACKAGES=str(proj / ".build" / "tmp" / "nuget"),
           TEMP=str(proj / ".build" / "TEMP"), TMP=str(proj / ".build" / "TEMP"))
sys.exit(subprocess.call(["dotnet", "run", "--project", str(work), "-c", "Release", "--nologo", "-p:UseSharedCompilation=false"], env=env, cwd=str(work)))
