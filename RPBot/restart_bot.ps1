Write-Output "Останавливаем текущий процесс бота..."
Stop-Process -Name "RPBot" -Force

Write-Output "Перекомпилируем проект..."
dotnet build "E:/НРИ/RoleplayerBotDiscord/RPBot/RPBot.csproj" -c Release

Write-Output "Запускаем новый процесс..."
Start-Process -FilePath "E:/НРИ/RoleplayerBotDiscord/RPBot/bin/Release/net8.0/RPBot.exe"

#Write-Output "Процесс завершён, продолжение выполнения скрипта..."
#Start-Sleep -Seconds 20