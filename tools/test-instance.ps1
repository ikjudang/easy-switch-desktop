param(
    [string]$Executable = 'artifacts\verify-023\bin\EasySwitch.Windows\debug\EasySwitch.exe',
    [string]$Directory = 'artifacts\instance-023'
)
$ErrorActionPreference='Stop'
$repo=Split-Path -Parent $PSScriptRoot
$appPath=[IO.Path]::GetFullPath((Join-Path $repo $Executable))
$testRoot=[IO.Path]::GetFullPath((Join-Path $repo $Directory))
if(-not $testRoot.StartsWith((Join-Path $repo 'artifacts')+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)){throw 'Fixture output must be inside artifacts.'}
New-Item -ItemType Directory -Path $testRoot -Force | Out-Null
$children=[Collections.Generic.List[Diagnostics.Process]]::new()
function Wait-Until([scriptblock]$Condition,[string]$Failure){
    $clock=[Diagnostics.Stopwatch]::StartNew()
    while(-not (& $Condition)){if($clock.Elapsed.TotalSeconds -gt 15){throw $Failure};Start-Sleep -Milliseconds 50}
}
function Launch([string]$Version,[string]$Root){
    $child=Start-Process -FilePath $appPath -ArgumentList '--instance-fixture',('"'+$Root+'"'),'--fixture-version',$Version -WindowStyle Hidden -PassThru
    $children.Add($child)
    Wait-Until {Test-Path -LiteralPath (Join-Path $Root "launch-$($child.Id).json")} "Launch failed: PID $($child.Id), $Root"
    return $child
}
function Stop-Fixture([string]$Root){
    $file=Get-ChildItem -LiteralPath $Root -Filter 'instance-*.json' | Select-Object -First 1
    if($file){$owner=Get-Content -LiteralPath $file.FullName -Raw | ConvertFrom-Json;$signal=[Threading.EventWaitHandle]::OpenExisting($owner.StopEvent);try{$signal.Set() | Out-Null}finally{$signal.Dispose()}}
}
try{
    $normal=Join-Path $testRoot ('normal-'+[guid]::NewGuid().ToString('N'))
    $old=Launch '0.2.2.0' $normal
    $new=Launch '0.2.3.0' $normal
    Wait-Until {$old.HasExited} 'The older instance remained running.'
    if(-not (Test-Path -LiteralPath (Join-Path $normal "stopped-$($old.Id).txt"))){throw 'The IPC-capable older instance was not stopped gracefully.'}
    $same=Launch '0.2.3.0' $normal
    Wait-Until {$same.HasExited} 'Duplicate version was not dismissed.'
    $lower=Launch '0.2.2.0' $normal
    Wait-Until {$lower.HasExited} 'Downgrade instance was not dismissed.'
    Wait-Until {Test-Path -LiteralPath (Join-Path $normal 'activated.txt')} 'Existing version was not activated.'
    if($new.HasExited){throw 'Duplicate or lower version stopped the newer app.'}
    $a=Start-Process -FilePath $appPath -ArgumentList '--instance-fixture',('"'+$normal+'"'),'--fixture-version','0.2.4.0' -WindowStyle Hidden -PassThru
    $b=Start-Process -FilePath $appPath -ArgumentList '--instance-fixture',('"'+$normal+'"'),'--fixture-version','0.2.5.0' -WindowStyle Hidden -PassThru
    $children.Add($a);$children.Add($b)
    Wait-Until {Test-Path -LiteralPath (Join-Path $normal "launch-$($b.Id).json")} 'Concurrent upgrade did not finish.'
    Wait-Until {$new.HasExited -and $a.HasExited} 'Concurrent upgrade left an older process running.'
    if($b.HasExited){throw 'The newest concurrent launch did not remain running.'}
    Stop-Fixture $normal
    Wait-Until {$b.HasExited} 'Final instance failed to close.'

    # Build a minimal pre-IPC 0.2.2 host: the original mutex and a hidden main window,
    # with no HID, settings, logs or tray. Its verified PE identity exercises the legacy path.
    $legacyProject=Join-Path $testRoot 'legacy-project'
    New-Item -ItemType Directory -Path $legacyProject -Force | Out-Null
    [IO.File]::WriteAllText((Join-Path $legacyProject 'Legacy.csproj'),'<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>WinExe</OutputType><TargetFramework>net10.0-windows</TargetFramework><UseWPF>true</UseWPF><AssemblyName>EasySwitch</AssemblyName><Version>0.2.2</Version></PropertyGroup></Project>')
    $source=@'
using System;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
class Program {
 [STAThread] static void Main(string[] args) {
  using(var mutex=new Mutex(true,"Local\\"+args[0]+".0",out var first)) {
   if(!first)return;
   var app=new Application{ShutdownMode=ShutdownMode.OnExplicitShutdown};
   var window=new Window{Title="EasySwitch",Width=1,Height=1,ShowInTaskbar=false,ShowActivated=false};window.Show();window.Hide();
   File.WriteAllText(args[1],Environment.ProcessId.ToString());
   var timer=new DispatcherTimer{Interval=TimeSpan.FromSeconds(45)};timer.Tick+=(_,__)=>app.Shutdown();timer.Start();app.Run();
  }
 }
}
'@
    [IO.File]::WriteAllText((Join-Path $legacyProject 'Program.cs'),$source)
    $sdk=Join-Path $env:LOCALAPPDATA 'Microsoft\dotnet\dotnet.exe'
    & $sdk build (Join-Path $legacyProject 'Legacy.csproj') --nologo --verbosity quiet
    if($LASTEXITCODE -ne 0){throw 'Legacy fixture build failed.'}
    $legacyRoot=Join-Path $testRoot ('legacy-'+[guid]::NewGuid().ToString('N'));New-Item -ItemType Directory -Path $legacyRoot -Force | Out-Null
    $scope='EasySwitch.Fixture.'+[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($legacyRoot.ToUpperInvariant()))).Substring(0,24)
    $ready=Join-Path $legacyRoot 'legacy-ready.txt'
    $legacy=Start-Process -FilePath (Join-Path $legacyProject 'bin\Debug\net10.0-windows\EasySwitch.exe') -ArgumentList $scope,('"'+$ready+'"') -WindowStyle Hidden -PassThru
    $children.Add($legacy);Wait-Until {Test-Path -LiteralPath $ready} 'Legacy fixture did not start.'
    $replacement=Launch '0.2.3.0' $legacyRoot
    Wait-Until {$legacy.HasExited} 'Legacy process was not replaced.'
    $launch=Get-Content -LiteralPath (Join-Path $legacyRoot "launch-$($replacement.Id).json") -Raw | ConvertFrom-Json
    if(-not $launch.entered -or $launch.Messages.Count -ne 1){throw 'Legacy replacement did not acquire ownership.'}
    Stop-Fixture $legacyRoot
    Wait-Until {$replacement.HasExited} 'Legacy replacement did not close.'
    [IO.File]::WriteAllText((Join-Path $testRoot 'result.json'),(@{gracefulUpgrade=$true;sameVersionActivates=$true;downgradeBlocked=$true;concurrentNewestWins=$true;legacyHiddenWindowReplaced=$true;hardwareStarted=$false}|ConvertTo-Json))
    Get-Content -LiteralPath (Join-Path $testRoot 'result.json')
}
finally{
    # Only fixture processes started by this script are ever terminated here.
    foreach($child in $children){if(-not $child.HasExited){$child.Kill();$child.WaitForExit()};$child.Dispose()}
}
