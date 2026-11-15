#!/bin/zsh
# 界面冒烟：3 条本机用例，验证交付的 .app 能启动、版本一致、能完成一次最小压缩。
# WorkBuddy, 2026-11-16：批次 25（P2）。
# 说明：真正的按钮点击需要 macOS 辅助功能授权，且受窗口位置影响，本脚本不做点击，
# 只做「进程 + 版本 + 真实归档任务」三件可稳定复跑的事。
#
# 用法：zsh 测试夹具/界面冒烟/run.sh
# 环境变量：BATCHCOMPRESS_APP 指定 .app 路径；DOTNET_BIN 指定 dotnet。

set -u

root="${0:A:h:h:h}"
app="${BATCHCOMPRESS_APP:-/Applications/BatchCompress.Avalonia.app}"
exe="$app/Contents/MacOS/BatchCompress.Avalonia"

pass=0
fail=0
started=0
work="$(mktemp -d /tmp/bc-ui-smoke.XXXXXX)"

cleanup() {
    rm -rf "$work"
    # 只关闭本次脚本启动的实例；用户原本开着的应用不动。
    if [[ "$started" -eq 1 ]]; then
        pkill -x BatchCompress.Avalonia 2>/dev/null
        pkill -x BatchCompress.StatusBarHelper 2>/dev/null
    fi
}
trap cleanup EXIT

report() {
    local name="$1" result="$2"
    if [[ "$result" -eq 0 ]]; then
        print -r -- "PASS $name"
        (( pass++ ))
    else
        print -r -- "FAIL $name"
        (( fail++ ))
    fi
}

if [[ ! -x "$exe" ]]; then
    print -r -- "找不到可执行文件：$exe"
    print -r -- "请先运行 zsh scripts/package-macos.sh，或设置 BATCHCOMPRESS_APP。"
    exit 2
fi

# 1. 启动后主程序与状态栏帮助进程都存在。
if ! pgrep -x BatchCompress.Avalonia >/dev/null; then
    open "$app"
    started=1
    sleep 4
fi
if pgrep -x BatchCompress.Avalonia >/dev/null && pgrep -x BatchCompress.StatusBarHelper >/dev/null; then
    r=0
else
    r=1
fi
report "启动后主程序与状态栏进程存在" "$r"

# 2. 应用内 CLI 的版本与仓库 VERSION 一致。
expected="$(tr -d '[:space:]' < "$root/VERSION")"
actual="$("$exe" --version 2>/dev/null | awk '{print $2}')"
if [[ "$actual" == "$expected" ]]; then
    r=0
else
    r=1
fi
report "应用版本与 VERSION 一致（$actual / $expected）" "$r"

# 3. 用交付产物跑一次真实最小压缩。
mkdir -p "$work/src" "$work/out"
print -r -- "smoke" > "$work/src/smoke.txt"
"$exe" compress --input "$work/src/smoke.txt" --output "$work/out" --format 7z \
    --log-file "$work/smoke.log" > "$work/stdout" 2> "$work/stderr"
rc=$?
archives=( "$work"/out/*.7z(N) )
if [[ $rc -eq 0 && ${#archives[@]} -ge 1 ]]; then
    r=0
else
    r=1
fi
report "最小压缩任务成功（退出码 $rc，产物 ${#archives[@]} 个）" "$r"

print ""
print "PASS=$pass FAIL=$fail"
[[ "$fail" -eq 0 ]]
