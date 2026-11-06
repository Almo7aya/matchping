<#
Builds MatchPing: one .exe for Windows 10/11 x64, WPF on the .NET Framework 4.8 that comes with Windows (nothing to
install). The C# compiler is the one in the .NET SDK; it compiles against the Framework's own assemblies, so no
packages are downloaded.
  .\build.ps1        dist\MatchPing.exe
#>
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$csc = Get-ChildItem "$env:ProgramFiles\dotnet\sdk" -Recurse -Filter csc.dll | Where-Object { $_.FullName -match 'Roslyn\\bincore' } |
	Sort-Object FullName -Descending | Select-Object -First 1 -ExpandProperty FullName
if (-not $csc) { throw 'No C# compiler: install the .NET SDK (https://dotnet.microsoft.com/download)' }
$fw = "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319"
if (-not (Test-Path "$fw\WPF\PresentationFramework.dll")) { throw "No .NET Framework 4.8 in $fw" }
$refs = @('mscorlib', 'System', 'System.Core', 'System.Xaml', 'System.Web.Extensions', 'System.Windows.Forms', 'System.Drawing') |
	ForEach-Object { "/reference:$fw\$_.dll" }
$refs += @('WindowsBase', 'PresentationCore', 'PresentationFramework') | ForEach-Object { "/reference:$fw\WPF\$_.dll" }
$src = (Get-ChildItem "$root\src\*.cs").FullName
New-Item -ItemType Directory -Force "$root\dist" | Out-Null
$exe = "$root\dist\MatchPing.exe"

function Compile([string[]]$extra) {
	dotnet $csc /nologo /noconfig /nostdlib+ /langversion:latest /optimize+ /platform:x64 /target:winexe /out:$exe `
		/win32manifest:$root\app.manifest @extra @refs $src
	if ($LASTEXITCODE) { throw 'build failed' }
}

# the first build draws the icon for the second
Compile @()
Start-Process -FilePath $exe -ArgumentList '--write-icon', "`"$root\dist\MatchPing.ico`"" -Wait
Compile @("/win32icon:$root\dist\MatchPing.ico")
Get-Item $exe | ForEach-Object { '{0}  {1:N0} KB  sha256 {2}' -f $_.Name, ($_.Length / 1KB), (Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLower() }
