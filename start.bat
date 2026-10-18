@echo off
setlocal EnableDelayedExpansion
cd /d "%~dp0"

REM ============================================================
REM  eRentalSystem 一键启动器（本机 / 局域网 二合一）
REM
REM  用法：
REM    start.bat        —— 启动时询问是否启用局域网（10 秒无输入默认仅本机）
REM    start.bat lan    —— 直接以局域网模式启动
REM    start.bat local  —— 直接以本机模式启动
REM
REM  流程：检查 MySQL -> 后端 5105 -> 前端 5173 -> 打开浏览器
REM  局域网模式额外：探测本机 IP、放行防火墙 5105/5173（仅专用/域网络）、
REM                  后端绑 0.0.0.0 并把回调地址按本机 IP 兜底（需管理员，自动 UAC 提权）
REM  演示结束：管理员命令行执行下面两条，移除防火墙放行规则（规则名固定）：
REM    netsh advfirewall firewall delete rule name="eRentalSystem-LAN-5105"
REM    netsh advfirewall firewall delete rule name="eRentalSystem-LAN-5173"
REM ============================================================

REM ----- configurable -----
set "DOTNET=C:\Users\32854\.dotnet\dotnet.exe"
REM [.NET 10] PATH 上的 dotnet（C:\Program Files\dotnet）通常只有运行时、没有 SDK，
REM 一旦被 where 命中就会导致 "No .NET SDKs were found"。改为：用户级 dotnet 不存在时才回退。
if not exist "%DOTNET%" (
  where dotnet >nul 2>nul && set "DOTNET=dotnet"
)
set "MYSQL_SERVICE=MySQL96"
set "RULE_PREFIX=eRentalSystem-LAN"
REM -------------------------

set "ROOT=%~dp0"
set "BACKDIR=%ROOT%back"
set "FRONTDIR=%ROOT%front"

echo ================================================
echo   eRentalSystem 一键启动器
echo ================================================

REM ===== 模式选择：命令行参数优先，否则询问 =====
set "LANMODE="
if /i "%~1"=="lan" set "LANMODE=1"
if /i "%~1"=="local" set "LANMODE=0"
if not defined LANMODE (
  echo 是否启用局域网访问（同一 Wi-Fi 下的手机/其他电脑可访问）？
  choice /c YN /n /t 10 /d N /m "  [Y=局域网 / N=仅本机，10 秒后默认仅本机] "
  if !errorlevel!==1 (set "LANMODE=1") else (set "LANMODE=0")
)
if "!LANMODE!"=="1" echo   模式：局域网（0.0.0.0，同一 Wi-Fi 可访问）
if not "!LANMODE!"=="1" echo   模式：仅本机（localhost）

REM ===== 局域网模式：管理员自检，不足则 UAC 提权重跑 =====
if not "!LANMODE!"=="1" goto :mode_ok
net session >nul 2>nul
if !errorlevel!==0 goto :mode_ok
echo [INFO] 局域网模式需要管理员权限配置防火墙，正在请求提权...
powershell -NoProfile -Command "Start-Process -FilePath '%~f0' -ArgumentList 'lan' -Verb RunAs" >nul 2>nul
if !errorlevel!==0 (
  echo [INFO] 已在新窗口以管理员身份重启，请在该窗口继续操作。
  exit /b 0
)
echo [WARN] 提权失败或被拒绝。防火墙规则将跳过，外部设备可能连不上。
:mode_ok

REM ===== 1/3 MySQL =====
echo [1/3] 检查 MySQL 服务 %MYSQL_SERVICE%...
sc query "%MYSQL_SERVICE%" | findstr "RUNNING" >nul 2>nul
if !errorlevel!==0 (
  echo   [OK] MySQL 已在运行，跳过
) else (
  net start "%MYSQL_SERVICE%" >nul 2>nul || net start MySQL >nul 2>nul || (
    echo   [WARN] MySQL 未能自动启动，请手动启动后重试，确认服务名是否为 %MYSQL_SERVICE%
  )
)

REM ===== 局域网模式：探测 IP + 防火墙规则（放在顶层执行，避免嵌套括号截断 for 命令）=====
if not "!LANMODE!"=="1" goto :backend
echo [INFO] 探测本机局域网 IP（无线优先，跳过 VPN/WSL/Docker 等虚拟网卡）...
set "LANIP="
for /f "usebackq tokens=*" %%i in (`powershell -NoProfile -Command "$c=(Get-NetIPAddress -AddressFamily IPv4).Where({$_.IPAddress -ne '127.0.0.1' -and $_.IPAddress -notlike '169.254.*' -and -not $_.SkipAsSource}); $w=$c.Where({$_.InterfaceAlias.ToUpper().Contains('WLAN') -or $_.InterfaceAlias.ToUpper().Contains('WI-FI')}); if($w.Count -gt 0){$w[0].IPAddress}else{if($c.Count -gt 0){$c[0].IPAddress}}"`) do set "LANIP=%%i"
if "!LANIP!"=="" (
  echo   [WARN] 未探测到局域网 IP，请确认已连接 Wi-Fi 或网线；分享地址将回退 127.0.0.1。
  set "LANIP=127.0.0.1"
) else (
  echo   [OK] 局域网 IP: !LANIP!
)
echo [INFO] 配置防火墙入站规则（仅对专用/域网络生效，公共网络不开放）...
for %%P in (5105 5173) do (
  netsh advfirewall firewall delete rule name="%RULE_PREFIX%-%%P" >nul 2>nul
  netsh advfirewall firewall add rule name="%RULE_PREFIX%-%%P" dir=in action=allow protocol=TCP localport=%%P profile=private,domain >nul 2>nul
  if !errorlevel!==0 (echo   [OK] 已放行 TCP %%P) else (echo   [WARN] 放行 TCP %%P 失败（通常没有管理员权限）)
)
:backend

REM ===== 2/3 后端 =====
echo [2/3] 启动 ASP.NET Core 后端（端口 5105）...
if not exist "%BACKDIR%\bin\Debug\net10.0\back.dll" (
  echo   首次启动需要先构建，耗时数十秒，请等待后端窗口出现就绪信息。
)
if not "!LANMODE!"=="1" (
  start "eRentalSystem-Backend" cmd /k "pushd "%BACKDIR%" && "%DOTNET%" run"
) else (
  REM 局域网：Lan__Enabled 让回调地址按本机 IP 兜底；Lan__Host 显式传 IP，避免后端自己探测选错网卡
  start "eRentalSystem-Backend-LAN" cmd /k "pushd "%BACKDIR%" && set Lan__Enabled=true && set Lan__Host=!LANIP! && "%DOTNET%" run --urls http://0.0.0.0:5105"
)

REM ===== 3/3 前端 =====
echo [3/3] 启动前端（5173）...
if not exist "%FRONTDIR%\node_modules" (
  echo   首次启动：安装前端依赖，可能需要几分钟，请等待...
  pushd "%FRONTDIR%" && npm install
  if errorlevel 1 (
    echo   [WARN] npm install 失败。请手动在 %FRONTDIR% 执行后重跑本脚本。
    pause >nul
    exit /b 1
  )
  popd
)
REM ===== 局域网 HTTPS 证书 =====
REM 手机通过 Wi-Fi 访问时必须走 HTTPS：同一网段的公共 Wi-Fi 里，HTTP 下的登录密码
REM 和 JWT 都是明文的。证书由 front/scripts/make-dev-cert.mjs 生成（本地 CA + 服务器证书，
REM SAN 写的是本机局域网 IP），已存在则跳过；换网络导致 IP 变化时用 npm run cert:force 重签。
if "!LANMODE!"=="1" if not exist "%FRONTDIR%\certs\dev.crt" (
  echo [INFO] 局域网模式：生成本地 HTTPS 证书...
  pushd "%FRONTDIR%"
  node scripts\make-dev-cert.mjs
  popd
)

REM 局域网模式给子进程注入 LAN_HOST=1，vite.config.ts 据此绑 0.0.0.0 并启用 HTTPS；
REM 默认（未注入）只绑 localhost 且走 HTTP，本机开发不必反复点证书警告。
REM 关键：环境变量会被 start 出的子窗口继承，无需改 npm 脚本。
if "!LANMODE!"=="1" set "LAN_HOST=1"
start "eRentalSystem-Frontend-5173" cmd /k "pushd "%FRONTDIR%" && npm run dev"

REM ===== 汇总 =====
if "!LANMODE!"=="1" timeout /t 12 /nobreak >nul
echo.
echo ================================================
if "!LANMODE!"=="1" (
  echo   局域网访问地址（同一 Wi-Fi 下的设备可用）
  echo.
  echo   前端    https://!LANIP!:5173
  echo   接口    http://!LANIP!:5105/swagger
  echo.
  echo   本机自测仍可用 https://localhost:5173
  echo   手机首次访问提示「证书不受信任」：点「高级 - 继续前往」即可；
  echo   想彻底不弹窗，把 front\certs\ca.crt 装进手机的受信任证书。
  echo   回退：管理员命令行执行 netsh advfirewall firewall delete rule name="eRentalSystem-LAN-5105"
  echo         以及 name="eRentalSystem-LAN-5173"，即可撤销本机对局域网的放行
) else (
  echo   启动完成后可打开：
  echo.
  echo   前端    http://localhost:5173
  echo   接口    http://localhost:5105/swagger
)
echo.
echo ================================================
echo.
echo 按任意键打开前端首页，然后可关闭本窗口（各服务在各自窗口继续运行）。
pause >nul
if "!LANMODE!"=="1" start https://!LANIP!:5173
if not "!LANMODE!"=="1" start http://localhost:5173
endlocal
echo.
echo [INFO] AI 生图依赖 Comfy Desktop（本机 127.0.0.1:8188）：
echo        未启动 Comfy Desktop 时，车辆图片「AI 生成」不可用，其余功能不受影响。

