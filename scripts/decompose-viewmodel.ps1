
# SessionWorkspaceViewModel Decomposition Script
# Splits the 2578-line god class into ~10 partial class files + sub-viewmodels

$file = "c:\Researcher\src\ResearchHive\ViewModels\SessionWorkspaceViewModel.cs"
$lines = [System.IO.File]::ReadAllLines($file)
$utf8NoBom = New-Object System.Text.UTF8Encoding $false
$dir = "c:\Researcher\src\ResearchHive\ViewModels"

Write-Host "Read $($lines.Length) lines from original file"

# Common usings for all partial files (same as root)
$headerText = @"
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ResearchHive.Core.Configuration;
using ResearchHive.Core.Models;
using ResearchHive.Core.Services;
using ResearchHive.Services;
using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;

namespace ResearchHive.ViewModels;

public partial class SessionWorkspaceViewModel
{
"@

function Write-PartialFile {
    param(
        [string]$Name,
        [int[][]]$Ranges
    )
    $bodyLines = New-Object System.Collections.Generic.List[string]
    foreach ($range in $Ranges) {
        $start = $range[0] - 1  # 1-indexed to 0-indexed
        $end   = $range[1] - 1
        for ($i = $start; $i -le $end; $i++) {
            $bodyLines.Add($lines[$i])
        }
        # Add blank separator between non-contiguous ranges
        if ($Ranges.IndexOf($range) -lt $Ranges.Count - 1) {
            $bodyLines.Add("")
        }
    }
    $body = [string]::Join("`r`n", $bodyLines)
    $content = "$headerText`r`n$body`r`n}`r`n"
    $path = "$dir\SessionWorkspaceViewModel.$Name.cs"
    [System.IO.File]::WriteAllText($path, $content, $utf8NoBom)
    Write-Host "  Created SessionWorkspaceViewModel.$Name.cs ($($bodyLines.Count) lines)"
}

# --- Create partial class files ---

# 1. Research: lines 531-739
Write-PartialFile -Name "Research" -Ranges @(,@(531, 739))

# 2. Evidence: lines 741-932
Write-PartialFile -Name "Evidence" -Ranges @(,@(741, 932))

# 3. NotebookQa: lines 934-950 (AddNote) + 1066-1129 (AskFollowUpAsync)
Write-PartialFile -Name "NotebookQa" -Ranges @(@(934, 950), @(1066, 1129))

# 4. Crud: lines 952-1064 (Delete commands) + 1131-1187 (Report handler + Ingestion)
Write-PartialFile -Name "Crud" -Ranges @(@(952, 1064), @(1131, 1187))

# 5. DomainRunners: lines 1188-1308 + 1805-1865 (BuildMaterialComparisonTable)
Write-PartialFile -Name "DomainRunners" -Ranges @(@(1188, 1308), @(1805, 1865))

# 6. RepoIntelligence: lines 1309-1461 + 1470-1495 (skip RefreshFusionInputOptions 1462-1469)
Write-PartialFile -Name "RepoIntelligence" -Ranges @(@(1309, 1461), @(1470, 1495))

# 7. Export: lines 1497-1573 + 1919-1934 (ViewLogs)
Write-PartialFile -Name "Export" -Ranges @(@(1497, 1573), @(1919, 1934))

# 8. HiveMind: lines 1574-1691
Write-PartialFile -Name "HiveMind" -Ranges @(,@(1574, 1691))

# 9. Verification: lines 1692-1804 + 1867-1916
Write-PartialFile -Name "Verification" -Ranges @(@(1692, 1804), @(1867, 1916))

# --- Create SubViewModels file (separate classes, NOT partial of SessionWorkspaceViewModel) ---
$subVmHeader = @"
using CommunityToolkit.Mvvm.ComponentModel;
using ResearchHive.Core.Models;
using ResearchHive.Core.Services;

namespace ResearchHive.ViewModels;

"@
$subVmBodyLines = New-Object System.Collections.Generic.List[string]
for ($i = 1936; $i -le ($lines.Length - 1); $i++) {
    $subVmBodyLines.Add($lines[$i])
}
$subVmBody = [string]::Join("`r`n", $subVmBodyLines)
$subVmContent = "$subVmHeader$subVmBody`r`n"
[System.IO.File]::WriteAllText("$dir\SessionWorkspaceSubViewModels.cs", $subVmContent, $utf8NoBom)
Write-Host "  Created SessionWorkspaceSubViewModels.cs ($($subVmBodyLines.Count) lines)"

# --- Rewrite root file: lines 1-529 + RefreshFusionInputOptions (1462-1469) + class close ---
$rootLines = New-Object System.Collections.Generic.List[string]
for ($i = 0; $i -le 528; $i++) {
    $rootLines.Add($lines[$i])
}
$rootLines.Add("")
# Add RefreshFusionInputOptions (lines 1462-1469)
for ($i = 1461; $i -le 1468; $i++) {
    $rootLines.Add($lines[$i])
}
$rootLines.Add("}")

$rootContent = [string]::Join("`r`n", $rootLines)
[System.IO.File]::WriteAllText($file, $rootContent, $utf8NoBom)
Write-Host "  Rewrote root SessionWorkspaceViewModel.cs ($($rootLines.Count) lines)"

Write-Host "`nDecomposition complete! Build to verify..."
