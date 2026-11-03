$ErrorActionPreference = 'Stop'
Set-Location -LiteralPath $PSScriptRoot
$runFolder = Join-Path $PSScriptRoot ('test-runs/' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $runFolder | Out-Null
$names = @('Cashier.Release110','Cashier.PartnerCodes','Cashier.InvoiceConservative','Cashier.MonthlyDebt','Cashier.SettlementStatus','Cashier.InvoiceBatch','Cashier.InvoiceQuickCustomer','Cashier.StressCalculation','Cashier.StatefulCalculation','Cashier.PartnerGuard',
           'Cashier.SelfTest','Cashier.Regression','Cashier.DeepCalculation',
           'Cashier.AutoTest','Cashier.AccountDrill','Cashier.InvoiceCalc','Cashier.PeriodInvoiceCash','Cashier.OpeningBalanceTests','Cashier.DataExchangeTests')
foreach ($name in $names) {
    dotnet build "tests/$name/$name.csproj" -c Release -p:NuGetAudit=false
    if ($LASTEXITCODE -ne 0) { throw "Build failed: $name" }
    dotnet "tests/$name/bin/Release/net10.0/$name.dll" (Join-Path $runFolder $name) |
        Tee-Object -FilePath (Join-Path $runFolder "$name.txt")
    if ($LASTEXITCODE -ne 0) { throw "Test failed: $name" }
}
dotnet build 'tests/Cashier.PartnerCodesUi/Cashier.PartnerCodesUi.csproj' -c Release -p:NuGetAudit=false
if ($LASTEXITCODE -ne 0) { throw 'Build failed: Cashier.PartnerCodesUi' }
dotnet 'tests/Cashier.PartnerCodesUi/bin/Release/net10.0-windows/Cashier.PartnerCodesUi.dll' (Join-Path $runFolder 'partner-dialog.png') |
    Tee-Object -FilePath (Join-Path $runFolder 'Cashier.PartnerCodesUi.txt')
if ($LASTEXITCODE -ne 0) { throw 'Test failed: Cashier.PartnerCodesUi' }
dotnet build 'tests/Cashier.MonthlyDebtUi/Cashier.MonthlyDebtUi.csproj' -c Release -p:NuGetAudit=false
if ($LASTEXITCODE -ne 0) { throw 'Build failed: Cashier.MonthlyDebtUi' }
dotnet 'tests/Cashier.MonthlyDebtUi/bin/Release/net10.0-windows/Cashier.MonthlyDebtUi.dll' (Join-Path $runFolder 'Cashier.MonthlyDebtUi') |
    Tee-Object -FilePath (Join-Path $runFolder 'Cashier.MonthlyDebtUi.txt')
if ($LASTEXITCODE -ne 0) { throw 'Test failed: Cashier.MonthlyDebtUi' }
dotnet build 'tests/Cashier.OpeningBalanceUiTests/Cashier.OpeningBalanceUiTests.csproj' -c Release -p:NuGetAudit=false
if ($LASTEXITCODE -ne 0) { throw 'Build failed: Cashier.OpeningBalanceUiTests' }
dotnet 'tests/Cashier.OpeningBalanceUiTests/bin/Release/net10.0-windows/Cashier.OpeningBalanceUiTests.dll' (Join-Path $runFolder 'Cashier.OpeningBalanceUiTests') |
    Tee-Object -FilePath (Join-Path $runFolder 'Cashier.OpeningBalanceUiTests.txt')
if ($LASTEXITCODE -ne 0) { throw 'Test failed: Cashier.OpeningBalanceUiTests' }
dotnet build 'tests/Cashier.DataExchangeUiTests/Cashier.DataExchangeUiTests.csproj' -c Release -p:NuGetAudit=false
if ($LASTEXITCODE -ne 0) { throw 'Build failed: Cashier.DataExchangeUiTests' }
dotnet 'tests/Cashier.DataExchangeUiTests/bin/Release/net10.0-windows/Cashier.DataExchangeUiTests.dll' (Join-Path $runFolder 'Cashier.DataExchangeUiTests') |
    Tee-Object -FilePath (Join-Path $runFolder 'Cashier.DataExchangeUiTests.txt')
if ($LASTEXITCODE -ne 0) { throw 'Test failed: Cashier.DataExchangeUiTests' }
Write-Output "All 23 test projects passed. Results: $runFolder"
