param(
    [Parameter(Mandatory)][string]$DistDir,
    [string]$MappingOutputDir = ''
)
$ErrorActionPreference = 'Stop'

# Verify Obfuscar global tool is installed.
# Install once with: dotnet tool install --global Obfuscar.GlobalTool
if (-not (Get-Command obfuscar.console -ErrorAction SilentlyContinue)) {
    Write-Error "Obfuscar not found. Install with: dotnet tool install --global Obfuscar.GlobalTool"
    exit 1
}

$ourDlls = @(
    'DynamoCopilot.Core.dll',
    'DynamoCopilot.GraphInterop.dll',
    'DynamoCopilot.Extension.dll'
)

# Dynamo packages that the extension references at runtime but doesn't ship.
# Obfuscar needs these to resolve the inheritance map.
$dynamoPackages = @(
    'dynamovisualprogramming.core',
    'dynamovisualprogramming.wpfuilibrary',
    'dynamovisualprogramming.dynamocorenodes',
    'dynamovisualprogramming.zerotouchlibrary'
)

function Get-ReferenceFolders([string]$Tfm) {
    $found = [System.Collections.Generic.List[string]]::new()

    # .NET Framework reference assemblies (needed for net48 WPF types like System.Xaml)
    $netFxRefPaths = @(
        'C:\Program Files (x86)\Reference Assemblies\Microsoft\Framework\.NETFramework\v4.8',
        'C:\Program Files\Reference Assemblies\Microsoft\Framework\.NETFramework\v4.8',
        'C:\Program Files (x86)\Reference Assemblies\Microsoft\Framework\.NETFramework\v4.8.1'
    )
    foreach ($p in $netFxRefPaths) {
        if (Test-Path $p) { $found.Add($p) }
    }

    # WPF assemblies (System.Xaml, PresentationCore, etc.) in the .NET runtime
    $wpfPath = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\WPF'
    if (Test-Path $wpfPath) { $found.Add($wpfPath) }

    # Dynamo NuGet packages (DynamoCoreWpf, DynamoCore, etc.)
    $nugetRoot = Join-Path $env:USERPROFILE '.nuget\packages'
    foreach ($pkg in $dynamoPackages) {
        $pkgBase = Join-Path $nugetRoot $pkg
        if (-not (Test-Path $pkgBase)) { continue }
        $versions = Get-ChildItem $pkgBase -Directory | Sort-Object Name -Descending
        foreach ($ver in $versions) {
            foreach ($candidate in @($Tfm, 'net48', 'netstandard2.0')) {
                $libPath = Join-Path $ver.FullName "lib\$candidate"
                if (Test-Path $libPath) { $found.Add($libPath); break }
            }
        }
    }
    return $found.ToArray()
}

function New-ObfuscarXml([string]$InPath, [string]$OutPath, [string[]]$Dlls, [string[]]$ExtraFolders) {
    $sb = New-Object System.Text.StringBuilder
    [void]$sb.AppendLine("<?xml version='1.0'?>")
    [void]$sb.AppendLine("<Obfuscator>")
    [void]$sb.AppendLine("  <Var name=""InPath""              value=""$InPath"" />")
    [void]$sb.AppendLine("  <Var name=""OutPath""             value=""$OutPath"" />")
    [void]$sb.AppendLine("  <Var name=""KeepPublicApi""       value=""true"" />")
    [void]$sb.AppendLine("  <Var name=""HidePrivateApi""      value=""true"" />")
    [void]$sb.AppendLine("  <Var name=""RenameProperties""    value=""true"" />")
    [void]$sb.AppendLine("  <Var name=""RenameEvents""        value=""true"" />")
    [void]$sb.AppendLine("  <Var name=""RenameFields""        value=""true"" />")
    [void]$sb.AppendLine("  <Var name=""OptimizeMethods""     value=""true"" />")
    [void]$sb.AppendLine("  <Var name=""RegenerateDebugInfo"" value=""false"" />")
    if ($ExtraFolders -and $ExtraFolders.Count -gt 0) {
        $folderList = $ExtraFolders -join ';'
        [void]$sb.AppendLine("  <Var name=""ExtraFrameworkFolders"" value=""$folderList"" />")
    }
    [void]$sb.AppendLine("")
    foreach ($dll in $Dlls) {
        $fullPath = Join-Path $InPath $dll
        [void]$sb.AppendLine("  <Module file=""$fullPath"" />")
    }
    [void]$sb.AppendLine("</Obfuscator>")
    return $sb.ToString()
}

foreach ($tfmDir in Get-ChildItem $DistDir -Directory) {
    $tfmPath = $tfmDir.FullName
    $tfm     = $tfmDir.Name

    $present = @($ourDlls | Where-Object { Test-Path (Join-Path $tfmPath $_) })
    if ($present.Count -eq 0) {
        Write-Host "[$tfm] No DynamoCopilot DLLs found, skipping"
        continue
    }

    Write-Host "[$tfm] Obfuscating: $($present -join ', ')"

    $outTmp = Join-Path $env:TEMP "ObfuscarOut_$tfm"
    if (Test-Path $outTmp) { Remove-Item $outTmp -Recurse -Force }
    New-Item -ItemType Directory $outTmp | Out-Null

    $extraFolders = Get-ReferenceFolders -Tfm $tfm
    Write-Host "[$tfm] Extra reference folders: $($extraFolders.Count)"

    $xmlPath = Join-Path $env:TEMP "obfuscar_$tfm.xml"
    New-ObfuscarXml -InPath $tfmPath -OutPath $outTmp -Dlls $present -ExtraFolders $extraFolders |
        Set-Content $xmlPath -Encoding UTF8

    obfuscar.console $xmlPath
    if ($LASTEXITCODE -ne 0) {
        Remove-Item $xmlPath -Force -ErrorAction SilentlyContinue
        Remove-Item $outTmp  -Recurse -Force -ErrorAction SilentlyContinue
        Write-Error "Obfuscar failed for [$tfm]"
        exit 1
    }
    Remove-Item $xmlPath -Force

    foreach ($dll in $present) {
        $src = Join-Path $outTmp $dll
        $dst = Join-Path $tfmPath $dll
        if (Test-Path $src) {
            Copy-Item $src $dst -Force
        } else {
            Write-Warning "[$tfm] Obfuscar did not produce $dll, original kept"
        }
    }

    $mappingSrc = Join-Path $outTmp 'Mapping.xml'
    if (Test-Path $mappingSrc) {
        if ($MappingOutputDir) {
            $mapDest = Join-Path $MappingOutputDir "Mapping_$tfm.xml"
        } else {
            $mapDest = Join-Path $DistDir "Mapping_$tfm.xml"
        }
        Copy-Item $mappingSrc $mapDest -Force
        Write-Host "[$tfm] Mapping saved to $mapDest (keep private)"
    }

    Remove-Item $outTmp -Recurse -Force

    $pdbs = Get-ChildItem $tfmPath -Filter 'DynamoCopilot.*.pdb' -ErrorAction SilentlyContinue
    foreach ($pdb in $pdbs) {
        Remove-Item $pdb.FullName -Force
        Write-Host "[$tfm] Removed $($pdb.Name)"
    }

    Write-Host "[$tfm] Done"
}

Write-Host "Obfuscation complete."
