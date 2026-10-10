param(
    [Parameter(ParameterSetName='Block', Mandatory=$true)][scriptblock]$ScriptBlock,
    [Parameter(ParameterSetName='File', Mandatory=$true)][string]$FilePath,
    [object[]]$ArgumentList = @(),
    [string]$VmName = 'pcv',
    [string]$CredentialPath = 'D:\VMs\HyperV\pcv\pcv-credentials.txt'
)
$ErrorActionPreference = 'Stop'
$session = $null
$credential = $null
$plain = $null
try {
    $lines = Get-Content -LiteralPath $CredentialPath
    $user = (($lines | Where-Object { $_ -like 'Windows user:*' }) -split ':', 2)[1].Trim()
    $plain = (($lines | Where-Object { $_ -like 'Windows password:*' }) -split ':', 2)[1].Trim()
    $credential = [PSCredential]::new("$VmName\$user", (ConvertTo-SecureString $plain -AsPlainText -Force))
    $lines = $null
    $plain = $null
    $session = New-PSSession -VMName $VmName -Credential $credential
    if ($PSCmdlet.ParameterSetName -eq 'File') {
        Invoke-Command -Session $session -FilePath $FilePath -ArgumentList $ArgumentList -ErrorAction Stop
    } else {
        Invoke-Command -Session $session -ScriptBlock $ScriptBlock -ArgumentList $ArgumentList -ErrorAction Stop
    }
} finally {
    if ($session) { Remove-PSSession -Session $session -ErrorAction SilentlyContinue }
    $credential = $null
    $plain = $null
}
