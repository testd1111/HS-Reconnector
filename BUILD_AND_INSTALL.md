# Build and Run

## Build Requirements

- Visual Studio 2022 / Build Tools with MSBuild and .NET Framework 4.7.2 targeting pack (no external NuGet packages required).

## Build Release x64

```powershell
& $msbuild 'HS-Reconnector.sln' /t:Clean,Build /p:Configuration=Release /p:Platform=x64 /v:minimal
```

Expected output:

```text
artifacts\HS-Reconnector\Release\HS-Reconnector.exe
```

## Run Tests and Verify Binary

```powershell
.\tests\Run-Tests.ps1
.\tests\Verify-Release.ps1 -Exe 'artifacts\HS-Reconnector\Release\HS-Reconnector.exe'
```
