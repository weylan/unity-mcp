param(
    [string]$VmName = 'pcv',
    [string]$IsoPath = 'D:\WinDualBootSetup\Windows.iso',
    [string]$VmRoot = 'D:\VMs\HyperV',
    [string]$SwitchName = 'MacDirect',
    [string]$LocalUser = 'wang8',
    [int]$ProcessorCount = 8,
    [UInt64]$StartupMemoryBytes = 12GB,
    [UInt64]$MinimumMemoryBytes = 8GB,
    [UInt64]$MaximumMemoryBytes = 24GB,
    [UInt64]$VhdSizeBytes = 256GB,
    [switch]$ResumePreparedVhd
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Assert-Administrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = [Security.Principal.WindowsPrincipal]::new($identity)
    if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw 'This script must run in an elevated PowerShell session.'
    }
}

function Invoke-Native {
    param(
        [Parameter(Mandatory = $true)][string]$FilePath,
        [Parameter(ValueFromRemainingArguments = $true)][string[]]$Arguments
    )
    & $FilePath @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "Command failed ($LASTEXITCODE): $FilePath $($Arguments -join ' ')"
    }
}

function New-Password {
    $alphabet = 'ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz23456789'.ToCharArray()
    $bytes = [byte[]]::new(28)
    [Security.Cryptography.RandomNumberGenerator]::Fill($bytes)
    -join ($bytes | ForEach-Object { $alphabet[$_ % $alphabet.Length] })
}

function Set-PrivateFileAcl {
    param([Parameter(Mandatory = $true)][string]$Path)
    $acl = Get-Acl -LiteralPath $Path
    $acl.SetAccessRuleProtection($true, $false)
    foreach ($rule in @($acl.Access)) {
        [void]$acl.RemoveAccessRule($rule)
    }
    $sidValues = @(
        [Security.Principal.WindowsIdentity]::GetCurrent().User.Value,
        'S-1-5-18',       # LOCAL SYSTEM
        'S-1-5-32-544'    # local Administrators, independent of UI language
    )
    foreach ($sidValue in $sidValues) {
        $identity = ([Security.Principal.SecurityIdentifier]$sidValue).Translate([Security.Principal.NTAccount]).Value
        $rule = [Security.AccessControl.FileSystemAccessRule]::new(
            $identity,
            'FullControl',
            'Allow'
        )
        $acl.AddAccessRule($rule)
    }
    Set-Acl -LiteralPath $Path -AclObject $acl
}

function Get-FreeDriveLetter {
    param(
        [Parameter(Mandatory = $true)][char[]]$Preferred,
        [Parameter()][char[]]$Exclude = @()
    )
    $used = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($volume in @(Get-Volume -ErrorAction SilentlyContinue)) {
        if ($volume.DriveLetter) { [void]$used.Add([string]$volume.DriveLetter) }
    }
    foreach ($drive in @(Get-PSDrive -PSProvider FileSystem -ErrorAction SilentlyContinue)) {
        if ($drive.Name) { [void]$used.Add([string]$drive.Name) }
    }
    foreach ($letter in $Preferred) {
        if ($Exclude -contains $letter) { continue }
        if (-not $used.Contains([string]$letter)) { return [string]$letter }
    }
    throw "No free drive letter in the requested set: $($Preferred -join ', ')"
}

Assert-Administrator

if (-not (Test-Path -LiteralPath $IsoPath -PathType Leaf)) {
    throw "Windows ISO not found: $IsoPath"
}
if (-not (Get-VMSwitch -Name $SwitchName -ErrorAction SilentlyContinue)) {
    throw "Hyper-V switch not found: $SwitchName"
}
if (Get-VM -Name $VmName -ErrorAction SilentlyContinue) {
    throw "VM already exists: $VmName. This script will not overwrite it."
}

$vmDir = Join-Path $VmRoot $VmName
$diskDir = Join-Path $vmDir 'Virtual Hard Disks'
$vhdPath = Join-Path $diskDir "$VmName.vhdx"
$credentialPath = Join-Path $vmDir "$VmName-credentials.txt"
$workDir = Join-Path $vmDir 'bootstrap'
New-Item -ItemType Directory -Force -Path $diskDir, $workDir | Out-Null
$vhdAlreadyExists = Test-Path -LiteralPath $vhdPath
if ($vhdAlreadyExists) {
    if (-not $ResumePreparedVhd) {
        throw "VHDX already exists: $vhdPath. Pass -ResumePreparedVhd only for a VHD created by a previous interrupted run."
    }
    $existingVhd = Get-VHD -Path $vhdPath
    if ($existingVhd.Attached -or $existingVhd.Size -ne $VhdSizeBytes) {
        throw "Refusing to reuse an attached or unexpected VHDX: $vhdPath"
    }
}

$password = New-Password
$credentialText = @"
VM: $VmName
Windows user: $LocalUser
Windows password: $password
Edition: Windows 11 Pro (unactivated until a separate license is supplied)
Created: $(Get-Date -Format o)
"@
$credentialText | Set-Content -LiteralPath $credentialPath -Encoding UTF8NoBOM
Set-PrivateFileAcl -Path $credentialPath

$isoMounted = $false
$vhdMounted = $false
$isoWasAttached = $false
$isoDrive = $null
$vhdDisk = $null
$efiLetter = $null
$osLetter = $null
try {
    $existingIso = Get-DiskImage -ImagePath $IsoPath -ErrorAction SilentlyContinue
    $isoWasAttached = [bool]($existingIso -and $existingIso.Attached)
    $iso = if ($isoWasAttached) { $existingIso } else { Mount-DiskImage -ImagePath $IsoPath -PassThru }
    $isoMounted = -not $isoWasAttached
    $isoVolume = $iso | Get-Volume
    $isoDrive = "$($isoVolume.DriveLetter):"
    $imageFile = Join-Path $isoDrive 'sources\install.esd'
    if (-not (Test-Path -LiteralPath $imageFile)) {
        $imageFile = Join-Path $isoDrive 'sources\install.wim'
    }
    if (-not (Test-Path -LiteralPath $imageFile)) {
        throw "No install.esd or install.wim found on $IsoPath"
    }

    if (-not $vhdAlreadyExists) {
        Write-Host "Creating $VhdSizeBytes VHDX at $vhdPath"
        New-VHD -Path $vhdPath -SizeBytes $VhdSizeBytes -Dynamic | Out-Null
    }
    $vhdDisk = Mount-VHD -Path $vhdPath -Passthru | Get-Disk
    $vhdMounted = $true
    $vhdDiskNumber = [int]$vhdDisk.Number
    Set-Disk -Number $vhdDiskNumber -IsOffline $false
    Set-Disk -Number $vhdDiskNumber -IsReadOnly $false
    $existingEfi = @(Get-Partition -DiskNumber $vhdDiskNumber | Where-Object {
        $_.GptType -eq '{c12a7328-f81f-11d2-ba4b-00a0c93ec93b}'
    })
    $existingOs = @(Get-Partition -DiskNumber $vhdDiskNumber | Where-Object {
        $_.GptType -eq '{ebd0a0a2-b9e5-4433-87c0-68b6b72699c7}'
    } | Where-Object {
        $volume = $_ | Get-Volume -ErrorAction SilentlyContinue
        $volume -and $volume.FileSystem -eq 'NTFS' -and $volume.FileSystemLabel -eq 'Windows'
    })
    $reusePreparedVhd = $vhdAlreadyExists -and $existingEfi.Count -eq 1 -and $existingOs.Count -eq 1
    if ($reusePreparedVhd) {
        $efi = $existingEfi[0]
        $os = $existingOs[0]
    }
    else {
        Initialize-Disk -Number $vhdDiskNumber -PartitionStyle GPT | Out-Null
        $efi = New-Partition -DiskNumber $vhdDiskNumber -Size 260MB -GptType '{C12A7328-F81F-11D2-BA4B-00A0C93EC93B}'
        Format-Volume -Partition $efi -FileSystem FAT32 -NewFileSystemLabel 'System' -Confirm:$false | Out-Null
        $msr = New-Partition -DiskNumber $vhdDiskNumber -Size 16MB -GptType '{E3C9E316-0B5C-4DB8-817D-F92DF00215AE}'
        $os = New-Partition -DiskNumber $vhdDiskNumber -UseMaximumSize -GptType '{EBD0A0A2-B9E5-4433-87C0-68B6B72699C7}'
        Format-Volume -Partition $os -FileSystem NTFS -NewFileSystemLabel 'Windows' -Confirm:$false | Out-Null
    }
    $efiVolume = @($efi | Get-Volume -ErrorAction SilentlyContinue) | Select-Object -First 1
    $osVolume = @($os | Get-Volume -ErrorAction SilentlyContinue) | Select-Object -First 1
    if ($efiVolume -and $efiVolume.DriveLetter) { $efiLetter = [string]$efiVolume.DriveLetter }
    else { $efiLetter = Get-FreeDriveLetter -Preferred @('S', 'R', 'Z') }
    if ($osVolume -and $osVolume.DriveLetter) { $osLetter = [string]$osVolume.DriveLetter }
    else { $osLetter = Get-FreeDriveLetter -Preferred @('T', 'W', 'Y') -Exclude @([char]$efiLetter) }
    if (-not ($efiVolume -and $efiVolume.DriveLetter)) {
        Add-PartitionAccessPath -DiskNumber $vhdDiskNumber -PartitionNumber $efi.PartitionNumber -AccessPath ($efiLetter + ':\')
    }
    if (-not ($osVolume -and $osVolume.DriveLetter)) {
        Add-PartitionAccessPath -DiskNumber $vhdDiskNumber -PartitionNumber $os.PartitionNumber -AccessPath ($osLetter + ':\')
    }
    $efiPath = $efiLetter + ':\'
    $osPath = $osLetter + ':\'
    $osVolume = Get-Volume -DriveLetter $osLetter -ErrorAction SilentlyContinue
    if (-not $osVolume -or $osVolume.FileSystem -ne 'NTFS' -or $osVolume.FileSystemLabel -ne 'Windows' -or -not (Test-Path -LiteralPath $osPath)) {
        throw "The new VHD OS partition is not mounted at $osPath; refusing to write another disk."
    }
    $imageAlreadyApplied = Test-Path -LiteralPath (Join-Path $osPath 'Windows')

    if ($imageAlreadyApplied) {
        Write-Host "Windows image is already present at $osPath; resuming bootstrap without reapplying it."
    }
    else {
        Write-Host "Applying Windows image from $imageFile"
        Invoke-Native dism.exe '/Apply-Image' "/ImageFile:$imageFile" '/Index:4' "/ApplyDir:$osPath" '/CheckIntegrity'
    }

    $escapedPassword = [Security.SecurityElement]::Escape($password)
    $unattend = @"
<?xml version="1.0" encoding="utf-8"?>
<unattend xmlns="urn:schemas-microsoft-com:unattend" xmlns:wcm="http://schemas.microsoft.com/WMIConfig/2002/State" xmlns:wa="http://www.microsoft.com/Windows/AssignedAccess/2017/config">
  <settings pass="specialize">
    <component name="Microsoft-Windows-Shell-Setup" processorArchitecture="amd64" publicKeyToken="31bf3856ad364e35" language="neutral" versionScope="nonSxS">
      <ComputerName>$VmName</ComputerName>
      <TimeZone>China Standard Time</TimeZone>
      <RegisteredOwner>wang8</RegisteredOwner>
    </component>
  </settings>
  <settings pass="oobeSystem">
    <component name="Microsoft-Windows-International-Core" processorArchitecture="amd64" publicKeyToken="31bf3856ad364e35" language="neutral" versionScope="nonSxS">
      <InputLocale>zh-CN</InputLocale>
      <SystemLocale>zh-CN</SystemLocale>
      <UILanguage>zh-CN</UILanguage>
      <UserLocale>zh-CN</UserLocale>
    </component>
    <component name="Microsoft-Windows-Shell-Setup" processorArchitecture="amd64" publicKeyToken="31bf3856ad364e35" language="neutral" versionScope="nonSxS">
      <UserAccounts>
        <LocalAccounts>
          <LocalAccount wcm:action="add">
            <Name>$LocalUser</Name>
            <DisplayName>$LocalUser</DisplayName>
            <Group>Administrators</Group>
            <Password><Value>$escapedPassword</Value><PlainText>true</PlainText></Password>
          </LocalAccount>
        </LocalAccounts>
      </UserAccounts>
      <AutoLogon>
        <Enabled>true</Enabled>
        <LogonCount>1</LogonCount>
        <Username>$LocalUser</Username>
        <Password><Value>$escapedPassword</Value><PlainText>true</PlainText></Password>
      </AutoLogon>
      <OOBE>
        <HideEULAPage>true</HideEULAPage>
        <HideLocalAccountScreen>true</HideLocalAccountScreen>
        <HideOEMRegistrationScreen>true</HideOEMRegistrationScreen>
        <HideOnlineAccountScreens>true</HideOnlineAccountScreens>
        <NetworkLocation>Work</NetworkLocation>
        <ProtectYourPC>3</ProtectYourPC>
        <SkipMachineOOBE>true</SkipMachineOOBE>
        <SkipUserOOBE>true</SkipUserOOBE>
      </OOBE>
      <FirstLogonCommands>
        <SynchronousCommand wcm:action="add">
          <Order>1</Order>
          <CommandLine>cmd.exe /c powershell.exe -NoProfile -ExecutionPolicy Bypass -File C:\ProgramData\pcv\firstboot.ps1</CommandLine>
          <Description>Initialize PCV bootstrap marker</Description>
        </SynchronousCommand>
      </FirstLogonCommands>
    </component>
  </settings>
</unattend>
"@
    $firstBoot = @'
$ErrorActionPreference = 'Continue'
$marker = 'C:\ProgramData\pcv\firstboot-ran.txt'
New-Item -ItemType Directory -Force -Path 'C:\ProgramData\pcv' | Out-Null
Set-Content -LiteralPath $marker -Value (Get-Date -Format o) -Encoding UTF8
try {
    Enable-PSRemoting -SkipNetworkProfileCheck -Force | Out-Null
} catch {}
try {
    Set-Service -Name WinRM -StartupType Automatic
    Start-Service -Name WinRM
} catch {}
try {
    Add-WindowsCapability -Online -Name OpenSSH.Server~~~~0.0.1.0 | Out-Null
    Set-Service -Name sshd -StartupType Automatic
    Start-Service -Name sshd
    if (-not (Get-NetFirewallRule -Name 'OpenSSH-Server-In-TCP' -ErrorAction SilentlyContinue)) {
        New-NetFirewallRule -Name 'OpenSSH-Server-In-TCP' -DisplayName 'OpenSSH Server (sshd)' -Enabled True -Direction Inbound -Protocol TCP -Action Allow -LocalPort 22 | Out-Null
    }
} catch {}
'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon' | ForEach-Object {
    foreach ($name in @('AutoAdminLogon', 'DefaultPassword', 'DefaultUserName', 'DefaultDomainName')) {
        Remove-ItemProperty -LiteralPath $_ -Name $name -ErrorAction SilentlyContinue
    }
}
foreach ($sensitivePath in @(
    'C:\Autounattend.xml',
    'C:\Windows\Panther\Unattend.xml',
    'C:\Windows\System32\Sysprep\unattend.xml'
)) {
    Remove-Item -LiteralPath $sensitivePath -Force -ErrorAction SilentlyContinue
}
'PCV first boot completed' | Set-Content -LiteralPath 'C:\ProgramData\pcv\firstboot-status.txt' -Encoding UTF8
'@
    $firstBootPath = Join-Path $osPath 'ProgramData\pcv\firstboot.ps1'
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $firstBootPath) | Out-Null
    New-Item -ItemType Directory -Force -Path (Join-Path $osPath 'Windows\Panther'), (Join-Path $osPath 'Windows\System32\Sysprep') | Out-Null
    $unattend | Set-Content -LiteralPath (Join-Path $osPath 'Autounattend.xml') -Encoding UTF8NoBOM
    $unattend | Set-Content -LiteralPath (Join-Path $osPath 'Windows\Panther\Unattend.xml') -Encoding UTF8NoBOM
    $unattend | Set-Content -LiteralPath (Join-Path $osPath 'Windows\System32\Sysprep\unattend.xml') -Encoding UTF8NoBOM
    $firstBoot | Set-Content -LiteralPath $firstBootPath -Encoding UTF8NoBOM
    Invoke-Native bcdboot.exe (Join-Path $osPath 'Windows') '/s' $efiPath '/f' 'UEFI'
}
finally {
    if ($vhdMounted) { Dismount-VHD -Path $vhdPath -ErrorAction SilentlyContinue }
    if ($isoMounted -and -not $isoWasAttached) { Dismount-DiskImage -ImagePath $IsoPath -ErrorAction SilentlyContinue }
}

Write-Host "Registering Hyper-V VM $VmName"
$vmCreated = $false
try {
    New-VM -Name $VmName -Generation 2 -MemoryStartupBytes $StartupMemoryBytes -SwitchName $SwitchName -Path $VmRoot | Out-Null
    $vmCreated = $true
    Set-VM -Name $VmName -ProcessorCount $ProcessorCount -AutomaticStartAction StartIfRunning -AutomaticStopAction ShutDown -AutomaticCheckpointsEnabled $false
    Set-VMMemory -VMName $VmName -DynamicMemoryEnabled $true -MinimumBytes $MinimumMemoryBytes -StartupBytes $StartupMemoryBytes -MaximumBytes $MaximumMemoryBytes
    Add-VMHardDiskDrive -VMName $VmName -ControllerType SCSI -ControllerNumber 0 -ControllerLocation 0 -Path $vhdPath
    Set-VMFirmware -VMName $VmName -EnableSecureBoot On -SecureBootTemplate 'MicrosoftWindows'
    Set-VMKeyProtector -VMName $VmName -NewLocalKeyProtector | Out-Null
    Enable-VMTPM -VMName $VmName
    Enable-VMIntegrationService -VMName $VmName -Name 'Guest Service Interface' -ErrorAction SilentlyContinue
    Start-VM -Name $VmName | Out-Null
}
catch {
    if ($vmCreated) { Remove-VM -Name $VmName -Force -ErrorAction SilentlyContinue }
    throw
}

Write-Host "Started $VmName. Credentials: $credentialPath"
Write-Host "The VM is Windows 11 Pro and remains unactivated until a separate license is supplied."
