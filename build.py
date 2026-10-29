# -*- coding: utf-8 -*-
"""音乐盒：编译 → 校验 → 打包（→ 可选装机）一条龙。

用法：
    python build.py                      只出包，不装机
    python build.py --install            出包并装进存档 Mods\
    python build.py --host-dir <目录>    指定含那两份宿主程序集的目录（否则读环境变量 PVZHOSTDIR）
    python build.py --probe <BGM目录>    额外跑一遍音频预检（可选）

只依赖标准库和 .NET SDK，不需要 pip install 任何东西。
"""

import argparse
import os
import pathlib
import subprocess
import sys

PROJ = pathlib.Path(__file__).resolve().parent
BUILD = PROJ / ".build"
BIN = BUILD / "bin" / "Release"


def fail(msg, code=1):
    print("[build] 终止：" + msg)
    sys.exit(code)


def run(argv, label):
    print("\n[build] %s：%s" % (label, " ".join(str(a) for a in argv)))
    completed = subprocess.run(argv, cwd=PROJ)
    if completed.returncode != 0:
        fail("%s 失败（返回码 %d）" % (label, completed.returncode), completed.returncode)
    print("[build] %s OK" % label)


def main():
    parser = argparse.ArgumentParser(description="音乐盒构建一条龙")
    parser.add_argument("--install", action="store_true", help="打包后装进存档目录的 Mods\\（默认不装）")
    parser.add_argument("--host-dir", help="含那两份宿主程序集的目录；不给就用环境变量 PVZHOSTDIR")
    parser.add_argument("--runtime-dir", help="可选：实际发行版裁剪 .NET DLL 目录，增加类型引用与直接方法签名形状检查")
    parser.add_argument("--probe", action="append", default=[], help="额外做一次音频预检的目录，可重复")
    args = parser.parse_args()

    host_dir = args.host_dir or os.environ.get("PVZHOSTDIR") or ""
    if not host_dir:
        fail("没指定宿主程序集目录：加 --host-dir <目录>，或设环境变量 PVZHOSTDIR="
             "<含那两份宿主程序集的目录>（取法见 README「构建与打包」）。")
    # 相对路径按仓库根目录算，并在这里就转成绝对路径：MSBuild 的 Exists() 是相对工程目录的，
    # 传相对目录会让构建报「宿主程序集目录不对」这种看不懂的错。
    host_dir = str((PROJ / host_dir).resolve())
    missing = [name for name in ("PlantsVsZombies.dll", "GodotSharp.dll")
               if not (pathlib.Path(host_dir) / name).is_file()]
    if missing:
        fail("%s 下缺 %s：--host-dir 要指到那两个文件真正所在的目录（取法见 README「构建与打包」）。"
             % (host_dir, "、".join(missing)))

    # 构建缓存收进仓库内：不污染系统临时目录，也让离线工具的 NuGet 落在可预期的地方。
    cache = BUILD / "tmp"
    temp = BUILD / "TEMP"
    for directory in (cache, temp):
        directory.mkdir(parents=True, exist_ok=True)
    env = dict(os.environ)
    env.update({
        "TEMP": str(temp), "TMP": str(temp), "TMPDIR": str(temp),
        "DOTNET_CLI_HOME": str(cache), "NUGET_PACKAGES": str(cache / "nuget"),
        "PVZHOSTDIR": str(host_dir),
        "DOTNET_NOLOGO": "1", "DOTNET_CLI_TELEMETRY_OPTOUT": "1",
        # 常驻 MSBuild 节点会锁住 obj\，卡住下一次构建；离线工具用 dotnet run，只能靠环境变量关掉。
        "MSBUILDDISABLENODEREUSE": "1", "DOTNET_CLI_USE_MSBUILD_SERVER": "0",
    })
    os.environ.update(env)

    # dotnet build 收 MSBuild 开关；dotnet run 只认 --project/--configuration，
    # 其余参数会原样转给工具本身（校验工具拿第一个参数当 mod.json 路径，预检工具把参数当目录列表）。
    build_flags = ["--configuration", "Release", "-nologo", "-m:1", "-nodeReuse:false",
                   "-p:UseSharedCompilation=false", "-p:PVZHostDir=%s" % host_dir]
    run_flags = ["--configuration", "Release"]

    run(["dotnet", "build", str(BUILD / "ModAssembly.csproj"), *build_flags,
         "-o", str(BIN)], "编译运行时 DLL")
    if args.runtime_dir:
        runtime_dir = str((PROJ / args.runtime_dir).resolve())
        run(["dotnet", "run", "--project", str(BUILD / "runtimecheck"), *run_flags,
             "-p:UseSharedCompilation=false", "--", str(BIN / "ModAssembly.dll"), runtime_dir],
            "实际发行裁剪运行库元数据检查（非完整运行验收）")
    run(["dotnet", "run", "--project", str(BUILD / "validate"), *run_flags, "--", "mod.json"],
        "离线校验 settings 声明")

    run([sys.executable, str(BUILD / "semver_probe.py")], "版本规则回归测试")
    run([sys.executable, str(BUILD / "regression.py")], "配置与指纹回归测试")

    for bgm in args.probe:
        run(["dotnet", "run", "--project", str(BUILD / "probe"), *run_flags, "--", bgm],
            "音频预检")

    package = [sys.executable, str(BUILD / "package.py"), str(PROJ)]
    if not args.install:
        package.append("--no-install")
    run(package, "打包" + ("并装机" if args.install else "（不装机）"))

    run([sys.executable, str(BUILD / "verify.py")], "核对包内容")
    report = temp / "verify.txt"
    if report.is_file():
        for line in report.read_text(encoding="utf-8").splitlines():
            print("    " + line)
    print("\n[build] 全部通过。包在 %s" % (PROJ / "dist"))
    if not args.install:
        print("[build] 未改动源码或游戏存档；缓存和产物写入 .build/ 与 dist/。要装机再加 --install。")


if __name__ == "__main__":
    main()
