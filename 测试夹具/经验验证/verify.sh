#!/bin/zsh
# 经验库实测验证脚本
# 用途：把 经验/ 目录里可检验的关键结论，用真实命令行与真实归档往返跑一遍，
#       避免经验文档只是照抄历史文档。可重复执行，不改动仓库内任何业务文件。
# 环境：macOS（Apple Silicon）/ .NET 11；其它平台需自行调整 stat 参数。
# 输出：每项 PASS/FAIL，最后汇总；所有产物落在系统临时目录并在退出时清理。
set -uo pipefail

repo_root="${0:A:h:h:h}"
fixture_dir="${0:A:h}/输入"
dotnet_bin="${DOTNET_BIN:-$(command -v dotnet)}"
app="$repo_root/bin/Debug/net11.0/BatchCompress.Avalonia.dll"
tests_dll="$repo_root/BatchCompress.Avalonia.Tests/bin/Debug/net11.0/BatchCompress.Avalonia.Tests.dll"
work="$(mktemp -d /tmp/bc-exp-verify.XXXXXX)"
trap 'rm -rf "$work"' EXIT

pass=0
fail=0
report() {
  if [[ "$2" == "0" ]]; then
    echo "PASS  $1"
    pass=$((pass + 1))
  else
    echo "FAIL  $1"
    fail=$((fail + 1))
  fi
}

echo "=== 0. 开测前重新构建，确认被测产物是最新的 ==="
"$dotnet_bin" build "$repo_root/BatchCompress.Avalonia.sln" --nologo > "$work/build.log" 2>&1
"$dotnet_bin" build "$repo_root/BatchCompress.Avalonia.Tests/BatchCompress.Avalonia.Tests.csproj" --nologo >> "$work/build.log" 2>&1
if [[ $? -ne 0 ]]; then
  echo "构建失败，测试结论不成立："
  tail -20 "$work/build.log"
  exit 1
fi
grep -E "Build succeeded|Warning\(s\)|Error\(s\)" "$work/build.log" | tail -3

cp -R "$fixture_dir" "$work/来源"
# 刻意造的系统元数据文件在运行时生成：仓库 .gitignore 会忽略 .DS_Store，
# 若依赖静态夹具，别人克隆后复跑会缺文件、让「元数据过滤」用例形同虚设。
printf 'metadata' > "$work/来源/.DS_Store"
printf 'temp' > "$work/来源/.tmp缓存"
expected_version="$(tr -d '[:space:]' < "$repo_root/VERSION")"

echo ""
echo "=== 1. 版本号唯一来源 ==="
ver="$("$dotnet_bin" "$app" --version 2>/dev/null)"
if [[ "$ver" == *"$expected_version"* ]]; then r=0; else r=1; fi
report "--version 输出与 VERSION 一致（$expected_version）：$ver" "$r"

echo ""
echo "=== 2. 帮助语言预扫描 ==="
help_en="$("$dotnet_bin" "$app" --help --lang en 2>/dev/null)"
if [[ "$help_en" == *"compress"* && "$help_en" != *"用法:"* ]]; then r=0; else r=1; fi
report "--help --lang en 输出英文帮助（不经过完整解析）" "$r"

echo ""
echo "=== 3. 参数错误退出码 2 ==="
"$dotnet_bin" "$app" compress --concurrency 99 --input "$work/来源/普通文本.txt" --output "$work/invalid" > "$work/invalid.log" 2>&1
rc=$?
if [[ $rc -eq 2 ]]; then r=0; else r=1; fi
report "非法并发度返回退出码 2（实际 $rc）" "$r"

echo ""
echo "=== 4. 试运行不改文件 ==="
"$dotnet_bin" "$app" compress --source "$work/来源" --output "$work/out-dry" --format 7z --dry-run \
  --log-file "$work/dry.log" > "$work/dry.txt" 2>&1
rc=$?
if [[ $rc -eq 0 ]] && grep -q "DRY-RUN" "$work/dry.txt" && [[ ! -d "$work/out-dry" ]]; then r=0; else r=1; fi
report "--dry-run 只列任务、不创建输出目录（退出码 $rc）" "$r"

echo ""
echo "=== 5. 系统元数据过滤 ==="
if ! grep -q "DS_Store" "$work/dry.txt" && ! grep -q "tmp缓存" "$work/dry.txt" \
   && grep -q "正常文件" "$work/dry.txt"; then r=0; else r=1; fi
report "跳过 .DS_Store 与 .tmp 前缀，同时保留普通文件" "$r"

echo ""
echo "=== 6. 来源路径尾部斜杠不产生空名归档 ==="
mkdir -p "$work/out1"
"$dotnet_bin" "$app" compress --input "$work/来源/另一个目录/" --output "$work/out1" --format 7z \
  --log-file "$work/c1.log" > "$work/c1.txt" 2>&1
rc=$?
empty_name="$(find "$work/out1" -maxdepth 1 -name ".7z" -print)"
named_archive="$(find "$work/out1" -maxdepth 1 -name "*另一个目录*.7z" -print)"
if [[ $rc -eq 0 && -z "$empty_name" && -n "$named_archive" ]]; then r=0; else r=1; fi
report "带尾斜杠目录压缩出正常归档名，无 .7z 空名文件（退出码 $rc）" "$r"
echo "      实际产物：$(ls -1 "$work/out1" | tr '\n' ' ')"

echo ""
echo "=== 7. 带密码真实压缩 → 解压 → 逐字节核对 ==="
printf 'verify-pass-2026\n' > "$work/pass.txt"
mkdir -p "$work/out2"
"$dotnet_bin" "$app" compress --input "$work/来源/普通文本.txt" --output "$work/out2" --format 7z \
  --password-file "$work/pass.txt" --test --log-file "$work/c2.log" > "$work/c2.txt" 2>&1
compress_rc=$?
archive="$(find "$work/out2" -maxdepth 1 -name "*.7z" -print | head -1)"
if [[ $compress_rc -eq 0 && -n "$archive" ]]; then
  "$dotnet_bin" "$app" extract --input "$archive" --output "$work/unpack" --format 7z \
    --password-file "$work/pass.txt" --log-file "$work/e1.log" > "$work/e1.txt" 2>&1
  extract_rc=$?
  extracted="$(find "$work/unpack" -type f -name "普通文本.txt" -print | head -1)"
  if [[ -n "$extracted" ]] && cmp -s "$fixture_dir/普通文本.txt" "$extracted"; then
    r=0
  else
    r=1
  fi
else
  extract_rc=-1
  extracted=""
  r=1
fi
report "7zz 真实带密码压缩与解压往返内容一致（压缩 $compress_rc / 解压 $extract_rc）" "$r"

echo ""
echo "=== 8. 日志文件权限 0600 ==="
if [[ "$(uname)" == "Darwin" ]]; then
  perm="$(stat -f "%Lp" "$work/c2.log" 2>/dev/null)"
else
  perm="$(stat -c "%a" "$work/c2.log" 2>/dev/null)"
fi
if [[ "$perm" == "600" ]]; then r=0; else r=1; fi
report "含密码日志权限为 0600（实际 $perm）" "$r"

echo ""
echo "=== 9. 结构化输出 NDJSON 不含明文密码 ==="
mkdir -p "$work/out3"
"$dotnet_bin" "$app" compress --input "$work/来源/普通文本.txt" --output "$work/out3" --format 7z \
  --password-file "$work/pass.txt" --output-format json --log-file "$work/j.log" \
  > "$work/j.txt" 2> "$work/j.err"
json_lines="$(grep -c '^{' "$work/j.txt" 2>/dev/null || true)"
if [[ "$json_lines" -gt 0 ]] && ! grep -q "verify-pass-2026" "$work/j.txt" \
   && grep -q "Found" "$work/j.err"; then r=0; else r=1; fi
report "真实压缩时 stdout 为 NDJSON 且不含密码、人类提示在 stderr（$json_lines 行）" "$r"

echo ""
echo "=== 9b. 试运行模式不产生结构化事件流（实测边界） ==="
"$dotnet_bin" "$app" compress --source "$work/来源" --output "$work/out-dry2" --format 7z --dry-run \
  --output-format json --log-file "$work/jd.log" > "$work/jd.txt" 2> "$work/jd.err"
if [[ ! -s "$work/jd.txt" ]] && grep -q "DRY-RUN" "$work/jd.err"; then r=0; else r=1; fi
report "--dry-run 提前返回：stdout 无事件，DRY-RUN 清单走 stderr" "$r"

echo ""
echo "=== 10. 全量回归测试 ==="
"$dotnet_bin" "$tests_dll" > "$work/tests.txt" 2>&1
tests_rc=$?
if [[ $tests_rc -eq 0 ]] && grep -q "全部 .* 项测试通过" "$work/tests.txt"; then r=0; else r=1; fi
report "测试控制台全部通过（退出码 $tests_rc）：$(tail -1 "$work/tests.txt")" "$r"

echo ""
echo "=== 11. 打包前置自检 ==="
"$repo_root/scripts/package-macos.sh" --check > "$work/pkg.txt" 2>&1
pkg_rc=$?
if [[ $pkg_rc -eq 0 ]]; then r=0; else r=1; fi
report "package-macos.sh --check 通过：$(cat "$work/pkg.txt")" "$r"

echo ""
echo "=== 12. 静态事实核对 ==="
rar_count="$(find "$repo_root/tools/rarLinux" -maxdepth 1 -type f | wc -l | tr -d ' ')"
if [[ "$rar_count" == "11" ]]; then r=0; else r=1; fi
report "tools/rarLinux 为官方完整分发包 11 文件（实际 $rar_count）" "$r"

v7="$(strings "$repo_root/tools/7zip/macos/7zz" | grep -m1 -o "7-Zip (z) [0-9.]*" || true)"
if [[ "$v7" == *"26.05"* ]]; then r=0; else r=1; fi
report "macOS 7zz 为 26.05：$v7" "$r"

sln_projects="$(grep -c '^Project(' "$repo_root/BatchCompress.Avalonia.sln" || true)"
if [[ "$sln_projects" == "1" ]]; then r=0; else r=1; fi
report "解决方案只含主项目（$sln_projects 个），测试需单独指定 csproj" "$r"

echo ""
echo "=== 13. Ctrl+C 协作式取消返回 130 ==="
# 关键：非交互 shell 启动的后台作业默认忽略 SIGINT（中断信号），直接 kill -INT 会被丢弃、
# 压缩会照常跑完并返回 0。必须在子进程里先重置信号处置再 exec，取消才会被程序捕获。
# 用三份较大文件串行压缩，保证发出信号时任务仍在途；产物全部落在临时目录。
if [[ "${SKIP_CANCEL_TEST:-0}" == "1" ]]; then
  echo "SKIP  按 SKIP_CANCEL_TEST=1 跳过取消用例"
else
  dd if=/dev/urandom of="$work/big1.bin" bs=1m count=180 2>/dev/null
  cp "$work/big1.bin" "$work/big2.bin"
  cp "$work/big1.bin" "$work/big3.bin"
  mkdir -p "$work/out-cancel"
  zsh -c 'trap - INT; exec "$@"' _ "$dotnet_bin" "$app" compress \
    --input "$work/big1.bin" --input "$work/big2.bin" --input "$work/big3.bin" \
    --output "$work/out-cancel" --format 7z --log-file "$work/cancel.log" \
    > "$work/cancel.out" 2> "$work/cancel.err" &
  cancel_pid=$!
  sleep 0.6
  kill -INT "$cancel_pid" 2>/dev/null
  wait "$cancel_pid"
  cancel_rc=$?
  leftover="$(pgrep -x 7zz 2>/dev/null || true)"
  leftover_files="$(find "$work/out-cancel" -type f \( -name "*.7z*" -o -name "*.tmp*" \) | wc -l | tr -d ' ')"
  if [[ "$cancel_rc" -eq 130 ]] \
     && grep -qi "Operation cancelled by user" "$work/cancel.log" \
     && [[ -z "$leftover" ]] \
     && [[ "$leftover_files" -eq 0 ]]; then r=0; else r=1; fi
  report "Ctrl+C 返回 130、日志有取消记录、无残留 7zz 进程且输出目录零残片（实际 $cancel_rc，残片数 $leftover_files）" "$r"
fi

echo ""
echo "=== 14. 硬件感知并发 --concurrency auto（批次 18 / P4-2）==="
# 试运行预检同样要显示推荐值；提示走 stderr，不污染 stdout 的任务清单。
"$dotnet_bin" "$app" compress --source "$work/来源" --output "$work/out-auto" --format 7z \
  --concurrency auto --dry-run --log-file "$work/auto.log" \
  > "$work/auto.out" 2> "$work/auto.err"
auto_rc=$?
if [[ $auto_rc -eq 0 ]] && grep -q "DRY-RUN" "$work/auto.out" \
   && grep -Eq "自动并发度|Auto concurrency" "$work/auto.err" \
   && [[ ! -d "$work/out-auto" ]]; then r=0; else r=1; fi
report "auto 预检显示推荐值且不创建输出目录（退出码 $auto_rc）" "$r"

# 结构化输出叠加 auto 时，stdout 必须仍是纯 NDJSON（逐行 JSON），日志要留下判断依据。
mkdir -p "$work/out-auto-json"
"$dotnet_bin" "$app" compress --input "$work/来源/普通文本.txt" --output "$work/out-auto-json" \
  --format 7z --concurrency auto --output-format json --log-file "$work/auto-json.log" \
  > "$work/auto-json.out" 2> "$work/auto-json.err"
json_auto_rc=$?
non_json="$(grep -vc '^{' "$work/auto-json.out" 2>/dev/null || true)"
if [[ $json_auto_rc -eq 0 && "$non_json" == "0" ]] \
   && grep -q "CONCURRENCY" "$work/auto-json.log"; then r=0; else r=1; fi
report "auto 叠加结构化输出时 stdout 仍为纯 NDJSON、日志含推荐依据" "$r"

echo ""
echo "=== 15. 多卷坏块提前终止（批次 19 / P4-1）==="
# 造 3 MiB 随机数据切成 1 MiB 分卷，篡改第二个分卷中间字节，再用本项目命令行解压。
# 断言：任务失败、终端能看到损坏原因、日志有原始错误行、输出目录零残片。
dd if=/dev/urandom of="$work/vol.bin" bs=1m count=3 2>/dev/null
z7="$repo_root/tools/7zip/macos/7zz"
"$z7" a -v1m -mx=1 "$work/vol.7z" "$work/vol.bin" > /dev/null 2>&1
first="$(ls -1 "$work"/vol.7z.* | sed -n '1p')"
second="$(ls -1 "$work"/vol.7z.* | sed -n '2p')"
printf '\x00\x00\x00\x00' | dd of="$second" bs=1 seek=200 count=4 conv=notrunc 2>/dev/null
"$dotnet_bin" "$app" extract --input "$first" --output "$work/out-corrupt" --format 7z \
  --log-file "$work/corrupt.log" > "$work/corrupt.out" 2> "$work/corrupt.err"
corrupt_rc=$?
leftover="$(find "$work/out-corrupt" -type f 2>/dev/null | wc -l | tr -d ' ')"
if [[ "$corrupt_rc" -eq 1 ]] && grep -q "归档损坏，已提前终止" "$work/corrupt.err" \
   && grep -q "CRC Failed" "$work/corrupt.log" && [[ "$leftover" == "0" ]]; then r=0; else r=1; fi
report "损坏分卷提前终止、终端可见原因、零残片（退出码 $corrupt_rc，残留 $leftover）" "$r"

echo ""
echo "=== 16. 批处理结果报告导出（批次 20 / P4-3）==="
# 带明文密码压缩两个文件，导出 CSV（逗号分隔值）与 Markdown，
# 断言逐项记录齐全、报告里搜不到密码、单独使用 --report 会被拒绝。
printf 'verify-report-secret\n' > "$work/report-pass.txt"
mkdir -p "$work/out-report"
"$dotnet_bin" "$app" compress \
  --input "$work/来源/普通文本.txt" --input "$work/来源/正常文件.txt" \
  --output "$work/out-report" --format 7z --password-file "$work/report-pass.txt" \
  --report "$work/result.csv" --log-file "$work/report.log" \
  > "$work/report.out" 2> "$work/report.err"
report_rc=$?
if [[ $report_rc -eq 0 ]] && grep -q "普通文本.txt" "$work/result.csv" \
   && grep -q "正常文件.txt" "$work/result.csv" \
   && ! grep -q "verify-report-secret" "$work/result.csv"; then r=0; else r=1; fi
report "结果报告含逐项记录且不含明文密码（退出码 $report_rc）" "$r"

"$dotnet_bin" "$app" compress --input "$work/来源/普通文本.txt" \
  --output "$work/out-report-md" --format 7z --report "$work/result.md" \
  --log-file "$work/report-md.log" > /dev/null 2>&1
md_rc=$?
"$dotnet_bin" "$app" --report "$work/only.csv" > /dev/null 2>&1
only_rc=$?
if [[ $md_rc -eq 0 ]] && grep -q "^# 批处理结果报告" "$work/result.md" \
   && [[ $only_rc -eq 2 ]]; then r=0; else r=1; fi
report "Markdown 报告可用、单独 --report 被拒绝（md=$md_rc, 单用=$only_rc）" "$r"

echo ""
echo "=== 17. 审查修复实测（0.8.9）：报告全路径、英文文案、CSV 防护 ==="
# 试运行也必须产出报告文件，并明确说明为何没有逐项结果。
"$dotnet_bin" "$app" compress --input "$work/来源/普通文本.txt" \
  --output "$work/out-dry" --format 7z --dry-run \
  --report "$work/dryrun.csv" --log-file "$work/dryrun.log" \
  > "$work/dryrun.out" 2> "$work/dryrun.err"
dry_rc=$?
if [[ $dry_rc -eq 0 && -f "$work/dryrun.csv" ]] \
   && grep -q "dry-run" "$work/dryrun.err"; then r=0; else r=1; fi
report "试运行也产出报告并说明原因（退出码 $dry_rc）" "$r"

# 参数校验失败同样产出报告，不再静默丢失。
"$dotnet_bin" "$app" compress --input "$work/不存在的文件.txt" \
  --output "$work/out-invalid" --format 7z --report "$work/invalid.csv" \
  > "$work/invalid.out" 2> "$work/invalid.err"
invalid_rc=$?
if [[ -f "$work/invalid.csv" ]]; then r=0; else r=1; fi
report "参数校验失败路径也产出报告（退出码 $invalid_rc）" "$r"

# 英文模式的报告必须全英文，不再出现中文表头配英文消息。
"$dotnet_bin" "$app" compress --input "$work/来源/普通文本.txt" \
  --output "$work/out-en" --format 7z --lang en --report "$work/en.csv" \
  --log-file "$work/en.log" > /dev/null 2>&1
en_rc=$?
if [[ $en_rc -eq 0 ]] && grep -q '"Operation"' "$work/en.csv" \
   && grep -q '"Success"' "$work/en.csv" \
   && ! grep -q '序号' "$work/en.csv"; then r=0; else r=1; fi
report "英文报告表头与状态不残留中文（退出码 $en_rc）" "$r"

# 特殊文件名不得被表格软件当成公式。
printf 'formula-probe' > "$work/来源/=1+1.txt"
"$dotnet_bin" "$app" compress --input "$work/来源/=1+1.txt" \
  --output "$work/out-formula" --format 7z --report "$work/formula.csv" \
  --log-file "$work/formula.log" > /dev/null 2>&1
formula_rc=$?
if [[ $formula_rc -eq 0 ]] && grep -q "\"'=1+1.txt\"" "$work/formula.csv" \
   && ! grep -q '"=1+1.txt"' "$work/formula.csv"; then r=0; else r=1; fi
report "CSV 中和以等号开头的来源名（退出码 $formula_rc）" "$r"

echo "=== 18. 关于弹窗（0.8.10）：路径口径与诊断包覆盖 ==="
# 在应用目录的 logs 下造一份「崩溃日志」命名风格的探针，验证诊断包不再只认 batchcompress_ 前缀。
app_logs="$repo_root/bin/Debug/net11.0/logs"
mkdir -p "$app_logs"
crash_probe="$app_logs/batchcompress-crash_verify_$$.log"
printf '[FATAL] --password "VerifyPlainSecret" unhandled\n' > "$crash_probe"

"$dotnet_bin" "$app" --export-diagnostics "$work/about-diag.zip" \
  --log-file "$work/batchcompress_about-cli.log" \
  > "$work/about.out" 2> "$work/about.err"
about_rc=$?
# 先取整份清单再判断：pipefail 下 grep -q 提前关管道会把 unzip 的 SIGPIPE 误判成失败。
diag_entries="$(unzip -Z1 "$work/about-diag.zip" 2>/dev/null)"
if [[ $about_rc -eq 0 && -f "$work/about-diag.zip" ]] \
   && [[ "$diag_entries" == *"batchcompress-crash_verify_"* ]] \
   && [[ "$diag_entries" == *"batchcompress_about-cli.log"* ]]; then r=0; else r=1; fi
report "诊断包同时收集崩溃日志与本次命令行日志（退出码 $about_rc）" "$r"

crash_text="$(unzip -p "$work/about-diag.zip" 'logs/batchcompress-crash_verify_*' 2>/dev/null)"
if [[ "$crash_text" == *'***'* && "$crash_text" != *'VerifyPlainSecret'* ]]; then r=0; else r=1; fi
report "诊断包中的崩溃日志明文口令已脱敏" "$r"

summary_text="$(unzip -p "$work/about-diag.zip" diagnostics_summary.json 2>/dev/null)"
if [[ "$summary_text" == *'LogDirectories'* ]]; then r=0; else r=1; fi
report "诊断摘要列出扫描过的多个日志目录" "$r"

expected_version="$(tr -d '[:space:]' < "$repo_root/VERSION")"
actual_version="$("$dotnet_bin" "$app" --version 2>/dev/null | awk '{print $2}')"
if [[ "$actual_version" == "$expected_version" ]] \
   && grep -q 'Binding AboutInfo.Version' "$repo_root/Views/AboutWindow.axaml"; then r=0; else r=1; fi
report "关于窗口绑定版本字段且命令行版本一致（$actual_version / $expected_version）" "$r"

rm -f "$crash_probe"

echo "=== 19. 复查修复（0.8.11）：崩溃日志配额与赋值形式密码脱敏 ==="
# 用独立的配额目录作为 --log-file 所在目录：它会被加入扫描范围，里面只有本次探针，
# 其余三类默认目录的日志共同参与竞争，正好复现「常规日志很多」的真实场景。
quota_dir="$work/quota"
mkdir -p "$quota_dir"
old_crash="$quota_dir/batchcompress-crash_verify_old_$$.log"
printf '[FATAL] old crash --password=OldPlainSecret\n' > "$old_crash"
touch -t 202611150900 "$old_crash"

for i in {1..12}; do
  printf '[INFO] routine log line %s\n' "$i" > "$quota_dir/batchcompress_quota_${i}.log"
done
printf '[INFO] cmd --password=EqualsPlainSecret\n[INFO] cmd --password:ColonPlainSecret\n' \
  > "$quota_dir/batchcompress_equals.log"

"$dotnet_bin" "$app" --export-diagnostics "$work/quota-diag.zip" \
  --log-file "$quota_dir/batchcompress_quota-cli.log" \
  > "$work/quota.out" 2> "$work/quota.err"
quota_rc=$?
quota_entries="$(unzip -Z1 "$work/quota-diag.zip" 2>/dev/null)"
if [[ $quota_rc -eq 0 ]] \
   && [[ "$quota_entries" == *"batchcompress-crash_verify_old_"* ]]; then r=0; else r=1; fi
report "较旧的崩溃日志不被大量常规日志挤出诊断包（退出码 $quota_rc）" "$r"

if [[ "$quota_entries" == *"batchcompress_equals.log"* ]]; then r=0; else r=1; fi
report "等号形式密码探针确实进入了诊断包（避免漏检）" "$r"

equals_text="$(unzip -p "$work/quota-diag.zip" 'logs/batchcompress_equals.log' 2>/dev/null)"
if [[ "$equals_text" != *'EqualsPlainSecret'* && "$equals_text" != *'ColonPlainSecret'* ]] \
   && [[ "$equals_text" == *'***'* ]]; then r=0; else r=1; fi
report "等号与冒号形式的密码在诊断包中已脱敏" "$r"

quota_log_count="$(print -r -- "$quota_entries" | grep -c '^logs/' || true)"
if [[ "$quota_log_count" -le 10 ]]; then r=0; else r=1; fi
report "诊断包日志总数仍不超过 10 份（实际 $quota_log_count）" "$r"

echo "=== 20. 统一默认参数（0.8.12）：界面与命令行同源 ==="
defaults_log="$work/defaults.log"
"$dotnet_bin" "$app" compress --input "$work/来源/普通文本.txt" \
  --output "$work/defaults-out" --dry-run --log-file "$defaults_log" \
  > "$work/defaults.out" 2>&1
defaults_rc=$?
defaults_line="$(grep -m1 '\[DEFAULTS\]' "$defaults_log" 2>/dev/null | sed 's/.*\[DEFAULTS\] //')"
if [[ $defaults_rc -eq 0 && "$defaults_line" == *"level=3"* \
      && "$defaults_line" == *"recovery=3%"* && "$defaults_line" == *"volume=none"* ]]; then r=0; else r=1; fi
report "命令行默认值：标准压缩、3% 恢复记录、不分卷（$defaults_line）" "$r"

override_log="$work/defaults-override.log"
"$dotnet_bin" "$app" compress --input "$work/来源/普通文本.txt" \
  --output "$work/defaults-out" --level 5 --recovery 7 \
  --volume-size 10 --volume-unit m --dry-run --log-file "$override_log" \
  > "$work/defaults-override.out" 2>&1
override_line="$(grep -m1 '\[DEFAULTS\]' "$override_log" 2>/dev/null | sed 's/.*\[DEFAULTS\] //')"
if [[ "$override_line" == *"level=5"* && "$override_line" == *"recovery=7%"* \
      && "$override_line" == *"volume=10m"* ]]; then r=0; else r=1; fi
report "显式参数仍能覆盖默认值（$override_line）" "$r"

if grep -q '_compressionLevel = ArchiveDefaults.DefaultCompressionLevel' "$repo_root/ViewModels/MainWindowViewModel.cs" \
   && grep -q '_enableVolume = ArchiveDefaults.DefaultEnableVolume' "$repo_root/ViewModels/MainWindowViewModel.cs" \
   && grep -q '_recoveryRecordPercent = ArchiveDefaults.DefaultRecoveryRecordPercent' "$repo_root/ViewModels/MainWindowViewModel.cs"; then
  r=0
else
  r=1
fi
report "界面字段初值引用 ArchiveDefaults 常量，不再各写一套" "$r"

# 注意：--shutdown 的真实行为不能在本机实测，否则会真的关掉开发机。
# 该命令的跨平台参数由单元测试「命令行关机命令跨平台构造」守护，这里只核对帮助仍在。
if "$dotnet_bin" "$app" --help 2>/dev/null | grep -q -- '--shutdown'; then r=0; else r=1; fi
report "帮助文本仍声明 --shutdown（真实关机命令由单元测试守护）" "$r"

echo "=== 21. 命令行来源校验与文案（0.8.13）==="
cli_dir="$work/cli-behavior"
mkdir -p "$cli_dir"
printf 'cli behavior payload\n' > "$cli_dir/a.txt"

"$dotnet_bin" "$app" compress -i "$cli_dir/a.txt" -i "$cli_dir/missing" \
  -o "$cli_dir/mixed-out" -e 7z --no-random-password \
  > "$cli_dir/mixed.out" 2> "$cli_dir/mixed.err"
mixed_rc=$?
if [[ $mixed_rc -eq 0 ]] && grep -q "NotFound=1" "$cli_dir/mixed.out" \
   && [[ -f "$cli_dir/mixed-out/a.txt.7z" ]]; then r=0; else r=1; fi
report "混合存在/缺失时处理存在项并记 NotFound=1（退出码 $mixed_rc）" "$r"

"$dotnet_bin" "$app" compress -i "$cli_dir/a.txt/" -o "$cli_dir/slash-out" \
  -e 7z --no-random-password > "$cli_dir/slash.out" 2> "$cli_dir/slash.err"
slash_rc=$?
if [[ $slash_rc -eq 0 && -f "$cli_dir/slash-out/a.txt.7z" ]]; then r=0; else r=1; fi
report "路径末尾带斜杠的文件不再被误判为不存在（退出码 $slash_rc）" "$r"

"$dotnet_bin" "$app" compress -i "$cli_dir/missing-dry" -o "$cli_dir/dry-out" \
  -e 7z --no-random-password --dry-run > "$cli_dir/dry.out" 2> "$cli_dir/dry.err"
dry_rc=$?
if [[ $dry_rc -eq 2 ]] && grep -q "未找到" "$cli_dir/dry.out"; then r=0; else r=1; fi
report "试运行遇到缺失来源仍明确报错并返回 2（退出码 $dry_rc）" "$r"

"$dotnet_bin" "$app" compress --lang en -i "$cli_dir/a.txt" -o "$cli_dir/en-out" \
  -e 7z --no-random-password > "$cli_dir/en.out" 2> "$cli_dir/en.err"
if grep -q "Found 1 items to compress" "$cli_dir/en.out" \
   && grep -q "Log file:" "$cli_dir/en.out"; then r=0; else r=1; fi
report "英文模式运行器文案为英文（Found / Log file）" "$r"

"$dotnet_bin" "$app" compress --lang de -i "$cli_dir/a.txt" -o "$cli_dir/de-out" \
  -e 7z --no-random-password > "$cli_dir/de.out" 2> "$cli_dir/de.err"
if grep -q "zu komprimierende Elemente gefunden" "$cli_dir/de.out" \
   && grep -q "Protokolldatei:" "$cli_dir/de.out"; then r=0; else r=1; fi
report "德文模式运行器文案为德文（gefunden / Protokolldatei）" "$r"

echo "=== 22. 命令行 TXT 清单缺失项（0.8.17） ==="
mkdir -p "$work/txt-src" "$work/txt-out" "$work/txt-extract"
printf 'txt missing probe' > "$work/txt-src/txt.txt"
"$dotnet_bin" "$app" compress -i "$work/txt-src/txt.txt" -o "$work/txt-out" \
  -e 7z --password txt-pass --log-file "$work/txt-c.log" > /dev/null 2>&1
printf 'txt.txt.7z\ntxt-pass\nmissing.7z\ntxt-pass\n' > "$work/txt-list.txt"
"$dotnet_bin" "$app" extract --source "$work/txt-out" --text-file "$work/txt-list.txt" \
  -o "$work/txt-extract" -e 7z --no-random-password --log-file "$work/txt-e.log" \
  > "$work/txt.out" 2> "$work/txt.err"
txt_rc=$?
if [[ $txt_rc -eq 0 ]] && grep -q "NotFound=1" "$work/txt.out" \
   && [[ -f "$work/txt-extract/txt.txt" ]]; then r=0; else r=1; fi
report "TXT 清单缺失项计 NotFound=1 且存在项正常解压（退出码 $txt_rc）" "$r"

echo ""
echo "=== 汇总 ==="
echo "PASS=$pass FAIL=$fail"
echo "临时目录：$work（退出时自动清理）"
if [[ $fail -gt 0 ]]; then
  echo "有用例未通过，请把失败项回写到经验文档复核。"
  exit 1
fi
echo "全部实测通过，经验结论已由真实运行验证。"
exit 0
