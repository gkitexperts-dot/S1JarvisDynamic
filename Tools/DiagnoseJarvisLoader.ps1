param(
    [Parameter(Mandatory=$true)]
    [string]$JarvisDll
)

$ErrorActionPreference = 'Stop'

function Write-ExceptionDetails {
    param([System.Exception]$Exception, [int]$Index = -1)

    $prefix = if ($Index -ge 0) { "LoaderExceptions[$Index]" } else { "Exception" }
    Write-Host ""
    Write-Host "===== $prefix ====="
    Write-Host ($Exception.GetType().FullName)
    Write-Host $Exception.Message

    if ($Exception.PSObject.Properties.Name -contains 'FileName' -and $Exception.FileName) {
        Write-Host ("FileName: " + $Exception.FileName)
    }

    if ($Exception.PSObject.Properties.Name -contains 'FusionLog' -and $Exception.FusionLog) {
        Write-Host "--- FusionLog ---"
        Write-Host $Exception.FusionLog
    }

    if ($Exception.InnerException) {
        Write-Host "--- InnerException ---"
        Write-ExceptionDetails -Exception $Exception.InnerException
    }
}

$fullPath = [System.IO.Path]::GetFullPath($JarvisDll)
Write-Host "Jarvis loader diagnostic"
Write-Host "DLL: $fullPath"
Write-Host ("CLR: " + [System.Environment]::Version)
Write-Host ("Process bitness: " + ($(if ([Environment]::Is64BitProcess) { 'x64' } else { 'x86' })))
Write-Host ""

if (-not [System.IO.File]::Exists($fullPath)) {
    throw "File not found: $fullPath"
}

try {
    $assembly = [System.Reflection.Assembly]::LoadFrom($fullPath)
    Write-Host ("Assembly loaded: " + $assembly.FullName)

    # Soft1 appears to enumerate plugin types during NETDLL discovery. Reproduce
    # that exact class of operation so ReflectionTypeLoadException exposes the
    # real missing/broken dependency through LoaderExceptions.
    $types = $assembly.GetTypes()
    Write-Host ("SUCCESS: GetTypes() returned " + $types.Length + " types.")

    Write-Host ""
    Write-Host "Referenced assemblies:"
    foreach ($reference in $assembly.GetReferencedAssemblies() | Sort-Object Name) {
        Write-Host ("  " + $reference.FullName)
    }

    exit 0
}
catch [System.Reflection.ReflectionTypeLoadException] {
    $ex = $_.Exception
    Write-Host "FAIL: ReflectionTypeLoadException"
    Write-Host $ex.Message

    if ($ex.LoaderExceptions) {
        for ($i = 0; $i -lt $ex.LoaderExceptions.Length; $i++) {
            if ($ex.LoaderExceptions[$i]) {
                Write-ExceptionDetails -Exception $ex.LoaderExceptions[$i] -Index $i
            }
        }
    }

    Write-Host ""
    Write-Host "Referenced assemblies (metadata):"
    try {
        $assembly = [System.Reflection.Assembly]::LoadFrom($fullPath)
        foreach ($reference in $assembly.GetReferencedAssemblies() | Sort-Object Name) {
            Write-Host ("  " + $reference.FullName)
        }
    }
    catch { }

    exit 2
}
catch {
    Write-Host "FAIL: loader diagnostic exception"
    Write-ExceptionDetails -Exception $_.Exception
    exit 3
}
