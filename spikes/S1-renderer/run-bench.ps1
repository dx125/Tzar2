# Spike S1 benchmark + pixel-exactness check (Windows). macOS/Linux: run-bench.sh.
#
#   pwsh spikes/S1-renderer/run-bench.ps1 -Godot C:\Godot\Godot_v4.7.2-stable_mono_win64_console.exe
#   pwsh spikes/S1-renderer/run-bench.ps1 -Godot ... -Quick          # 10k only, short runs (smoke test)
#
# Use the *_console.exe build so stdout reaches the terminal. Exit code is non-zero if any run crashed or any
# verification failed. Results: <Out>/results.csv, <Out>/bench.log, one PNG frame per renderer/resolution.
param(
    [string]$Godot = "godot",
    [string]$Out = (Join-Path $PSScriptRoot ("results/local-" + (Get-Date -Format "yyyyMMdd-HHmmss"))),
    [string[]]$Resolutions = @("1920x1080", "2560x1440"),
    [string[]]$Renderers = @("gl_compatibility:opengl3", "mobile:d3d12", "forward_plus:d3d12", "mobile:vulkan", "forward_plus:vulkan"),
    [string]$BenchArgs = "",
    [switch]$Quick
)

$ErrorActionPreference = "Stop"
if ($Quick) { $BenchArgs = "--counts=10000 --warmup=0.5 --seconds=1.5 $BenchArgs" }
New-Item -ItemType Directory -Force -Path $Out | Out-Null
$log = Join-Path $Out "bench.log"
$status = 0

dotnet build (Join-Path $PSScriptRoot "S1Renderer.csproj") -nologo -v q
if ($LASTEXITCODE -ne 0) { exit 1 }
if (-not (Test-Path (Join-Path $PSScriptRoot ".godot/imported"))) {
    & $Godot --headless --path $PSScriptRoot --import *>> $log
}

function Invoke-Godot([string[]]$GodotArgs, [string]$Filter) {
    # Print only the S1* lines; the function's single output is Godot's exit code.
    & $Godot @GodotArgs 2>&1 | ForEach-Object { "$_" } | Tee-Object -FilePath $log -Append |
        Select-String -Pattern $Filter | ForEach-Object { Write-Host $_.Line }
    return $LASTEXITCODE
}

foreach ($r in $Renderers) {
    $method, $driver = $r.Split(":")
    foreach ($res in $Resolutions) {
        "== bench $method/$driver $res" | Tee-Object -FilePath $log -Append
        $extra = if ($BenchArgs) { $BenchArgs.Split(" ", [StringSplitOptions]::RemoveEmptyEntries) } else { @() }
        $a = @("--path", $PSScriptRoot, "--resolution", $res, "--position", "0,0", "--rendering-method", $method, "--rendering-driver", $driver,
            "--", "--bench", "--size=$res", "--out=$(Join-Path $Out 'results.csv')", "--screenshot=$(Join-Path $Out "frame-$method-$driver-$res.png")") + $extra
        if ((Invoke-Godot $a "^S1(INFO|RESULT|SHOT|ERROR)") -ne 0) { $status = 1 }
    }
    "== verify $method/$driver" | Tee-Object -FilePath $log -Append
    $a = @("--path", $PSScriptRoot, "--resolution", "1280x720", "--position", "0,0", "--rendering-method", $method, "--rendering-driver", $driver,
        "--", "--verify", "--size=1280x720", "--shotdir=$Out")
    if ((Invoke-Godot $a "^S1(INFO|VERIFY|ERROR)") -ne 0) { $status = 1 }
}

"results: $(Join-Path $Out 'results.csv') (log: $log)"
exit $status
