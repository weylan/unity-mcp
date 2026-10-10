param(
    [string]$Url = 'https://download.unity3d.com/download_unity/96770f904ca7/Windows64EditorInstaller/UnitySetup64-2022.3.62f3.exe',
    [string]$Output = 'D:\Installers\Unity\UnitySetup64-2022.3.62f3.exe',
    [string]$Proxy = 'http://127.0.0.1:17897',
    [Int64]$ExpectedBytes = 3695061288,
    [string]$ExpectedMd5 = '2e9ec527a1329e0d72c3454047f37d24',
    [Int64]$ChunkBytes = 134217728,
    [int]$Concurrency = 8
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$outputDirectory = Split-Path -Parent $Output
$chunkDirectory = Join-Path $outputDirectory ((Split-Path -Leaf $Output) + '.parts')
$logDirectory = Join-Path $chunkDirectory 'logs'
New-Item -ItemType Directory -Path $outputDirectory, $chunkDirectory, $logDirectory -Force | Out-Null

$chunkCount = [int][math]::Ceiling($ExpectedBytes / [double]$ChunkBytes)
$chunks = @()
for ($index = 0; $index -lt $chunkCount; $index++) {
    $start = [Int64]$index * $ChunkBytes
    $end = [math]::Min($ExpectedBytes - 1, $start + $ChunkBytes - 1)
    $path = Join-Path $chunkDirectory ('part-{0:D3}.bin' -f $index)
    $chunks += [pscustomobject]@{ Index = $index; Start = $start; End = $end; Bytes = $end - $start + 1; Path = $path; Attempts = 0 }
}

function Get-ChunkState($chunk) {
    if (Test-Path -LiteralPath $chunk.Path) {
        $item = Get-Item -LiteralPath $chunk.Path
        if ([Int64]$item.Length -eq $chunk.Bytes) { return 'complete' }
        Remove-Item -LiteralPath $chunk.Path -Force
    }
    return 'missing'
}

$remaining = @($chunks | Where-Object { (Get-ChunkState $_) -ne 'complete' })
$running = @{}
$curl = Join-Path $env:SystemRoot 'System32\curl.exe'
Write-Output ('chunks={0} complete={1} remaining={2} concurrency={3}' -f $chunkCount, ($chunkCount - $remaining.Count), $remaining.Count, $Concurrency)

while ($remaining.Count -gt 0 -or $running.Count -gt 0) {
    while ($remaining.Count -gt 0 -and $running.Count -lt $Concurrency) {
        $chunk = $remaining[0]
        if ($remaining.Count -eq 1) { $remaining = @() } else { $remaining = @($remaining[1..($remaining.Count - 1)]) }
        $chunk.Attempts++
        if ($chunk.Attempts -gt 4) { throw "Repeated download failure: chunk $($chunk.Index)" }
        $stderr = Join-Path $logDirectory ('part-{0:D3}.err.log' -f $chunk.Index)
        $stdout = Join-Path $logDirectory ('part-{0:D3}.out.log' -f $chunk.Index)
        $arguments = @(
            '--location', '--fail', '--retry', '8', '--retry-delay', '2',
            '--connect-timeout', '30', '--max-time', '1800', '--proxy', $Proxy,
            '--range', ('{0}-{1}' -f $chunk.Start, $chunk.End),
            '--output', $chunk.Path, $Url
        )
        $process = Start-Process -FilePath $curl -ArgumentList $arguments -WindowStyle Hidden -RedirectStandardError $stderr -RedirectStandardOutput $stdout -PassThru
        $running[$process.Id] = [pscustomobject]@{ Process = $process; Chunk = $chunk }
        Write-Output ('started part {0}/{1} pid={2}' -f ($chunk.Index + 1), $chunkCount, $process.Id)
    }

    Start-Sleep -Seconds 2
    foreach ($downloadProcessId in @($running.Keys)) {
        $job = $running[$downloadProcessId]
        if (-not $job.Process.HasExited) { continue }
        $job.Process.WaitForExit()
        $exitCode = $job.Process.ExitCode
        $valid = (Test-Path -LiteralPath $job.Chunk.Path) -and ([Int64](Get-Item -LiteralPath $job.Chunk.Path).Length -eq $job.Chunk.Bytes)
        $running.Remove($downloadProcessId)
        if ($valid -and ($null -eq $exitCode -or $exitCode -eq 0)) {
            Write-Output ('completed part {0}/{1} bytes={2}' -f ($job.Chunk.Index + 1), $chunkCount, $job.Chunk.Bytes)
        } else {
            Remove-Item -LiteralPath $job.Chunk.Path -Force -ErrorAction SilentlyContinue
            $remaining += $job.Chunk
            Write-Output ('retry part {0}/{1} exit={2} valid={3}' -f ($job.Chunk.Index + 1), $chunkCount, $exitCode, $valid)
        }
    }
}

$temporaryOutput = $Output + '.partial'
Remove-Item -LiteralPath $temporaryOutput -Force -ErrorAction SilentlyContinue
$writeStream = [IO.File]::Open($temporaryOutput, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
try {
    foreach ($chunk in $chunks) {
        if ((Get-ChunkState $chunk) -ne 'complete') { throw "Chunk is incomplete: $($chunk.Index)" }
        $readStream = [IO.File]::OpenRead($chunk.Path)
        try { $readStream.CopyTo($writeStream) } finally { $readStream.Dispose() }
    }
} finally { $writeStream.Dispose() }

$actualBytes = [Int64](Get-Item -LiteralPath $temporaryOutput).Length
if ($actualBytes -ne $ExpectedBytes) { throw "Combined size mismatch: $actualBytes / $ExpectedBytes" }
$actualMd5 = (Get-FileHash -LiteralPath $temporaryOutput -Algorithm MD5).Hash.ToLowerInvariant()
Write-Output ('combined bytes={0} md5={1}' -f $actualBytes, $actualMd5)
if ($actualMd5 -ne $ExpectedMd5.ToLowerInvariant()) { throw "MD5 mismatch: $actualMd5 / $ExpectedMd5" }
Move-Item -LiteralPath $temporaryOutput -Destination $Output -Force
Write-Output ('verified output={0}' -f $Output)
