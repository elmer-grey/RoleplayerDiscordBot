Write-Output "Останавливаем текущий процесс бота..."
Stop-Process -Name "RPBot" -Force

Write-Output "Перекомпилируем проект..."
dotnet build "C:/Favorites/Bot Discord/RPBot/RPBot/RPBot.csproj" -c Release

Write-Output "Запускаем новый процесс..."
Start-Process -FilePath "C:/Favorites/Bot Discord/RPBot/RPBot/bin/Release/net8.0/RPBot.exe"

#Write-Output "Процесс завершён, продолжение выполнения скрипта..."
#Start-Sleep -Seconds 20