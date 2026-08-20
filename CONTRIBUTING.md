# Contributing

BeamSplit is a Windows WPF application targeting .NET 10. Install the .NET 10 SDK, clone
the repository on Windows, then run:

```powershell
dotnet restore
dotnet build -c Release
dotnet run --project BeamSplit.csproj -- --selftest selftest.txt
```

Keep changes focused, avoid committing runtime profiles or credentials, update the
changelog for user-visible behavior, and include the relevant self-test or manual test
result in the pull request. Controller, tiling, and launch changes should be exercised
with at least two real instances before release.
