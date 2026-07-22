# SSCS

EDI / EDI13 to SSCS desktop converter and reference fixtures.

All project files live under [`SSCS_EDI_FORMAT`](./SSCS_EDI_FORMAT).

## Quick start

```powershell
cd SSCS_EDI_FORMAT\converter
dotnet publish src/EdiToSscs.App -c Release -r win-x64 -o publish
```

The portable EXE is written to `SSCS_EDI_FORMAT/converter/publish/EDI-to-SSCS-Converter.exe`.
