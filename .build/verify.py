import json, pathlib, re, zipfile, hashlib

proj = pathlib.Path(__file__).resolve().parents[1]
pkg = next((proj / "dist").glob("*.pmod"))
out = []
with zipfile.ZipFile(pkg) as z:
    bad = z.testzip()
    names = z.namelist()
    man_raw = z.read("mod.json")
    man = json.loads(man_raw.decode("utf-8"))
    dll = z.read("Runtime/ModAssembly.dll")

body = (proj / "changelog.txt").read_text(encoding="utf-8")
lines = body.split("\n")
lines[0] = ""
body = "\n".join(l.rstrip() for l in lines).strip()

changelog_item = next(s for s in man["settings"] if s["key"] == "aboutChangelog")
seed = changelog_item["text"] if "text" in changelog_item else changelog_item.get("content", "")

block_121 = body.split("v1.2.1")[1].split("v1.2.0")[0]
block_120 = body.split("v1.2.0")[1].split("v1.1.0")[0]
old_name = "CF音乐".encode("utf-8")
out.append("ZIP_OK: %s" % (bad is None))
out.append("NAMES: %s" % ", ".join(names))
out.append("SIZE_PKG: %d  DLL: %d" % (pkg.stat().st_size, len(dll)))
out.append("VERSION: %s" % man["version"])
out.append("SETTINGS: %d" % len(man["settings"]))
out.append("DLL_HAS_PREFLIGHT: %s" % (b"DescribeAudioRejection" in dll and b"DescribeFlacRejection" in dll
                                      and b"_blockedSlots" in dll and b"UnscannedAudioExtensions" in dll))
out.append("DLL_HAS_RESUME: %s" % (b"OnResumeFrame" in dll and b"ResumeMusicKeys" in dll))
out.append("DLL_HAS_SNIFF: %s" % (b"DetectAudioExtension" in dll and b"ReadFileHead" in dll and b"Matches" in dll))
out.append("ID: %s  NAME: %s" % (man["id"], man["name"]))
out.append("SCRIPTS: %s  ENTRY: %s" % (",".join(man["scripts"]), man["runtimeEntryType"]))
out.append("NO_OLD_NAME: %s" % (old_name not in man_raw and old_name not in body.encode("utf-8")))
out.append("DLL_IS_NEW_TYPE: %s" % (b"MusicBoxMod" in dll and b"CFMusic" not in dll))
# 更新检测走 GitHub Release + 严格 semver：这几个标记不在 DLL 里，说明改动没编进包（跑起来还是老的 update.txt 逻辑）。
# 只查类型成员名——它们进的是 UTF-8 元数据；字符串字面量存在 UTF-16 堆里，按字节搜是搜不到的。
out.append("DLL_HAS_RELEASE_FEED: %s" % (b"TryReadReleaseRecord" in dll and b"UpdateFeedHeaders" in dll
                                         and b"IsStrictSemVer" in dll and b"ComparePrerelease" in dll))
# 重打包只能调单参 CreateEntry：带 CompressionLevel 的重载在游戏裁剪过的运行时里不存在（实机 Method not found），
# 宿主自己写包也只用单参。DLL 元数据里再出现这个名字，就说明那处调用被改回去了。
out.append("DLL_AVOIDS_COMPRESSION_LEVEL: %s" % (b"CompressionLevel" not in dll))
# 旧名只允许出现在一处：编译器写进 DLL/PDB 的 pdb 路径（构建时的工作区目录）。
# 这里为 True 就说明包体里混进了玩家可见的旧名文本，或者构建还是在旧目录里跑的——重跑一次打包即可。
out.append("OLD_NAME_ONLY_IN_PDB_PATH: %s" % (old_name in dll and old_name not in man_raw))
out.append("PI_HAS_FORMAT_HINT: %s" % ("16-bit 立体声" in man.get("playInstructions", "")))

out.append("KEYS: %s" % ",".join(s["key"] for s in man["settings"]))
out.append("SEED_MATCHES_BODY: %s" % (seed == body))

# 版本号：mod.json 的 version 是唯一来源，严格 semver（主.次.补丁，内测可带 -alpha.1 / -beta.1 / -rc.1）。
# 构建号已经取消：「关于」页那句只写 `v<版本>`，包名也只带版本，两处对不上就是没跟着 version 改。
SEMVER = re.compile(r"^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)"
                    r"(-[0-9A-Za-z-]+(\.[0-9A-Za-z-]+)*)?"
                    r"(\+[0-9A-Za-z-]+(\.[0-9A-Za-z-]+)*)?$")
version = str(man["version"])
out.append("VERSION_IS_SEMVER: %s" % bool(SEMVER.match(version)))
out.append("VERSION_HAS_NO_BUILD: %s" % ("+" not in version))
about_item = next(s for s in man["settings"] if s["key"] == "aboutAuthor")
about_line = about_item.get("content") or about_item.get("text") or ""
about_match = re.search(r"v\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?(?: \+\d+)?", about_line)
out.append("ABOUT_VERSION: %s  (mod %s)" % (about_match.group(0) if about_match else "缺失 v<版本>", version))
out.append("ABOUT_VERSION_MATCHES: %s" % (about_match is not None and about_match.group(0) == "v" + version))
pkg_stem = "%s-%s" % (re.sub(r"[^0-9A-Za-z._-]", "_", man["id"]), version)
out.append("PKG_NAME_MATCHES: %s" % (pkg.name == pkg_stem + ".pmod"))
out.append("PKG_NAME_HAS_NO_HASH: %s" % (re.search(r"-[0-9A-F]{8}(?=\.pmod$)", pkg.name) is None))

# 更新源：模组每次启动查本仓库最新的 GitHub Release（tag 名当最新版、Release 页当下载页）。
# 这几条发布前必须都 True——地址里的占位没填、忘了带 User-Agent、或者仓库改过名，
# 线上表现都是同一个：永远「已最新」，玩家看不到提醒。
# 更新检测拆到了 Scripts/MusicBoxUpdate.cs，两个文件一起当"模组源码"扫，闸门不认文件在哪。
entry_src = "".join((proj / "Scripts" / name).read_text(encoding="utf-8")
                    for name in ("MusicBoxEntry.cs", "MusicBoxUpdate.cs"))
feed_url = ""
for src_line in entry_src.splitlines():
    if "api.github.com" in src_line and '";' in src_line:
        feed_url = src_line.split('"')[1]
        break
out.append("FEED_URL: %s" % feed_url)
out.append("FEED_URL_NO_PLACEHOLDER: %s" % (bool(feed_url) and not any(
    token in feed_url for token in ("OWNER", "REPO", "<", " "))))
out.append("FEED_IS_LATEST_RELEASE_API: %s" % feed_url.endswith("/releases/latest"))
out.append("FEED_SENDS_USER_AGENT: %s" % ('"User-Agent: ' in entry_src))
out.append("FEED_NO_LEGACY_FILE: %s" % (not (proj / "update.txt").exists()))

# 「关于」页那个仓库链接必须和更新源指向同一个仓库：仓库改名/搬家时只改了一处的话，
# 玩家在游戏里点开的就是 404 页面，而更新提醒看着一切正常。两边都从原文里抓，不做拼接。
repo_link = re.search(r"\[url=(https://github\.com/[0-9A-Za-z._/-]+)\]", about_line)
feed_slug = re.search(r"https://api\.github\.com/repos/([0-9A-Za-z._-]+/[0-9A-Za-z._-]+)/", feed_url)
out.append("ABOUT_REPO_LINK: %s" % (repo_link.group(1) if repo_link else "缺失"))
out.append("ABOUT_REPO_MATCHES_FEED: %s" % bool(repo_link and feed_slug
    and repo_link.group(1).rstrip("/").endswith("/" + feed_slug.group(1))))

out.append("PLAY_INSTRUCTIONS_LEN: %d" % len(man.get("playInstructions", "")))
out.append("RESUME_LINE_IN_121_BLOCK: %s" % ("修复：从载入界面" in block_121))
out.append("SNIFF_LINE_IN_121_BLOCK: %s" % ("文件名和歌曲实际格式" in block_121))
out.append("RESUME_LINE_IN_120_BLOCK: %s" % ("修复：从载入界面" in block_120))
out.append("SHA16: %s" % hashlib.sha256(pkg.read_bytes()).hexdigest()[:16])
(proj / ".build/TEMP/verify.txt").write_text("\n".join(out), encoding="utf-8")
print("WROTE")
